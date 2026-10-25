using System.Net;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Share;
using Roost.Core.Sites;
using Roost.Daemon;
using Roost.Extensibility;

namespace Roost.Daemon.Tests;

/// <summary>
/// Compartir (oleada 6) en el daemon: el túnel y la red local sobre un stack de mentira, como en
/// <c>StackServiceTests</c> (procesos que son <c>TaskCompletionSource</c>, un <c>nginx -t</c> que da
/// por buena la configuración y un <c>nginx -s reload</c> que se apunta), con un cloudflared de pega
/// que escribe lo que escribiría el de verdad y espera a que lo paren. Nada va a la red, nada escucha
/// en la red local y nadie toca el cortafuegos.
/// </summary>
public class ShareServiceTests : IDisposable
{
    private const string TunnelUrl = "https://alpha-beta-gamma-delta.trycloudflare.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-share-" + Guid.NewGuid().ToString("N"));
    private readonly RoostPaths _paths;
    private readonly Launcher _launcher = new();
    private readonly NginxRunner _runner = new();
    private readonly TunnelRunner _tunnels = new();
    private readonly List<string> _log = [];
    private readonly HashSet<string> _localAddresses = ["192.168.1.20"];
    private readonly HashSet<(string, int)> _bound = [];
    private int _nextPort = 49731;

    public ShareServiceTests()
    {
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", OsNames.Exe("php")));
        Touch(Path.Combine(_paths.PhpBin, "8.4.27-nts-x64", OsNames.Exe("php-cgi")));
        Touch(Path.Combine(_paths.NginxBin, "1.30.6", OsNames.Exe("nginx")));
        Touch(Path.Combine(_paths.ApacheBin, "2.4.69", "bin", "httpd.exe"));
        Touch(Path.Combine(_paths.CloudflaredBin, "2026.10.3", OsNames.Exe("cloudflared")));
        Config("nginx");
        AddSite("tienda");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temporal */ }
    }

    private void Config(string server) =>
        File.WriteAllText(_paths.ConfigFile,
            $"[php]\nversion = \"8.4\"\npool = 1\n\n[web]\nserver = \"{server}\"\nport = 8765\nhttps_port = 8443\n\n[db]\nengine = \"none\"\n");

    private void AddSite(string name)
    {
        var folder = Path.Combine(_paths.Www, name);
        Directory.CreateDirectory(Path.Combine(folder, "public"));
        new SiteRegistry(_paths).Upsert(new Site { Name = name, Folder = folder, Root = "public" });
    }

    private StackService Service() =>
        new(_paths, line => { lock (_log) _log.Add(line); }, ExtensionSet.Empty, new StackEnvironment(
            _launcher,
            _runner,
            _ => [],
            (_, _) => Task.FromResult(true),
            TimeSpan.FromSeconds(5),
            Ipv6: false,
            ReloadWeb: (web, pid) => Task.FromResult(web.Name == "nginx" ? WebReload.Signal(_runner, web, pid!.Value) : _runner.ApacheReload()),
            Share: new ShareEnvironment(
                _tunnels,
                () => _nextPort++,
                (ip, port) => !_bound.Contains((ip.ToString(), port)),
                ip => _localAddresses.Contains(ip.ToString()),
                UrlTimeout: TimeSpan.FromSeconds(5),
                ConnectTimeout: TimeSpan.FromSeconds(2))));

    private string ShareConf(string file, string server = "nginx") => File.ReadAllText(Path.Combine(_paths.Etc, server, "share", file));

    private bool ShareConfExists(string file, string server = "nginx") => File.Exists(Path.Combine(_paths.Etc, server, "share", file));

    private static string Quick(string url) => $"""
        2026-10-24T05:32:17Z INF Thank you for trying Cloudflare Tunnel. Doing so, without a Cloudflare account, is a quick way to experiment and try it out. However, be aware that these account-less Tunnels have no uptime guarantee, are subject to the Cloudflare Online Services Terms of Use (https://www.cloudflare.com/website-terms/), and Cloudflare reserves the right to investigate your use of Tunnels for violations of such terms.
        2026-10-24T05:32:17Z INF Requesting new quick Tunnel on trycloudflare.com...
        2026-10-24T05:32:19Z INF +--------------------------------------------------------------------------------------------+
        2026-10-24T05:32:19Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |
        2026-10-24T05:32:19Z INF |  {url}                                         |
        2026-10-24T05:32:19Z INF +--------------------------------------------------------------------------------------------+
        2026-10-24T05:32:20Z INF Registered tunnel connection connIndex=0 connection=8c1f event=0 ip=198.41.192.7 location=mad01 protocol=quic
        """;

    // El túnel

    [Fact]
    public async Task Tunel_AbreSuEscuchaEnLoopback_RecargaNginxSinReiniciarlo_YDaLaUrl()
    {
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _tunnels.Next(Quick(TunnelUrl));

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.True(response.Ok, response.Error);
        Assert.Equal(TunnelUrl, response.Share!.Url);
        Assert.Equal("tunnel", response.Share.Kind);
        Assert.Equal("127.0.0.1", response.Share.Address);
        Assert.Equal(49731, response.Share.Port);
        Assert.True(response.Share.Connected);
        Assert.Contains("recargado nginx", response.Applied);
        // E2: abrir la escucha recarga nginx en caliente; no lo reinicia.
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Equal(1, _runner.Reloads);

        var conf = ShareConf("tienda-tunnel.conf");
        Assert.Contains("listen 127.0.0.1:49731;", conf);
        Assert.DoesNotContain(" ssl;", conf);
        Assert.Contains("server_name tienda.test;", conf);
        Assert.Contains("/www/tienda/public\"", conf);
        Assert.Contains("fastcgi_pass php-8.4;", conf);
        Assert.Contains("fastcgi_param HTTPS on;", conf);
        Assert.Contains("fastcgi_param REMOTE_ADDR $http_cf_connecting_ip if_not_empty;", conf);
        Assert.Contains("X-Robots-Tag \"noindex, nofollow\"", conf);
        Assert.Contains("/share/*.conf\";", File.ReadAllText(Path.Combine(_paths.Etc, "nginx", "nginx.conf")));
        Assert.Contains(49731, stack.Status().Ports!);

        // cloudflared, aislado de lo del usuario y hacia la escucha propia.
        var spec = _tunnels.Specs.Single();
        Assert.EndsWith(Path.Combine("cloudflared", "2026.10.3", OsNames.Exe("cloudflared")), spec.Exe);
        var args = string.Join(' ', spec.Arguments);
        Assert.StartsWith("tunnel --config ", args);
        Assert.Contains("--no-autoupdate", args);
        Assert.EndsWith("--url http://127.0.0.1:49731", args);
        Assert.True(File.Exists(QuickTunnel.ConfigFile(_paths)));
        Assert.True(spec.Environment!.ContainsKey("NO_TLS_VERIFY") && spec.Environment["NO_TLS_VERIFY"] is null);

        var listed = Assert.Single(stack.Shares().Shares);
        Assert.Equal(TunnelUrl, listed.Url);
        Assert.Contains("Your quick Tunnel has been created", ReadShared(QuickTunnel.LogFile(_paths, "tienda")));
    }

    /// <summary>Como lo lee la pantalla Registros: sin bloquear a cloudflared, que sigue escribiendo.</summary>
    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Tunel_DosVeces_DevuelveElMismo_SinOtroCloudflared()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");

        var again = await stack.ShareTunnelAsync("tienda");

        Assert.True(again.Ok);
        Assert.Equal(TunnelUrl, again.Share!.Url);
        Assert.Single(_tunnels.Specs);
    }

    [Fact]
    public async Task Tunel_DejarDeCompartir_ParaCloudflared_QuitaLaEscucha_YAvisaAQuienEspera()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");
        var hold = stack.HoldShareAsync("tienda", ShareKind.Tunnel, CancellationToken.None);

        var stop = await stack.StopShareAsync("tienda", ShareKind.Tunnel);

        Assert.True(stop.Ok, stop.Error);
        Assert.True(_tunnels.Cancelled);
        Assert.False(ShareConfExists("tienda-tunnel.conf"));
        Assert.Equal(2, _runner.Reloads);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
        Assert.Empty(stack.Shares().Shares);
        var ended = await hold.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Se ha dejado de compartir.", ended.Error);
        Assert.DoesNotContain(49731, stack.Status().Ports!);
    }

    [Fact]
    public async Task Tunel_SinUrl_NoComparte_DiceElMotivo_YQuitaLaEscucha()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next("""
            2026-10-24T05:32:17Z INF Requesting new quick Tunnel on trycloudflare.com...
            failed to request quick Tunnel: Post "https://api.trycloudflare.com/tunnel": dial tcp: lookup api.trycloudflare.com: no such host
            """, exitCode: 1);

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.False(response.Ok);
        Assert.StartsWith("No se ha podido hablar con trycloudflare.com", response.Error);
        Assert.Contains("no such host", response.Error);
        Assert.False(ShareConfExists("tienda-tunnel.conf"));
        Assert.Empty(stack.Shares().Shares);
        Assert.Equal(1, _launcher.StartsOf("nginx"));
    }

    [Fact]
    public async Task Tunel_DemasiadosSeguidos_LoDice()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next("failed to request quick Tunnel: quick tunnel provisioning failed with status 429", exitCode: 1);

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.False(response.Ok);
        Assert.StartsWith("Cloudflare no da mas tuneles rapidos ahora mismo", response.Error);
    }

    [Fact]
    public async Task Tunel_SiCloudflaredSeCierraSolo_DejaDeCompartir_YLoDice()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl), exitCode: 1, exitAfterLines: true,
            extra: "2026-10-24T05:40:00Z ERR Connection terminated error=\"there are no free edge addresses left to resolve to\"");
        var response = await stack.ShareTunnelAsync("tienda");
        Assert.True(response.Ok, response.Error);
        var hold = stack.HoldShareAsync("tienda", ShareKind.Tunnel, CancellationToken.None);

        _tunnels.ExitNow();
        var ended = await hold.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith("El tunel se ha cerrado: Connection terminated error=", ended.Error);
        await WaitUntil(() => !ShareConfExists("tienda-tunnel.conf"));
        Assert.Empty(stack.Shares().Shares);
    }

    [Fact]
    public async Task Hold_SiQuienLoPidioSeVa_DejaDeCompartir()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");
        using var client = new CancellationTokenSource();

        var hold = stack.HoldShareAsync("tienda", ShareKind.Tunnel, client.Token);
        client.Cancel();   // un Ctrl+C, la terminal cerrada o la app que sale
        await hold.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_tunnels.Cancelled);
        Assert.Empty(stack.Shares().Shares);
        Assert.False(ShareConfExists("tienda-tunnel.conf"));
    }

    [Fact]
    public async Task Down_ParaTodoLoCompartido()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");
        var hold = stack.HoldShareAsync("tienda", ShareKind.Tunnel, CancellationToken.None);

        await stack.DownAsync();

        Assert.True(_tunnels.Cancelled);
        Assert.Empty(stack.Shares().Shares);
        Assert.Equal("Se ha parado Roost (roost down).", (await hold.WaitAsync(TimeSpan.FromSeconds(5))).Error);

        // Un up después no vuelve a abrir nada: lo compartido no se guarda en disco.
        Assert.True((await stack.UpAsync()).Ok);
        Assert.False(ShareConfExists("tienda-tunnel.conf"));
        Assert.Single(_tunnels.Specs);
    }

    [Fact]
    public async Task Up_ConAlgoCompartido_LoConserva_SinRecargarDeMas()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");

        var up = await stack.UpAsync();

        Assert.True(up.Ok, up.Error);
        Assert.Empty(up.Applied);
        Assert.Equal(1, _runner.Reloads);
        Assert.True(ShareConfExists("tienda-tunnel.conf"));
        Assert.Single(stack.Shares().Shares);
    }

    [Fact]
    public async Task QuitarElSitio_ElSiguienteUp_DejaDeCompartirlo()
    {
        var stack = Service();
        AddSite("blog");
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        await stack.ShareTunnelAsync("tienda");

        new SiteRegistry(_paths).Remove("tienda");
        var up = await stack.UpAsync();

        Assert.True(up.Ok, up.Error);
        Assert.True(_tunnels.Cancelled);
        Assert.Empty(stack.Shares().Shares);
        Assert.False(ShareConfExists("tienda-tunnel.conf"));
    }

    [Fact]
    public async Task SinElStackEnMarcha_NoSeComparte()
    {
        var stack = Service();

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.False(response.Ok);
        Assert.StartsWith("El stack no esta en marcha", response.Error);
        Assert.Empty(_tunnels.Specs);
    }

    [Fact]
    public async Task UnSitioQueNoEsta_NoSeComparte()
    {
        var stack = Service();
        await stack.UpAsync();

        var response = await stack.ShareTunnelAsync("otro");

        Assert.False(response.Ok);
        Assert.Contains("otro no esta en el stack", response.Error);
    }

    [Fact]
    public async Task SinCloudflared_LoDice_SinTocarNada()
    {
        Directory.Delete(_paths.CloudflaredBin, recursive: true);
        var stack = Service();
        await stack.UpAsync();

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.False(response.Ok);
        Assert.StartsWith("cloudflared no esta instalado", response.Error);
        Assert.Equal(0, _runner.Reloads);
    }

    [Fact]
    public async Task Apache_ElTunel_LlevaSuListen_YSeRecarga()
    {
        Config("apache");
        var stack = Service();
        Assert.True((await stack.UpAsync()).Ok);
        _tunnels.Next(Quick(TunnelUrl));

        var response = await stack.ShareTunnelAsync("tienda");

        Assert.True(response.Ok, response.Error);
        var conf = ShareConf("tienda-tunnel.conf", "apache");
        Assert.Contains("Listen 127.0.0.1:49731", conf);
        Assert.Contains("<VirtualHost 127.0.0.1:49731>", conf);
        Assert.Contains("ProxyFCGISetEnvIf \"true\" HTTPS \"on\"", conf);
        // En Windows, el balancer del pool; en Linux (el Apache de la distro, FakeDistroApache), directo al php-fpm del pool.
        Assert.Contains(OperatingSystem.IsWindows() ? "SetHandler \"proxy:balancer://php-8.4/\"" : "SetHandler \"proxy:fcgi://127.0.0.1:9840\"", conf);
        Assert.Contains("IncludeOptional", File.ReadAllText(Path.Combine(_paths.Etc, "apache", "httpd.conf")));
        Assert.Equal(1, _runner.ApacheReloads);
        Assert.Equal(1, _launcher.StartsOf("apache"));
    }

    // La red local

    [Fact]
    public async Task Lan_AbreHttpsSoloEnEsaIp_ConUnCertificadoQueLlevaLaIp_YRecarga()
    {
        var stack = Service();
        await stack.UpAsync();

        var response = await stack.ShareLanAsync("tienda", "192.168.1.20", 8443);

        Assert.True(response.Ok, response.Error);
        Assert.Equal("https://192.168.1.20:8443/", response.Share!.Url);
        Assert.Equal("lan", response.Share.Kind);
        Assert.Contains("recargado nginx", response.Applied);
        Assert.Equal(1, _launcher.StartsOf("nginx"));

        var conf = ShareConf("tienda-lan.conf");
        Assert.Contains("listen 192.168.1.20:8443 ssl;", conf);
        Assert.DoesNotContain("0.0.0.0", conf);
        Assert.DoesNotContain("listen 8443", conf);
        Assert.Contains("/certs/lan/tienda.crt\"", conf);
        Assert.Contains("location = /__roost/roost-ca.crt", conf);
        Assert.DoesNotContain("fastcgi_param HTTPS on;", conf);

        var ca = new LocalCa(_paths);
        using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(ca.LanCertFile("tienda")));
        Assert.True(cert.MatchesHostname("192.168.1.20"));
        Assert.True(cert.MatchesHostname("tienda.test"));
        Assert.False(cert.MatchesHostname("192.168.1.21"));
        Assert.False(_tunnels.Specs.Any());
    }

    [Fact]
    public async Task Lan_DejarDeCompartir_QuitaLaEscucha_YElCertificadoConLaIp()
    {
        var stack = Service();
        await stack.UpAsync();
        await stack.ShareLanAsync("tienda", "192.168.1.20", 8443);

        var stop = await stack.StopShareAsync("tienda", ShareKind.Lan);

        Assert.True(stop.Ok, stop.Error);
        Assert.False(ShareConfExists("tienda-lan.conf"));
        Assert.False(File.Exists(new LocalCa(_paths).LanCertFile("tienda")));
        Assert.Equal(2, _runner.Reloads);
    }

    [Theory]
    [InlineData("192.168.1.99", "no es una direccion de este equipo")]
    [InlineData("8.8.8.8", "no es una direccion de red local")]
    [InlineData("0.0.0.0", "no es una direccion de red local")]
    [InlineData("127.0.0.1", "no es una direccion de red local")]
    [InlineData("no-es-ip", "no es una direccion de red local")]
    public async Task Lan_UnaIpQueNoVale_SeRechaza_SinTocarNada(string address, string message)
    {
        var stack = Service();
        await stack.UpAsync();

        var response = await stack.ShareLanAsync("tienda", address, 8443);

        Assert.False(response.Ok);
        Assert.Contains(message, response.Error);
        Assert.Equal(0, _runner.Reloads);
        Assert.False(ShareConfExists("tienda-lan.conf"));
    }

    [Fact]
    public async Task Lan_UnPuertoCogidoEnEsaIp_SeRechaza()
    {
        var stack = Service();
        await stack.UpAsync();
        _bound.Add(("192.168.1.20", 8443));

        var response = await stack.ShareLanAsync("tienda", "192.168.1.20", 8443);

        Assert.False(response.Ok);
        Assert.Contains("ya lo usa otro programa", response.Error);
    }

    [Fact]
    public async Task Lan_UnPuertoQueElCortafuegosNoAbre_SeRechaza()
    {
        var stack = Service();
        await stack.UpAsync();

        var response = await stack.ShareLanAsync("tienda", "192.168.1.20", 3389);

        Assert.False(response.Ok);
        Assert.Contains("no vale para compartir", response.Error);
    }

    [Fact]
    public async Task Lan_SiLaIpDejaDeSerDelEquipo_ElSiguienteUpNoSeRompe()
    {
        var stack = Service();
        await stack.UpAsync();
        Assert.True((await stack.ShareLanAsync("tienda", "192.168.1.20", 8443)).Ok);

        // La wifi ha cambiado: el siguiente up no puede escuchar en esa IP.
        _localAddresses.Clear();
        var up = await stack.UpAsync();

        Assert.True(up.Ok, up.Error);
        Assert.Empty(stack.Shares().Shares);
        Assert.False(ShareConfExists("tienda-lan.conf"));
    }

    [Fact]
    public async Task TunelYRedLocal_AlaVez_CadaUnoEnSuEscucha()
    {
        var stack = Service();
        await stack.UpAsync();
        _tunnels.Next(Quick(TunnelUrl));
        Assert.True((await stack.ShareTunnelAsync("tienda")).Ok);
        Assert.True((await stack.ShareLanAsync("tienda", "192.168.1.20", 8443)).Ok);

        Assert.Equal(["lan", "tunnel"], stack.Shares().Shares.Select(s => s.Kind).Order());
        Assert.True(ShareConfExists("tienda-tunnel.conf"));
        Assert.True(ShareConfExists("tienda-lan.conf"));

        await stack.StopShareAsync("tienda", ShareKind.Tunnel);
        Assert.Equal("lan", Assert.Single(stack.Shares().Shares).Kind);
        Assert.True(ShareConfExists("tienda-lan.conf"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No ha pasado a tiempo.");
            await Task.Delay(20);
        }
    }

    private static void Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }

    // Dobles

    /// <summary>
    /// cloudflared de pega: suelta las líneas del guion y se queda «en marcha» hasta que lo cancelan
    /// (o, con <c>exitAfterLines</c>, hasta <see cref="ExitNow"/>), o sale enseguida con su código.
    /// </summary>
    private sealed class TunnelRunner : ICommandRunner
    {
        private readonly Queue<(string Output, int? ExitCode, bool WaitForExit, string? Extra)> _scripts = new();
        private readonly TaskCompletionSource _exitNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CommandSpec> Specs { get; } = [];
        public bool Cancelled { get; private set; }

        /// <param name="exitCode">null: se queda en marcha hasta que lo paren.</param>
        public void Next(string output, int? exitCode = null, bool exitAfterLines = false, string? extra = null) =>
            _scripts.Enqueue((output, exitCode, exitAfterLines, extra));

        public void ExitNow() => _exitNow.TrySetResult();

        public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Specs.Add(spec);
            var (output, exitCode, waitForExit, extra) = _scripts.Dequeue();
            foreach (var line in output.Split('\n')) lines?.Report(line.TrimEnd('\r'));
            try
            {
                if (exitCode is null || waitForExit)
                {
                    if (waitForExit) await _exitNow.Task.WaitAsync(cancellation);
                    else await Task.Delay(Timeout.Infinite, cancellation);
                }
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            if (extra is not null) lines?.Report(extra);
            return new CommandResult(exitCode ?? 0, "", output, false);
        }
    }

    /// <summary><c>nginx -t</c> y <c>httpd -t</c> siempre buenos; <c>nginx -s reload</c> y la recarga de Apache se cuentan.</summary>
    private sealed class NginxRunner : IProcessRunner
    {
        private int _reloads;
        private int _apacheReloads;

        public int Reloads => Volatile.Read(ref _reloads);
        public int ApacheReloads => Volatile.Read(ref _apacheReloads);

        public string? ApacheReload()
        {
            Interlocked.Increment(ref _apacheReloads);
            return null;
        }

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            if (arguments.StartsWith("-s reload ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _reloads);
                return "";
            }
            return "nginx: configuration file test is successful\nSyntax OK";
        }
    }

    private sealed class Process(int id) : IServiceProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;
        public int? ExitCode { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);

        public void Kill()
        {
            ExitCode = -1;
            _exit.TrySetResult();
        }

        public void Dispose() { }
    }

    private sealed class Launcher : IProcessLauncher
    {
        private readonly object _lock = new();
        private readonly List<string> _started = [];
        private int _nextId = 100;

        public IServiceProcess Start(ServiceDefinition definition)
        {
            lock (_lock)
            {
                _started.Add(definition.Name);
                return new Process(_nextId++);
            }
        }

        public int StartsOf(string name)
        {
            lock (_lock) return _started.Count(s => s == name);
        }
    }
}
