using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Install;

/// <summary>
/// El catálogo de PHP para Linux: los tar.gz que compila get-roost/roost-builds con static-php-cli
/// (target glibc 2.17, NTS, x86_64; con <c>php</c>, <c>php-fpm</c>, las extensiones que necesitan
/// Laravel, WordPress y Symfony integradas y el <c>xdebug.so</c> de su rama dentro), fijados a mano en
/// Roost con su SHA-256 y su tamaño, como MinGit o Composer. No se lee nada de la red para saber qué
/// hay: lo que vale es lo que esta versión de Roost comprobó, y el fichero se verifica antes de
/// extraerlo. PHP 7.4 y 8.0 no se pueden compilar así (<see cref="WhyWindowsOnly"/>): en Linux Roost
/// instala de la 8.1 a la 8.5; en Windows siguen estando.
/// </summary>
/// <remarks>
/// Para cambiar de versión: lanzar <c>php.yml</c> en roost-builds (sale un tar.gz por rama, verificado
/// contra el SHA-256 que publica php.net y probado en el runner), publicar la release
/// <c>php-&lt;versión&gt;</c> con su <c>SHA256SUMS.txt</c> y la attestation, y fijar aquí la versión, el
/// SHA-256 y el tamaño del fichero publicado (<c>gh attestation verify &lt;fichero&gt; --repo
/// get-roost/roost-builds</c> lo comprueba). <b>Cada run compila de nuevo</b> (lo reproducible es el
/// proceso, no el byte a byte): lo que se fija aquí tiene que ser el SHA-256 del tar.gz que está en la
/// release, así que si se publica desde otro run distinto del que se probó, se vuelven a fijar los
/// hashes de ese run (el <c>SHA256SUMS.txt</c> de la release, o el artefacto <c>php-&lt;rama&gt;</c> del run)
/// antes de cerrar la versión de Roost. La URL sale de <see cref="BaseUrl"/>; con
/// <see cref="UrlVariable"/> se apunta a un espejo o a un servidor de pruebas, y el SHA-256 se
/// comprueba igual.
/// </remarks>
public static partial class LinuxPhpCatalog
{
    public const string SourceName = "roost-builds";

    /// <summary>Otra base para las URLs de roost-builds (un espejo, o un servidor local de pruebas). El SHA-256 se comprueba igual.</summary>
    public const string UrlVariable = "ROOST_BUILDS_URL";

    /// <summary>Las releases de roost-builds: <c>&lt;base&gt;php-&lt;versión&gt;/php-&lt;versión&gt;-nts-linux-x86_64.tar.gz</c>.</summary>
    public static readonly Uri DefaultBaseUrl = new("https://github.com/get-roost/roost-builds/releases/download/");

    /// <summary>Lo que va en <see cref="PhpBuild.Compiler"/>: la glibc mínima con la que corren (static-php-cli, <c>SPC_TARGET=x86_64-linux-gnu.2.17</c>).</summary>
    public const string Compiler = "glibc2.17";

    /// <summary>Las ramas que en Linux no existen: solo están en Windows (static-php-cli no las compila).</summary>
    public static readonly IReadOnlyList<string> WindowsOnlyBranches = ["7.4", "8.0"];

    // Los tar.gz del run 40124456483 de php.yml (24-oct-2026; StaticPHP 3.0.0-alpha1, commit d7f1a88 de
    // v3; fuentes de php.net y xdebug.org verificadas contra su SHA-256 publicado; Xdebug 3.5.3; GLIBC
    // 2.17): el SHA-256 y el tamaño son los del SHA256SUMS.txt de cada artefacto, comprobados al bajarlo.
    // Si la release se publica desde otro run, hay que volver a fijarlos (ver remarks).
    private static readonly (string Version, string Sha256, long Size)[] Pinned =
    [
        ("8.5.12", "PENDIENTE", 0),
        ("8.4.27", "5b5cb8036491c978e30e823181cec874d5421285e88dea514ec49c8ef74dfb95", 54_258_532),
        ("8.3.36", "e32286ea703c77f6e63a8073b361156f38eaf27de410af53370e8a5a52a35b26", 52_496_952),
        ("8.2.35", "6bb77305ef9bf19ddcb0ae4d4c0f823351c7487c46e5e252b6ba28326d897618", 52_262_193),
        ("8.1.34", "PENDIENTE", 0),
    ];

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex BranchPattern();

    /// <summary>La base de las URLs: la de <see cref="UrlVariable"/> si está puesta (con la barra final), o la de GitHub.</summary>
    public static Uri BaseUrl => BaseUrlFrom(Environment.GetEnvironmentVariable(UrlVariable));

    /// <summary>PURO: la base a partir del valor de la variable; null o vacío es la de GitHub.</summary>
    public static Uri BaseUrlFrom(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultBaseUrl;
        var text = configured.Trim();
        if (!text.EndsWith('/')) text += "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new RoostException($"{UrlVariable}={configured} no es una URL http(s).");
    }

