using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Descubre instalaciones de Apache httpd. En Windows: la de Roost, Laragon, XAMPP, WAMP, chocolatey,
/// un <c>C:\Apache24</c> de Apache Lounge, «Archivos de programa», scoop, los servicios de Windows
/// (Apache suele instalarse como servicio) y el PATH; en Linux y macOS, <c>apache2</c> de Debian y
/// Ubuntu, <c>httpd</c> de Red Hat, Fedora, Arch o el de macOS, lo compilado a mano o la imagen Docker
/// <c>httpd</c> (<c>/usr/local/apache2</c>), Homebrew y el PATH. La versión se lee con <c>httpd -v</c>,
/// que no lee la configuración ni arranca nada, y se parsea con <see cref="ComponentParser.ParseApache"/>.
/// </summary>
public sealed class ApacheScanner : ComponentScannerBase
{
    private readonly ScanRoots? _roots;

    /// <param name="roots">Dónde buscar; null es esta máquina (<see cref="ScanRoots.Machine"/>).</param>
    public ApacheScanner(IProcessRunner runner, ScanRoots? roots = null) : base(runner) => _roots = roots;

    public override ComponentKind Kind => ComponentKind.Apache;

    protected override string VersionArguments => "-v";

    protected override IReadOnlyList<string> PathExecutableNames =>
        OperatingSystem.IsWindows() ? ["httpd.exe"] : ["apache2", "httpd"];

    protected override IEnumerable<Candidate> EnumerateCandidates()
    {
        var roots = _roots ?? ScanRoots.Machine();
        return OperatingSystem.IsWindows() ? WindowsCandidates(roots) : PosixCandidates(roots);
    }

    /// <summary>
    /// Los candidatos de Windows, en orden: las raíces conocidas antes que los servicios y el PATH,
    /// para que el Apache de XAMPP que corre como servicio siga siendo «XAMPP».
    /// </summary>
    public static IEnumerable<Candidate> WindowsCandidates(ScanRoots roots)
    {
        // Lo instalado por el propio Roost (roost web use apache): bin/apache/<v>/bin/httpd.exe.
        foreach (var c in RoostCandidates(roots.RoostRoot, "apache", @"bin\httpd.exe"))
            yield return c;

        foreach (var drive in roots.Drives)
        {
            foreach (var p in Globber.Expand(Path.Combine(drive, @"laragon\bin\apache\*\bin\httpd.exe")))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"xampp\apache\bin\httpd.exe")))
                yield return new Candidate(p, "XAMPP");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"wamp64\bin\apache\*\bin\httpd.exe")))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"wamp\bin\apache\*\bin\httpd.exe")))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"tools\Apache2*\bin\httpd.exe")))   // chocolatey
                yield return new Candidate(p, "chocolatey");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"Apache2*\bin\httpd.exe")))         // C:\Apache24 de Apache Lounge
                yield return new Candidate(p, "manual");
        }

        // El paquete de chocolatey instala, si no se le dice otra cosa, en %APPDATA%\Apache24.
        if (!string.IsNullOrEmpty(roots.AppData))
            foreach (var p in Globber.Expand(Path.Combine(roots.AppData, @"Apache2*\bin\httpd.exe")))
                yield return new Candidate(p, "chocolatey");

        foreach (var pf in roots.ProgramFiles)
        {
            foreach (var p in Globber.Expand(Path.Combine(pf, @"Apache Software Foundation\Apache2*\bin\httpd.exe")))
                yield return new Candidate(p, "Program Files");
            foreach (var p in Globber.Expand(Path.Combine(pf, @"Apache2*\bin\httpd.exe")))
                yield return new Candidate(p, "Program Files");
        }

        if (!string.IsNullOrEmpty(roots.UserProfile))
            foreach (var p in Globber.Expand(Path.Combine(roots.UserProfile, @"scoop\apps\apache\current\bin\httpd.exe")))
                yield return new Candidate(p, "scoop");

        // Un Apache instalado como servicio (httpd -k install) fuera de todo lo anterior.
        foreach (var exe in roots.ServiceExecutables())
            if (Path.GetFileName(exe).Equals("httpd.exe", StringComparison.OrdinalIgnoreCase))
                yield return new Candidate(exe, NginxScanner.Services);

        foreach (var dir in roots.PathDirectories)
            yield return new Candidate(Path.Combine(dir, "httpd.exe"), "PATH");
    }

    /// <summary>
    /// Los de Linux y macOS: la distro (<c>apache2</c> o <c>httpd</c>, también el de macOS), lo
    /// compilado a mano o la imagen Docker <c>httpd:*</c> (<c>/usr/local/apache2</c>), Homebrew y
    /// <c>/opt</c>; el PATH, al final.
    /// </summary>
    public static IEnumerable<Candidate> PosixCandidates(ScanRoots roots)
    {
        foreach (var pattern in new[]
                 {
                     "/usr/sbin/apache2", "/usr/sbin/httpd", "/usr/bin/httpd",
                     "/opt/homebrew/bin/httpd",
                     "/usr/local/apache2/bin/httpd", "/usr/local/sbin/httpd", "/usr/local/bin/httpd",
                     "/opt/*/bin/httpd",
                 })
            foreach (var c in roots.Posix(pattern))
                yield return c;

        foreach (var dir in roots.PathDirectories)
        {
            yield return new Candidate(Path.Combine(dir, "apache2"), "PATH");
            yield return new Candidate(Path.Combine(dir, "httpd"), "PATH");
        }
    }

    protected override DetectedComponent? Build(Candidate candidate, string? versionOutput, bool isOnPath) =>
        ComponentParser.ParseApache(versionOutput, candidate.Path, candidate.Source, isOnPath, InstallFolder(candidate.Path));

    /// <summary>La carpeta de la instalación (la que lleva <c>bin\httpd.exe</c>): «httpd-2.4.62-win64-VS17», «Apache24».</summary>
    private static string? InstallFolder(string path)
    {
        try { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))); }
        catch { return null; }
    }
}
