using System.Text.RegularExpressions;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Descubre instalaciones de PHP. En Windows barre PATH + Laragon/XAMPP/WAMP/scoop/
/// chocolatey/Herd en varias unidades; en Linux/macOS barre PATH + rutas típicas de
/// distro y Homebrew. La versión se lee con <c>php -v</c> y se parsea con
/// <see cref="ComponentParser.ParsePhp"/>.
/// </summary>
public sealed partial class PhpScanner : ComponentScannerBase
{
    public PhpScanner(IProcessRunner runner) : base(runner) { }

    public override ComponentKind Kind => ComponentKind.Php;

    protected override string VersionArguments => "-v";

    protected override IReadOnlyList<string> PathExecutableNames =>
        OperatingSystem.IsWindows() ? ["php.exe"] : ["php"];

    // Solo nombres php o php8.x; nunca phpize, php-config, php-fpm… (igual que el script Python).
    [GeneratedRegex(@"^php(8\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PhpNamePattern();

    protected override IEnumerable<Candidate> EnumerateCandidates() =>
        OperatingSystem.IsWindows() ? WindowsCandidates() : PosixCandidates();

    private static IEnumerable<Candidate> WindowsCandidates()
    {
        // Lo instalado por el propio Roost (roost php install).
        foreach (var c in RoostCandidates("php", "php.exe"))
            yield return c;

        // Primero las raíces conocidas: así una instalación de Laragon/XAMPP/… conserva
        // su origen real aunque además esté en el PATH (lo activo lo marca IsOnPath, aparte).
        foreach (var drive in WindowsPaths.Drives())
        {
            foreach (var p in Globber.Expand(drive + @"laragon\bin\php\*\php.exe"))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(drive + @"xampp\php\php.exe"))
                yield return new Candidate(p, "XAMPP");
            foreach (var p in Globber.Expand(drive + @"wamp64\bin\php\*\php.exe"))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(drive + @"wamp\bin\php\*\php.exe"))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(drive + @"tools\php*\php.exe")) // chocolatey
                yield return new Candidate(p, "chocolatey");
            foreach (var p in Globber.Expand(drive + @"php*\php.exe")) // C:\php8.3\php.exe suelto
                yield return new Candidate(p, "manual");
        }

        var home = WindowsPaths.UserProfile();
        if (!string.IsNullOrEmpty(home))
        {
            foreach (var p in Globber.Expand(Path.Combine(home, @"scoop\apps\php*\current\php.exe")))
                yield return new Candidate(p, "scoop");
            foreach (var p in Globber.Expand(Path.Combine(home, @".config\herd\bin\php*\php.exe")))
                yield return new Candidate(p, "Herd");
        }

        // El PATH al final: solo aporta las que no estén ya en una raíz conocida.
        foreach (var dir in PathProbe.Directories())
            yield return new Candidate(Path.Combine(dir, "php.exe"), "PATH");
    }

    private static IEnumerable<Candidate> PosixCandidates()
    {
        // Rutas típicas primero: Debian/Ondřej, remi, compilados a mano, Homebrew.
        var system = new[]
        {
            "/usr/bin/php", "/usr/bin/php8.*",
            "/usr/local/bin/php", "/usr/local/bin/php8.*",
            "/opt/*/bin/php", "/opt/remi/*/root/usr/bin/php",
        };
        foreach (var pattern in system)
            foreach (var p in Globber.Expand(pattern))
                if (PhpNameOk(p))
                    yield return new Candidate(p, "system");

        var homebrew = new[] { "/opt/homebrew/bin/php*", "/opt/homebrew/opt/php*/bin/php" };
        foreach (var pattern in homebrew)
            foreach (var p in Globber.Expand(pattern))
                if (PhpNameOk(p))
                    yield return new Candidate(p, "Homebrew");

        // PATH al final: php y php8.0 … php8.9 que no estén ya en una ruta típica.
        foreach (var dir in PathProbe.Directories())
        {
            yield return new Candidate(Path.Combine(dir, "php"), "PATH");
            for (var minor = 0; minor <= 9; minor++)
                yield return new Candidate(Path.Combine(dir, $"php8.{minor}"), "PATH");
        }
    }

    private static bool PhpNameOk(string path) =>
        PhpNamePattern().IsMatch(Path.GetFileName(path));

    protected override DetectedComponent? Build(Candidate candidate, string? versionOutput, bool isOnPath) =>
        ComponentParser.ParsePhp(
            versionOutput,
            candidate.Path,
            candidate.Source,
            isOnPath,
            FolderName(candidate.Path));

    /// <summary>Nombre de la carpeta contenedora, de donde se sacan arch/thread-safety en Laragon.</summary>
    private static string? FolderName(string path)
    {
        try { return Path.GetFileName(Path.GetDirectoryName(path)); }
        catch { return null; }
    }
}