    /// <summary>El nombre del tar.gz de una versión en las releases de roost-builds.</summary>
    public static string FileName(string version) => $"php-{version}-nts-linux-x86_64.tar.gz";

    /// <summary>La URL de una versión bajo una base: <c>&lt;base&gt;php-8.4.27/php-8.4.27-nts-linux-x86_64.tar.gz</c>.</summary>
    public static Uri UrlFor(Uri baseUrl, string version) => new(baseUrl, $"php-{version}/{FileName(version)}");

    /// <summary>Los builds fijados en esta versión de Roost, de la rama más nueva a la más vieja, con las URLs de <see cref="BaseUrl"/>.</summary>
    public static IReadOnlyList<PhpBuild> Builds => BuildsFor(BaseUrl);

    /// <summary>PURO: los builds fijados con las URLs bajo <paramref name="baseUrl"/>.</summary>
    public static IReadOnlyList<PhpBuild> BuildsFor(Uri baseUrl) =>
        Pinned.Select(p =>
        {
            if (!ComponentVersion.TryParse(p.Version, out var version))
                throw new InvalidOperationException($"LinuxPhpCatalog: '{p.Version}' no es una version.");
            return new PhpBuild
            {
                Version = version,
                Branch = $"{version.Major}.{version.Minor}",
                ThreadSafe = false,
                Compiler = Compiler,
                Architecture = "x64",
                FileName = FileName(p.Version),
                Url = UrlFor(baseUrl, p.Version),
                Sha256 = p.Sha256,
                SizeText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Size / 1048576.0:0.0}MB"),
                Size = p.Size,
                Os = PhpOs.Linux,
            };
        }).ToList();

    /// <summary>Lo mismo que en Windows, sin red: el catálogo va fijado en Roost.</summary>
    public static Task<IReadOnlyList<PhpBuild>> FetchAsync(HttpClient http, CancellationToken cancellation = default) =>
        Task.FromResult(Builds);

    /// <summary>Por qué una rama no está en Linux, o null si está (o si no es de las que faltan por eso).</summary>
    public static string? WhyWindowsOnly(string branch) => branch switch
    {
        "7.4" => "PHP 7.4 solo esta en Windows: static-php-cli no lo compila (OPcache estatico solo existe desde PHP 8.0). En Linux Roost instala de la 8.1 a la 8.5.",
        "8.0" => "PHP 8.0 solo esta en Windows: no compila con static-php-cli. En Linux Roost instala de la 8.1 a la 8.5.",
        _ => null,
    };

    /// <summary>
    /// Elige el build que pide el usuario: una rama ("8.4", la última de esa rama) o una versión exacta
    /// ("8.4.27", que tiene que ser la fijada). Solo hay NTS x64: <c>--ts</c> y <c>--arch x86</c> se
    /// rechazan con un texto claro, igual que 7.4 y 8.0.
    /// </summary>
    public static PhpBuild Resolve(IEnumerable<PhpBuild> builds, string spec, bool threadSafe = false, string architecture = "x64")
    {
        var all = builds.ToList();
        spec = spec.Trim();

        string branch;
        ComponentVersion? exact = null;
        if (BranchPattern().IsMatch(spec))
        {
            branch = spec;
        }
        else if (ComponentVersion.TryParse(spec, out var parsed))
        {
            exact = parsed;
            branch = $"{parsed.Major}.{parsed.Minor}";
        }
        else
        {
            throw new RoostException($"'{spec}' no es una version de PHP: usa una rama (8.4) o una version (8.4.27).");
        }

        if (threadSafe)
            throw new RoostException("En Linux Roost solo tiene builds NTS (los que usa php-fpm): quita --ts.");
        if (!architecture.Equals("x64", StringComparison.OrdinalIgnoreCase))
            throw new RoostException("En Linux Roost solo tiene builds de 64 bits (x86_64): quita --arch.");
        if (WhyWindowsOnly(branch) is { } why)
            throw new RoostException(why);

        var inBranch = all.Where(b => b.Branch == branch).ToList();
        if (inBranch.Count == 0)
        {
            var branches = string.Join(", ", all.Select(b => b.Branch).Distinct());
            throw new RoostException($"La rama {branch} no esta entre los PHP de roost-builds que trae esta version de Roost. Ramas disponibles: {branches}.");
        }

        var latest = inBranch[0].Version;
        if (exact is { } wanted && wanted.CompareTo(latest) != 0)
        {
            throw new RoostException(
                $"Esta version de Roost trae de la rama {branch} solo la {latest} (una por rama, verificada). "
                + $"Instala {latest} (o pide la rama: {branch}).");
        }
        return inBranch[0];
    }
}
