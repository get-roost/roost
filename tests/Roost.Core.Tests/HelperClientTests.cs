using System.Text;
using Roost.Core.Platform;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Lo que llama al helper desde Core en Linux, con una Elevation de pega (root, así el runner falso
/// hace de helper): el hosts por stdin, el cortafuegos y su lista de puertos abiertos, la CA del
/// sistema, el setcap de nginx y la www. Nada se eleva ni se escribe fuera de la carpeta temporal.
/// </summary>
public class HelperClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-helper-client-" + Guid.NewGuid().ToString("N"));
    private const string Helper = "/usr/libexec/roost/roost-helper";

    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    public HelperClientTests() => Directory.CreateDirectory(_root);

    private static Elevation AsRoot(FakeCommandRunner runner) => new(runner, linux: true, isRoot: () => true, exists: _ => true);

    [Fact]
    public void Hosts_EnLinux_LosNombresVanPorStdin_YSeDejanTambienEnElFichero()
    {
        var hosts = Path.Combine(_root, "hosts");
        var request = Path.Combine(_root, "run", "hosts-request.txt");
        File.WriteAllText(hosts, "127.0.0.1 localhost\n::1 localhost\n");
        var runner = new FakeCommandRunner().On("hosts-sync", onRun: spec =>
        {
            // Lo que haría el helper: el bloque con lo que llega por stdin.
            File.WriteAllText(hosts, HostsBlock.Apply(File.ReadAllText(hosts), spec.Stdin!.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        });
        var updater = new HostsUpdater(hosts, isSystemFile: true, windowsFromWsl: false) { Elevation = AsRoot(runner), LinuxHelper = true };

        Assert.True(updater.Sync(["tienda.test", "api.tienda.test", "tienda.test"], Helper, request));

        var call = Assert.Single(runner.Calls);
        Assert.Equal(Helper, call.Exe);
        Assert.Equal(["hosts-sync"], call.Arguments);   // sin rutas: los nombres van por stdin
        Assert.Equal("api.tienda.test\ntienda.test\n", call.Stdin);
        Assert.Equal(["api.tienda.test", "tienda.test"], File.ReadAllLines(request));   // para la orden manual (< fichero)
        Assert.Equal("127.0.0.1 localhost\n::1 localhost\n\n# roost:begin  (lo gestiona Roost: no edites estas lineas)\n127.0.0.1 api.tienda.test\n127.0.0.1 tienda.test\n# roost:end\n", File.ReadAllText(hosts));
        Assert.False(updater.Sync(["api.tienda.test", "tienda.test"], Helper, request));   // ya está: no se pide nada
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void Hosts_SiElHelperLoRechaza_SeDiceElCodigo_YElRegistro()
    {
        var hosts = Path.Combine(_root, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\n");
        var runner = new FakeCommandRunner().On("hosts-sync", exitCode: 65);
        var updater = new HostsUpdater(hosts, isSystemFile: true, windowsFromWsl: false) { Elevation = AsRoot(runner), LinuxHelper = true };

        var ex = Assert.Throws<RoostException>(() => updater.Sync(["tienda.test"], Helper, Path.Combine(_root, "run", "req.txt")));

        Assert.Contains("codigo 65", ex.Message);
        Assert.Contains(HostsUpdater.HelperLog, ex.Message);
    }

    [Fact]
    public void Hosts_Cancelado_NoSeTocaNada()
    {
        var hosts = Path.Combine(_root, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\n");
        var runner = new FakeCommandRunner().On($"{Helper} hosts-sync", exitCode: 126);
        var cancelled = new Elevation(runner, linux: true, isRoot: () => false, exists: _ => true);
        var updater = new HostsUpdater(hosts, isSystemFile: true, windowsFromWsl: false) { Elevation = cancelled, LinuxHelper = true };

        var ex = Assert.Throws<RoostException>(() => updater.Sync(["tienda.test"], Helper, Path.Combine(_root, "run", "req.txt")));

        Assert.Contains("Has cancelado el permiso de administrador", ex.Message);
        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(hosts));
    }

    [Fact]
    public void Cortafuegos_EnLinux_RecuerdaLosPuertosQueAbre_YLosOlvidaAlQuitar()
    {
        if (OperatingSystem.IsWindows()) return;   // en Windows los lee LanProbe, no el fichero
        var runner = new FakeCommandRunner().On("firewall-allow").On("firewall-remove");

        LanFirewall.Allow(Helper, 8443, Paths, AsRoot(runner));
        LanFirewall.Allow(Helper, 8444, Paths, AsRoot(runner));
        LanFirewall.Allow(Helper, 8443, Paths, AsRoot(runner));   // repetido: una sola vez

        Assert.Equal(["firewall-allow 8443", "firewall-allow 8444", "firewall-allow 8443"], runner.Commands);
        Assert.Equal([8443, 8444], LanFirewall.Remembered(Paths));
        Assert.Equal([8443, 8444], LanFirewall.RulePortsAsync(runner, default, Paths).GetAwaiter().GetResult());

        LanFirewall.Remove(Helper, 8443, Paths, AsRoot(runner));
        Assert.Equal([8444], LanFirewall.Remembered(Paths));
        LanFirewall.Remove(Helper, 8444, Paths, AsRoot(runner));
        Assert.Empty(LanFirewall.Remembered(Paths));
        Assert.False(File.Exists(LanFirewall.RecordFile(Paths)));
    }

    [Fact]
    public void Cortafuegos_ElFicheroDePuertos_SoloAdmitePuertosQueValen()
    {
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(LanFirewall.RecordFile(Paths), "8443\nbasura\n3389\n 8080 \n8443\n-1\n");

        Assert.Equal([8080, 8443], LanFirewall.Remembered(Paths));   // 3389 está prohibido; lo demás no es un puerto
    }

    [Fact]
    public void Cortafuegos_UnPuertoProhibido_NiSePideAlHelper()
    {
        var runner = new FakeCommandRunner();
        var ex = Assert.Throws<RoostException>(() => LanFirewall.Allow(Helper, 3389, Paths, AsRoot(runner)));
        Assert.Contains("3389", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void CaDelSistema_ElPemVaPorStdin_YSeCancelaSinTocarNada()
    {
        var ca = new LocalCa(Paths);
        using var cert = ca.LoadOrCreate();
        var runner = new FakeCommandRunner().On("ca-trust").On("ca-untrust");

        ca.TrustSystem(Helper, AsRoot(runner));
        ca.UntrustSystem(Helper, AsRoot(runner));

        Assert.Equal(["ca-trust", "ca-untrust"], runner.Commands);
        Assert.Equal(cert.ExportCertificatePem(), runner.Calls[0].Stdin);
        Assert.Null(runner.Calls[1].Stdin);

        var refused = new FakeCommandRunner().On("ca-trust", exitCode: 65);
        var ex = Assert.Throws<RoostException>(() => ca.TrustSystem(Helper, AsRoot(refused)));
        Assert.Contains("codigo 65", ex.Message);
    }

    [Fact]
    public void SetcapYWww_TraducenLosCodigosDelHelper()
    {
        var ok = new FakeCommandRunner().On("setcap-nginx").On("www-init");
        NginxCapability.Request(Helper, "/home/ana/.local/share/roost/bin/nginx/1.30.6/nginx", AsRoot(ok));
        WwwFolder.Init(Helper, AsRoot(ok));
        Assert.Equal(["setcap-nginx /home/ana/.local/share/roost/bin/nginx/1.30.6/nginx", "www-init"], ok.Commands);

        var rejected = new FakeCommandRunner().On("setcap-nginx", exitCode: 65);
        Assert.Contains("dentro de tu carpeta personal", Assert.Throws<RoostException>(() => NginxCapability.Request(Helper, "/tmp/nginx", AsRoot(rejected))).Message);

        var exists = new FakeCommandRunner().On("www-init", exitCode: 73);
        Assert.Contains("es de otro usuario", Assert.Throws<RoostException>(() => WwwFolder.Init(Helper, AsRoot(exists))).Message);

        Assert.Equal("sudo setcap cap_net_bind_service=+ep '/home/ana maria/nginx'", NginxCapability.ManualCommand("/home/ana maria/nginx"));
    }

    [Fact]
    public void ElBloqueParaCopiar_EsElMismoQueEscribiriaElHelper()
    {
        var block = HostsUpdater.ManualBlock(["b.test", "a.test", "a.test"]);
        Assert.Equal(["a.test", "b.test"], HostsBlock.ReadHosts(block));
        Assert.Equal(Encoding.ASCII.GetBytes(block), Encoding.Latin1.GetBytes(block));   // ASCII puro
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
