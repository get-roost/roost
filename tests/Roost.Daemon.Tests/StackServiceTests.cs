using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Sites;
using Roost.Daemon;
using Roost.Extensibility;

namespace Roost.Daemon.Tests;

/// <summary>
/// Testea el stack del daemon (<see cref="StackService"/>) sin arrancar nada de verdad: una raíz
/// de Roost en una carpeta temporal con "binarios" vacíos (PHP 8.4, nginx, MariaDB con su datadir),
/// un lanzador falso cuyos procesos son <c>TaskCompletionSource</c>, un <c>nginx -t</c> que siempre
/// da por buena la configuración, y sondas de puertos que dicen lo que el test quiera. Nada toca
/// la red, el hosts ni el almacén de certificados.
/// </summary>
public class StackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-daemon-" + Guid.NewGuid().ToString("N"));
    private readonly RoostPaths _paths;
    private readonly FakeLauncher _launcher = new();
    private readonly HashSet<int> _busy = [];
    private readonly List<string> _log = [];

    public StackServiceTests()
    {
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", "php.exe"));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", "php-cgi.exe"));
        Touch(Path.Combine(_paths.NginxBin, "1.30.6", "nginx.exe"));
        Touch(Path.Combine(_paths.DbBin("mariadb"), "11.4.13", "bin", "mariadbd.exe"));
        Touch(Path.Combine(_paths.DbBin("mariadb"), "11.4.13", "bin", "mariadb-admin.exe"));
        // Un datadir ya creado: Prepare no tiene que ejecutar mariadb-install-db.
        Directory.CreateDirectory(Path.Combine(_paths.Data, "mariadb", "11.4", "mysql"));
        Config(httpPort: 8765, httpsPort: 8443, dbPort: 3407);
    }

    private void Config(int httpPort, int httpsPort, int dbPort) =>
        File.WriteAllText(_paths.ConfigFile,
            $"[php]\nversion = \"8.4\"\npool = 2\n\n[web]\nport = {httpPort}\nhttps_port = {httpsPort}\n\n[db]\nengine = \"mariadb\"\nversion = \"11.4\"\nport = {dbPort}\n");

    private void AddSite(string name)
    {
        var folder = Path.Combine(_paths.Www, name);
        Directory.CreateDirectory(folder);
        new SiteRegistry(_paths).Upsert(new Site { Name = name, Folder = folder });
    }

    private StackService Service() => new(_paths, _log.Add, ExtensionSet.Empty, new StackEnvironment(
        _launcher,
        new NginxTestOk(),
        ports => ports.Where(_busy.Contains).ToList(),
        (_, _) => Task.FromResult(true),
        TimeSpan.FromSeconds(5),
        Ipv6: true));

    /// <summary>Como en la máquina de verdad: los puertos del stack en marcha están cogidos (por nosotros).</summary>
    private void HoldPorts(StatusResponse status)
    {
        _busy.Clear();
        foreach (var port in status.Ports!) _busy.Add(port);
    }

    private static string NginxConf(RoostPaths paths) => File.ReadAllText(Path.Combine(paths.Etc, "nginx", "nginx.conf"));

    private static string State(StatusResponse status, string service) => status.Services.Single(s => s.Name == service).State;

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
    public async Task Up_ConElStackEnMarcha_LosPuertosPropiosNoCuentanComoOcupados_YSeAplicaElCambio()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);

        // La misma configuración (un php use, por ejemplo): sus puertos son nuestros y se liberan al parar.
        var second = await stack.UpAsync();

        Assert.True(second.Ok, second.Error);
        Assert.Equal(2, _launcher.StartsOf("nginx"));
        // Lo de antes se paró como siempre: la base de datos limpia, y antes de arrancar lo nuevo.
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(["nginx", "php-8.4-1", "php-8.4-0"], _launcher.KillOrder());
        Assert.Equal(["mariadb-11.4", "php-8.4-0", "php-8.4-1", "nginx"], _launcher.StartOrder().Where(n => !n.EndsWith("-shutdown", StringComparison.Ordinal)).Skip(4));
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_CambioDePuertoDeLaBaseDeDatos_LaParaLimpiaConSuMyIniDeAntes()
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
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4-shutdown"));
        Assert.Equal(3408, second.Status.DbPort);
        Assert.Contains("port = 3408", File.ReadAllText(Path.Combine(_paths.Etc, "mariadb", "11.4", "my.ini")));   // y luego queda el nuevo
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
        Assert.Equal("server { }\n", File.ReadAllText(conf));   // tal como lo dejó, para que lo arregle
    }

    [Fact]
    public async Task Up_ConElStackEnMarcha_SiLoNuevoNoArranca_QuedaParado_YLoDice()
    {
        var stack = Service();
        var first = await stack.UpAsync();
        Assert.True(first.Ok, first.Error);
        HoldPorts(first.Status);
        _launcher.Throws = "nginx";   // lo único que no se puede saber antes de parar

        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.StartsWith("nginx no arranca", second.Error);
        Assert.Contains("el stack ha quedado parado", second.Error);
        Assert.False(second.Status.Running);
        Assert.Contains("stack parado", _log);
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

        // Un up vuelve a arrancarlo todo, también lo parado a mano.
        HoldPorts(stopped.Status);
        var up = await stack.UpAsync();
        Assert.True(up.Ok, up.Error);
        Assert.Equal("Running", State(up.Status, "mariadb-11.4"));
        Assert.Equal(2, _launcher.StartsOf("mariadb-11.4"));
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
    public async Task RenewCertificates_RenuevaLosQueCaducanPronto_YReiniciaSoloElServidorWeb()
    {
        AddSite("blog");
        AddSite("tienda");
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        Assert.Equal(0, await stack.RenewCertificatesAsync());   // recién emitidos: nada que hacer
        Assert.Equal(1, _launcher.StartsOf("nginx"));

        // El de blog caduca en 10 días (menos de los 30 de LocalCa.RenewBefore).
        var ca = new LocalCa(_paths);
        IssueShortLived(ca, new SiteRegistry(_paths).Find("blog")!, days: 10);
        var tiendaBefore = File.ReadAllText(ca.SiteCertFile("tienda"));

        Assert.Equal(1, await stack.RenewCertificatesAsync());

        using var renewed = X509Certificate2.CreateFromPem(File.ReadAllText(ca.SiteCertFile("blog")));
        Assert.True(renewed.NotAfter > DateTime.Now.AddDays(300));
        Assert.Equal(tiendaBefore, File.ReadAllText(ca.SiteCertFile("tienda")));   // el otro no se toca
        // RestartAsync mata nginx y el bucle del supervisor lo relanza por su cuenta: se espera a verlo.
        await UntilAsync(() => _launcher.StartsOf("nginx") == 2, "el relanzamiento de nginx");
        Assert.Equal(2, _launcher.StartsOf("nginx"));      // solo el servidor web
        Assert.Equal(1, _launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, _launcher.StartsOf("mariadb-11.4"));
        Assert.Contains(_log, l => l.StartsWith("certificados renovados: blog.test", StringComparison.Ordinal));

        Assert.Equal(0, await stack.RenewCertificatesAsync());   // y ya no hay nada que renovar
        Assert.Equal(2, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task RenewCertificates_SinStackEnMarcha_NoHaceNada()
    {
        AddSite("blog");
        Assert.Equal(0, await Service().RenewCertificatesAsync());
        Assert.Empty(_launcher.StartOrder());
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
        Touch(Path.Combine(_paths.RedisBin, "8.10.2", "redis-server.exe"));
        Touch(Path.Combine(_paths.RedisBin, "8.10.2", "redis-cli.exe"));
        Touch(Path.Combine(_paths.MailpitBin, "1.32.0", "mailpit.exe"));
        File.AppendAllText(_paths.ConfigFile, "\n[redis]\nenabled = true\nport = 6380\n\n[mail]\nenabled = true\nsmtp_port = 1026\nweb_port = 8026\n");
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
        Assert.Contains(_log, l => l.Contains("Redis 8.10.2 en el 6380", StringComparison.Ordinal) && l.Contains("Mailpit 1.32.0", StringComparison.Ordinal));

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
        var redisConf = File.ReadAllText(Roost.Core.Stack.RedisConf.ConfPath(_paths));
        var mailIni = Path.Combine(_paths.Etc, "php", "conf.d", Roost.Core.Stack.MailConf.PhpIniName);
        var mailText = File.ReadAllText(mailIni);

        // Otros puertos para Redis y el correo, y el de la web cogido por otro: se rechaza.
        Config(httpPort: 9999, httpsPort: 8443, dbPort: 3407);
        File.AppendAllText(_paths.ConfigFile, "\n[redis]\nenabled = true\nport = 6390\n\n[mail]\nenabled = true\nsmtp_port = 1036\nweb_port = 8036\n");
        _busy.Add(9999);
        var second = await stack.UpAsync();

        Assert.False(second.Ok);
        Assert.Equal(redisConf, File.ReadAllText(Roost.Core.Stack.RedisConf.ConfPath(_paths)));   // un Redis relanzado leería el puerto viejo
        Assert.Equal(mailText, File.ReadAllText(mailIni));
    }

    // Dobles

    /// <summary>nginx -t falso: siempre da por buena la configuración.</summary>
    private sealed class NginxTestOk : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "nginx: configuration file test is successful";
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
    private sealed class FakeLauncher : IProcessLauncher
    {
        private readonly object _lock = new();
        private int _nextId = 100;
        private readonly List<(string Name, FakeProcess Process)> _started = [];
        private readonly List<string> _killed = [];

        public string? Throws { get; set; }

        /// <summary>Se llama al arrancar cada proceso, con su nombre y su definición (para mirar el disco en ese momento).</summary>
        public Action<string, ServiceDefinition>? OnStart { get; set; }

        public IServiceProcess Start(ServiceDefinition definition)
        {
            if (definition.Name == Throws) throw new FileNotFoundException("no existe");
            OnStart?.Invoke(definition.Name, definition);
            lock (_lock)
            {
                var process = new FakeProcess(_nextId++, _ => { lock (_lock) _killed.Add(definition.Name); });
                _started.Add((definition.Name, process));
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
}
