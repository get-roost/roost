using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;

namespace Roost.Core.Composer;

/// <summary>Lo que se le pide a Composer desde la app y desde <c>roost site composer</c>.</summary>
public enum ComposerAction
{
    /// <summary><c>composer install</c>: lo del composer.lock (o resuelve y lo crea si no hay).</summary>
    Install,

    /// <summary><c>composer update</c>: lo más nuevo que permite el composer.json, y reescribe el composer.lock.</summary>
    Update,

    /// <summary><c>composer require &lt;paquete&gt;</c>: lo añade al composer.json y lo instala.</summary>
    Require,
}

/// <summary>
/// El Composer de Roost: lo localiza (o lo instala la primera vez que hace falta, no al instalar
/// Roost) y arma la orden <c>php composer.phar …</c> con el PHP que toca en la carpeta
/// (<see cref="TerminalPhp.Resolve"/>: <c>.php-version</c>, el del sitio o el global) y con el
/// mismo php.ini que el servidor (<see cref="TerminalPhp.Environment"/>: <c>PHPRC</c> y
/// <c>PHP_INI_SCAN_DIR</c>, con openssl, curl, zip, mbstring y el bundle de CA). Así los scripts del
/// proyecto que lance Composer (<c>@php artisan …</c>) heredan la misma configuración. No toca
/// <c>COMPOSER_HOME</c>: la caché y el <c>auth.json</c> del usuario siguen valiendo. Lo usan la
/// CLI, la app y el shim <c>composer</c>; se ejecuta sobre <see cref="ICommandRunner"/>.
/// </summary>
public static partial class ComposerTool
{
    /// <summary>Lo mismo que <c>--no-interaction</c>: sin preguntas (la app no tiene dónde contestarlas).</summary>
    public const string NoInteractionVariable = "COMPOSER_NO_INTERACTION";

    /// <summary>El aviso de siempre antes de ejecutar Composer sobre un proyecto.</summary>
    public const string ScriptsWarning =
        "Composer ejecuta los scripts del composer.json del proyecto (post-install-cmd, post-autoload-dump...) y los plugins que permita: hazlo solo con proyectos en los que confies.";

    /// <summary>
    /// El Composer que usa Roost: el fijado en <see cref="ComposerCatalog.Stable"/> si está y, si no,
    /// el más nuevo de los instalados (uno que dejó una versión anterior de Roost). null si no hay ninguno.
    /// </summary>
    public static InstalledComposer? Locate(RoostPaths paths)
    {
        var installer = new ComposerInstaller(paths);
        return installer.Find(ComposerCatalog.Stable) ?? installer.ListInstalled().FirstOrDefault();
    }

    /// <summary>true si el Composer fijado en esta versión de Roost ya está instalado (si no, <see cref="EnsureAsync"/> lo instala).</summary>
    public static bool IsCurrentInstalled(RoostPaths paths) => new ComposerInstaller(paths).Find(ComposerCatalog.Stable) is not null;

    /// <summary>
    /// El Composer fijado en esta versión de Roost, instalándolo si falta. Uno más viejo que ya
    /// estuviera no basta: Composer saca parches de seguridad y se sube con Roost.
    /// </summary>
    public static async Task<InstalledComposer> EnsureAsync(
        RoostPaths paths, HttpClient http, IProgress<long>? downloaded = null, CancellationToken cancellation = default)
    {
        var installer = new ComposerInstaller(paths, http);
        return installer.Find(ComposerCatalog.Stable) ?? await installer.InstallAsync(ComposerCatalog.Stable, downloaded, cancellation);
    }

    /// <summary>El PHP con el que se ejecuta Composer en <paramref name="directory"/>, y por qué (el mismo que el de la terminal).</summary>
    public static PhpChoice PhpFor(RoostPaths paths, string directory) => TerminalPhp.Resolve(paths, directory, pinned: null);

    /// <summary>Lanza <see cref="RoostException"/> si <paramref name="php"/> es más viejo de lo que pide Composer.</summary>
    public static void CheckPhp(InstalledPhp php, ComposerBuild? build = null)
    {
        build ??= ComposerCatalog.Stable;
        if (php.Version is { } version && version < build.MinPhp)
            throw new RoostException(
                $"{build.Label} necesita PHP {build.MinPhp} o mas nuevo, y aqui toca {php.Name}. Usa otro PHP en esta carpeta (roost php use 8.4 --here).");
    }

