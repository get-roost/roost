using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Descubre instalaciones de nginx. En Windows: la de Roost, Laragon, chocolatey, un <c>C:\nginx…</c>
/// suelto, scoop, los servicios de Windows y el PATH; en Linux y macOS, las rutas de distro, lo
/// compilado a mano (<c>/usr/local/nginx</c>), Homebrew y el PATH, cada una con su origen
/// (<see cref="PosixOrigin"/>). La versión se lee con <c>nginx -v</c>, que solo la escribe (en stderr):
/// nunca se arranca nada. Se parsea con <see cref="ComponentParser.ParseNginx"/>.
/// </summary>
public sealed class NginxScanner : ComponentScannerBase
{
    private readonly ScanRoots? _roots;

    /// <param name="roots">Dónde buscar; null es esta máquina (<see cref="ScanRoots.Machine"/>).</param>
    public NginxScanner(IProcessRunner runner, ScanRoots? roots = null) : base(runner) => _roots = roots;

    public override ComponentKind Kind => ComponentKind.Nginx;

    protected override string VersionArguments => "-v";

    protected override IReadOnlyList<string> PathExecutableNames =>
        OperatingSystem.IsWindows() ? ["nginx.exe"] : ["nginx"];

    protected override IEnumerable<Candidate> EnumerateCandidates()
    {
        var roots = _roots ?? ScanRoots.Machine();
        return OperatingSystem.IsWindows() ? WindowsCandidates(roots) : PosixCandidates(roots);
    }

    /// <summary>
    /// Los candidatos de Windows, en orden: las raíces conocidas antes que los servicios y el PATH,
    /// para que cada binario conserve su origen real.
    /// </summary>
    public static IEnumerable<Candidate> WindowsCandidates(ScanRoots roots)
    {
        // Lo instalado por el propio Roost (roost up / roost web use nginx).
        foreach (var c in RoostCandidates(roots.RoostRoot, "nginx", "nginx.exe"))
            yield return c;

        foreach (var drive in roots.Drives)
        {
            foreach (var p in Globber.Expand(Path.Combine(drive, @"laragon\bin\nginx\*\nginx.exe")))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"tools\nginx*\nginx.exe")))   // chocolatey
                yield return new Candidate(p, "chocolatey");
            foreach (var p in Globber.Expand(Path.Combine(drive, @"nginx*\nginx.exe")))         // C:\nginx-1.30.6 suelto
                yield return new Candidate(p, "manual");
        }

        if (!string.IsNullOrEmpty(roots.UserProfile))
            foreach (var p in Globber.Expand(Path.Combine(roots.UserProfile, @"scoop\apps\nginx\current\nginx.exe")))
                yield return new Candidate(p, "scoop");

        // Un nginx como servicio (con WinSW, por ejemplo) que no esté en ninguna de las anteriores.
        foreach (var exe in roots.ServiceExecutables())
            if (Path.GetFileName(exe).Equals("nginx.exe", StringComparison.OrdinalIgnoreCase))
                yield return new Candidate(exe, Services);

        foreach (var dir in roots.PathDirectories)
            yield return new Candidate(Path.Combine(dir, "nginx.exe"), "PATH");
    }

    /// <summary>
    /// Los de Linux y macOS: la distro (<c>/usr/sbin</c>, y <c>/usr/bin</c> en Arch), lo compilado a
    /// mano con el prefijo de serie (<c>/usr/local/nginx</c>), Homebrew y <c>/opt</c>; el PATH, al final.
    /// </summary>
    public static IEnumerable<Candidate> PosixCandidates(ScanRoots roots)
    {
        foreach (var pattern in new[]
                 {
                     "/usr/sbin/nginx", "/usr/bin/nginx",
                     "/opt/homebrew/bin/nginx",
                     "/usr/local/nginx/sbin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx",
                     "/opt/*/sbin/nginx",
                 })
            foreach (var c in roots.Posix(pattern))
                yield return c;

        foreach (var dir in roots.PathDirectories)
            yield return new Candidate(Path.Combine(dir, "nginx"), "PATH");
    }

    /// <summary>El origen de lo que solo se encuentra por un servicio de Windows.</summary>
    public const string Services = "servicio de Windows";

    protected override DetectedComponent? Build(Candidate candidate, string? versionOutput, bool isOnPath) =>
        ComponentParser.ParseNginx(versionOutput, candidate.Path, candidate.Source, isOnPath, FolderName(candidate.Path));

    private static string? FolderName(string path)
    {
        try { return Path.GetFileName(Path.GetDirectoryName(path)); }
        catch { return null; }
    }
}
