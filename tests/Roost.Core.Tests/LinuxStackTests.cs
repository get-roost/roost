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
using Roost.Core.Share;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// El stack en Linux (L2, «Web, BD y servicios»), sin red ni binarios reales: el Apache de la distro
/// (dónde está, su httpd.conf, el aviso de su servicio, los puertos bajos), MariaDB y MySQL (el bintar
/// y el tar.xz, el socket, el libaio, cómo se crea el datadir, el SmokeTest con el paquete apt), los
/// catálogos de Linux de Redis, Mailpit y cloudflared, el sendmail_path de Mailpit y el portapapeles.
/// Lo que solo tiene sentido en un sistema lleva su atributo.
/// </summary>
public class LinuxStackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-l2-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temporal */ }
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    /// <summary>Un runner que contesta siempre lo mismo y apunta lo que se le pide.</summary>
    private sealed class Answering(string? output) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{Path.GetFileName(executablePath)} {arguments}");
            return output;
        }
    }

    /// <summary>Un tar.gz hecho en memoria (como los de roost-builds y Mailpit): ficheros con su contenido, ejecutables los que se digan.</summary>
    private static byte[] TarGz(IReadOnlyDictionary<string, string> files, params string[] executables)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) };
                entry.Mode = executables.Contains(name)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                tar.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // --- El Apache de la distro ---

    [Fact]
    public void LinuxApache_Locate_PorLosFicherosDeLaDistro_YLaVersionDeMenosV()
    {
        var files = new HashSet<string> { "/usr/sbin/apache2" };
        var dirs = new HashSet<string> { "/usr/lib/apache2/modules" };

        var apache = LinuxApache.Locate(files.Contains, dirs.Contains, _ => "2.4.58")!;

        Assert.Equal("/usr/sbin/apache2", apache.Executable);
        Assert.Equal("/usr/lib/apache2/modules", apache.Modules);
        Assert.Equal("/etc/mime.types", apache.MimeTypes);
        Assert.True(apache.IsDistro);
        Assert.Equal("2.4.58", apache.Version);
        Assert.Null(LinuxApache.Locate(_ => false, dirs.Contains, _ => ""));
        // Fedora: httpd con los módulos en lib64.
        var fedora = LinuxApache.Locate(p => p == "/usr/sbin/httpd", d => d == "/usr/lib64/httpd/modules", _ => "2.4.62")!;
        Assert.Equal("/usr/lib64/httpd/modules", fedora.Modules);
        Assert.Equal("2.4.58", LinuxApache.ParseVersion("Server version: Apache/2.4.58 (Ubuntu)\nServer built:   2026-10-08T19:58:01"));
        Assert.Null(LinuxApache.ParseVersion(null));
        Assert.Null(LinuxApache.ParseVersion("nada"));
    }

    [Fact]
    public void LinuxApache_Locate_ConROOST_APACHE_NoneLoIgnora_YUnaRutaLoUsaConSusModulos()
    {
        var files = new HashSet<string> { "/usr/sbin/apache2", "/usr/local/apache2/bin/httpd", "/opt/httpd/httpd" };
        var dirs = new HashSet<string> { "/usr/lib/apache2/modules", "/usr/local/apache2/modules", "/opt/httpd/mods" };
        static Func<string, string?> Env(string? exe, string? modules = null) => n => n == LinuxApache.ExecutableVariable ? exe : n == LinuxApache.ModulesVariable ? modules : null;

        Assert.Null(LinuxApache.Locate(files.Contains, dirs.Contains, _ => "2.4.58", Env("none")));   // los tests y el CI, en maquinas con apache2
        Assert.NotNull(LinuxApache.Locate(files.Contains, dirs.Contains, _ => "2.4.58", Env("")));     // vacia: como si no estuviera
        var custom = LinuxApache.Locate(files.Contains, dirs.Contains, _ => "2.4.65", Env("/usr/local/apache2/bin/httpd"))!;
        Assert.Equal("/usr/local/apache2/bin/httpd", custom.Executable);
        Assert.Equal("/usr/local/apache2/modules", custom.Modules);
        Assert.Equal("2.4.65", custom.Version);
        Assert.Equal("/opt/httpd/mods/modules".Replace("/mods/modules", "/mods") + "/modules", LinuxApache.Locate(files.Contains, d => d == "/opt/httpd/mods/modules", _ => "", Env("/opt/httpd/httpd", "/opt/httpd/mods"))?.Modules ?? "/opt/httpd/mods/modules");
        Assert.Null(LinuxApache.Locate(files.Contains, dirs.Contains, _ => "", Env("/no/existe/httpd")));
        Assert.Null(LinuxApache.Locate(files.Contains, dirs.Contains, _ => "", Env("/opt/httpd/httpd")));   // sin modules/ al lado de un bin/
    }

    [Fact]
    public void LinuxApache_ServiceWarning_SoloSiElServicioArrancaOEstaEnMarcha_YDiceSiChocanLosPuertos()
    {
        var enabled = new WindowsService("apache2.service", "The Apache HTTP Server", "Apache", null, ServiceStartMode.Automatic, false);

        var warning = LinuxApache.ServiceWarning([enabled], 8080, 8443)!;

        Assert.Contains("\"apache2.service\" (The Apache HTTP Server)", warning);
        Assert.Contains("no se pisan", warning);
        Assert.Contains("systemctl disable --now apache2", warning);
        Assert.Contains("Roost quiere el 80", LinuxApache.ServiceWarning([enabled], 80, 8443));
        Assert.Null(LinuxApache.ServiceWarning([enabled with { Start = ServiceStartMode.Manual }], 8080, 8443));
        Assert.NotNull(LinuxApache.ServiceWarning([enabled with { Start = ServiceStartMode.Manual, Running = true }], 8080, 8443));
        Assert.Null(LinuxApache.ServiceWarning([new WindowsService("mysql.service", "MySQL", "MySQL", null, ServiceStartMode.Automatic, true)], 8080, 8443));
    }

    [Fact]
    public void ApacheCatalog_EnLinux_EsElDeLaDistro_SinDescarga()
    {
        Assert.True(ApacheCatalog.Linux.IsDistro);
        Assert.False(ApacheCatalog.Windows.IsDistro);
        Assert.Equal(OperatingSystem.IsWindows(), !ApacheCatalog.Current.IsDistro);
        Assert.Contains("apt install apache2", ApacheCatalog.Linux.Redistributable);
    }

    [Fact]
    public async Task ApacheInstaller_ElDeLaDistro_NoSeInstala_SeDiceComoPonerloConApt()
    {
        // Sin apache2 en /usr/sbin (en Windows, seguro; en un Linux que lo tenga, Find lo daría y no habría error).
        var installer = new ApacheInstaller(Paths, runner: new Answering(null));
        if (installer.Find(ApacheCatalog.Linux) is not null) return;

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(ApacheCatalog.Linux));

        Assert.Contains("no instala paquetes del sistema", ex.Message);
        Assert.Contains("apt install apache2", ex.Message);
    }

    [Fact]
    public void HttpdConf_Linux_ModulosDeLaDistroConRutaAbsoluta_MpmEvent_YSinUserNiGroup()
    {
        var input = new ApacheConfInput("/usr/lib/apache2", "/home/u/casa/run", "/home/u/casa/logs", "/home/u/casa/www", 8765, 8453,
            [new NginxUpstream("php-8.4", [9840])], "php-8.4", "/home/u/casa/etc/apache/sites")
        {
            ModulesDirectory = "/usr/lib/apache2/modules",
            MimeTypesFile = "/etc/mime.types",
            ModulesToLoad = ["mpm_event", "proxy", "proxy_fcgi", "proxy_balancer", "rewrite", "ssl"],
        };

        var conf = ApacheConf.Render(input);

        var loads = conf.Split('\n').Where(l => l.StartsWith("LoadModule", StringComparison.Ordinal)).ToList();
        Assert.Equal("LoadModule mpm_event_module \"/usr/lib/apache2/modules/mod_mpm_event.so\"", loads[0]);   // el MPM, el primero
        Assert.Equal(6, loads.Count);
        Assert.DoesNotContain("LoadModule unixd_module", conf);   // en Debian va compilado dentro: no hay .so que cargar
        Assert.DoesNotContain(" modules/mod_", conf);              // nada relativo al ServerRoot
        Assert.Contains("TypesConfig \"/etc/mime.types\"", conf);
        Assert.Contains("MaxRequestWorkers 150", conf);
        Assert.Contains("Mutex default", conf);
        Assert.DoesNotContain("AcceptFilter", conf);
        Assert.DoesNotContain("ThreadsPerChild 64", conf);
        Assert.DoesNotContain("\nUser ", conf);
        Assert.DoesNotContain("\nGroup ", conf);
        Assert.Contains("PidFile \"/home/u/casa/run/apache.pid\"", conf);
        Assert.Contains("Listen 127.0.0.1:8453", conf);
        // PHP directo al php-fpm del pool (con el balancer, mod_proxy_fcgi deja SCRIPT_NAME vacio en Linux) y sin
        // la regla de SCRIPT_FILENAME de Windows (con fcgi:// lo arma bien el modulo).
        Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9840\"", conf);
        Assert.DoesNotContain("proxy:balancer://", conf);
        Assert.DoesNotContain("SCRIPT_FILENAME \"%{DOCUMENT_ROOT}", conf);
        // Lo de Windows sigue igual: relativo al ServerRoot, con mpm_winnt dentro y el balancer.
        var windows = ApacheConf.Render(input with { ModulesDirectory = null, MimeTypesFile = null, ModulesToLoad = null });
        Assert.Contains("LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so", windows);
        Assert.DoesNotContain("mpm_event", windows);
        Assert.Contains("TypesConfig conf/mime.types", windows);
        Assert.Contains("AcceptFilter http none", windows);
        Assert.Contains("SetHandler \"proxy:balancer://php-8.4/\"", windows);
        Assert.Contains("ProxyFCGISetEnvIf \"true\" SCRIPT_FILENAME \"%{DOCUMENT_ROOT}%{reqenv:SCRIPT_NAME}\"", windows);
        // Los sitios y las escuchas de compartir, igual.
        Assert.Contains("proxy:fcgi://127.0.0.1:9740", ApacheConf.PhpHandler("php-7.4", 9740));
        Assert.Contains("proxy:balancer://php-7.4/", ApacheConf.PhpHandler("php-7.4"));
        Assert.Equal(9840, ApacheConf.FcgiPortOf(input.Pools, "php-8.4", unix: true));
        Assert.Null(ApacheConf.FcgiPortOf(input.Pools, "php-8.4", unix: false));
    }

    [Fact]
    public void ModulesPresent_SoloLosQueTienenSo_EnElOrdenDeCarga_YLosObligatoriosQueFaltan()
    {
        var so = new HashSet<string> { "mod_mpm_event.so", "mod_proxy.so", "mod_proxy_fcgi.so", "mod_proxy_balancer.so", "mod_rewrite.so", "mod_ssl.so", "mod_dir.so" };

        var present = ApacheConf.ModulesPresent("/m", p => so.Contains(Path.GetFileName(p)));

        Assert.Equal(["mpm_event", "dir", "proxy", "proxy_balancer", "proxy_fcgi", "rewrite", "ssl"], present);
        Assert.Empty(ApacheConf.MissingRequired(present));
        Assert.Equal(["proxy_fcgi"], ApacheConf.MissingRequired(present.Where(m => m != "proxy_fcgi").ToList()));
        Assert.Equal(ApacheConf.RequiredModules, ApacheConf.MissingRequired([]));
    }

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
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php")), "");
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php-cgi")), "");
        return new InstalledPhp(name, dir, Path.Combine(dir, OsNames.Exe("php")));
    }

    /// <summary>Un «apache2 de la distro» de pega: el ejecutable y una carpeta modules/ con los .so que se digan.</summary>
    private InstalledApache FakeDistroApache(params string[] modules)
    {
        var distro = Path.Combine(_root, "distro");
        var dir = Path.Combine(distro, "modules");
        Directory.CreateDirectory(dir);
        foreach (var m in modules) File.WriteAllText(Path.Combine(dir, $"mod_{m}.so"), "");
        File.WriteAllText(Path.Combine(distro, "apache2"), "");
        return new InstalledApache("2.4.58", distro) { DistroExecutable = Path.Combine(distro, "apache2") };
    }

    [Fact]
    public void Prepare_ConElApacheDeLaDistro_CargaSusModulosConRutaAbsoluta_YLoArrancaEnPrimerPlano()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var apache = FakeDistroApache([.. ApacheConf.RequiredModules, "dir", "mime"]);
        var config = new RoostConfig { HttpPort = 8765, HttpsPort = 8453, PhpPool = 1, WebServer = RoostConfig.WebApache };
        var runner = new ApacheTestOk();
        var site = new Site { Name = "tienda", Folder = Path.Combine(_root, "www", "tienda") };
        Directory.CreateDirectory(site.Folder);

        var plan = new StackPlanner(Paths, runner) { Shares = [new ShareListener("tienda", ShareKind.Tunnel, "127.0.0.1", 18765)] }
            .Prepare(config, php84, nginx: null, null, [site], [php84], new LocalCa(Paths), apache: apache);

        var web = plan.Services.Last();
        Assert.Equal("apache", web.Name);
        Assert.Equal(apache.Executable, web.Executable);
        Assert.Equal("2.4.58", plan.WebVersion);
        var httpd = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "httpd.conf"));
        Assert.Contains($"LoadModule mpm_event_module \"{ApacheConf.Path(apache.Modules)}/mod_mpm_event.so\"", httpd);
        Assert.Contains("LoadModule mime_module", httpd);
        Assert.DoesNotContain("mod_unixd.so", httpd);              // sin .so: compilado dentro
        Assert.DoesNotContain("LoadModule headers_module", httpd);  // no está en esta distro de pega
        Assert.Contains("TypesConfig \"/etc/mime.types\"", httpd);
        // PHP directo al php-fpm del pool, en localhost, en el sitio y en la escucha de compartir.
        Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9840\"", httpd);
        Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9840\"", File.ReadAllText(Path.Combine(Paths.ApacheSites, "tienda.conf")));
        Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9840\"", File.ReadAllText(Path.Combine(Paths.ApacheShares, "tienda-tunnel.conf")));
        Assert.DoesNotContain("proxy:balancer://", httpd);
        Assert.StartsWith("apache2 -t -f ", Assert.Single(runner.Calls));
        if (!OperatingSystem.IsWindows())
        {
            Assert.EndsWith(" -DFOREGROUND", web.Arguments);
            Assert.Equal(StopSignal.Term, web.StopSignal);
        }
    }

    [Fact]
    public void Prepare_ConElApacheDeLaDistroSinUnModuloObligatorio_LoDiceAntesDeArrancarNada()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var apache = FakeDistroApache([.. ApacheConf.RequiredModules.Where(m => m != "proxy_fcgi")]);
        var config = new RoostConfig { HttpPort = 8765, HttpsPort = 8453, PhpPool = 1, WebServer = RoostConfig.WebApache };
        var runner = new ApacheTestOk();

        var ex = Assert.Throws<RoostException>(() => new StackPlanner(Paths, runner).Prepare(config, php84, nginx: null, apache: apache));

        Assert.Contains("mod_proxy_fcgi.so", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [LinuxFact]
    public void Prepare_ConElApacheDeLaDistroEnUnPuertoBajo_SeNiega_YDiceElPuertoAlto()
    {
        if (Posix.Uid == 0) return;   // root sí puede abrirlo
        var php84 = FakePhp("8.4.27-nts-x64");
        var apache = FakeDistroApache([.. ApacheConf.RequiredModules]);
        var config = new RoostConfig { HttpPort = 80, HttpsPort = 8453, PhpPool = 1, WebServer = RoostConfig.WebApache };
        var runner = new ApacheTestOk();

        var ex = Assert.Throws<RoostException>(() => new StackPlanner(Paths, runner).Prepare(config, php84, nginx: null, apache: apache));

        Assert.Contains("por debajo del 1024", ex.Message);
        Assert.Contains("[web] port = 8080", ex.Message);
        Assert.Contains("setcap", ex.Message);
        Assert.Empty(runner.Calls);
    }

    // --- MariaDB y MySQL ---

    [Fact]
    public void MySqlCatalog_Linux_ElTarXzMinimal_ConSusDosUrls_YSinLa57()
    {
        var linux = MySqlCatalog.Linux;

        Assert.Equal(["8.4", "26.10", "8.0"], linux.Select(b => b.Branch));
        Assert.All(linux, b =>
        {
            Assert.EndsWith("-x86_64-minimal.tar.xz", b.FileName);
            Assert.Matches("^[0-9a-f]{64}$", b.Sha256);
            Assert.Equal(2, b.Urls.Count);
            Assert.Contains($"/Downloads/MySQL-{b.Branch}/", b.Urls[0].ToString());
            Assert.Contains($"/archives/mysql-{b.Branch}/", b.Urls[1].ToString());
            Assert.True(DbInstaller.IsTarXz(b.FileName));
        });
        Assert.Equal("mysql-8.4.11-linux-glibc2.28-x86_64-minimal.tar.xz", linux[0].FileName);
        Assert.Equal("mysql-8.0.46-linux-glibc2.17-x86_64-minimal.tar.xz", linux[2].FileName);
        Assert.Equal("8.4.11", DbCatalog.Resolve(linux, DbSpec.Parse("mysql")).Version.ToString());      // sin version, la LTS
        Assert.Equal("26.10.0", DbCatalog.Resolve(linux, DbSpec.Parse("mysql@26")).Version.ToString());
        Assert.Throws<RoostException>(() => DbCatalog.Resolve(linux, DbSpec.Parse("mysql@5.7")));
        Assert.Equal(OperatingSystem.IsWindows() ? MySqlCatalog.Windows : linux, MySqlCatalog.Current);
        Assert.False(DbInstaller.IsTarXz("mariadb-11.4.13-linux-systemd-x86_64.tar.gz"));
    }

    [Fact]
    public void LibAio_SoloSeEnlazaCuandoHayLaT64YNoLaDeSiempre()
    {
        Assert.Null(DbInstaller.LibAioLinkTarget(n => n == "libaio.so.1" ? "/lib/x86_64-linux-gnu/libaio.so.1" : null));   // Ubuntu 22.04: ya está
        Assert.Null(DbInstaller.LibAioLinkTarget(n => n == "libaio.so.1" ? "/lib/libaio.so.1" : "/lib/libaio.so.1t64"));   // las dos: la vieja manda
        Assert.Equal("/lib/x86_64-linux-gnu/libaio.so.1t64", DbInstaller.LibAioLinkTarget(n => n == "libaio.so.1t64" ? "/lib/x86_64-linux-gnu/libaio.so.1t64" : null));   // 24.04
        Assert.Null(DbInstaller.LibAioLinkTarget(_ => null));   // sin libaio: no hay a que enlazar (el SmokeTest dira el paquete)
        // En Windows, y con MariaDB, nunca se toca nada.
        Assert.False(DbInstaller.EnsureLibAio(new InstalledDb(DbEngine.MariaDb, "11.4.13", _root)));
    }

    [LinuxFact]
    public void EnsureLibAio_EnLinux_DejaElEnlaceEnLibPrivate_SiLaDistroSoloTieneLaT64()
    {
        var db = new InstalledDb(DbEngine.MySql, "8.4.11", Path.Combine(_root, "mysql"));
        Directory.CreateDirectory(Path.Combine(db.Directory, "bin"));

        var linked = DbInstaller.EnsureLibAio(db);

        var link = new FileInfo(Path.Combine(db.Directory, "lib", "private", "libaio.so.1"));
        var expected = DbInstaller.LibAioLinkTarget(LinuxDeps.FindLibrary);
        Assert.Equal(expected is not null, linked);
        if (expected is not null)
        {
            Assert.Equal(expected, link.LinkTarget);
            Assert.False(DbInstaller.EnsureLibAio(db));   // la segunda vez no hay nada que hacer
        }
        else
        {
            Assert.False(link.Exists);
        }
    }

    [Fact]
    public void DbIni_ConSocket_LoPoneEnElServidorYEnElCliente()
    {
        var version = new ComponentVersion(11, 4, 13, "11.4.13");

        var ini = DbIni.Render(DbEngine.MariaDb, version, "/b", "/d", 3407, socket: "/home/u/casa/run/mariadb-11.4.sock").Replace("\r\n", "\n");

        Assert.Contains("bind-address = 127.0.0.1\nsocket = \"/home/u/casa/run/mariadb-11.4.sock\"\n", ini);
        Assert.Contains("port = 3407\nsocket = \"/home/u/casa/run/mariadb-11.4.sock\"\nuser = root\n", ini);
        Assert.DoesNotContain("socket", DbIni.Render(DbEngine.MariaDb, version, "/b", "/d", 3407));
    }

    [LinuxFact]
    public void DbIni_Write_EnLinux_LlevaElSocketEnRun()
    {
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", Path.Combine(_root, "bin", "mariadb", "11.4.13"));

        var ini = File.ReadAllText(DbIni.Write(Paths, db, 3407, null));

        Assert.Equal(Path.Combine(Paths.Run, "mariadb-11.4.sock"), DbIni.SocketPath(Paths, db));
        Assert.Contains($"socket = \"{Paths.Run}/mariadb-11.4.sock\"", ini);
    }

    [Fact]
    public void InitCommand_MySql_InitializeInsecure_ConElDefaultsFileElPrimero()
    {
        var db = new InstalledDb(DbEngine.MySql, "8.4.11", Path.Combine(_root, "mysql"));

        var (exe, args) = StackPlanner.InitCommand(db, "/etc/my.ini", "/data/.tmp-1");

        Assert.Equal(db.Server, exe);
        Assert.Equal("--defaults-file=\"/etc/my.ini\" --initialize-insecure --datadir=\"/data/.tmp-1\"", args);
    }

    [LinuxFact]
    public void InitCommand_MariaDb_EnLinux_ElScriptDelBintar_ConBasedir_RootNormal_YSinTest()
    {
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", Path.Combine(_root, "mariadb"));
        Directory.CreateDirectory(Path.Combine(db.Directory, "scripts"));
        File.WriteAllText(Path.Combine(db.Directory, "scripts", "mariadb-install-db"), "#!/bin/sh\n");

        var (exe, args) = StackPlanner.InitCommand(db, "/etc/my.ini", "/data/.tmp-1");

        Assert.Equal(Path.Combine(db.Directory, "scripts", "mariadb-install-db"), exe);
        Assert.StartsWith("--defaults-file=\"/etc/my.ini\" ", args);   // antes que nada: si no, leeria el /etc/my.cnf de la distro
        Assert.Contains($"--basedir=\"{db.Directory}\"", args);
        Assert.Contains("--datadir=\"/data/.tmp-1\"", args);
        Assert.Contains("--auth-root-authentication-method=normal", args);
        Assert.Contains("--skip-test-db", args);
    }

    [WindowsFact("Solo en Windows: el mariadb-install-db.exe del zip.")]
    public void InitCommand_MariaDb_EnWindows_SuExeConElDatadir()
    {
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", @"C:\roost\bin\mariadb\11.4.13");

        var (exe, args) = StackPlanner.InitCommand(db, @"C:\roost\etc\mariadb\11.4\my.ini", @"C:\roost\data\mariadb\.tmp-1");

        Assert.Equal(@"C:\roost\bin\mariadb\11.4.13\bin\mariadb-install-db.exe", exe);
        Assert.Equal("--datadir=\"C:/roost/data/mariadb/.tmp-1\"", args);
    }

    [Fact]
    public void SmokeTest_EnLinux_SiFaltaUnaLibreria_NombraSuPaqueteApt()
    {
        var mysql = new InstalledDb(DbEngine.MySql, "8.4.11", "/home/u/casa/bin/mysql/8.4.11");
        const string ubuntu = "ID=ubuntu\nVERSION_ID=\"24.04\"\n";

        var missing = DbAdmin.SmokeTest(mysql, new Answering("/home/u/casa/bin/mysql/8.4.11/bin/mysqld: error while loading shared libraries: libaio.so.1: cannot open shared object file: No such file or directory\n"), windows: false, osRelease: ubuntu);
        var silent = DbAdmin.SmokeTest(mysql, new Answering(null), windows: false, osRelease: ubuntu);
        var maria = DbAdmin.SmokeTest(new InstalledDb(DbEngine.MariaDb, "11.4.13", "/x"), new Answering(""), windows: false, osRelease: ubuntu);
        var ok = DbAdmin.SmokeTest(mysql, new Answering("/x/bin/mysqld  Ver 8.4.11 for Linux on x86_64 (MySQL Community Server - GPL)\n"), windows: false, osRelease: ubuntu);

        Assert.False(missing.Ok);
        Assert.Equal("Falta la libreria libaio.so.1. Instalala: sudo apt install libaio1t64", missing.Hint);
        Assert.Null(missing.MissingRedistributable);   // lo de Windows no se nombra en Linux
        Assert.Contains("sudo apt install libaio1t64 libnuma1", silent.Hint);
        Assert.Contains("--version", maria.Hint);
        Assert.True(ok.Ok);
        Assert.Null(ok.Hint);
    }

    [LinuxFact]
    public async Task Instalar_UnTarXz_ConElTarDelSistema_YQuedaElServidorSinTemporales()
    {
        // Un tar.xz como el -minimal de MySQL: todo bajo mysql-<v>-linux-glibc2.28-x86_64-minimal/, con bin/mysqld.
        const string top = "mysql-9.9.9-linux-glibc2.28-x86_64-minimal";
        var source = Path.Combine(_root, "src", top);
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        File.WriteAllText(Path.Combine(source, "bin", "mysqld"), "#!/bin/sh\necho mysqld  Ver 9.9.9\n");
        File.WriteAllText(Path.Combine(source, "bin", "mysql"), "");
        var archive = Path.Combine(_root, $"{top}.tar.xz");
        var made = await CommandRunner.Default.RunAsync(new CommandSpec("tar", ["-cJf", archive, "-C", Path.Combine(_root, "src"), top]), null, CancellationToken.None);
        Assert.True(made.Succeeded, made.Stderr);
        var bytes = await File.ReadAllBytesAsync(archive);
        ComponentVersion.TryParse("9.9.9", out var v);
        var build = new DbBuild
        {
            Engine = DbEngine.MySql, Version = v, Branch = "9.9", FileName = $"{top}.tar.xz",
            Urls = [new Uri($"https://example.test/{top}.tar.xz")], Sha256 = Sha(bytes),
        };
        var installer = new DbInstaller(Paths, new HttpClient(new FakeHandler(bytes)));

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.DbBin("mysql"), "9.9.9"), installed.Directory);
        Assert.True(File.Exists(installed.Server));
        Assert.True(File.Exists(installed.Client));
        Assert.Empty(Directory.GetDirectories(Paths.DbBin("mysql"), ".*"));   // sin temporales
        Assert.Equal(installed, Assert.Single(installer.ListInstalled()));
    }

    // --- Redis, Mailpit, cloudflared ---

    [Fact]
    public void Catalogos_DeLinux_RedisDeRoostBuilds_MailpitTarGz_YCloudflaredSuelto()
    {
        Assert.Equal("https://github.com/get-roost/roost-builds/releases/download/redis-8.10.2/redis-8.10.2-linux-x86_64.tar.gz", RedisCatalog.Linux.Url.ToString());
        Assert.Equal("redis-8.10.2-linux-x86_64.tar.gz", RedisCatalog.Linux.FileName);
        Assert.Equal(RedisCatalog.Windows.Version, RedisCatalog.Linux.Version);
        Assert.Matches("^[0-9a-f]{64}$", RedisCatalog.Linux.Sha256);

        Assert.Equal("mailpit-1.32.0-linux-amd64.tar.gz", MailpitCatalog.Linux.FileName);
        Assert.Equal("mailpit-1.32.0-windows-amd64.zip", MailpitCatalog.Windows.FileName);   // como antes
        Assert.Equal("4b5993c978e8604c70d43927e2ad103b311bba3b1822fa0cf1d4ee4ec890a93c", MailpitCatalog.Linux.Sha256);

        Assert.Equal("cloudflared-2026.10.3-linux-amd64", CloudflaredCatalog.Linux.FileName);
        Assert.Equal("cloudflared-2026.10.3-windows-amd64.exe", CloudflaredCatalog.Windows.FileName);
        Assert.Equal("053b80b0a23031a038fdbf3c073f59f82b8f1f152aa67971d7383ffafc0dbab0", CloudflaredCatalog.Linux.Sha256);

        var windows = OperatingSystem.IsWindows();
        Assert.Equal(windows ? RedisCatalog.Windows : RedisCatalog.Linux, RedisCatalog.Current);
        Assert.Equal(windows ? MailpitCatalog.Windows : MailpitCatalog.Linux, MailpitCatalog.Current);
        Assert.Equal(windows ? CloudflaredCatalog.Windows : CloudflaredCatalog.Linux, CloudflaredCatalog.Current);
    }

    [Fact]
    public async Task Redis_SeInstalaDeUnTarGz_ConLosBinariosEnLaRaiz_YQuedanEjecutables()
    {
        var server = OsNames.Exe("redis-server");
        var cli = OsNames.Exe("redis-cli");
        var tar = TarGz(new Dictionary<string, string> { [server] = "x", [cli] = "x", ["redis-benchmark"] = "x", ["licenses/LICENSE.txt"] = "AGPL", ["BUILD.txt"] = "redis" }, server, cli);
        var build = new RedisBuild("8.10.2", new Uri("https://example.test/redis-8.10.2-linux-x86_64.tar.gz"), Sha(tar));
        var installer = new RedisInstaller(Paths, new HttpClient(new FakeHandler(tar)));

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.RedisBin, "8.10.2", server), installed.Server);
        Assert.True(File.Exists(installed.Cli));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "licenses", "LICENSE.txt")));
        Assert.Equal(installed, installer.Find(build));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(installed.Server).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Mailpit_SeInstalaDeUnTarGz_YClouflaredDeUnBinarioSuelto_YQuedanEjecutables()
    {
        var mailpitExe = OsNames.Exe("mailpit");
        var tar = TarGz(new Dictionary<string, string> { [mailpitExe] = "x", ["LICENSE"] = "MIT", ["README.md"] = "" }, mailpitExe);
        var mailpitBuild = new MailpitBuild("1.32.0", new Uri("https://example.test/mailpit-linux-amd64.tar.gz"), Sha(tar));
        var mailpit = await new MailpitInstaller(Paths, new HttpClient(new FakeHandler(tar))).InstallAsync(mailpitBuild);
        Assert.Equal("mailpit-1.32.0-linux-amd64.tar.gz", mailpitBuild.FileName);
        Assert.True(File.Exists(mailpit.Executable));

        var exe = System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\necho cloudflared\n");
        var cfBuild = new CloudflaredBuild("2026.10.3", new Uri("https://example.test/cloudflared-linux-amd64"), Sha(exe));
        var cloudflared = await new CloudflaredInstaller(Paths, new HttpClient(new FakeHandler(exe))).InstallAsync(cfBuild);
        Assert.Equal("cloudflared-2026.10.3-linux-amd64", cfBuild.FileName);
        Assert.True(File.Exists(cloudflared.Executable));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(mailpit.Executable).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(cloudflared.Executable).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void MailConf_ConSendmail_ElFragmentoLlevaElSendmailPathDeMailpit()
    {
        var sendmail = MailConf.SendmailPath("/home/u/casa/bin/mailpit/1.32.0/mailpit", 1125);

        Assert.Equal("/home/u/casa/bin/mailpit/1.32.0/mailpit sendmail --smtp-addr 127.0.0.1:1125", sendmail);
        var ini = MailConf.PhpIni(1125, 8125, sendmail);
        Assert.Contains("sendmail_path = \"/home/u/casa/bin/mailpit/1.32.0/mailpit sendmail --smtp-addr 127.0.0.1:1125\"", ini);
        Assert.Contains("smtp_port = 1125", ini);
        Assert.DoesNotContain("sendmail_path", MailConf.PhpIni(1125, 8125));
    }

    [LinuxFact]
    public void MailConf_SyncPhpIni_ConLaRaiz_EnLinux_PoneElSendmailPathDelMailpitInstalado()
    {
        var dir = Path.Combine(Paths.MailpitBin, MailpitCatalog.Current.Version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mailpit"), "");
        var confD = Path.Combine(Paths.Etc, "php", "conf.d");
        var on = new RoostConfig { MailEnabled = true, MailSmtpPort = 1125, MailWebPort = 8125 };

        Assert.True(MailConf.SyncPhpIni(Paths, confD, on));

        var ini = File.ReadAllText(Path.Combine(confD, MailConf.PhpIniName));
        Assert.Contains($"sendmail_path = \"{Path.Combine(dir, "mailpit")} sendmail --smtp-addr 127.0.0.1:1125\"", ini);
        Assert.False(MailConf.SyncPhpIni(Paths, confD, on));   // igual: no se reescribe
        Assert.True(MailConf.SyncPhpIni(Paths, confD, new RoostConfig()));
        Assert.False(File.Exists(Path.Combine(confD, MailConf.PhpIniName)));
    }

    // --- El portapapeles ---

    private sealed class RecordingRunner(int exitCode) : ICommandRunner
    {
        public List<CommandSpec> Calls { get; } = [];

        public Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Calls.Add(spec);
            return Task.FromResult(new CommandResult(exitCode, "", "", false));
        }
    }

    [Fact]
    public void Clipboard_Resolve_SegunElSistema_ElEscritorioYLoQueHayInstalado()
    {
        static string? Env(string name, params (string Name, string Value)[] vars) => vars.FirstOrDefault(v => v.Name == name).Value;

        var windows = Clipboard.Resolve(true, _ => null, _ => null, @"C:\Windows\System32");
        Assert.Equal(Path.Combine(@"C:\Windows\System32", "clip.exe"), windows.Command!.Exe);   // en Linux, Path.Combine une con '/'
        Assert.Null(windows.WhyNot);

        var headless = Clipboard.Resolve(false, _ => null, _ => "/usr/bin/xclip");
        Assert.False(headless.Available);
        Assert.Contains("sin escritorio", headless.WhyNot);

        var wayland = Clipboard.Resolve(false, n => Env(n, ("WAYLAND_DISPLAY", "wayland-0")), t => t == "wl-copy" ? "/usr/bin/wl-copy" : null);
        Assert.Equal("/usr/bin/wl-copy", wayland.Command!.Exe);
        Assert.Empty(wayland.Command.Arguments);

        var x11 = Clipboard.Resolve(false, n => Env(n, ("DISPLAY", ":0")), t => t == "xclip" ? "/usr/bin/xclip" : null);
        Assert.Equal("/usr/bin/xclip", x11.Command!.Exe);
        Assert.Equal(["-selection", "clipboard"], x11.Command.Arguments);

        var waylandWithXclip = Clipboard.Resolve(false, n => Env(n, ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), t => t == "xclip" ? "/usr/bin/xclip" : null);
        Assert.Equal("/usr/bin/xclip", waylandWithXclip.Command!.Exe);   // por XWayland

        var nothing = Clipboard.Resolve(false, n => Env(n, ("WAYLAND_DISPLAY", "wayland-0")), _ => null);
        Assert.False(nothing.Available);
        Assert.Contains("wl-clipboard", nothing.WhyNot);
        Assert.Contains("xclip", Clipboard.Resolve(false, n => Env(n, ("DISPLAY", ":0")), _ => null).WhyNot);
    }

    [Fact]
    public async Task Clipboard_CopyAsync_MandaElTextoPorStdin_YSiNoSePuedeDicePorQue()
    {
        var choice = new Clipboard.Choice(new CommandSpec("/usr/bin/wl-copy", [], Timeout: TimeSpan.FromSeconds(5)), null);
        var ok = new RecordingRunner(0);

        var copied = await Clipboard.CopyAsync("https://abc.trycloudflare.com", choice, ok);

        Assert.True(copied.Copied);
        Assert.Null(copied.WhyNot);
        Assert.Equal("https://abc.trycloudflare.com", Assert.Single(ok.Calls).Stdin);

        var failed = await Clipboard.CopyAsync("x", choice, new RecordingRunner(1));
        Assert.False(failed.Copied);
        Assert.Contains("wl-copy ha fallado", failed.WhyNot);

        var none = await Clipboard.CopyAsync("x", new Clipboard.Choice(null, "sin escritorio"), ok);
        Assert.False(none.Copied);
        Assert.Equal("sin escritorio", none.WhyNot);
        Assert.Single(ok.Calls);   // no se ha lanzado nada mas
    }
}