    /// <summary>
    /// La orden: <c>&lt;php.exe&gt; &lt;composer.phar&gt; &lt;argumentos&gt;</c> en <paramref name="workingDirectory"/>,
    /// con el php.ini de Roost en el entorno (si aún no existe, se genera) y, salvo
    /// <paramref name="interactive"/>, sin preguntas.
    /// </summary>
    public static CommandSpec Command(
        RoostPaths paths, InstalledPhp php, InstalledComposer composer, IReadOnlyList<string> arguments, string workingDirectory, bool interactive = false)
    {
        var environment = new Dictionary<string, string?>();
        foreach (var (name, value) in TerminalPhp.Environment(paths, php)) environment[name] = value;
        if (!interactive) environment[NoInteractionVariable] = "1";
        return new CommandSpec(php.Executable, [composer.Phar, .. arguments], workingDirectory, environment);
    }

    /// <summary>Los argumentos de cada acción. <c>require</c> exige un paquete válido (<see cref="NormalizePackage"/>).</summary>
    public static IReadOnlyList<string> Arguments(ComposerAction action, string? package = null, bool dev = false) => action switch
    {
        ComposerAction.Install => ["install"],
        ComposerAction.Update => ["update"],
        ComposerAction.Require => dev
            ? ["require", "--dev", NormalizePackage(package ?? "")]
            : ["require", NormalizePackage(package ?? "")],
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Lo que se ve de la orden en un diálogo o en la consola: <c>composer require --dev phpunit/phpunit</c>.</summary>
    public static string Display(IReadOnlyList<string> arguments) => string.Join(' ', new[] { "composer" }.Concat(arguments));

    // El nombre como lo valida Composer (ValidatingArrayLoader::hasPackageNamingError): vendor/paquete
    // en minúsculas, con . _ - entre letras y números.
    [GeneratedRegex(@"^[a-z0-9]([_.-]?[a-z0-9]+)*/[a-z0-9](([_.]|-{1,2})?[a-z0-9]+)*$")]
    private static partial Regex PackageNamePattern();

    // Una restricción de versión: ^3.0, ~2.1, >=1.0 <2.0, 3.*, 1.0 || 2.0, dev-main, 2.0.0-beta1@dev.
    // Cerrada a esos caracteres; nunca empieza por guion, así que no se lee como una opción.
    [GeneratedRegex(@"^[A-Za-z0-9.*^~<>=!|@_+][A-Za-z0-9.*^~<>=!|@_+ ,\-]{0,99}$")]
    private static partial Regex ConstraintPattern();

    /// <summary><c>vendor/paquete</c> o <c>vendor/paquete:versión</c>, como lo acepta Composer (el nombre, sin importar mayúsculas).</summary>
    public static bool IsValidPackage(string? spec)
    {
        try
        {
            NormalizePackage(spec ?? "");
            return true;
        }
        catch (RoostException)
        {
            return false;
        }
    }

    /// <summary>
    /// El paquete listo para <c>composer require</c>: el nombre en minúsculas y, si la hay, la versión
    /// tras <c>:</c>. Lanza <see cref="RoostException"/> si no vale. Solo paquetes <c>vendor/paquete</c>:
    /// las de la plataforma (<c>php</c>, <c>ext-*</c>) se piden a mano en el composer.json.
    /// </summary>
    public static string NormalizePackage(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0)
            throw new RoostException("Falta el paquete: vendor/paquete, y si quieres su version (monolog/monolog:^3).");
        var colon = spec.IndexOf(':');
        var name = (colon < 0 ? spec : spec[..colon]).Trim().ToLowerInvariant();
        var version = colon < 0 ? null : spec[(colon + 1)..].Trim();
        if (!PackageNamePattern().IsMatch(name))
            throw new RoostException($"'{spec}' no es un paquete de Composer: tiene que ser vendor/paquete (minusculas, numeros, . _ -), como monolog/monolog.");
        if (version is not null && !ConstraintPattern().IsMatch(version))
            throw new RoostException($"'{version}' no es una version que entienda Composer (prueba con ^3.0, ~2.1, 3.* o dev-main).");
        return version is null ? name : $"{name}:{version}";
    }

    /// <summary>
    /// PURO: lo que le pasa el shim <c>composer</c> al php real: el phar entre comillas y detrás la línea
    /// de órdenes tal cual llegó (sin volver a partirla, como el shim de php).
    /// </summary>
    public static string ShimArguments(string phar, string rest) =>
        rest.Length == 0 ? $"\"{phar}\"" : $"\"{phar}\" {rest}";

    [GeneratedRegex(@"Composer (?:version )?(\d+\.\d+\.\d+\S*)")]
    private static partial Regex VersionPattern();

    /// <summary>PURO: la versión de la salida de <c>composer --version</c> («Composer version 2.10.3 2026-08-27 …»), o null.</summary>
    public static string? ParseVersion(string? output) =>
        output is not null && VersionPattern().Match(output) is { Success: true } m ? m.Groups[1].Value : null;
}
