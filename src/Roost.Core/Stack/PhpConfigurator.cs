using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;

namespace Roost.Core.Stack;

/// <summary>Una extensión de una instalación: si se carga, si va de serie y si es zend (OPcache, Xdebug).</summary>
public sealed record PhpExtensionState(string Name, bool On, bool ByDefault, bool Zend);

/// <summary>Lo que dijo <c>php -v</c> después de un cambio: su primera línea, la versión de Xdebug si cargó, y los avisos de arranque.</summary>
public sealed record PhpCheck(string? Banner, string? XdebugVersion, IReadOnlyList<string> Warnings)
{
    /// <summary>php.exe no arrancó (o no contestó a tiempo): no se ha podido comprobar nada.</summary>
    public static readonly PhpCheck NotRun = new(null, null, []);

    public bool Ran => Banner is not null;
}

/// <summary>
/// Activa y quita extensiones y Xdebug de una instalación y cambia sus ajustes rápidos. Es la misma
/// elección para la CLI (<c>roost php ext</c>, <c>roost php xdebug</c>) y la app: se guarda
/// (<c>extensiones.json</c>, <c>ajustes.json</c>), se rehace su php.ini y se comprueba con
/// <c>php -v</c> que PHP arranca con ello; si lo que se acaba de activar no carga, se deja como
/// estaba y se dice por qué (un pool que no carga una DLL avisa en cada petición). No reinicia
/// nada: eso lo hacen la CLI y la app, que hablan con el daemon.
/// </summary>
public sealed partial class PhpConfigurator(RoostPaths paths, ICommandRunner runner)
{
    private readonly StackPlanner _planner = new(paths, ProcessRunner.Default);

    public StackPlanner Planner => _planner;

    /// <summary>
    /// Las extensiones de la instalación (las DLL de su <c>ext/</c> y Xdebug si Roost tiene la suya),
    /// por nombre, con si se cargan. OPcache sin DLL (PHP 8.5, que la lleva dentro) no sale.
    /// </summary>
    public IReadOnlyList<PhpExtensionState> Extensions(InstalledPhp php)
    {
        var available = new HashSet<string>(PhpIni.AvailableExtensions(php.Directory), StringComparer.OrdinalIgnoreCase);
        if (new XdebugInstaller(paths).Locate(php) is not null) available.Add(PhpIni.Xdebug);
        var enabled = PhpIni.Enabled(available, Choice(php));
        return available.Order(StringComparer.Ordinal)
            .Select(name => new PhpExtensionState(name, enabled.Contains(name), PhpIni.EnabledByDefault(name), PhpIni.IsZendExtension(name)))
            .ToList();
    }

    public PhpExtensionChoice Choice(InstalledPhp php) => PhpExtensionChoice.Load(_planner.PhpExtensionChoicePath(php));

    public PhpSettings Settings(InstalledPhp php) => PhpSettings.Load(_planner.PhpSettingsPath(php));

    /// <summary>true si Xdebug está activado en la instalación (y su DLL está: sin ella no se carga).</summary>
    public bool XdebugOn(InstalledPhp php) =>
        Choice(php).On.Contains(PhpIni.Xdebug) && Extensions(php).Any(e => e.Name == PhpIni.Xdebug && e.On);

    /// <summary>
    /// Activa o quita una extensión de <c>ext/</c>. Xdebug no va por aquí: tiene su DLL y su
    /// comprobación (<see cref="SetXdebugAsync"/>). Lanza <see cref="RoostException"/> si el build no
    /// trae esa extensión, o si al activarla PHP no la carga (y entonces la deja como estaba).
    /// </summary>
    public async Task<PhpCheck> SetExtensionAsync(InstalledPhp php, string name, bool enabled, CancellationToken cancellation = default)
    {
        name = NormalizeName(name);
        if (name.Equals(PhpIni.Xdebug, StringComparison.OrdinalIgnoreCase))
            throw new RoostException("Xdebug tiene su propio comando: roost php xdebug on|off (baja la DLL que toca la primera vez).");
        var available = PhpIni.AvailableExtensions(php.Directory);
        if (!available.Contains(name))
        {
            throw new RoostException($"PHP {php.Name} no trae la extension '{name}' (no hay ext\\php_{name}.dll). "
                + $"Las que trae: {string.Join(", ", available.Order(StringComparer.Ordinal))}.");
        }
        return await ApplyChoiceAsync(php, Choice(php).With(name, enabled), enabled ? name : null, cancellation);
    }

