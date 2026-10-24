using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// PHP en Linux (L1): el catálogo de roost-builds fijado en Roost, el tar.gz con su layout
/// (<c>bin/php</c>, <c>bin/php-fpm</c>, <c>ext/xdebug.so</c>, <c>BUILD.json</c>), el php-fpm.conf, el
/// Xdebug que viene dentro, las extensiones integradas y el <c>sendmail_path</c> de Mailpit. Sin red
/// ni binarios: el tar.gz se construye en memoria y los ejecutables son ficheros vacíos. Lo puro
/// corre en los dos sistemas; lo que mira el layout de Linux, solo allí.
/// </summary>
public class LinuxPhpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-linux-php-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    private const string BuildJson = """
        {
          "php": "8.4.27", "variant": "nts", "os": "linux", "arch": "x86_64",
          "extensions": ["curl", "mbstring", "opcache", "openssl", "pdo_mysql", "PDO", "Zend OPcache"],
          "shared": { "xdebug": "3.5.3" },
          "static_php_cli": { "version": "3.0.0-alpha1", "commit": "d7f1a88" },
          "target": "x86_64-linux-gnu.2.17", "glibc": "2.17",
          "source": { "file": "php-8.4.27.tar.xz", "sha256": "dcf93923b0e38e0d5252b25b796520b5a47761a08b3488b6d0b5d1115764d3f0" }
        }
        """;

    /// <summary>Una instalación con el layout de roost-builds (ejecutables vacíos).</summary>
    private InstalledPhp LinuxPhp(string name = "8.4.27-nts-x64", bool xdebug = true, bool buildJson = true, bool fpm = true)
    {
        var dir = Path.Combine(Paths.PhpBin, name);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "ext"));
        File.WriteAllText(Path.Combine(dir, "bin", "php"), "");
        if (fpm) File.WriteAllText(Path.Combine(dir, "bin", "php-fpm"), "");
        if (xdebug) File.WriteAllText(Path.Combine(dir, "ext", "xdebug.so"), "");
        if (buildJson) File.WriteAllText(Path.Combine(dir, PhpBuildInfo.FileName), BuildJson);
        return new InstalledPhp(name, dir, Path.Combine(dir, "bin", "php"));
    }

    // ---- BUILD.json --------------------------------------------------------------------------------

    [Fact]
    public void BuildInfo_LeeLasIntegradasEnMinusculas_XdebugYLaFuente()
    {
        var info = PhpBuildInfo.Parse(BuildJson)!;

        Assert.Equal("8.4.27", info.Version);
        Assert.Contains("curl", info.Extensions);
        Assert.Contains("pdo", info.Extensions);              // «PDO» en minúsculas
        Assert.Contains("zend opcache", info.Extensions);     // tal cual, en minúsculas: lo que importa es opcache
        Assert.Contains("opcache", info.Extensions);
        Assert.Equal("3.5.3", info.Xdebug);
        Assert.Equal("3.0.0-alpha1", info.StaticPhp);
        Assert.Equal("2.17", info.Glibc);
        Assert.StartsWith("dcf93923", info.SourceSha256);
        Assert.Null(PhpBuildInfo.Parse("no es json"));
        Assert.Null(PhpBuildInfo.Parse("[1,2]"));
        Assert.Null(PhpBuildInfo.Read(Path.Combine(_root, "no-existe")));
    }

    // ---- El catálogo de roost-builds ----------------------------------------------------------------

    [Fact]
    public void Catalogo_UnaPorRama_DeLa81ALa85_ConSuSha256YSuTamano_YLaUrlDeLaRelease()
    {
        var builds = LinuxPhpCatalog.BuildsFor(LinuxPhpCatalog.DefaultBaseUrl);

        Assert.Equal(["8.5", "8.4", "8.3", "8.2", "8.1"], builds.Select(b => b.Branch));
        Assert.All(builds, b =>
        {
            Assert.Equal(PhpOs.Linux, b.Os);
            Assert.False(b.ThreadSafe);
            Assert.Equal("x64", b.Architecture);
            Assert.Matches("^[0-9a-f]{64}$", b.Sha256);
            Assert.True(b.Size > 10_000_000, $"{b.FileName}: {b.Size} bytes");
            Assert.Equal($"php-{b.Version}-nts-linux-x86_64.tar.gz", b.FileName);
            Assert.Equal($"https://github.com/get-roost/roost-builds/releases/download/php-{b.Version}/{b.FileName}", b.Url.ToString());
            Assert.Equal($"{b.Version}-nts-x64", b.InstallName);
            Assert.Equal(Path.Combine("bin", "php"), b.ExecutableRelativePath);
        });
        Assert.Equal(["7.4", "8.0"], LinuxPhpCatalog.WindowsOnlyBranches);
    }

    [Fact]
    public void Catalogo_LaBaseDeLasUrls_SeCambiaConRoostBuildsUrl_ParaEspejosYPruebas()
    {
        Assert.Equal(LinuxPhpCatalog.DefaultBaseUrl, LinuxPhpCatalog.BaseUrlFrom(null));
        Assert.Equal(LinuxPhpCatalog.DefaultBaseUrl, LinuxPhpCatalog.BaseUrlFrom("  "));
        var local = LinuxPhpCatalog.BaseUrlFrom("http://127.0.0.1:8000");
        Assert.Equal("http://127.0.0.1:8000/php-8.4.27/php-8.4.27-nts-linux-x86_64.tar.gz", LinuxPhpCatalog.UrlFor(local, "8.4.27").ToString());
        Assert.Equal("http://127.0.0.1:8000/", LinuxPhpCatalog.BuildsFor(local)[0].Url.GetLeftPart(UriPartial.Authority) + "/");
        Assert.Throws<RoostException>(() => LinuxPhpCatalog.BaseUrlFrom("ftp://x"));
        Assert.Throws<RoostException>(() => LinuxPhpCatalog.BaseUrlFrom("no es una url"));
    }

    [Fact]
    public void Catalogo_Resolve_RamaOVersionExacta_YDiceClaroLoQueNoHayEnLinux()
    {
        var builds = LinuxPhpCatalog.BuildsFor(LinuxPhpCatalog.DefaultBaseUrl);
        var latest84 = builds.Single(b => b.Branch == "8.4");

        Assert.Same(latest84, LinuxPhpCatalog.Resolve(builds, "8.4"));
        Assert.Same(latest84, LinuxPhpCatalog.Resolve(builds, latest84.Version.ToString()));
        var other = Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "8.4.1")).Message;
        Assert.Contains("rama 8.4", other);
        Assert.Contains($"solo la {latest84.Version}", other);
        Assert.Contains("solo esta en Windows", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "7.4")).Message);
        Assert.Contains("solo esta en Windows", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "8.0.30")).Message);
        Assert.Contains("--ts", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "8.4", threadSafe: true)).Message);
        Assert.Contains("--arch", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "8.4", architecture: "x86")).Message);
        Assert.Contains("no es una version", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "ocho")).Message);
        Assert.Contains("Ramas disponibles", Assert.Throws<RoostException>(() => LinuxPhpCatalog.Resolve(builds, "9.9")).Message);
        Assert.NotNull(LinuxPhpCatalog.WhyWindowsOnly("7.4"));
        Assert.Null(LinuxPhpCatalog.WhyWindowsOnly("8.4"));
    }

    // ---- El instalador con el tar.gz -----------------------------------------------------------------

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    /// <summary>Un tar.gz como el de roost-builds, en memoria: ejecutables con permiso de ejecución.</summary>
    private static byte[] TarGz(params (string Path, string Content, bool Executable)[] entries)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, content, executable) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
                    Mode = executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                tar.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    private static PhpBuild LinuxBuild(byte[] tarGz, string? sha = null) =>
        LinuxPhpCatalog.BuildsFor(new Uri("https://example.test/builds/")).Single(b => b.Branch == "8.4") with
        {
            Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(tarGz)),
        };

    [LinuxFact]
    public async Task Install_ExtraeElTarGzConSuLayout_YElEjecutableEsBinPhp()
    {
        var tarGz = TarGz(("bin/php", "#!php", true), ("bin/php-fpm", "#!fpm", true), ("ext/xdebug.so", "so", false), ("BUILD.json", BuildJson, false), ("licenses/PHP-LICENSE", "PHP License", false));
        var handler = new FakeHandler(tarGz);
        var installer = new PhpInstaller(Paths, new HttpClient(handler));
        var build = LinuxBuild(tarGz);

        var installed = await installer.InstallAsync(build);

        Assert.Equal($"{build.Version}-nts-x64", installed.Name);
        Assert.Equal(Path.Combine(installed.Directory, "bin", "php"), installed.Executable);
        Assert.Equal(Path.Combine(installed.Directory, "bin", "php-fpm"), installed.FpmExecutable);
        Assert.True(installed.HasFpm);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "ext", "xdebug.so")));
        Assert.Equal("3.5.3", installed.ReadBuildInfo()?.Xdebug);
        Assert.True(File.GetUnixFileMode(installed.Executable).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.GetUnixFileMode(installed.FpmExecutable).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal([build.Url], handler.Requests);
        Assert.Equal($"https://example.test/builds/php-{build.Version}/php-{build.Version}-nts-linux-x86_64.tar.gz", build.Url.ToString());
        Assert.Single(installer.ListInstalled());
        Assert.Equal(installed.Executable, installer.ListInstalled()[0].Executable);
        Assert.Empty(Directory.GetDirectories(Paths.PhpBin, ".*"));   // sin temporales
    }

    [LinuxFact]
    public async Task Install_UnTarGzSinBinPhp_NoInstala_YConOtroShaTampoco()
    {
        var tarGz = TarGz(("php", "suelto", true), ("BUILD.json", BuildJson, false));
        var installer = new PhpInstaller(Paths, new HttpClient(new FakeHandler(tarGz)));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(LinuxBuild(tarGz)));
        Assert.Empty(installer.ListInstalled());

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(LinuxBuild(tarGz, sha: new string('0', 64))));
        Assert.Empty(Directory.GetFiles(Paths.Downloads));
    }

    [LinuxFact]
    public void ListInstalled_AceptaBinPhp_YTambienUnPhpEnLaRaiz_ComoAntes()
    {
        var nested = LinuxPhp("8.4.27-nts-x64");
        var flat = Path.Combine(Paths.PhpBin, "8.3.36-nts-x64");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, "php"), "");
        File.WriteAllText(Path.Combine(flat, "php-cgi"), "");

        var installed = new PhpInstaller(Paths).ListInstalled();

        Assert.Equal(["8.4.27-nts-x64", "8.3.36-nts-x64"], installed.Select(p => p.Name));
        Assert.Equal(nested.Executable, installed[0].Executable);
        Assert.Equal(Path.Combine(flat, "php"), installed[1].Executable);
        Assert.Equal(Path.Combine(flat, "php-cgi"), installed[1].CgiExecutable);   // los demas binarios, al lado del ejecutable
        Assert.False(installed[1].HasFpm);
    }

    // ---- Xdebug dentro de cada PHP -------------------------------------------------------------------

    [LinuxFact]
    public void Xdebug_EnLinuxVieneDentro_NoSeBajaNada_YLocateDaElSoDeLaInstalacion()
    {
        var php = LinuxPhp();
        var installer = new XdebugInstaller(Paths);

        Assert.Empty(XdebugCatalog.Current);
        Assert.True(XdebugCatalog.IsBundled(php));
        Assert.Null(XdebugCatalog.WhyNot(php));
        Assert.Null(XdebugCatalog.For(php));                                   // nada que bajar
        Assert.Equal(Path.Combine(php.Directory, "ext", "xdebug.so"), installer.Locate(php));
        Assert.Equal("3.5.3", XdebugInstaller.BundledVersion(php));

        var sinXdebug = LinuxPhp("8.3.36-nts-x64", xdebug: false);
        Assert.Contains("no trae Xdebug", XdebugCatalog.WhyNot(sinXdebug));
        Assert.Null(installer.Locate(sinXdebug));
    }

    [LinuxFact]
    public async Task Xdebug_SetXdebugOn_EscribeElSoEnElPhpIni_YDesdeLaCli_NoHayDllQueBajar()
    {
        var php = LinuxPhp();
        var runner = new FakeCommandRunner().On("-v", stdout: """
            PHP 8.4.27 (cli) (built: Oct 24 2026 10:00:00) (NTS)
            Copyright (c) The PHP Group
            Zend Engine v4.4.27, Copyright (c) Zend Technologies
                with Zend OPcache v8.4.27, Copyright (c), by Zend Technologies
                with Xdebug v3.5.3, Copyright (c) 2002-2026, by Derick Rethans
            """);
        var configurator = new PhpConfigurator(Paths, runner);

        var check = await configurator.SetXdebugAsync(php, enabled: true);

        Assert.Equal("3.5.3", check.XdebugVersion);
        var ini = File.ReadAllText(configurator.Planner.PhpIniPath(php));
        Assert.Contains($"zend_extension=\"{Path.Combine(php.Directory, "ext", "xdebug.so")}\"", ini);
        Assert.Contains("openssl.cafile", ini);
        Assert.True(configurator.XdebugOn(php));
        await Assert.ThrowsAsync<RoostException>(() => new XdebugInstaller(Paths, new HttpClient(new FakeHandler([]))).InstallAsync(XdebugCatalog.Windows[0]));
    }

    // ---- Las extensiones integradas -----------------------------------------------------------------

    [LinuxFact]
    public async Task Extensiones_LasIntegradasSalenActivas_YNoSePuedenQuitar()
    {
        var php = LinuxPhp();
        var configurator = new PhpConfigurator(Paths, new FakeCommandRunner());

        var extensions = configurator.Extensions(php);

        var curl = extensions.Single(e => e.Name == "curl");
        Assert.True(curl.On && curl.BuiltIn && curl.ByDefault);
        Assert.True(extensions.Single(e => e.Name == "pdo_mysql").BuiltIn);
        Assert.True(extensions.Single(e => e.Name == "xdebug").On == false && !extensions.Single(e => e.Name == "xdebug").BuiltIn);
        var ex = await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "curl", enabled: false));
        Assert.Contains("integrada", ex.Message);
        Assert.Contains("no se puede quitar", ex.Message);
        Assert.Contains("siempre esta activa", (await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "mbstring", enabled: true))).Message);
        Assert.Contains("ni va integrada ni hay ext/ldap.so", (await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "ldap", enabled: true))).Message);

        // El php.ini: ninguna integrada como extension=, y OPcache sin zend_extension.
        var ini = File.ReadAllText(configurator.Planner.WritePhpIni(php));
        Assert.DoesNotContain("\nextension=", ini);
        Assert.DoesNotContain("zend_extension=opcache", ini);
        Assert.Contains("opcache.enable = 1", ini);
    }

    [LinuxFact]
    public void Extensiones_SinBuildJson_LasIntegradasSalenDePhpM()
    {
        var php = LinuxPhp(buildJson: false);
        var planner = new StackPlanner(Paths, new CannedRunner("[PHP Modules]\nCore\ncurl\nmbstring\nPDO\nZend OPcache\n\n[Zend Modules]\nZend OPcache\n"));

        var builtIn = planner.BuiltInExtensions(php);

        Assert.Contains("curl", builtIn);
        Assert.Contains("pdo", builtIn);
        Assert.Contains("opcache", builtIn);
        Assert.DoesNotContain("[php modules]", builtIn);
    }

    // ---- php-fpm: la conf y el plan -------------------------------------------------------------------

    [Theory]
    [InlineData(1, 4, 1, 1, 2)]
    [InlineData(4, 16, 4, 2, 8)]
    [InlineData(10, 40, 10, 5, 20)]
    [InlineData(0, 4, 1, 1, 2)]
    public void PhpFpmConf_Sizing_CumpleLoQueExigeFpm(int pool, int maxChildren, int start, int minSpare, int maxSpare)
    {
        var s = PhpFpmConf.Sizing(pool);
        Assert.Equal((maxChildren, start, minSpare, maxSpare), s);
        Assert.True(s.MinSpare <= s.StartServers && s.StartServers <= s.MaxSpare && s.MaxSpare <= s.MaxChildren);
    }

    [Fact]
    public void PhpFpmConf_Render_UnPoolEnTcp_SinUserNiGroup_ConClearEnvNoYMaxRequests()
    {
        var conf = PhpFpmConf.Render("8.5.12-nts-x64", "/casa/run/php-8.5.pid", "/casa/logs/php-8.5-fpm.log", 9850, 4, 500);

        Assert.Contains("[global]\npid = \"/casa/run/php-8.5.pid\"\nerror_log = \"/casa/logs/php-8.5-fpm.log\"\ndaemonize = no\n", conf);
        Assert.Contains("[www]\nlisten = 127.0.0.1:9850\nlisten.allowed_clients = 127.0.0.1\npm = dynamic\n", conf);
        Assert.Contains("pm.max_children = 16\npm.start_servers = 4\npm.min_spare_servers = 2\npm.max_spare_servers = 8\npm.max_requests = 500\n", conf);
        Assert.Contains("catch_workers_output = yes\n", conf);
        Assert.Contains("clear_env = no\n", conf);
        Assert.DoesNotContain("\nuser", conf);
        Assert.DoesNotContain("\ngroup", conf);
        Assert.Equal("-F -y \"/casa/etc/php/8.5.12-nts-x64/php-fpm.conf\" -c \"/casa/etc/php/8.5.12-nts-x64/php.ini\"", PhpFpmConf.Arguments("/casa/etc/php/8.5.12-nts-x64/php-fpm.conf", "/casa/etc/php/8.5.12-nts-x64/php.ini"));
    }

    [LinuxFact]
    public void Plan_EnLinuxConPhpFpm_UnServicioPorRama_EnElPrimerPuertoDelPool_QueSeParaConQuit()
    {
        var php = LinuxPhp("8.5.12-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var planner = new StackPlanner(Paths, new CannedRunner("nginx: configuration file test is successful"));

        var plan = planner.Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 4 }, php, nginx);

        var service = Assert.Single(plan.Services, s => s.Name.StartsWith("php-", StringComparison.Ordinal));
        Assert.Equal("php-8.5-0", service.Name);
        Assert.Equal(php.FpmExecutable, service.Executable);
        var conf = planner.PhpFpmConfPath(php);
        Assert.Equal(PhpFpmConf.Arguments(conf, planner.PhpIniPath(php)), service.Arguments);
        Assert.Equal(9850, service.Port);
        Assert.Equal(StopSignal.Quit, service.StopSignal);
        Assert.Equal(planner.PhpScanDir(php), service.Environment["PHP_INI_SCAN_DIR"]);
        Assert.DoesNotContain("PHP_FCGI_MAX_REQUESTS", service.Environment.Keys);
        var text = File.ReadAllText(conf);
        Assert.Contains("listen = 127.0.0.1:9850\n", text);
        Assert.Contains("pm.start_servers = 4\n", text);
        Assert.Contains($"pid = \"{Path.Combine(Paths.Run, "php-8.5.pid")}\"", text);
        Assert.Contains("server 127.0.0.1:9850", File.ReadAllText(planner.NginxConfPath));
        Assert.DoesNotContain("server 127.0.0.1:9851", File.ReadAllText(planner.NginxConfPath));
    }

    [LinuxFact]
    public void Plan_EnLinuxSinPhpFpm_SirveConPhpCgiComoEnWindows_YSinNingunoLoDiceClaro()
    {
        var php = LinuxPhp("8.5.12-nts-x64", fpm: false);
        File.WriteAllText(Path.Combine(php.Directory, "bin", "php-cgi"), "");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var planner = new StackPlanner(Paths, new CannedRunner("nginx: configuration file test is successful"));

        var plan = planner.Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 2 }, php, nginx);

        Assert.Equal(["php-8.5-0", "php-8.5-1"], plan.Services.Where(s => s.Name.StartsWith("php-", StringComparison.Ordinal)).Select(s => s.Name));
        Assert.All(plan.Services.Where(s => s.Name.StartsWith("php-", StringComparison.Ordinal)), s => Assert.Equal(php.CgiExecutable, s.Executable));

        File.Delete(Path.Combine(php.Directory, "bin", "php-cgi"));
        var ex = Assert.Throws<RoostException>(() => planner.Prepare(new RoostConfig { HttpPort = 8766 }, php, nginx));
        Assert.Contains("bin/php-fpm", ex.Message);
    }

    // ---- Mailpit: sendmail_path ---------------------------------------------------------------------

    [Fact]
    public void MailConf_EnLinuxElMailDePhpVaPorSendmailPath_ConMailpitHaciendoDeSendmail()
    {
        var sendmail = MailConf.SendmailPath("/casa/bin/mailpit/1.32.0/mailpit", 1026);
        Assert.Equal("/casa/bin/mailpit/1.32.0/mailpit sendmail --smtp-addr 127.0.0.1:1026", sendmail);

        var ini = MailConf.PhpIni(1026, 8026, sendmail);
        Assert.Contains("smtp_port = 1026\n", ini);
        Assert.Contains($"sendmail_path = \"{sendmail}\"\n", ini);
        Assert.DoesNotContain("sendmail_path", MailConf.PhpIni(1026, 8026));   // Windows: mail() no lo mira
    }

    [LinuxFact]
    public void MailConf_SyncPhpIni_ConMailpitPoneSendmailPath_YSinElNoPoneNada()
    {
        var confD = Path.Combine(_root, "conf.d");
        var config = new RoostConfig { MailEnabled = true, MailSmtpPort = 1026, MailWebPort = 8026 };

        Assert.True(MailConf.SyncPhpIni(confD, config, "/casa/bin/mailpit/1.32.0/mailpit"));
        Assert.Contains("sendmail_path = \"/casa/bin/mailpit/1.32.0/mailpit sendmail --smtp-addr 127.0.0.1:1026\"", File.ReadAllText(Path.Combine(confD, MailConf.PhpIniName)));
        Assert.True(MailConf.SyncPhpIni(confD, config));                      // sin Mailpit instalado: solo el SMTP
        Assert.DoesNotContain("sendmail_path", File.ReadAllText(Path.Combine(confD, MailConf.PhpIniName)));
        Assert.True(MailConf.SyncPhpIni(Paths, confD, config));                // busca el Mailpit de Roost: no hay ninguno
        Assert.False(MailConf.SyncPhpIni(confD, config with { MailEnabled = false }) && File.Exists(Path.Combine(confD, MailConf.PhpIniName)));
    }

    /// <summary>Un <see cref="IProcessRunner"/> que contesta siempre lo mismo (el <c>nginx -t</c> o el <c>php -m</c>).</summary>
    private sealed class CannedRunner(string output) : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
