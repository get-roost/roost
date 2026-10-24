using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Scanning.Scanners;

namespace Roost.Core.Tests;

/// <summary>
/// K2: los scanners de nginx y Apache. Las salidas de <c>nginx -v</c> (que va por stderr) y de
/// <c>httpd -v</c> están grabadas de binarios reales; las rutas son ficheros vacíos en una carpeta
/// temporal que hace de unidad, de raíz de Roost, de perfil y de PATH (<see cref="ScanRoots"/>).
/// Nada se ejecuta.
/// </summary>
public class WebServerScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-web-scan-" + Guid.NewGuid().ToString("N"));

    // Grabadas: el nginx 1.30.6 de nginx.org y el Apache 2.4.69 de Apache Lounge (lo que instala Roost).
    private const string NginxWindows = "nginx version: nginx/1.30.6\r\n";
    private const string ApacheLounge = "Server version: Apache/2.4.69 (Win64)\r\nApache Lounge VS18 Server built:   Oct  1 2026 09:34:06\r\n";
    private const string ApacheXampp = "Server version: Apache/2.4.58 (Win64)\r\nServer built:   Oct 16 2023 15:12:22\r\n";

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    // ---- Las salidas de versión --------------------------------------------------------------

    [Theory]
    [InlineData(NginxWindows, "1.30.6", "nginx version: nginx/1.30.6")]
    [InlineData("nginx version: nginx/1.24.0 (Ubuntu)\n", "1.24.0", "nginx version: nginx/1.24.0 (Ubuntu)")]
    [InlineData("nginx: [warn] algo\nnginx version: nginx/1.26.3\n", "1.26.3", "nginx version: nginx/1.26.3")]
    public void NginxV_SacaLaVersion_YGuardaLaLineaParaLosSufijosDeDistro(string output, string version, string raw)
    {
        var c = ComponentParser.ParseNginx(output, @"C:\nginx\nginx.exe", "manual", isOnPath: false);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.Nginx, c!.Kind);
        Assert.Equal(version, c.Version.ToString());
        Assert.Equal(raw, c.Version.Raw);
        Assert.Null(c.Sapi);
    }

    [Theory]
    [InlineData("nginx version: openresty/1.25.3.1\n")]   // un fork: numera y parchea a su manera
    [InlineData("Tengine version: Tengine/3.1.0\nnginx version: Tengine/3.1.0\n")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("'nginx' no se reconoce como un comando interno o externo")]
    public void NginxV_LoQueNoEsNginx_NoCuenta(string? output)
        => Assert.Null(ComponentParser.ParseNginx(output, "/usr/sbin/nginx", "system", false));

    [Theory]
    [InlineData(ApacheLounge, "2.4.69", "x64")]
    [InlineData(ApacheXampp, "2.4.58", "x64")]
    [InlineData("Server version: Apache/2.4.58 (Ubuntu)\nServer built:   2025-08-11T11:10:09\n", "2.4.58", null)]
    [InlineData("Server version: Apache/2.4.62 (Red Hat Enterprise Linux)\nServer built:   Jan 29 2025 00:00:00\n", "2.4.62", null)]
    [InlineData("Server version: Apache/2.2.31 (Win32)\nServer built:   Jul 15 2015 12:13:51\n", "2.2.31", "x86")]
    public void HttpdV_SacaLaVersionYLaArquitectura(string output, string version, string? arch)
    {
        var c = ComponentParser.ParseApache(output, @"C:\xampp\apache\bin\httpd.exe", "XAMPP", isOnPath: false);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.Apache, c!.Kind);
        Assert.Equal(version, c.Version.ToString());
        Assert.StartsWith("Server version: Apache/", c.Version.Raw);
        Assert.Equal(arch, c.Architecture);
    }

    [Theory]
    [InlineData("Server version: IBM_HTTP_Server/9.0.5.20 (Unix)\n")]
    [InlineData("httpd: Syntax error on line 1\n")]
    [InlineData("")]
    public void HttpdV_LoQueNoEsApache_NoCuenta(string output)
        => Assert.Null(ComponentParser.ParseApache(output, "/usr/sbin/httpd", "system", false));

    [Fact]
    public void LaCarpetaDeLaragon_DaLaArquitectura_SiElBannerNoLaDice()
    {
        var c = ComponentParser.ParseApache("Server version: Apache/2.4.62 (Win)\n", @"C:\laragon\bin\apache\httpd-2.4.62-240904-win64-VS17\bin\httpd.exe",
            "Laragon", false, "httpd-2.4.62-240904-win64-VS17");
        Assert.Equal("x64", c!.Architecture);
    }

    // ---- Dónde se busca y en qué orden ----------------------------------------------------------

    private ScanRoots Windows(params string[] services) => new()
    {
        Drives = [Path.Combine(_dir, "C")],
        ProgramFiles = [Path.Combine(_dir, "C", "Program Files")],
        UserProfile = Path.Combine(_dir, "C", "Users", "ana"),
        AppData = Path.Combine(_dir, "C", "Users", "ana", "AppData", "Roaming"),
        RoostRoot = Path.Combine(_dir, "C", "roost"),
        PathDirectories = [Path.Combine(_dir, "C", "laragon", "bin", "nginx", "nginx-1.27.4"), Path.Combine(_dir, "C", "bin")],
        ServiceExecutables = () => services,
    };

    [Fact]
    public void Nginx_EnWindows_LasRaicesConocidasVanAntesQueLosServiciosYElPath()
    {
        var roost = Touch("C", "roost", "bin", "nginx", "1.30.6", "nginx.exe");
        var tmp = Touch("C", "roost", "bin", "nginx", ".tmp-123", "nginx.exe");          // a medio extraer: no
        var laragon = Touch("C", "laragon", "bin", "nginx", "nginx-1.27.4", "nginx.exe");
        var choco = Touch("C", "tools", "nginx-1.28.0", "nginx.exe");
        var manual = Touch("C", "nginx-1.26.3", "nginx.exe");
        var scoop = Touch("C", "Users", "ana", "scoop", "apps", "nginx", "current", "nginx.exe");
        var service = Touch("C", "srv", "web", "nginx.exe");

        var candidates = NginxScanner.WindowsCandidates(Windows(service, Path.Combine(_dir, "C", "srv", "mysqld.exe"))).ToList();

        Assert.DoesNotContain(candidates, c => c.Path == tmp);
        Assert.Equal(
            [(roost, "Roost"), (laragon, "Laragon"), (choco, "chocolatey"), (manual, "manual"), (scoop, "scoop"), (service, NginxScanner.Services)],
            candidates.Where(c => c.Source != "PATH").Select(c => (c.Path, c.Source)));
        // El PATH al final: el de Laragon otra vez (lo quita el escaneo) y una carpeta sin nginx.
        Assert.Equal([laragon, Path.Combine(_dir, "C", "bin", "nginx.exe")], candidates.Where(c => c.Source == "PATH").Select(c => c.Path));
    }

    [Fact]
    public void Apache_EnWindows_XamppComoServicioSigueSiendoXampp()
    {
        var roost = Touch("C", "roost", "bin", "apache", "2.4.69", "bin", "httpd.exe");
        var laragon = Touch("C", "laragon", "bin", "apache", "httpd-2.4.62-240904-win64-VS17", "bin", "httpd.exe");
        var xampp = Touch("C", "xampp", "apache", "bin", "httpd.exe");
        var wamp = Touch("C", "wamp64", "bin", "apache", "apache2.4.59", "bin", "httpd.exe");
        var choco = Touch("C", "tools", "Apache24", "bin", "httpd.exe");
        var lounge = Touch("C", "Apache24", "bin", "httpd.exe");
        var chocoDefault = Touch("C", "Users", "ana", "AppData", "Roaming", "Apache24", "bin", "httpd.exe");
        var asf = Touch("C", "Program Files", "Apache Software Foundation", "Apache2.4", "bin", "httpd.exe");
        var scoop = Touch("C", "Users", "ana", "scoop", "apps", "apache", "current", "bin", "httpd.exe");
        var service = Touch("D", "web", "Apache2.4", "bin", "httpd.exe");

        var candidates = ApacheScanner.WindowsCandidates(Windows(xampp, service)).ToList();

        Assert.Equal(
            [(roost, "Roost"), (laragon, "Laragon"), (xampp, "XAMPP"), (wamp, "WAMP"), (choco, "chocolatey"), (lounge, "manual"),
             (chocoDefault, "chocolatey"), (asf, "Program Files"), (scoop, "scoop"), (xampp, NginxScanner.Services), (service, NginxScanner.Services)],
            candidates.Where(c => c.Source != "PATH").Select(c => (c.Path, c.Source)));
    }

    private sealed class FakeRunner(Func<string, string?> output) : IProcessRunner
    {
        public List<(string Exe, string Args)> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add((executablePath, arguments));
            return output(executablePath);
        }
    }

    [Fact]
    public void ElEscaneo_LeeSoloConMenosV_YCuentaUnaVezLoQueSeVePorDosSitios()
    {
        if (!OperatingSystem.IsWindows()) return;   // las rutas de Windows; las de POSIX, abajo
        var laragon = Touch("C", "laragon", "bin", "nginx", "nginx-1.27.4", "nginx.exe");
        var xampp = Touch("C", "xampp", "apache", "bin", "httpd.exe");
        var runner = new FakeRunner(exe => exe.EndsWith("nginx.exe", StringComparison.OrdinalIgnoreCase) ? "nginx version: nginx/1.27.4\r\n" : ApacheXampp);

        var nginx = Assert.Single(new NginxScanner(runner, Windows()).Scan());   // Laragon y su carpeta en el PATH
        var apache = Assert.Single(new ApacheScanner(runner, Windows(xampp)).Scan());   // XAMPP y su servicio

        Assert.Equal(("Laragon", "1.27.4", laragon), (nginx.Source, nginx.Version.ToString(), nginx.ExecutablePath));
        Assert.Equal(("XAMPP", "2.4.58", xampp), (apache.Source, apache.Version.ToString(), apache.ExecutablePath));
        Assert.All(runner.Calls, c => Assert.Equal("-v", c.Args));   // nada de -t, -k ni -s: solo leer la versión
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void EnLinux_LaDistroEsAmbarYLoDeUsrLocalOHomebrewNo()
    {
        var roots = new ScanRoots { PosixRoot = Path.Combine(_dir, "raiz"), PathDirectories = [] };
        Touch("raiz", "usr", "sbin", "nginx");
        Touch("raiz", "usr", "local", "nginx", "sbin", "nginx");
        Touch("raiz", "opt", "homebrew", "bin", "nginx");
        Touch("raiz", "usr", "sbin", "apache2");
        Touch("raiz", "usr", "local", "apache2", "bin", "httpd");   // la imagen Docker httpd:*
        Touch("raiz", "opt", "homebrew", "bin", "httpd");

        // /opt/*/bin/httpd vuelve a ver el de Homebrew: el escaneo lo quita por la ruta real, y gana el primero.
        Assert.Equal(["system", "Homebrew", "/usr/local"], NginxScanner.PosixCandidates(roots).DistinctBy(c => c.Path).Select(c => c.Source));
        Assert.Equal(["system", "Homebrew", "/usr/local"], ApacheScanner.PosixCandidates(roots).DistinctBy(c => c.Path).Select(c => c.Source));

        // El Apache de la distro sale en ámbar (puede llevar parches por backport); el de la imagen Docker, con certeza.
        var evaluator = new AuditEvaluator(AuditDataset.LoadEmbedded(), new DateOnly(2026, 10, 24));
        var distro = ComponentParser.ParseApache("Server version: Apache/2.4.58 (Ubuntu)\n", "/usr/sbin/apache2", "system", false)!;
        var docker = ComponentParser.ParseApache("Server version: Apache/2.4.58 (Unix)\n", "/usr/local/apache2/bin/httpd", "/usr/local", false)!;
        Assert.True(evaluator.Evaluate(distro).Uncertain);
        Assert.False(evaluator.Evaluate(docker).Uncertain);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