    /// <summary>
    /// Activa o quita Xdebug. Para activarlo, la DLL tiene que estar ya (<see cref="XdebugInstaller.InstallAsync"/>):
    /// la descarga la hacen la CLI y la app, con su progreso. Si PHP no la carga, la deja apagada y lo dice.
    /// </summary>
    public async Task<PhpCheck> SetXdebugAsync(InstalledPhp php, bool enabled, CancellationToken cancellation = default)
    {
        if (enabled)
        {
            if (XdebugCatalog.WhyNot(php) is { } why) throw new RoostException(why);
            if (new XdebugInstaller(paths).Locate(php) is null)
                throw new RoostException($"Xdebug no esta instalado para PHP {php.Name}: roost php xdebug on lo baja.");
        }
        var check = await ApplyChoiceAsync(php, Choice(php).With(PhpIni.Xdebug, enabled), enabled ? PhpIni.Xdebug : null, cancellation);
        if (enabled && check.Ran && check.XdebugVersion is null)
        {
            await ApplyChoiceAsync(php, Choice(php).With(PhpIni.Xdebug, false), null, cancellation);
            throw new RoostException($"PHP {php.Name} no ha cargado Xdebug (php -v no lo nombra); se deja apagado."
                + (check.Warnings.Count > 0 ? " " + string.Join(" ", check.Warnings) : ""));
        }
        return check;
    }

    /// <summary>Guarda los ajustes rápidos (validados: lanza si no valen) y rehace el php.ini.</summary>
    public string SetSettings(InstalledPhp php, PhpSettings settings)
    {
        settings.Save(_planner.PhpSettingsPath(php));
        return _planner.WritePhpIni(php);
    }

    /// <summary>
    /// La orden para ejecutar el <c>php.exe</c> de la instalación como en la terminal: con su php.ini
    /// de Roost (<c>PHPRC</c>) y las dos conf.d (<c>PHP_INI_SCAN_DIR</c>). Para <c>-v</c>, <c>-i</c> y
    /// <c>-m</c> (phpinfo y módulos en la app).
    /// </summary>
    public CommandSpec Command(InstalledPhp php, params string[] arguments)
    {
        var environment = TerminalPhp.Environment(paths, php).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        return new CommandSpec(php.Executable, arguments, php.Directory, environment, TimeSpan.FromSeconds(30));
    }

    /// <summary><c>php -v</c> con la configuración de Roost; <see cref="PhpCheck.NotRun"/> si php.exe no arranca.</summary>
    public async Task<PhpCheck> CheckAsync(InstalledPhp php, CancellationToken cancellation = default)
    {
        CommandResult result;
        try { result = await runner.RunAsync(Command(php, "-v"), null, cancellation); }
        catch (RoostException) { return PhpCheck.NotRun; }
        if (result.TimedOut) return PhpCheck.NotRun;
        var output = result.Stdout + "\n" + result.Stderr;
        var banner = result.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PHP ", StringComparison.Ordinal));
        return new PhpCheck(banner, XdebugVersion(output), StartupErrors(output));
    }

    // Guarda la elección, rehace el php.ini y comprueba. Si la que se acaba de activar sale en un
    // aviso de arranque, vuelve a la de antes.
    private async Task<PhpCheck> ApplyChoiceAsync(InstalledPhp php, PhpExtensionChoice choice, string? activated, CancellationToken cancellation)
    {
        var file = _planner.PhpExtensionChoicePath(php);
        var before = PhpExtensionChoice.Load(file);
        choice.Save(file);
        _planner.WritePhpIni(php);
        if (activated is null) return await CheckAsync(php, cancellation);

        var check = await CheckAsync(php, cancellation);
        var failed = check.Warnings.Where(w => Mentions(w, activated)).ToList();
        if (failed.Count == 0) return check;

        before.Save(file);
        _planner.WritePhpIni(php);
        throw new RoostException($"PHP {php.Name} no puede cargar {activated}; se deja como estaba. {failed[0]}");
    }

