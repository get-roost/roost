using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Apache como servidor web, sin red ni binarios reales: el httpd.conf y los .conf de los sitios
/// (puros), <c>[web] server</c>, el instalador con un zip hecho en memoria y un <c>Prepare</c>
/// entero con un <c>httpd -t</c> falso.
/// </summary>
public class ApacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-apache-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static ApacheConfInput Input(int? https = null) => new(
        @"C:\roost\bin\apache\2.4.69", @"C:\roost\run", @"C:\roost\logs", @"C:\roost\www", 8765, https,
        [new NginxUpstream("php-8.4", [9840, 9841]), new NginxUpstream("php-7.4", [9740])], "php-8.4", @"C:\roost\etc\apache\sites");

    [Fact]
    public void HttpdConf_PhpPorLosPools_HtaccessYSoloEn127()
    {
        var conf = ApacheConf.Render(Input());

        Assert.Contains("ServerRoot \"C:/roost/bin/apache/2.4.69\"", conf);
        Assert.Contains("Listen 127.0.0.1:8765", conf);
        Assert.DoesNotContain("Listen 127.0.0.1:8443", conf);         // sin sitios, no abre el HTTPS
        Assert.DoesNotContain("Listen 80", conf);
        Assert.Contains("LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so", conf);
        Assert.Contains("LoadModule rewrite_module modules/mod_rewrite.so", conf);
        Assert.Contains("<Proxy \"balancer://php-8.4\">", conf);
        Assert.Contains("BalancerMember \"fcgi://127.0.0.1:9841\"", conf);
        Assert.Contains("BalancerMember \"fcgi://127.0.0.1:9740\"", conf);
        Assert.Contains("SetHandler \"proxy:balancer://php-8.4/\"", conf);   // localhost, con el global
        Assert.Contains("ProxyFCGISetEnvIf \"true\" SCRIPT_FILENAME \"%{DOCUMENT_ROOT}%{reqenv:SCRIPT_NAME}\"", conf);
        Assert.Contains("AllowOverride All", conf);
        Assert.Contains("DocumentRoot \"C:/roost/www\"", conf);
        Assert.Contains("IncludeOptional \"C:/roost/etc/apache/sites/*.conf\"", conf);
        Assert.Contains("Listen 127.0.0.1:8443", ApacheConf.Render(Input(https: 8443)));
    }

    private static ApacheSiteConf.Input SiteInput() => new(
        new Site { Name = "tienda", Folder = @"C:\www\tienda", Root = "public" }, "php-7.4", 8765, 8443,
        @"C:\roost\certs\sites\tienda.crt", @"C:\roost\certs\sites\tienda.key");

    [Fact]
    public void ConfDeUnSitio_HttpYHttps_ConSuPool_YLoDeFueraSeRespeta()
    {
        var fresh = ApacheSiteConf.RenderNew(SiteInput());

        Assert.Contains("<VirtualHost 127.0.0.1:8765>", fresh);
        Assert.Contains("<VirtualHost 127.0.0.1:8443>", fresh);
        Assert.Contains("ServerAlias *.tienda.test", fresh);
        Assert.Contains("DocumentRoot \"C:/www/tienda/public\"", fresh);
        Assert.Contains("SetHandler \"proxy:balancer://php-7.4/\"", fresh);
        Assert.Contains("SSLCertificateFile \"C:/roost/certs/sites/tienda.crt\"", fresh);

        var edited = "# lo mio arriba\n" + fresh + "<Directory \"C:/www/tienda/public/uploads\">\n    Require all denied\n</Directory>\n";
        var merged = ApacheSiteConf.Merge(edited, SiteInput() with { Pool = "php-8.4" }, "tienda.conf");
        Assert.StartsWith("# lo mio arriba\n", merged);
        Assert.Contains("uploads", merged);
        Assert.Contains("proxy:balancer://php-8.4", merged);
        Assert.DoesNotContain("proxy:balancer://php-7.4", merged);

        Assert.Throws<RoostException>(() => ApacheSiteConf.Merge("<VirtualHost *:80>\n</VirtualHost>\n", SiteInput(), "tienda.conf"));
    }

    [Fact]
    public void WebServer_SeLeeYSeGuarda_YUnoQueNoExisteFalla()
    {
        var file = Path.Combine(_root, "roost.toml");
        Assert.False(RoostConfig.Load(file).UsesApache);   // por defecto, nginx

        RoostConfig.SetWebServer(file, RoostConfig.WebApache);
        Assert.True(RoostConfig.Load(file).UsesApache);
        RoostConfig.SetWebServer(file, RoostConfig.WebNginx);
        Assert.False(RoostConfig.Load(file).UsesApache);

        Assert.Throws<RoostException>(() => RoostConfig.SetWebServer(file, "caddy"));
        File.WriteAllText(file, "[web]\nserver = \"iis\"\n");
        Assert.Throws<RoostException>(() => RoostConfig.Load(file));
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write("x");
            }
        }
        return ms.ToArray();
    }

    private ApacheBuild Build(byte[] zip) => new("2.4.69", new Uri("https://example.test/httpd-2.4.69-Win64-VS18.zip"),
        Convert.ToHexStringLower(SHA256.HashData(zip)), "VC++");

    [Fact]
    public async Task Instalar_SoloLaCarpetaApache24_YFindLoEncuentra()
    {
        // Como el de Apache Lounge: Apache24/ más un ReadMe y un fichero suelto al lado.
        var zip = Zip("Apache24/bin/httpd.exe", "Apache24/conf/mime.types", "Apache24/modules/mod_proxy_fcgi.so", "ReadMe.txt", "-- Win64 VS18  --");
        var installer = new ApacheInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var installed = await installer.InstallAsync(Build(zip));

        Assert.Equal(Path.Combine(Paths.ApacheBin, "2.4.69", "bin", "httpd.exe"), installed.Executable);
        Assert.True(File.Exists(installed.MimeTypes));
        Assert.False(File.Exists(Path.Combine(Paths.ApacheBin, "2.4.69", "ReadMe.txt")));
        Assert.Equal(installed, installer.Find(Build(zip)));
        Assert.Empty(Directory.GetDirectories(Paths.ApacheBin, ".*"));   // sin temporales
    }

    [Fact]
    public async Task UnZipSinApache24_NoSeInstala()
    {
        var zip = Zip("httpd-2.4.69/bin/httpd.exe");
        var installer = new ApacheInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(Build(zip)));
        Assert.Contains("Apache24", ex.Message);
        Assert.Null(installer.Find(Build(zip)));
    }

    /// <summary>httpd -t falso: siempre da por buena la configuración.</summary>
    private sealed class ApacheTestOk : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{Path.GetFileName(executablePath)} {arguments}");
            return "Syntax OK";
        }
    }

    private InstalledPhp FakePhp(string name)
    {
        var dir = Path.Combine(Paths.PhpBin, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "php.exe"), "");
        File.WriteAllText(Path.Combine(dir, "php-cgi.exe"), "");
        return new InstalledPhp(name, dir, Path.Combine(dir, "php.exe"));
    }

    [Fact]
    public void Prepare_ConApache_LosMismosPools_UnConfPorSitio_YApacheElUltimo()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var php74 = FakePhp("7.4.33-nts-x64");
        var apache = new InstalledApache("2.4.69", Path.Combine(Paths.ApacheBin, "2.4.69"));
        Site[] sites =
        [
            new() { Name = "blog", Folder = Path.Combine(_root, "www", "blog") },
            new() { Name = "legacy", Folder = Path.Combine(_root, "www", "legacy"), Php = "7.4" },
        ];
        var config = new RoostConfig { HttpPort = 8765, HttpsPort = 8443, PhpPool = 2, WebServer = RoostConfig.WebApache };
        var runner = new ApacheTestOk();

        var plan = new StackPlanner(Paths, runner).Prepare(config, php84, nginx: null, null, sites, [php84, php74], new LocalCa(Paths), apache: apache);

        Assert.Equal(["php-8.4-0", "php-8.4-1", "php-7.4-0", "php-7.4-1", "apache"], plan.Services.Select(s => s.Name));
        Assert.Equal("apache", plan.WebServer);
        Assert.Equal("2.4.69", plan.WebVersion);
        Assert.Contains(8443, plan.Ports);
        Assert.StartsWith("httpd.exe -t -f ", Assert.Single(runner.Calls));
        var httpd = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "httpd.conf"));
        Assert.Contains("balancer://php-7.4", httpd);
        Assert.Contains("Listen 127.0.0.1:8443", httpd);
        Assert.Contains("proxy:balancer://php-7.4", File.ReadAllText(Path.Combine(Paths.ApacheSites, "legacy.conf")));
        Assert.False(Directory.Exists(Paths.NginxSites));   // de nginx no se genera nada
    }

    [Fact]
    public void Prepare_ConApacheSinInstalar_LoDiceSinArrancarNada()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var config = new RoostConfig { WebServer = RoostConfig.WebApache };

        var ex = Assert.Throws<RoostException>(() => new StackPlanner(Paths, new ApacheTestOk()).Prepare(config, php84, nginx: null));
        Assert.Contains("Apache", ex.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
