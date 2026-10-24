using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Daemon;
using Roost.Extensibility;

namespace Roost.Daemon.Tests;

/// <summary>
/// Testea el stack del daemon (<see cref="StackService"/>) sin arrancar nada de verdad: una raíz
/// de Roost en una carpeta temporal con "binarios" vacíos (PHP 8.4 y 7.4, nginx, Apache, MariaDB con
/// su datadir), un lanzador falso cuyos procesos son <c>TaskCompletionSource</c>, un <c>nginx -t</c>
/// que da por buena la configuración (y un <c>nginx -s reload</c> que se apunta), y sondas de
/// puertos que dicen lo que el test quiera. Nada toca la red, el hosts ni el almacén de certificados.
/// </summary>
public class StackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-daemon-" + Guid.NewGuid().ToString("N"));
    private readonly RoostPaths _paths;
    private readonly Events _events = new();
    private readonly FakeLauncher _launcher;
    private readonly FakeRunner _runner;
    private readonly HashSet<int> _busy = [];
    private readonly List<string> _log = [];
    private readonly List<(string Web, int? Pid)> _apacheReloads = [];

    public StackServiceTests()
    {
        _launcher = new FakeLauncher(_events);
        _runner = new FakeRunner(_events);
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", "php.exe"));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", "php-cgi.exe"));
        Touch(Path.Combine(_paths.PhpBin, "7.4.33-nts-x64", "php.exe"));
        Touch(Path.Combine(_paths.PhpBin, "7.4.33-nts-x64", "php-cgi.exe"));
        Touch(Path.Combine(_paths.NginxBin, "1.30.6", "nginx.exe"));
        Touch(Path.Combine(_paths.ApacheBin, "2.4.69", "bin", "httpd.exe"));
        Touch(Path.Combine(_paths.DbBin("mariadb"), "11.4.13", "bin", "mariadbd.exe"));
        Touch(Path.Combine(_paths.DbBin("mariadb"), "11.4.13", "bin", "mariadb-admin.exe"));
        // Un datadir ya creado: Prepare no tiene que ejecutar mariadb-install-db.
        Directory.CreateDirectory(Path.Combine(_paths.Data, "mariadb", "11.4", "mysql"));
        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3407);
    }

    private void Config(int httpPort, int httpsPort, int dbPort, string server = "nginx") =>
        File.WriteAllText(_paths.ConfigFile,
            $"[php]\nversion = \"8.4\"\npool = 2\n\n[web]\nserver = \"{server}\"\nport = {httpPort}\nhttps_port = {httpsPort}\n\n[db]\nengine = \"mariadb\"\nversion = \"11.4\"\nport = {dbPort}\n");

    private void AddSite(string name, string? php = null)
    {
        var folder = Path.Combine(_paths.Www, name);
        Directory.CreateDirectory(folder);
        new SiteRegistry(_paths).Upsert(new Site { Name = name, Folder = folder, Php = php });
    }

    private void Log(string line)
    {
        lock (_log) _log.Add(line);
    }

    private bool Logged(Func<string, bool> match)
    {
        lock (_log) return _log.Any(match);
    }

    private StackService Service(IProcessRunner? runner = null, ExtensionSet? extensions = null, Func<ServiceDefinition, int?, string?>? reloadWeb = null) =>
        new(_paths, Log, extensions ?? ExtensionSet.Empty, new StackEnvironment(
            _launcher,
            runner ?? _runner,
            ports => ports.Where(_busy.Contains).ToList(),
            (_, _) => Task.FromResult(true),
            TimeSpan.FromSeconds(5),
            Ipv6: true,
            ReloadWeb: (web, pid) => Task.FromResult(reloadWeb is not null ? reloadWeb(web, pid) : FakeReload(runner ?? _runner, web, pid)),
            Connections: port => _connections(port)));

    /// <summary>Las conexiones establecidas a cada puerto (las peticiones de un php-cgi): ninguna, salvo que el test diga.</summary>
    private Func<int, IReadOnlyCollection<int>> _connections = _ => [];

    /// <summary>
    /// nginx -s reload con el runner falso (que lo apunta), sin esperar a los procesos hijos (los pid
    /// son de mentira); Apache, aquí: su evento no se puede probar sin un Apache de verdad.
    /// </summary>
    private string? FakeReload(IProcessRunner runner, ServiceDefinition web, int? pid)
    {
        if (web.Name != "apache") return WebReload.Signal(runner, web, pid!.Value);
        lock (_apacheReloads) _apacheReloads.Add((web.Name, pid));
        _events.Add("reload:apache");
        return null;
    }

    /// <summary>Como en la máquina de verdad: los puertos del stack en marcha están cogidos (por nosotros).</summary>
    private void HoldPorts(StatusResponse status)
    {
        _busy.Clear();
        foreach (var port in status.Ports!) _busy.Add(port);
    }

    private static string NginxConf(RoostPaths paths) => File.ReadAllText(Path.Combine(paths.Etc, "nginx", "nginx.conf"));

    private static string State(StatusResponse status, string service) => status.Services.Single(s => s.Name == service).State;

    private static int? Pid(StatusResponse status, string service) => status.Services.Single(s => s.Name == service).Pid;

    // E1: validar antes de parar

    [Fact]
    public async Task Up_ConElStackEnMarcha_UnPuertoOcupado_NoLoPara_YDevuelveLosFicherosGenerados()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], first.Status.Services.Select(s => s.Name));
        Assert.Equal([3407, 9840, 9841, 8765], first.Status.Ports);
        HoldPorts(first.Status);
        var myIni = File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini"));

        // El puerto nuevo lo tiene otro: el cambio se rechaza antes de parar nada.
        Config(httpPort: 9999, httpsPort: 8443, dbPort: 3408);
        _busy.Add(9999);
        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("No se ha aplicado el cambio: El puerto 9999", second.Error);
        Assert.Contains("Sigue en marcha lo de antes", second.Error);
        Assert.True(second.Status.Running);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Empty(_launcher.KillOrder());
        Assert.Equal(0, _launcher.StartsOf("mariadb-11.4-shutdown"));
        // Lo generado por el Prepare rechazado (puertos 9999 y 3408) ha vuelto a ser lo de antes.
        Assert.Contains("listen 127.0.0.1:8765;", NginxConf(_paths));
        Assert.Equal(myIni, File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini")));
        Assert.Equal(ServiceState.Running.ToString(), State(stack.Status(), "nginx"));
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_SinCambios_NoTocaNada_YSusPuertosNoCuentanComoOcupados()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        // La misma configuración: sus puertos son nuestros, y no hay nada que cambiar.
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Empty(second.Applied);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(0, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Empty(_launcher.KillOrder());
        Assert.Equal(0, _runner.Reloads);
        Assert.Equal(first.Status.Since, second.Status.Since);   // el stack no se ha parado
        Assert.True(Logged(l => l == "up: nada que cambiar en lo que esta en marcha"));
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_CambioDePuertoDeLaBaseDeDatos_SoloLaReinicia_LimpiaYConSuMyIniDeAntes()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        _launcher.OnStart = (name, _) =>
        {
            // La parada limpia lee el puerto del my.ini: tiene que ser el de la base de datos que corre (3407).
            if (name == "mariadb-11.4-shutdown")
                Assert.Contains("port = 3407", File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini")));
        };

        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3408);
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["reiniciado mariadb-11.4"], second.Applied);
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(2, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(3408, second.Status.DbPort);
        Assert.Equal(3408, second.Status.Services.Single(s => s.Name == "mariadb-11.4").Port);
        Assert.Contains("port = 3408", File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini")));   // y luego queda el nuevo
        // El resto, ni tocarlo.
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(0, _runner.Reloads);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], second.Status.Services.Select(s => s.Name));
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_UnVhostSinMarcas_NoLoPara()
    {
        AddSite("blog");
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        Assert.Contains(8443, first.Status.Ports!);
        HoldPorts(first.Status);
        var conf = Path.Combine(_paths.NginxSites, "blog.conf");
        File.WriteAllText(conf, "server { }\n");   // el usuario ha borrado las marcas

        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.Contains("roost:begin", second.Error);
        Assert.Contains("Sigue en marcha lo de antes", second.Error);
        Assert.True(second.Status.Running);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(0, _runner.Reloads);
        Assert.Equal("server { }\n", File.ReadAllText(conf));   // tal como lo dejó, para que lo arregle
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_SiLoQueCambiaNoArranca_ElRestoSigue_YElSiguienteUpLoReintenta()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        _launcher.Throws = "mariadb-11.4";   // lo único que no se puede saber antes: que lo nuevo arranque

        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3408);
        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("mariadb-11.4 no arranca", second.Error);
        Assert.Contains("El resto del cambio se ha aplicado y sigue en marcha", second.Error);
        Assert.Equal(["reiniciado mariadb-11.4"], second.Applied);
        Assert.True(second.Status.Running);
        Assert.Equal("Failed", State(second.Status, "mariadb-11.4"));
        Assert.Equal("Running", State(second.Status, "nginx"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));

        // Arreglado lo que la tiraba, el siguiente up la arranca (y solo a ella).
        _launcher.Throws = null;
        var third = await stack.UpAsync();

        Assert.True(third.Ok, third.Error);
        Assert.Equal(["arrancado mariadb-11.4 (no arrancaba)"], third.Applied);
        Assert.Equal("Running", State(third.Status, "mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Up_SinStackEnMarcha_UnPuertoOcupado_NoArrancaNada()
    {
        _busy.Add(8765);
        var stack = Service();

        var response = await stack.UpAsync();

        Assert.False(response.Ok);
        Assert.StartsWith("El puerto 8765", response.Error);   // sin «No se ha aplicado»: no había nada
        Assert.False(response.Status.Running);
        Assert.Empty(_launcher.StartOrder());
    }

    // E3: parar e iniciar un servicio suelto

    [Fact]
    public async Task Stop_DeUnPool_LoDejaParado_SinRelanzarlo_YStart_LoVuelveAArrancar()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);

        var stopped = await stack.StopServiceAsync("php-8.4");
        await Task.Delay(100);   // margen para que un relanzamiento indebido se viera

        Assert.True(stopped.Ok, stopped.Error);
        Assert.Equal("Stopped", State(stopped.Status, "php-8.4-0"));
        Assert.Equal("Stopped", State(stopped.Status, "php-8.4-1"));
        Assert.Equal("Running", State(stopped.Status, "nginx"));
        Assert.True(stopped.Status.Running);
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(["php-8.4-1", "php-8.4-0"], _launcher.KillOrder());

        var started = await stack.StartServiceAsync("php-8.4");

        Assert.True(started.Ok, started.Error);
        Assert.Equal("Running", State(started.Status, "php-8.4-0"));
        Assert.Equal(2, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(2, _launcher.StartsOf("php-8.4-1"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Stop_DeLaBaseDeDatos_LaParaLimpia_YUnNombreQueNoExiste_Falla()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);

        var stopped = await stack.StopServiceAsync("mariadb-11.4");
        await Task.Delay(100);

        Assert.True(stopped.Ok, stopped.Error);
        Assert.Equal("Stopped", State(stopped.Status, "mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Empty(_launcher.KillOrder());

        var unknown = await stack.StopServiceAsync("redis");
        Assert.False(unknown.Ok);
        Assert.Contains("'redis'", unknown.Error);

        // Un up vuelve a arrancarlo todo, también lo parado a mano (y solo eso).
        HoldPorts(stopped.Status);
        var up = await stack.UpAsync();
        Assert.True(up.Ok, up.Error);
        Assert.Equal(["arrancado mariadb-11.4 (estaba parado)"], up.Applied);
        Assert.Equal("Running", State(up.Status, "mariadb-11.4"));
        Assert.Equal(2, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task RestartAll_ParaEnOrdenInverso_YArrancaEnOrden_ConElMismoPlan()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        Assert.True((await stack.StopServiceAsync("php-8.4")).Ok);   // lo parado a mano también vuelve

        var response = await stack.RestartAllAsync();

        Assert.True(response.Ok, response.Error);
        Assert.All(response.Status.Services, s => Assert.Equal("Running", s.State));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(["php-8.4-1", "php-8.4-0", "nginx"], _launcher.KillOrder());   // php ya estaba parado; nginx, al reiniciar
        var second = _launcher.StartOrder().Where(n => !n.EndsWith("-shutdown", StringComparison.Ordinal)).Skip(4).ToList();
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], second);
    }

    // E6: renovar los certificados

    [Fact]
    public async Task RenewCertificates_RenuevaLosQueCaducanPronto_YRecargaElServidorWebSinReiniciarlo()
    {
        AddSite("blog");
        AddSite("tienda");
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        Assert.Equal(0, await stack.RenewCertificatesAsync());   // recién emitidos: nada que hacer
        Assert.Equal(0, _runner.Reloads);

        // El de blog caduca en 10 días (menos de los 30 de LocalCa.RenewBefore).
        var ca = new LocalCa(_paths);
        IssueShortLived(ca, new SiteRegistry(_paths).Find("blog")!, days: 10);
        var tiendaBefore = File.ReadAllText(ca.SiteCertFile("tienda"));

        Assert.Equal(1, await stack.RenewCertificatesAsync());

        using var renewed = X509Certificate2.CreateFromPem(File.ReadAllText(ca.SiteCertFile("blog")));
        Assert.True(renewed.NotAfter > DateTime.Now.AddDays(300));
        Assert.Equal(tiendaBefore, File.ReadAllText(ca.SiteCertFile("tienda")));   // el otro no se toca
        Assert.Equal(1, _runner.Reloads);                   // nginx -s reload: lo lee sin cortar
        Assert.Equal(1, _launcher.StartsOf("nginx"));      // y sin reiniciarlo
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.True(Logged(l => l.StartsWith("certificados renovados: blog.test", StringComparison.Ordinal)));

        Assert.Equal(0, await stack.RenewCertificatesAsync());   // y ya no hay nada que renovar
        Assert.Equal(1, _runner.Reloads);

        // El siguiente up ve que nginx ya tiene el certificado nuevo: no lo recarga otra vez.
        var up = await stack.UpAsync();
        Assert.True(up.Ok, up.Error);
        Assert.Empty(up.Applied);
    }

    [Fact]
    public async Task RenewCertificates_SinStackEnMarcha_NoHaceNada()
    {
        AddSite("blog");
        Assert.Equal(0, await Service().RenewCertificatesAsync());
        Assert.Empty(_launcher.StartOrder());
    }

    [Fact]
    public async Task Up_TrasUnCertRenew_RecargaElServidorWeb()
    {
        // roost cert renew reemite los certificados por su cuenta y luego pide un up.
        AddSite("blog");
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        var ca = new LocalCa(_paths);
        IssueShortLived(ca, new SiteRegistry(_paths).Find("blog")!, days: 200);   // otro, válido: Prepare no lo toca

        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["recargado nginx"], second.Applied);
        Assert.Equal(1, _runner.Reloads);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Se ha esperado demasiado a: {what}");
            await Task.Delay(20);
        }
    }

    /// <summary>Un certificado del sitio firmado por la CA de Roost que caduca en <paramref name="days"/> días.</summary>
    private static void IssueShortLived(LocalCa ca, Site site, int days)
    {
        using var root = ca.LoadOrCreate();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={site.Host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(site.Host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = request.Create(root, now.AddDays(-1), now.AddDays(days), RandomNumberGenerator.GetBytes(8));
        File.WriteAllText(ca.SiteCertFile(site.Name), cert.ExportCertificatePem());
        File.WriteAllText(ca.SiteKeyFile(site.Name), key.ExportPkcs8PrivateKeyPem());
    }

    // Servicios (oleada 3): Redis y Mailpit

    private void EnableRedisAndMail()
    {
        InstallRedisAndMail();
        File.AppendAllText(_paths.ConfigFile, "\n[redis]\nenabled = true\nport = 6380\n\n[mail]\nenabled = true\nsmtp_port = 1026\nweb_port = 8026\n");
    }

    private void InstallRedisAndMail()
    {
        Touch(Path.Combine(_paths.RedisBin, "8.10.2", "redis-server.exe"));
        Touch(Path.Combine(_paths.RedisBin, "8.10.2", "redis-cli.exe"));
        Touch(Path.Combine(_paths.MailpitBin, "1.32.0", "mailpit.exe"));
    }

    [Fact]
    public async Task Up_ConRedisYMailpit_ArrancanTrasLaBaseDeDatos_YRedisSeParaLimpio()
    {
        EnableRedisAndMail();
        var stack = Service();

        var up = await stack.UpAsync();

        Assert.True(up.Ok, up.Error);
        Assert.Equal(["mariadb-11.4", "redis", "mailpit", "php-8.4-0", "php-8.4-1", "nginx"], up.Status.Services.Select(s => s.Name));
        Assert.Equal([3407, 6380, 1026, 9840, 9841, 8765, 8026], up.Status.Ports);   // la bandeja (8026) también es nuestra
        Assert.True(Logged(l => l.Contains("Redis 8.10.2 en el 6380", StringComparison.Ordinal) && l.Contains("Mailpit 1.32.0", StringComparison.Ordinal)));

        await stack.DownAsync();

        // Al revés que el arranque; Redis y la base de datos, con su comando de parada.
        Assert.Equal(1, _launcher.StartsOf("redis-shutdown"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(["nginx", "php-8.4-1", "php-8.4-0", "mailpit"], _launcher.KillOrder());
    }

    [Fact]
    public async Task Up_ConElPuertoDeRedisOcupado_LoDiceYComoSalir()
    {
        EnableRedisAndMail();
        _busy.Add(6380);

        var response = await Service().UpAsync();

        Assert.False(response.Ok);
        Assert.StartsWith("El puerto 6380 (Redis) ya esta en uso", response.Error);
        Assert.Contains("roost redis port", response.Error);
        Assert.Contains("roost redis disable", response.Error);
    }

    [Fact]
    public async Task Up_ConLaBandejaDeMailpitOcupada_TambienSeRechazaAntesDeArrancar()
    {
        EnableRedisAndMail();
        _busy.Add(8026);

        var response = await Service().UpAsync();

        Assert.False(response.Ok);
        Assert.StartsWith("El puerto 8026 (Mailpit)", response.Error);
        Assert.Contains("[mail] web_port", response.Error);
        Assert.Empty(_launcher.StartOrder());
    }

    [Fact]
    public async Task Up_Rechazado_DevuelveTambienElRedisConfYElFragmentoDelCorreo()
    {
        EnableRedisAndMail();
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        var redisConf = File.ReadAllText(RedisConf.ConfPath(_paths));
        var mailIni = Path.Combine(_paths.Etc, "php", "conf.d", MailConf.PhpIniName);
        var mailText = File.ReadAllText(mailIni);

        // Otros puertos para Redis y el correo, y el de la web cogido por otro: se rechaza.
        Config(httpPort: 9999, httpsPort: 8443, dbPort: 3407);
        File.AppendAllText(_paths.ConfigFile, "\n[redis]\nenabled = true\nport = 6390\n\n[mail]\nenabled = true\nsmtp_port = 1036\nweb_port = 8036\n");
        _busy.Add(9999);
        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.Equal(redisConf, File.ReadAllText(RedisConf.ConfPath(_paths)));   // un Redis relanzado leería el puerto viejo
        Assert.Equal(mailText, File.ReadAllText(mailIni));
    }

    // Cambiar de servidor web con el stack en marcha: el -t no puede abrir puertos que son nuestros

    private const string OwnPortBind = "nginx: [emerg] bind() to 127.0.0.1:8765 failed (10013: An attempt was made to access a socket in a way forbidden by its access permissions)\nnginx: configuration file test failed";

    [Fact]
    public async Task Up_ElTestNoPuedeAbrirNuestrosPuertos_ParaSoloElServidorWeb_YValidaOtraVez()
    {
        // Lo que pasa al pasar de Apache a nginx: nginx -t abre el 8765 y Apache lo tiene en exclusiva.
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _runner.FailNext(OwnPortBind);

        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.True(second.Status.Running);
        Assert.Equal(2, _launcher.StartsOf("nginx"));
        Assert.True(Logged(l => l.Contains("se para antes de validar", StringComparison.Ordinal)));
        // La base de datos y PHP siguen: solo se ha parado el servidor web.
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], second.Status.Services.Select(s => s.Name));
    }

    [Fact]
    public async Task Up_ElTestNoPuedeAbrirNuestrosPuertos_YLoNuevoNoVale_VuelveElServidorWebDeAntes()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _runner.FailNext(OwnPortBind);
        _runner.FailNext("nginx: [emerg] unknown directive \"servr\" in sites/tienda.conf:3\nnginx: configuration file test failed");

        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("No se ha aplicado el cambio: nginx no acepta", second.Error);
        Assert.Contains("ya vuelve a estar en marcha", second.Error);
        Assert.True(second.Status.Running);
        Assert.Equal("Running", State(second.Status, "nginx"));
        Assert.Equal(2, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
    }

    [Fact]
    public async Task Up_ElTestNoPuedeAbrirUnPuertoAjeno_NoParaNada()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _runner.FailNext(OwnPortBind.Replace(":8765", ":8445", StringComparison.Ordinal));

        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("No se ha aplicado el cambio", second.Error);
        Assert.True(second.Status.Running);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Theory]
    [InlineData("bind() to 127.0.0.1:80 failed (10013: forbidden)", true)]
    [InlineData("bind() to [::1]:443 failed (10048: in use)\nbind() to 127.0.0.1:80 failed (10013: x)", true)]
    [InlineData("bind() to 127.0.0.1:80 failed (10013: x)\nbind() to 127.0.0.1:8080 failed (10013: x)", false)]   // uno no es nuestro
    [InlineData("bind() to 127.0.0.1:80 failed (10049: address not valid)", false)]
    [InlineData("unknown directive \"servr\" in sites/tienda.conf:3", false)]
    public void FailsOnlyOnOwnPorts_SoloSiTodosLosBindSonNuestrosPuertos(string message, bool expected) =>
        Assert.Equal(expected, StackService.FailsOnlyOnOwnPorts(message, [80, 443, 3407]));

    // E2: la recarga selectiva

    [Fact]
    public async Task Up_AnadirUnSitio_RecargaNginx_SinTocarLaBaseDeDatosNiPhp()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        var pids = first.Status.Services.ToDictionary(s => s.Name, s => s.Pid);

        AddSite("blog");
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["recargado nginx"], second.Applied);
        Assert.Equal(1, _runner.Reloads);
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Empty(_launcher.KillOrder());
        Assert.Equal(pids, second.Status.Services.ToDictionary(s => s.Name, s => s.Pid));   // los mismos procesos
        // roost status cuadra: el sitio, y el HTTPS entre los puertos del stack.
        Assert.Equal("https://blog.test:8443/", Assert.Single(second.Status.Sites).Url);
        Assert.Contains(8443, second.Status.Ports!);
        Assert.Contains("-s reload -p ", _runner.ReloadArguments.Single());   // con el mismo -p y -c que al arrancar
    }

    [Fact]
    public async Task Up_UnSitioConOtroPhp_ArrancaSuPoolAntesDeRecargar_YAlVolverAlGlobal_LoQuitaDespues()
    {
        AddSite("blog");
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        AddSite("blog", php: "7.4");
        _events.Clear();
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["recargado nginx", "arrancado php-7.4"], second.Applied);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "php-7.4-0", "php-7.4-1", "nginx"], second.Status.Services.Select(s => s.Name));
        Assert.Equal("7.4.33-nts-x64", Assert.Single(second.Status.Sites).Php);
        Assert.Contains(9740, second.Status.Ports!);
        // El pool nuevo, en marcha antes de que nginx le mande nada.
        Assert.Equal(["start:php-7.4-0", "start:php-7.4-1", "reload:nginx"], _events.All());
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));

        HoldPorts(second.Status);
        AddSite("blog");   // otra vez con el global
        _events.Clear();
        var third = await stack.UpAsync();

        Assert.True(third.Ok, third.Error);
        Assert.Equal(["recargado nginx", "quitado php-7.4"], third.Applied);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], third.Status.Services.Select(s => s.Name));
        // Se quita cuando nginx ya no le manda peticiones.
        Assert.Equal(["reload:nginx", "kill:php-7.4-1", "kill:php-7.4-0"], _events.All());
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
    }

    [Fact]
    public async Task Up_SiElPoolNuevoNoArranca_NoSeAplicaNada_YNginxNiSeRecarga()
    {
        AddSite("blog");
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        var conf = NginxConf(_paths);

        AddSite("blog", php: "7.4");
        _launcher.Throws = "php-7.4-0";
        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("No se ha aplicado el cambio: php-7.4-0 no arranca", second.Error);
        Assert.Contains("Sigue en marcha lo de antes", second.Error);
        Assert.True(second.Status.Running);
        Assert.Empty(second.Applied);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], second.Status.Services.Select(s => s.Name));
        Assert.Equal(0, _runner.Reloads);
        Assert.Equal(conf, NginxConf(_paths));   // sin el upstream de la 7.4: como estaba
        Assert.Equal("8.4.27-nts-x64", Assert.Single(second.Status.Sites).Php);
    }

    [Fact]
    public async Task Up_CambioEnElConfD_ReiniciaLosPhpCgiUnoAUno_SinTocarNadaMas()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        // El usuario cambia su conf.d (o un ajuste rápido reescribe el php.ini).
        File.AppendAllText(Path.Combine(_paths.Etc, "php", "conf.d", "99-usuario.ini"), "memory_limit = 1G\n");
        _events.Clear();
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["reiniciado php-8.4 (uno a uno)"], second.Applied);
        // Uno a uno: el segundo no se toca hasta que el primero ha vuelto.
        Assert.Equal(["kill:php-8.4-0", "start:php-8.4-0", "kill:php-8.4-1", "start:php-8.4-1"], _events.All());
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(0, _runner.Reloads);
        Assert.All(second.Status.Services, s => Assert.Equal("Running", s.State));
    }

    [Fact]
    public async Task Restart_DeUnPool_VaUnoAUno()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _events.Clear();

        var response = await stack.RestartAsync("php-8.4");

        Assert.True(response.Ok, response.Error);
        Assert.Equal(["kill:php-8.4-0", "start:php-8.4-0", "kill:php-8.4-1", "start:php-8.4-1"], _events.All());
        Assert.All(response.Status.Services, s => Assert.Equal("Running", s.State));
        Assert.Equal(1, _launcher.StartsOf("nginx"));

        // Y el siguiente up no lo vuelve a reiniciar: ya lee lo de ahora.
        var up = await stack.UpAsync();
        Assert.Empty(up.Applied);
    }

    [Fact]
    public async Task Restart_DeUnPool_AntesDeParaCadaUno_EsperaAQueAcabeLoQueTiene()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        // El segundo tiene dos peticiones (las que le pasó nginx al reiniciar el primero) que tardan un poco;
        // las que le lleguen después (otro puerto de origen) no cuentan.
        var calls = 0;
        _connections = port =>
        {
            if (port != 9841) return [];
            if (++calls < 5) return [50001, 50002];
            if (calls == 5) _events.Add("acabadas:9841");
            return [50003];
        };
        _events.Clear();

        var response = await stack.RestartAsync("php-8.4");

        Assert.True(response.Ok, response.Error);
        Assert.Equal(["kill:php-8.4-0", "start:php-8.4-0", "acabadas:9841", "kill:php-8.4-1", "start:php-8.4-1"], _events.All());
    }

    [Fact]
    public async Task Up_CambiarLaContrasenaDeRoot_NoReiniciaLaBaseDeDatos()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        // Va en el [client] del my.ini, que el servidor no lee.
        new DbPasswords(_paths).Set(new DbInstaller(_paths).ListInstalled().Single(), "secreta");
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Contains("password = \"secreta\"", File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini")));
        Assert.Empty(second.Applied);
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));

        // Lo del usuario.cnf, en cambio, sí es del servidor.
        File.AppendAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "usuario.cnf"), "max_allowed_packet = 256M\n");
        var third = await stack.UpAsync();
        Assert.Equal(["reiniciado mariadb-11.4"], third.Applied);
        Assert.Equal(2, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Up_ActivarMailpit_LoArranca_YReiniciaLosPoolsPorElCorreoDePhp()
    {
        InstallRedisAndMail();
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        File.AppendAllText(_paths.ConfigFile, "\n[mail]\nenabled = true\nsmtp_port = 1026\nweb_port = 8026\n");
        var second = await stack.UpAsync();

        // El mail() de PHP va a Mailpit por un .ini del conf.d: los php-cgi lo tienen que leer.
        Assert.True(second.Ok, second.Error);
        Assert.Equal(["arrancado mailpit", "reiniciado php-8.4 (uno a uno)"], second.Applied);
        Assert.Equal(["mariadb-11.4", "mailpit", "php-8.4-0", "php-8.4-1", "nginx"], second.Status.Services.Select(s => s.Name));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Up_OtroPuertoParaRedis_SoloReiniciaRedis_YLoParaConElPuertoDeAntes()
    {
        EnableRedisAndMail();
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        File.WriteAllText(_paths.ConfigFile, File.ReadAllText(_paths.ConfigFile).Replace("port = 6380", "port = 6390", StringComparison.Ordinal));
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["reiniciado redis"], second.Applied);
        Assert.Equal("-h 127.0.0.1 -p 6380 shutdown", _launcher.LastDefinition("redis-shutdown").Arguments);
        Assert.Equal(6390, second.Status.Services.Single(s => s.Name == "redis").Port);
        Assert.Equal(1, _launcher.StartsOf("mailpit"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Up_DeNginxAApache_SoloCambiaElServidorWeb()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3407, server: "apache");
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["cambiado nginx por apache"], second.Applied);
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "apache"], second.Status.Services.Select(s => s.Name));
        Assert.Equal("apache", second.Status.Web);
        Assert.Equal(["nginx"], _launcher.KillOrder());
        Assert.Equal(1, _launcher.StartsOf("apache"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
    }

    [Fact]
    public async Task Up_ConApache_AnadirUnSitio_LoRecargaConElPidDeSuPadre()
    {
        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3407, server: "apache");
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        AddSite("blog");
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["recargado apache"], second.Applied);
        Assert.Equal(("apache", Pid(first.Status, "apache")), Assert.Single(_apacheReloads));
        Assert.Equal(1, _launcher.StartsOf("apache"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
    }

    [Fact]
    public async Task Up_OtroPuertoHttp_ReiniciaSoloElServidorWeb()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        Config(httpPort: 8766, httpsPort: 8443, dbPort: 3407);
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(["reiniciado nginx"], second.Applied);
        Assert.Equal(8766, second.Status.Services.Single(s => s.Name == "nginx").Port);
        Assert.Equal(2, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
    }

    [Fact]
    public async Task Up_UnServicioDeExtensionQueNoCambia_NoSeToca_YSiCambia_SoloEse()
    {
        var extension = new FakeStackExtension(Path.Combine(_root, "memcached.exe"));
        var stack = Service(extensions: new ExtensionSet([extension]));
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        Assert.Contains("memcached", first.Status.Services.Select(s => s.Name));
        HoldPorts(first.Status);

        // Cada up le pide sus servicios, con un diccionario nuevo: con lo mismo dentro, es el mismo.
        AddSite("blog");
        var second = await stack.UpAsync();
        Assert.Equal(["recargado nginx"], second.Applied);
        Assert.Equal(1, _launcher.StartsOf("memcached"));

        extension.Arguments = "-m 128";
        var third = await stack.UpAsync();
        Assert.Equal(["reiniciado memcached"], third.Applied);
        Assert.Equal(2, _launcher.StartsOf("memcached"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
    }

    [Fact]
    public async Task Reload_DelServidorWeb_ValidaYRecargaEnCaliente_YSiNoValeNoLoToca()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);

        var reloaded = await stack.ReloadServiceAsync("nginx");

        Assert.True(reloaded.Ok, reloaded.Error);
        Assert.Equal(["recargado nginx"], reloaded.Applied);
        Assert.Equal(1, _runner.Reloads);
        Assert.Equal(1, _launcher.StartsOf("nginx"));

        // Con un vhost roto, el -t falla: nginx sigue con la configuración que tenía.
        _runner.FailNext("nginx: [emerg] unknown directive \"servr\" in sites/tienda.conf:3\nnginx: configuration file test failed");
        var rejected = await stack.ReloadServiceAsync("nginx");

        Assert.False(rejected.Ok);
        Assert.StartsWith("nginx no acepta la configuracion, asi que sigue con la que tenia", rejected.Error);
        Assert.Contains("servr", rejected.Error);
        Assert.Equal(1, _runner.Reloads);
        Assert.Equal("Running", State(rejected.Status, "nginx"));
    }

    [Fact]
    public async Task Reload_SiNoSePuedeRecargarEnCaliente_LoReinicia()
    {
        var stack = Service(reloadWeb: (_, _) => "nginx: [error] OpenEvent(\"Global\\ngx_reload_1\") failed");
        Assert.True((await stack.UpAsync()).Ok);

        var response = await stack.ReloadServiceAsync("nginx");

        Assert.True(response.Ok, response.Error);
        Assert.StartsWith("reiniciado nginx (no se pudo recargar", Assert.Single(response.Applied));
        await UntilAsync(() => _launcher.StartsOf("nginx") == 2, "el relanzamiento de nginx");
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
    }

    [Fact]
    public async Task Reload_DeOtroServicio_EsUnReinicio_YElDeUnPool_UnoAUno()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _events.Clear();

        var pool = await stack.ReloadServiceAsync("php-8.4");

        Assert.True(pool.Ok, pool.Error);
        Assert.Equal(["kill:php-8.4-0", "start:php-8.4-0", "kill:php-8.4-1", "start:php-8.4-1"], _events.All());
        var unknown = await stack.ReloadServiceAsync("redis");
        Assert.False(unknown.Ok);
        Assert.Contains("'redis'", unknown.Error);
    }

    // Dobles

    /// <summary>Lo que pasa, en orden, entre el lanzador y el runner: «start:nginx», «kill:php-8.4-0», «reload:nginx».</summary>
    private sealed class Events
    {
        private readonly List<string> _all = [];

        public void Add(string e) { lock (_all) _all.Add(e); }

        public void Clear() { lock (_all) _all.Clear(); }

        /// <summary>Sin los comandos de parada.</summary>
        public List<string> All() { lock (_all) return _all.Where(e => !e.EndsWith("-shutdown", StringComparison.Ordinal)).ToList(); }
    }

    /// <summary>
    /// nginx -t (y httpd -t) falso que falla las veces que se le diga y luego da por buena la
    /// configuración; nginx -s reload se apunta y va bien.
    /// </summary>
    private sealed class FakeRunner(Events events) : IProcessRunner
    {
        private readonly Queue<string> _failures = new();
        private readonly List<string> _reloads = [];

        public void FailNext(string output) { lock (_failures) _failures.Enqueue(output); }

        public int Reloads { get { lock (_failures) return _reloads.Count; } }

        public List<string> ReloadArguments { get { lock (_failures) return [.. _reloads]; } }

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            lock (_failures)
            {
                if (arguments.StartsWith("-s reload ", StringComparison.Ordinal))
                {
                    _reloads.Add(arguments);
                    events.Add("reload:nginx");
                    return "";
                }
                return _failures.Count > 0 ? _failures.Dequeue() : "nginx: configuration file test is successful\nSyntax OK";
            }
        }
    }

    /// <summary>Una extensión con un servicio: un diccionario de entorno nuevo en cada up, como haría una de verdad.</summary>
    private sealed class FakeStackExtension(string executable) : IStackExtension
    {
        public string Id => "memcached-prueba";
        public string Name => "Memcached de prueba";
        public string Version => "1.0";
        public string Arguments { get; set; } = "-m 64";

        public IEnumerable<ServiceDefinition> Services(StackContext context) =>
        [
            new ServiceDefinition
            {
                Name = "memcached",
                Executable = executable,
                Arguments = Arguments,
                Environment = new Dictionary<string, string> { ["MEMCACHED_X"] = "1" },
                Port = 11211,
            },
        ];
    }

    private sealed class FakeProcess(int id, Action<FakeProcess> onKill) : IServiceProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;
        public int? ExitCode { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);

        public void Exit(int code = 0)
        {
            ExitCode = code;
            _exit.TrySetResult();
        }

        public void Kill()
        {
            onKill(this);
            Exit(-1);
        }

        public void Dispose() { }
    }

    /// <summary>Como el de SupervisorTests: un comando «X-shutdown» hace que X salga solo (mariadb-admin shutdown).</summary>
    private sealed class FakeLauncher(Events events) : IProcessLauncher
    {
        private readonly object _lock = new();
        private int _nextId = 100;
        private readonly List<(string Name, FakeProcess Process)> _started = [];
        private readonly List<string> _killed = [];
        private readonly List<ServiceDefinition> _definitions = [];

        public string? Throws { get; set; }

        /// <summary>Se llama al arrancar cada proceso, con su nombre y su definición (para mirar el disco en ese momento).</summary>
        public Action<string, ServiceDefinition>? OnStart { get; set; }

        public IServiceProcess Start(ServiceDefinition definition)
        {
            if (definition.Name == Throws) throw new FileNotFoundException("no existe");
            OnStart?.Invoke(definition.Name, definition);
            lock (_lock)
            {
                var process = new FakeProcess(_nextId++, _ =>
                {
                    lock (_lock) _killed.Add(definition.Name);
                    events.Add("kill:" + definition.Name);
                });
                _started.Add((definition.Name, process));
                _definitions.Add(definition);
                events.Add("start:" + definition.Name);
                if (definition.Name.EndsWith("-shutdown", StringComparison.Ordinal))
                {
                    var service = definition.Name[..^"-shutdown".Length];
                    _started.Last(s => s.Name == service).Process.Exit(0);
                    process.Exit(0);
                }
                return process;
            }
        }

        public int StartsOf(string name) { lock (_lock) return _started.Count(s => s.Name == name); }
        public List<string> StartOrder() { lock (_lock) return _started.Select(s => s.Name).ToList(); }
        public List<string> KillOrder() { lock (_lock) return [.. _killed]; }
        public ServiceDefinition LastDefinition(string name) { lock (_lock) return _definitions.Last(d => d.Name == name); }
    }

    private static void Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Sitios avanzados (oleada 6): el .roost.toml y las carpetas aparcadas en el daemon.

    [Fact]
    public async Task Up_PoneAlDiaLosSitiosConSuRoostToml_YConLasCarpetasAparcadas()
    {
        AddSite("blog");
        File.WriteAllText(ProjectFile.PathIn(Path.Combine(_paths.Www, "blog")), "php = \"7.4\"\n");
        var parked = Path.Combine(_root, "proyectos");
        Directory.CreateDirectory(Path.Combine(parked, "alfa"));
        Directory.CreateDirectory(Path.Combine(parked, "Mal Nombre"));
        new ParkedFolders(_paths).Add(parked);

        var up = await Service().UpAsync();

        Assert.True(up.Ok, up.Error);
        Assert.Contains("php-7.4-0", up.Status.Services.Select(s => s.Name));
        Assert.Equal(["alfa", "blog"], up.Status.Sites.Select(s => s.Name));
        Assert.Equal("7.4", new SiteRegistry(_paths).Find("blog")!.Php);
        Assert.True(Logged(l => l == ".roost.toml: blog.test pasa a PHP 7.4"));
        Assert.True(Logged(l => l.StartsWith("aparcado: alfa.test nuevo", StringComparison.Ordinal)));
        Assert.True(Logged(l => l.StartsWith("aparcado: se salta", StringComparison.Ordinal) && l.Contains("Mal Nombre")));
        Assert.True(Logged(l => l.StartsWith("faltan en el hosts: alfa.test", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ParkedChanged_ConElStackEnMarcha_SoloRecargaElServidorWeb_YParadoNoHaceNada()
    {
        var parked = Path.Combine(_root, "proyectos");
        Directory.CreateDirectory(Path.Combine(parked, "alfa"));
        new ParkedFolders(_paths).Add(parked);
        var stack = Service();

        // Parado: lo hará el siguiente up (o la app).
        Directory.CreateDirectory(Path.Combine(parked, "beta"));
        Assert.False(await stack.ParkedChangedAsync());
        Assert.Null(new SiteRegistry(_paths).Find("beta"));

        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        Assert.Equal(["alfa", "beta"], first.Status.Sites.Select(s => s.Name));
        Assert.False(await stack.ParkedChangedAsync());   // nada nuevo: ni recarga

        Directory.CreateDirectory(Path.Combine(parked, "gamma"));
        Directory.Delete(Path.Combine(parked, "alfa"));
        Assert.True(await stack.ParkedChangedAsync());

        Assert.Equal(["beta", "gamma"], stack.Status().Sites.Select(s => s.Name));
        Assert.Equal(1, _runner.Reloads);                // nginx -s reload, sin cortar
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.True(File.Exists(Path.Combine(_paths.NginxSites, "alfa.conf.removed")));
        Assert.True(Logged(l => l.StartsWith("aparcado: gamma.test nuevo", StringComparison.Ordinal)));
        Assert.True(Logged(l => l == "aparcado: alfa.test quitado (su carpeta ya no esta)"));
    }
}