    /// <summary>
    /// Un <c>.vscode/launch.json</c> para la extensión PHP Debug (<c>xdebug.php-debug</c>) con esta
    /// instalación: «Escuchar Xdebug» (las peticiones web con XDEBUG_TRIGGER, sin <c>pathMappings</c>
    /// porque todo es local) y «Este script», que lanza el fichero abierto con el <c>php.exe</c> de la
    /// instalación y el php.ini y las conf.d de Roost (sin depender de los shims).
    /// </summary>
    public string VsCodeLaunchJson(InstalledPhp php)
    {
        var env = new Dictionary<string, string>(TerminalPhp.Environment(paths, php))
        {
            ["XDEBUG_MODE"] = "debug,develop",
            ["XDEBUG_CONFIG"] = "client_port=${port}",
        };
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            json.WriteStartObject();
            json.WriteString("version", "0.2.0");
            json.WriteStartArray("configurations");

            json.WriteStartObject();
            json.WriteString("name", "Escuchar Xdebug (Roost)");
            json.WriteString("type", "php");
            json.WriteString("request", "launch");
            json.WriteNumber("port", 9003);
            json.WriteEndObject();

            json.WriteStartObject();
            json.WriteString("name", $"Este script con PHP {php.Version?.ToString() ?? php.Name} (Roost)");
            json.WriteString("type", "php");
            json.WriteString("request", "launch");
            json.WriteString("program", "${file}");
            json.WriteString("cwd", "${fileDirname}");
            json.WriteNumber("port", 0);
            json.WriteString("runtimeExecutable", php.Executable);
            json.WriteStartArray("runtimeArgs");
            json.WriteStringValue("-dxdebug.start_with_request=yes");
            json.WriteEndArray();
            json.WriteStartObject("env");
            foreach (var (name, value) in env) json.WriteString(name, value);
            json.WriteEndObject();
            json.WriteEndObject();

            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary><c>php_curl.dll</c>, <c>php_curl</c> o <c>Curl</c> → <c>curl</c>.</summary>
    public static string NormalizeName(string name)
    {
        var n = name.Trim();
        if (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        return n.ToLowerInvariant();
    }

    /// <summary>PURO: la versión de Xdebug que dice <c>php -v</c> («with Xdebug v3.5.3, Copyright…»), o null.</summary>
    public static string? XdebugVersion(string phpV) =>
        XdebugPattern().Match(phpV) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// PURO: los avisos de arranque de PHP, sin repetir (los da por stdout y por stderr) y sin el
    /// «PHP Warning:» ni el «in Unknown on line 0»: una DLL que no carga (<c>Unable to load dynamic
    /// library</c>), una zend que no carga (<c>Failed loading</c> en 8.x, <c>Failed to load</c> en 7.4)
    /// o Xdebug cargado como extensión normal o dos veces.
    /// </summary>
    public static IReadOnlyList<string> StartupErrors(string output)
    {
        var seen = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!(line.Contains("PHP Startup:", StringComparison.Ordinal)
                  || line.Contains("Failed loading", StringComparison.Ordinal)
                  || line.Contains("Failed to load", StringComparison.Ordinal)
                  || line.Contains("Cannot load Xdebug", StringComparison.Ordinal)
                  || line.Contains("MUST be loaded as a Zend extension", StringComparison.Ordinal)))
                continue;
            line = WarningPrefix().Replace(line, "");
            line = line.Replace(" in Unknown on line 0", "", StringComparison.Ordinal).Trim();
            if (line.StartsWith("PHP Startup: ", StringComparison.Ordinal)) line = line["PHP Startup: ".Length..];
            if (!seen.Contains(line)) seen.Add(line);
        }
        return seen;
    }

    /// <summary>PURO: los módulos que lista <c>php -m</c>, sin las cabeceras (<c>[PHP Modules]</c>, <c>[Zend Modules]</c>) ni repetidos.</summary>
    public static IReadOnlyList<string> Modules(string phpM) =>
        phpM.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.Contains("PHP Startup", StringComparison.Ordinal) && !l.Contains("Warning", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Mentions(string warning, string extension) =>
        extension == PhpIni.Xdebug
            ? warning.Contains("xdebug", StringComparison.OrdinalIgnoreCase)
            : warning.Contains($"'{extension}'", StringComparison.OrdinalIgnoreCase)
              || warning.Contains($"php_{extension}.dll", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"with Xdebug v([0-9][^\s,]*)")]
    private static partial Regex XdebugPattern();

    [GeneratedRegex(@"^(PHP )?(Warning|Fatal error|Notice|Deprecated):\s+")]
    private static partial Regex WarningPrefix();
}
