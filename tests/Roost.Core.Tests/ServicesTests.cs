using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Los servicios de la oleada 3 sin red ni binarios reales: Redis, Mailpit y Adminer con zips y
/// ficheros servidos en memoria, su configuración generada, <c>[redis]</c> y <c>[mail]</c> en
/// roost.toml, su sitio en el <c>Prepare</c> y el <c>/adminer</c> de nginx y Apache.
/// </summary>
public class ServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-servicios-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
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

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // Redis

    private static readonly string[] RedisZip =
    [
        "Redis-8.10.2-Windows-x64-msys2/redis-server.exe", "Redis-8.10.2-Windows-x64-msys2/redis-cli.exe",
        "Redis-8.10.2-Windows-x64-msys2/msys-2.0.dll", "Redis-8.10.2-Windows-x64-msys2/redis.conf",
    ];

    private static RedisBuild RedisBuildFor(byte[] zip, string? sha = null) =>
        new("8.10.2", new Uri("https://example.test/Redis-8.10.2-Windows-x64-msys2.zip"), sha ?? Sha(zip));

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Redis_Install_QuitaLaCarpetaEnvolvente_YLocateLoEncuentra()
    {
        var zip = Zip(RedisZip);
        var installer = new RedisInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var redis = await installer.InstallAsync(RedisBuildFor(zip));

        Assert.Equal(Path.Combine(Paths.RedisBin, "8.10.2", OsNames.Exe("redis-server")), redis.Server);
        Assert.True(File.Exists(redis.Cli));
        Assert.True(File.Exists(Path.Combine(redis.Directory, "msys-2.0.dll")));
        Assert.Equal(redis, installer.Find(RedisBuildFor(zip)));
        Assert.Equal(["8.10.2"], installer.ListInstalled().Select(r => r.Version));
        Assert.Empty(Directory.GetDirectories(Paths.RedisBin, ".*"));   // sin temporales
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(RedisBuildFor(zip)));   // ya está
    }

    [Fact]
    public async Task Redis_Install_ConUnShaQueNoCuadra_NoInstalaNada()
    {
        var zip = Zip(RedisZip);
        var installer = new RedisInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(RedisBuildFor(zip, new string('0', 64))));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Null(installer.Locate());
        Assert.False(Directory.Exists(Paths.RedisBin) && Directory.EnumerateFileSystemEntries(Paths.RedisBin).Any());
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Redis_Install_SinRedisCli_NoInstala_PorqueNoPodriaPararloLimpio()
    {
        var zip = Zip("Redis-8.10.2-Windows-x64-msys2/redis-server.exe");
        var installer = new RedisInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(RedisBuildFor(zip)));

        Assert.Contains(OsNames.Exe("redis-cli"), ex.Message);
        Assert.Null(installer.Locate());
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Redis_Remove_QuitaLosBinarios_PeroNuncaLosDatos()
    {
        var zip = Zip(RedisZip);
        var installer = new RedisInstaller(Paths, new HttpClient(new FakeHandler(zip)));
        var redis = await installer.InstallAsync(RedisBuildFor(zip));
        Directory.CreateDirectory(Paths.RedisData);
        File.WriteAllText(Path.Combine(Paths.RedisData, "dump.rdb"), "datos");

        installer.Remove(redis);

        Assert.Null(installer.Locate());
        Assert.True(File.Exists(Path.Combine(Paths.RedisData, "dump.rdb")));
    }

    [Fact]
    public void Redis_Locate_PrefiereElDeEstaRoost_YSiNoElMasNuevo()
    {
        foreach (var version in new[] { "7.4.6", "8.8.3" })
        {
            Directory.CreateDirectory(Path.Combine(Paths.RedisBin, version));
            File.WriteAllText(Path.Combine(Paths.RedisBin, version, OsNames.Exe("redis-server")), "");
            File.WriteAllText(Path.Combine(Paths.RedisBin, version, OsNames.Exe("redis-cli")), "");
        }
        Directory.CreateDirectory(Path.Combine(Paths.RedisBin, ".tmp-x"));

        Assert.Equal("8.8.3", new RedisInstaller(Paths).Locate()!.Version);   // el de RedisCatalog (8.10.2) no está

        Directory.CreateDirectory(Path.Combine(Paths.RedisBin, RedisCatalog.Windows.Version));
        File.WriteAllText(Path.Combine(Paths.RedisBin, RedisCatalog.Windows.Version, OsNames.Exe("redis-server")), "");
        File.WriteAllText(Path.Combine(Paths.RedisBin, RedisCatalog.Windows.Version, OsNames.Exe("redis-cli")), "");
        Assert.Equal(RedisCatalog.Windows.Version, new RedisInstaller(Paths).Locate()!.Version);
    }

    [Fact]
    public void RedisCatalog_LaBuildMsys2Fijada()
    {
        var build = RedisCatalog.Windows;
        Assert.Equal("8.10.2", build.Version);
        Assert.Equal("Redis-8.10.2-Windows-x64-msys2.zip", build.FileName);
        Assert.Equal("github.com", build.Url.Host);
        Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
    }

    [Fact]
    public void RedisConf_SoloEnLoopback_ConAof_YElIncludeDelUsuarioAlFinal()
    {
        var conf = RedisConf.Render("8.10.2", 6380, @"C:\roost\data\redis", @"C:\roost\etc\redis\conf.d\99-usuario.conf");
        var lines = conf.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        Assert.Contains("bind 127.0.0.1", lines);
        Assert.Contains("port 6380", lines);
        Assert.Contains("protected-mode yes", lines);
        Assert.Contains("daemonize no", lines);
        Assert.Contains("dir \"C:/roost/data/redis\"", lines);   // con '/': lo entiende la build MSYS2
        Assert.Contains("appendonly yes", lines);
        Assert.Contains("logfile \"\"", lines);   // a la salida, que guarda Roost en logs/redis.log
        // Absoluto (Redis hace chdir al leer dir) y el último: lo del usuario manda.
        Assert.Equal("include \"C:/roost/etc/redis/conf.d/99-usuario.conf\"", lines[^1]);
    }

    [Fact]
    public void RedisConf_Write_CreaElFragmentoDelUsuarioUnaVez_YLaCarpetaDeDatos()
    {
        var conf = RedisConf.Write(Paths, "8.10.2", 6379);
        File.WriteAllText(RedisConf.UserFragmentPath(Paths), "notify-keyspace-events Ex\n");

        RedisConf.Write(Paths, "8.10.2", 6380);

        Assert.Contains("port 6380", File.ReadAllText(conf));
        Assert.Equal("notify-keyspace-events Ex\n", File.ReadAllText(RedisConf.UserFragmentPath(Paths)));   // del usuario: no se pisa
        Assert.True(Directory.Exists(Paths.RedisData));
    }

    [Theory]
    [InlineData("# requirepass comentada\n", null)]
    [InlineData("requirepass secreto\n", "secreto")]
    [InlineData("requirepass \"con espacio\"\n", "con espacio")]
    [InlineData("requirepass uno\nrequirepass dos\n", "dos")]   // cuenta la última, como en Redis
    [InlineData("", null)]
    public void RedisConf_UserPassword_LaDeRequirepass(string fragment, string? expected) =>
        Assert.Equal(expected, RedisConf.UserPassword(fragment));

    [Fact]
    public async Task RedisProbe_Ping_ContestaPong_YNuncaLanza()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer);
            Assert.Equal("*1\r\n$4\r\nPING\r\n", Encoding.ASCII.GetString(buffer, 0, read));
            await stream.WriteAsync("+PONG\r\n"u8.ToArray());
        });

        var reply = await RedisProbe.PingAsync(port);
        await server;
        listener.Stop();

        Assert.True(RedisProbe.IsPong(reply));
        Assert.Null(await RedisProbe.PingAsync(port));   // ya no escucha nadie: null, sin excepción
        Assert.True(RedisProbe.NeedsPassword("-NOAUTH Authentication required."));
    }

    // Mailpit

    [Fact]
    public async Task Mailpit_Install_ZipPlano_YNoSePisa()
    {
        var zip = Zip(OsNames.Exe("mailpit"), "LICENSE", "README.md");
        var build = new MailpitBuild("1.32.0", new Uri("https://example.test/mailpit-windows-amd64.zip"), Sha(zip));
        var installer = new MailpitInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var mailpit = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.MailpitBin, "1.32.0", OsNames.Exe("mailpit")), mailpit.Executable);
        Assert.True(File.Exists(mailpit.Executable));
        Assert.Equal("mailpit-1.32.0-windows-amd64.zip", build.FileName);   // en la caché, con la versión
        Assert.True(File.Exists(Path.Combine(Paths.Downloads, build.FileName)));
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));
    }

    [Fact]
    public async Task Mailpit_Install_ConUnShaQueNoCuadra_NoInstalaNada_YLaBandejaNoSeToca()
    {
        var zip = Zip(OsNames.Exe("mailpit"));
        var build = new MailpitBuild("1.32.0", new Uri("https://example.test/mailpit-windows-amd64.zip"), new string('a', 64));
        Directory.CreateDirectory(Paths.MailpitData);
        File.WriteAllText(Path.Combine(Paths.MailpitData, "mailpit.db"), "bandeja");
        var installer = new MailpitInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Null(installer.Locate());
        Assert.Equal("bandeja", File.ReadAllText(Path.Combine(Paths.MailpitData, "mailpit.db")));
    }

    [Fact]
    public void MailConf_Argumentos_TodoEnLoopback_ConLaBandejaPersistente_YSinPreguntarPorVersiones()
    {
        var args = MailConf.Arguments(1026, 8026, @"C:\roost\data\mailpit\mailpit.db");

        Assert.Contains("--smtp 127.0.0.1:1026", args);
        Assert.Contains("--listen 127.0.0.1:8026", args);
        Assert.Contains("--database \"C:\\roost\\data\\mailpit\\mailpit.db\"", args);
        Assert.Contains("--allowed-hosts localhost,127.0.0.1", args);   // contra el DNS rebinding
        Assert.Contains("--smtp-auth-accept-any", args);
        Assert.Contains("--disable-version-check", args);
        Assert.DoesNotContain("0.0.0.0", args);
        Assert.DoesNotContain("[::]", args);
    }

    [Fact]
    public void MailConf_ElFragmentoDePhp_SeEscribeConMailpitYSeQuitaSinEl()
    {
        var confD = Path.Combine(Paths.Etc, "php", "conf.d");
        var on = new RoostConfig { MailEnabled = true, MailSmtpPort = 1026, MailWebPort = 8026 };

        Assert.True(MailConf.SyncPhpIni(confD, on));
        var ini = File.ReadAllText(Path.Combine(confD, MailConf.PhpIniName)).Replace("\r\n", "\n");
        Assert.Contains("SMTP = 127.0.0.1\n", ini);
        Assert.Contains("smtp_port = 1026\n", ini);
        Assert.Contains("sendmail_from = \"roost@localhost\"", ini);
        Assert.False(MailConf.SyncPhpIni(confD, on));   // igual: no se reescribe

        Assert.True(MailConf.SyncPhpIni(confD, new RoostConfig()));
        Assert.False(File.Exists(Path.Combine(confD, MailConf.PhpIniName)));
        Assert.False(MailConf.SyncPhpIni(confD, new RoostConfig()));
        Assert.True(string.CompareOrdinal(MailConf.PhpIniName, PhpIni.UserFragmentName) < 0);   // el del usuario se carga después y manda
    }

    [Fact]
    public void EnvLines_DeLaravel_ParaRedisYParaElCorreo()
    {
        Assert.Equal("REDIS_CLIENT=predis\nREDIS_HOST=127.0.0.1\nREDIS_PASSWORD=null\nREDIS_PORT=6380", RedisConf.EnvLines(6380));
        Assert.Contains("MAIL_MAILER=smtp\nMAIL_HOST=127.0.0.1\nMAIL_PORT=1026\n", MailConf.EnvLines(1026));
        Assert.Contains("MAIL_SCHEME=null", MailConf.EnvLines(1026));
    }

    // Adminer

    [Fact]
    public async Task Adminer_Install_ElFicheroTalCual_YLaEntradaDeRoost()
    {
        var script = Encoding.UTF8.GetBytes("<?php /** Adminer */ namespace Adminer; echo 'adminer';");
        var build = new AdminerBuild("6.2.0", new Uri("https://example.test/adminer-6.2.0-mysql.php"), Sha(script));
        var installer = new AdminerInstaller(Paths, new HttpClient(new FakeHandler(script)));

        var adminer = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.AdminerBin, "6.2.0"), adminer.Directory);
        Assert.Equal(script, File.ReadAllBytes(adminer.Script));   // byte a byte: no se toca
        Assert.Equal(AdminerEntry.Text, File.ReadAllText(adminer.Entry));
        Assert.Equal(adminer, installer.Find(build));
        Assert.Empty(Directory.GetDirectories(Paths.AdminerBin, ".*"));
        Assert.False(Directory.Exists(Path.Combine(Paths.Www, "adminer")));   // la www es del usuario
    }

    [Fact]
    public async Task Adminer_Install_ConUnShaQueNoCuadra_NoInstalaNada()
    {
        var script = Encoding.UTF8.GetBytes("<?php echo 'otro';");
        var build = new AdminerBuild("6.2.0", new Uri("https://example.test/adminer-6.2.0-mysql.php"), new string('b', 64));
        var installer = new AdminerInstaller(Paths, new HttpClient(new FakeHandler(script)));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Null(installer.Find(build));
        Assert.False(Directory.Exists(installer.InstallDirectory(build)));
    }

    [Fact]
    public void AdminerEntry_SoloLocalhost_SinContrasena_YSinPreguntarAAdminerOrg()
    {
        var entry = AdminerEntry.Text;

        Assert.StartsWith("<?php", entry);
        Assert.Contains("array('localhost', '127.0.0.1', '[::1]')", entry);
        Assert.Contains("HTTP/1.1 403 Forbidden", entry);
        Assert.Contains("function adminer_object()", entry);
        Assert.Contains("extends \\Adminer\\Adminer", entry);
        Assert.Contains("public function login($login, $password)", entry);
        Assert.Contains("public function verifyVersion()", entry);
        Assert.Contains("require __DIR__ . '/adminer.php';", entry);
        Assert.Equal("http://localhost:8766/adminer/?server=127.0.0.1:3408&username=root", AdminerEntry.Url(8766, 3408));
        Assert.Equal("http://localhost/adminer/?server=127.0.0.1:3306&username=root", AdminerEntry.Url(80, 3306));
    }

    [Fact]
    public void AdminerInstaller_EnsureEntry_RehaceLaEntradaSoloSiHaCambiado()
    {
        var adminer = new InstalledAdminer("6.2.0", Path.Combine(Paths.AdminerBin, "6.2.0"));
        Directory.CreateDirectory(adminer.Directory);
        File.WriteAllText(adminer.Entry, "<?php // vieja");

        Assert.True(AdminerInstaller.EnsureEntry(adminer));
        Assert.Equal(AdminerEntry.Text, File.ReadAllText(adminer.Entry));
        Assert.False(AdminerInstaller.EnsureEntry(adminer));
    }

    [Fact]
    public void NginxConf_Adminer_TodoLoDeAdminerALaEntrada_ConElPhpGlobal()
    {
        var input = new NginxConfInput(@"C:\roost\bin\nginx\1.30.6\conf\mime.types", @"C:\roost\bin\nginx\1.30.6\conf\fastcgi_params",
            @"C:\roost\run", @"C:\roost\logs", @"C:\roost\www", 8766, [new NginxUpstream("php-8.4", [9840])], "php-8.4", @"C:\roost\etc\nginx\sites");

        var conf = NginxConf.Render(input with { AdminerDirectory = @"C:\roost\bin\adminer\6.2.0" }).Replace("\r\n", "\n");

        Assert.Contains("location = /adminer {\n            return 301 /adminer/;", conf);
        Assert.Contains("location ^~ /adminer/ {\n            fastcgi_pass php-8.4;\n            fastcgi_param SCRIPT_FILENAME \"C:/roost/bin/adminer/6.2.0/index.php\";", conf);
        // En el bloque de localhost, antes de los .php de la www (el ^~ hace que no pase por ellos).
        Assert.True(conf.IndexOf("location ^~ /adminer/", StringComparison.Ordinal) < conf.IndexOf("location ~ \\.php$", StringComparison.Ordinal));
        Assert.True(conf.IndexOf("location ^~ /adminer/", StringComparison.Ordinal) < conf.IndexOf("include \"C:/roost/etc/nginx/sites/*.conf\"", StringComparison.Ordinal));
        Assert.DoesNotContain("adminer", NginxConf.Render(input));
    }

    [Fact]
    public void ApacheConf_Adminer_AliasALaCarpeta_SinElAdminerSuelto_YConSuScriptFilename()
    {
        var input = new ApacheConfInput(@"C:\roost\bin\apache\2.4.69", @"C:\roost\run", @"C:\roost\logs", @"C:\roost\www", 8766, null,
            [new NginxUpstream("php-8.4", [9840])], "php-8.4", @"C:\roost\etc\apache\sites");

        var conf = ApacheConf.Render(input with { AdminerDirectory = @"C:\roost\bin\adminer\6.2.0" }).Replace("\r\n", "\n");

        Assert.Contains("Alias \"/adminer\" \"C:/roost/bin/adminer/6.2.0\"", conf);
        Assert.Contains("<Files \"adminer.php\">\n            Require all denied\n        </Files>", conf);
        Assert.Contains("ProxyFCGISetEnvIf \"true\" SCRIPT_FILENAME \"C:/roost/bin/adminer/6.2.0/index.php\"", conf);
        var vhost = conf.IndexOf("<VirtualHost", StringComparison.Ordinal);
        Assert.True(conf.IndexOf("Alias \"/adminer\"", StringComparison.Ordinal) > vhost);   // dentro del de localhost
        Assert.True(conf.IndexOf("Alias \"/adminer\"", StringComparison.Ordinal) < conf.IndexOf("</VirtualHost>", StringComparison.Ordinal));
        Assert.DoesNotContain("adminer", ApacheConf.Render(input));
    }

    // roost.toml

    [Fact]
    public void Config_RedisYMail_PorDefectoFuera_YSeLeen()
    {
        var file = Path.Combine(_root, "roost.toml");
        Directory.CreateDirectory(_root);

        var none = RoostConfig.Load(file);
        Assert.False(none.RedisEnabled);
        Assert.Equal(6379, none.RedisPort);
        Assert.False(none.MailEnabled);
        Assert.Equal((1025, 8025), (none.MailSmtpPort, none.MailWebPort));

        File.WriteAllText(file, "[redis]\nenabled = true\nport = 6380\n\n[mail]\nenabled = true\nsmtp_port = 1026\nweb_port = 8026\n");
        var config = RoostConfig.Load(file);
        Assert.True(config.RedisEnabled);
        Assert.Equal(6380, config.RedisPort);
        Assert.True(config.MailEnabled);
        Assert.Equal("http://localhost:8026/", config.MailWebUrl);
        Assert.Equal([RoostConfig.DefaultHttpPort, 3306, 6380, 1026, 8026], config.PlannedPorts(withSites: false, withDb: true));

        File.WriteAllText(file, "[redis]\nenabled = \"si\"\n");
        Assert.Contains("true o false", Assert.Throws<RoostException>(() => RoostConfig.Load(file)).Message);
    }

    [Theory]
    [InlineData("[redis]\nenabled = true\nport = 3306\n", "[redis] port (3306) es el mismo puerto que [db] port")]
    [InlineData("[mail]\nenabled = true\nsmtp_port = 8025\n", "[mail] web_port (8025) es el mismo puerto que [mail] smtp_port")]
    [InlineData("[web]\nport = 8080\n[redis]\nenabled = true\nport = 8080\n", "[redis] port (8080) es el mismo puerto que [web] port")]
    [InlineData("[redis]\nenabled = true\nport = 70000\n", "[redis] port tiene que estar entre 1 y 65535")]
    public void Config_PuertosQueChocan_SeRechazanAlLeer(string toml, string expected)
    {
        var file = Path.Combine(_root, "roost.toml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, toml);

        Assert.Contains(expected, Assert.Throws<RoostException>(() => RoostConfig.Load(file)).Message);
    }

    [Fact]
    public void Config_UnPuertoQueChoca_SinElServicioEnElStack_NoMolestaHastaQueSeActiva()
    {
        var file = Path.Combine(_root, "roost.toml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "[redis]\nport = 3306\n");

        Assert.False(RoostConfig.Load(file).RedisEnabled);   // se lee bien
        Assert.Throws<RoostException>(() => RoostConfig.SetRedis(file, enabled: true));
        Assert.Throws<RoostException>(() => RoostConfig.SetRedis(file, port: RoostConfig.DefaultHttpPort));   // un puerto nuevo se valida ya (el web por defecto: 80, o 8080 en Linux)
        Assert.Equal("[redis]\nport = 3306\n", File.ReadAllText(file));   // y no se ha escrito nada
    }

    [Fact]
    public void Config_SetRedisYSetMail_TocanSusClaves_YRespetanLoDemas()
    {
        var file = Path.Combine(_root, "roost.toml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "# mi config\n[php]\nversion = \"8.4\" # la buena\n");

        RoostConfig.SetRedis(file, port: 6380);
        RoostConfig.SetRedis(file, enabled: true);
        RoostConfig.SetMail(file, enabled: true, smtpPort: 1026, webPort: 8026);
        RoostConfig.SetMail(file, enabled: false);

        var text = File.ReadAllText(file);
        Assert.StartsWith("# mi config\n[php]\nversion = \"8.4\" # la buena\n", text);
        var config = RoostConfig.Load(file);
        Assert.True(config.RedisEnabled);
        Assert.Equal(6380, config.RedisPort);
        Assert.False(config.MailEnabled);
        Assert.Equal((1026, 8026), (config.MailSmtpPort, config.MailWebPort));
    }

    [Fact]
    public void Config_LaPlantilla_TraeRedisYMailFuera()
    {
        var file = Path.Combine(_root, "nuevo", "roost.toml");

        RoostConfig.SetWebServer(file, RoostConfig.WebNginx);   // sin fichero: escribe la plantilla

        var text = File.ReadAllText(file);
        Assert.Contains("[redis]\n", text.Replace("\r\n", "\n"));
        Assert.Contains("[mail]\n", text.Replace("\r\n", "\n"));
        var config = RoostConfig.Load(file);
        Assert.False(config.RedisEnabled);
        Assert.False(config.MailEnabled);
        Assert.Equal(6379, config.RedisPort);
    }

    // Prepare

    /// <summary>nginx -t falso: siempre da por buena la configuracion.</summary>
    private sealed class NginxTestOk : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "nginx: configuration file test is successful";
    }

    private InstalledPhp FakePhp(string name)
    {
        var dir = Path.Combine(Paths.PhpBin, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php")), "");
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php-cgi")), "");
        return new InstalledPhp(name, dir, Path.Combine(dir, OsNames.Exe("php")));
    }

    private void FakeInstall(string bin, string version, params string[] files)
    {
        Directory.CreateDirectory(Path.Combine(bin, version));
        foreach (var file in files) File.WriteAllText(Path.Combine(bin, version, file), "");
    }

    private (InstalledPhp Php, InstalledNginx Nginx, InstalledDb Db) FakeStack()
    {
        var php = FakePhp("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var dbDir = Path.Combine(Paths.DbBin("mariadb"), "11.4.13");
        Directory.CreateDirectory(Path.Combine(dbDir, "bin"));
        Directory.CreateDirectory(Path.Combine(Paths.Data, "mariadb", "11.4", "mysql"));   // datadir ya creado
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", dbDir);
        return (php, nginx, db);
    }

    [Fact]
    public void Prepare_ConRedisYMailpit_VanTrasLaBaseDeDatos_YAntesDeLasExtensiones()
    {
        var (php, nginx, db) = FakeStack();
        FakeInstall(Paths.RedisBin, "8.10.2", OsNames.Exe("redis-server"), OsNames.Exe("redis-cli"));
        FakeInstall(Paths.MailpitBin, "1.32.0", OsNames.Exe("mailpit"));
        var extension = new ServiceDefinition { Name = "memcached", Executable = "memcached.exe", Port = 11211 };
        var config = new RoostConfig { HttpPort = 8766, PhpPool = 1, DbPort = 3408, RedisEnabled = true, RedisPort = 6380, MailEnabled = true, MailSmtpPort = 1026, MailWebPort = 8026 };

        var plan = new StackPlanner(Paths, new NginxTestOk()).Prepare(config, php, nginx, db, extraServices: [extension]);

        Assert.Equal(["mariadb-11.4", "redis", "mailpit", "memcached", "php-8.4-0", "nginx"], plan.Services.Select(s => s.Name));
        Assert.Equal([3408, 6380, 1026, 11211, 9840, 8766, 8026], plan.Ports);   // la bandeja también tiene que estar libre
        Assert.Equal("8.10.2", plan.Redis!.Version);
        Assert.Equal(8026, plan.MailWebPort);

        var redis = plan.Services.Single(s => s.Name == "redis");
        Assert.Equal("redis.conf", redis.Arguments);   // relativa: sin rutas de Cygwin en la línea de órdenes
        Assert.Equal(RedisConf.Directory(Paths), redis.WorkingDirectory);
        Assert.Equal(6380, redis.Port);
        Assert.Equal("-h 127.0.0.1 -p 6380 shutdown", redis.StopCommand!.Arguments);
        Assert.EndsWith(OsNames.Exe("redis-cli"), redis.StopCommand.Executable);
        Assert.Empty(redis.StopCommand.Environment);
        Assert.Contains("port 6380", File.ReadAllText(RedisConf.ConfPath(Paths)));
        Assert.True(File.Exists(RedisConf.UserFragmentPath(Paths)));

        var mailpit = plan.Services.Single(s => s.Name == "mailpit");
        Assert.Equal(1026, mailpit.Port);
        Assert.Contains("--listen 127.0.0.1:8026", mailpit.Arguments);
        Assert.Contains(MailConf.Database(Paths), mailpit.Arguments);
        Assert.True(Directory.Exists(Paths.MailpitData));
        Assert.Contains("smtp_port = 1026", File.ReadAllText(Path.Combine(Paths.Etc, "php", "conf.d", MailConf.PhpIniName)));
    }

    [Fact]
    public void Prepare_SinRedisNiMailpit_NoLosArranca_YQuitaElFragmentoDelCorreo()
    {
        var (php, nginx, _) = FakeStack();
        var confD = Path.Combine(Paths.Etc, "php", "conf.d");
        Directory.CreateDirectory(confD);
        File.WriteAllText(Path.Combine(confD, MailConf.PhpIniName), "; de antes");

        var plan = new StackPlanner(Paths, new NginxTestOk()).Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 1 }, php, nginx);

        Assert.Equal(["php-8.4-0", "nginx"], plan.Services.Select(s => s.Name));
        Assert.Null(plan.Redis);
        Assert.Null(plan.MailWebPort);
        Assert.False(File.Exists(Path.Combine(confD, MailConf.PhpIniName)));
    }

    [Fact]
    public void Prepare_ConRequirepass_LaParadaLimpiaLlevaLaContrasenaPorElEntorno()
    {
        var (php, nginx, _) = FakeStack();
        FakeInstall(Paths.RedisBin, "8.10.2", OsNames.Exe("redis-server"), OsNames.Exe("redis-cli"));
        Directory.CreateDirectory(Path.GetDirectoryName(RedisConf.UserFragmentPath(Paths))!);
        File.WriteAllText(RedisConf.UserFragmentPath(Paths), "requirepass s3creta\n");

        var plan = new StackPlanner(Paths, new NginxTestOk()).Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 1, RedisEnabled = true }, php, nginx);

        var stop = plan.Services.Single(s => s.Name == "redis").StopCommand!;
        Assert.Equal("s3creta", stop.Environment["REDISCLI_AUTH"]);
        Assert.DoesNotContain("s3creta", stop.Arguments);
    }

    [Theory]
    [InlineData(true, false, "roost redis install")]
    [InlineData(false, true, "roost mail install")]
    public void Prepare_EnElStackPeroSinInstalar_DiceComoInstalarlo(bool redis, bool mail, string expected)
    {
        var (php, nginx, _) = FakeStack();
        var config = new RoostConfig { HttpPort = 8766, PhpPool = 1, RedisEnabled = redis, MailEnabled = mail };

        var ex = Assert.Throws<RoostException>(() => new StackPlanner(Paths, new NginxTestOk()).Prepare(config, php, nginx));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Prepare_Adminer_EnLocalhost_AunqueNoEsteInstalado_SalvoQueElUsuarioTengaElSuyo()
    {
        var (php, nginx, _) = FakeStack();
        var planner = new StackPlanner(Paths, new NginxTestOk());
        var dir = NginxConf.Nginx(new AdminerInstaller(Paths).InstallDirectory(AdminerCatalog.Current));

        planner.Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 1 }, php, nginx);
        Assert.Contains($"fastcgi_param SCRIPT_FILENAME \"{dir}/index.php\";", File.ReadAllText(planner.NginxConfPath));

        // Instalado con una entrada vieja: el up la deja al día.
        var adminer = new InstalledAdminer(AdminerCatalog.Current.Version, new AdminerInstaller(Paths).InstallDirectory(AdminerCatalog.Current));
        Directory.CreateDirectory(adminer.Directory);
        File.WriteAllText(adminer.Script, "<?php");
        File.WriteAllText(adminer.Entry, "<?php // de otra Roost");
        planner.Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 1 }, php, nginx);
        Assert.Equal(AdminerEntry.Text, File.ReadAllText(adminer.Entry));

        Directory.CreateDirectory(Path.Combine(Paths.Www, "adminer"));
        planner.Prepare(new RoostConfig { HttpPort = 8766, PhpPool = 1 }, php, nginx);
        Assert.DoesNotContain("adminer", File.ReadAllText(planner.NginxConfPath));
    }

    // Servicios de Windows (E7)

    private static WindowsService Service(string name, string kind, string? exe, ServiceStartMode start, bool running, string? display = null) =>
        new(name, display ?? name, kind, exe, start, running);

    [Fact]
    public void WindowsServices_Conflicts_DiceLosPuertosQueSeQuedaria()
    {
        WindowsService[] services =
        [
            Service("MariaDB", "MariaDB", @"C:\Program Files\MariaDB 10.6\bin\mysqld.exe", ServiceStartMode.Automatic, running: true, "MariaDB 10.6"),
            Service("Memurai", "Redis", @"C:\Program Files\Memurai\memurai.exe", ServiceStartMode.Automatic, running: false),
            Service("Apache2.4", "Apache", @"C:\Apache24\bin\httpd.exe", ServiceStartMode.Manual, running: false),
        ];

        var conflicts = WindowsServices.Conflicts(services, [80, 443, 3306, 6379]);

        Assert.Equal(["MariaDB", "Memurai"], conflicts.Select(c => c.Service.Name));
        Assert.Equal([3306], conflicts[0].Ports);
        Assert.Equal([6379], conflicts[1].Ports);
        Assert.Empty(WindowsServices.Conflicts(services, [8766, 8444, 3408, 6380]));   // con otros puertos, nada
    }

    [WindowsFact("Solo en Windows: los servicios de Windows.")]
    public void WindowsServices_OwnerOf_PorElProcesoQueTieneElPuerto()
    {
        WindowsService[] services =
        [
            Service("MariaDB", "MariaDB", @"C:\Program Files\MariaDB 10.6\bin\mysqld.exe", ServiceStartMode.Automatic, running: true, "MariaDB 10.6"),
            Service("W3SVC", "IIS", @"C:\Windows\system32\svchost.exe", ServiceStartMode.Automatic, running: true, "World Wide Web Publishing Service"),
            Service("MySQL80", "MySQL", @"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe", ServiceStartMode.Manual, running: false),
        ];

        Assert.Equal("MariaDB", WindowsServices.OwnerOf(new PortOwner(3306, 4120, "mysqld"), services)?.Name);   // el parado no cuenta
        Assert.Equal("W3SVC", WindowsServices.OwnerOf(new PortOwner(80, 4, "System"), services)?.Name);   // http.sys
        Assert.Null(WindowsServices.OwnerOf(new PortOwner(445, 4, "System"), services));
        Assert.Null(WindowsServices.OwnerOf(new PortOwner(3306, 999, "mariadbd"), services));
        Assert.Null(WindowsServices.OwnerOf(null, services));
    }

    [Fact]
    public void WindowsServices_LosTextos_ParaDejarloEnManual()
    {
        var maria = Service("MariaDB", "MariaDB", null, ServiceStartMode.Automatic, running: true, "MariaDB 10.6");

        Assert.Equal("sc config \"MariaDB\" start= demand", WindowsServices.ManualCommand(maria));
        Assert.Equal("sc stop \"MariaDB\"", WindowsServices.StopCommand(maria));
        Assert.Equal("\"MariaDB\" (MariaDB 10.6)", WindowsServices.Label(maria));
        Assert.Equal("\"Apache2.4\"", WindowsServices.Label(Service("Apache2.4", "Apache", null, ServiceStartMode.Manual, false)));
        Assert.Equal("arranca con Windows y esta en marcha", WindowsServices.StateText(maria));
        Assert.Equal("arranca con Windows (ahora esta parado)", WindowsServices.StateText(maria with { Running = false }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
