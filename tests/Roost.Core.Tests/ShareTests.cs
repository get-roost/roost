using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Share;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Compartir (oleada 6) sin red, sin cloudflared de verdad, sin escuchar en la red local y sin tocar
/// el cortafuegos: la regla que compila el helper, los vhosts de las escuchas, lo que se le pasa a
/// cloudflared y cómo se lee su salida (líneas como las del de verdad), la IP y el puerto de la red
/// local, el certificado con la IP y la instalación de cloudflared con un exe servido en memoria.
/// </summary>
public class ShareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-compartir-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temporal */ }
    }

    private Site NewSite(string name = "tienda", string? root = "public")
    {
        var folder = Path.Combine(_root, "www", name);
        Directory.CreateDirectory(root is null ? folder : Path.Combine(folder, root));
        return new Site { Name = name, Folder = folder, Root = root };
    }

    // La regla del cortafuegos (la compila el helper elevado)

    [Theory]
    [InlineData("8443", 8443)]
    [InlineData("443", 443)]
    [InlineData("80", 80)]
    [InlineData("1024", 1024)]
    [InlineData("65535", 65535)]
    public void FirewallRule_UnPuertoDeServidorWeb_Vale(string text, int port)
    {
        Assert.True(FirewallRule.TryParsePort(text, out var parsed));
        Assert.Equal(port, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("08443")]
    [InlineData("-1")]
    [InlineData("+8443")]
    [InlineData(" 8443")]
    [InlineData("8443 ")]
    [InlineData("65536")]
    [InlineData("100000")]
    [InlineData("1023")]
    [InlineData("22")]
    [InlineData("0x20FB")]
    [InlineData("8443;calc")]
    [InlineData("٨٤٤٣")]        // cifras árabes: char.IsDigit diría que sí
    [InlineData("８４４３")]    // cifras de ancho completo
    [InlineData("3389")]         // escritorio remoto
    [InlineData("5985")]         // WinRM
    [InlineData("3306")]         // MySQL de otro
    [InlineData("6379")]         // Redis de otro
    public void FirewallRule_LoDemas_SeRechaza(string? text) =>
        Assert.False(FirewallRule.TryParsePort(text, out _));

    [Fact]
    public void FirewallRule_ElScript_EsFijo_SoloCambiaElPuerto_YSoloElPerfilPrivadoDesdeLaSubredLocal()
    {
        var allow = FirewallRule.AllowScript(8443);

        Assert.Contains("New-NetFirewallRule -Name 'Roost-LAN-8443' -DisplayName 'Roost LAN 8443' -Group 'Roost'", allow);
        Assert.Contains("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 8443 -Profile Private -RemoteAddress LocalSubnet", allow);
        // Repetirlo no la duplica: antes se quita la que hubiera con ese mismo identificador.
        Assert.True(allow.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal) < allow.IndexOf("New-NetFirewallRule", StringComparison.Ordinal));
        Assert.Equal(allow.Replace("8443", "9000"), FirewallRule.AllowScript(9000));
        Assert.DoesNotContain("Any", allow);
        Assert.DoesNotContain("Public", allow);

        var remove = FirewallRule.RemoveScript(8443);
        Assert.Contains("Get-NetFirewallRule -Name 'Roost-LAN-8443' -ErrorAction SilentlyContinue | Remove-NetFirewallRule", remove);
        Assert.DoesNotContain("New-NetFirewallRule", remove);

        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRule.AllowScript(3389));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRule.RemoveScript(70000));
    }

    [Theory]
    [InlineData("Roost LAN 8443", true, 8443)]
    [InlineData("Roost LAN 443", true, 443)]
    [InlineData("Roost LAN 3389", false, 0)]
    [InlineData("Roost LAN ", false, 0)]
    [InlineData("roost lan 8443", false, 0)]
    [InlineData("Otra regla 8443", false, 0)]
    public void FirewallRule_ElNombreVisible_DaElPuerto(string name, bool ok, int port)
    {
        Assert.Equal(ok, FirewallRule.TryParseDisplayName(name, out var parsed));
        Assert.Equal(port, parsed);
    }

    // Las escuchas en el servidor web

    [Fact]
    public void ShareConf_Nginx_ElTunel_EsHttpEnLoopback_YPhpLoVeHttps()
    {
        var site = NewSite();
        var conf = ShareConf.RenderNginx(new ShareConf.Input(
            new ShareListener("tienda", ShareKind.Tunnel, "127.0.0.1", 49731), site, "php-8.4", @"C:\roost\bin\nginx\1.30.6\conf\fastcgi_params"));

        Assert.Contains("listen 127.0.0.1:49731;", conf);
        Assert.DoesNotContain("ssl", conf);
        Assert.Contains("server_name tienda.test;", conf);
        Assert.Contains($"root \"{site.DocumentRoot.Replace('\\', '/')}\";", conf);
        Assert.Contains("fastcgi_pass php-8.4;", conf);
        Assert.Contains("include \"C:/roost/bin/nginx/1.30.6/conf/fastcgi_params\";", conf);
        Assert.Contains("try_files $uri $uri/ /index.php?$query_string;", conf);
        Assert.Contains("add_header X-Robots-Tag \"noindex, nofollow\" always;", conf);
        Assert.Contains("fastcgi_hide_header X-Powered-By;", conf);   // la version de PHP no se anuncia a internet
        // Lo del túnel va detrás del include: php-cgi se queda con el último valor.
        var include = conf.IndexOf("include \"", StringComparison.Ordinal);
        Assert.True(conf.IndexOf("fastcgi_param HTTPS on;", StringComparison.Ordinal) > include);
        Assert.True(conf.IndexOf("fastcgi_param REMOTE_ADDR $http_cf_connecting_ip if_not_empty;", StringComparison.Ordinal) > include);
        Assert.Contains("fastcgi_param SERVER_PORT 443;", conf);
        Assert.DoesNotContain(ShareConf.CaPath, conf);
        AssertSinTildes(conf);
    }

    [Fact]
    public void ShareConf_Nginx_LaRedLocal_EsHttpsSoloEnLaIp_ConSuCertificado_YLaCa()
    {
        var site = NewSite();
        var conf = ShareConf.RenderNginx(new ShareConf.Input(
            new ShareListener("tienda", ShareKind.Lan, "192.168.1.20", 8443), site, "php-8.4", @"C:\n\fastcgi_params",
            @"C:\roost\certs\lan\tienda.crt", @"C:\roost\certs\lan\tienda.key", @"C:\roost\certs\ca\roost-ca.crt"));

        Assert.Contains("listen 192.168.1.20:8443 ssl;", conf);
        Assert.Single(conf.Split('\n'), l => l.TrimStart().StartsWith("listen ", StringComparison.Ordinal));
        Assert.Contains("http2 on;", conf);
        Assert.Contains("ssl_certificate \"C:/roost/certs/lan/tienda.crt\";", conf);
        Assert.Contains("ssl_certificate_key \"C:/roost/certs/lan/tienda.key\";", conf);
        Assert.Contains($"location = {ShareConf.CaPath} {{", conf);
        Assert.Contains("alias \"C:/roost/certs/ca/roost-ca.crt\";", conf);
        Assert.DoesNotContain("roost-ca.key", conf);
        Assert.DoesNotContain("HTTPS on", conf);
        Assert.DoesNotContain("X-Robots-Tag", conf);
        Assert.DoesNotContain("X-Powered-By", conf);
        AssertSinTildes(conf);
    }

    [Fact]
    public void ShareConf_Apache_ElTunelYLaRedLocal_TienenSuListenYSuVirtualHost()
    {
        var site = NewSite();
        // Los literales del código salen con el fin de línea del fichero fuente: se comparan en LF.
        var tunnel = ShareConf.RenderApache(new ShareConf.Input(new ShareListener("tienda", ShareKind.Tunnel, "127.0.0.1", 49731), site, "php-8.4")).ReplaceLineEndings("\n");
        Assert.StartsWith("# Generado por Roost", tunnel);
        Assert.Contains("Listen 127.0.0.1:49731\n<VirtualHost 127.0.0.1:49731>", tunnel);
        Assert.Contains("SetHandler \"proxy:balancer://php-8.4/\"", tunnel);
        Assert.Contains("ProxyFCGISetEnvIf \"true\" HTTPS \"on\"", tunnel);
        Assert.Contains("ProxyFCGISetEnvIf \"-n req('CF-Connecting-IP')\" REMOTE_ADDR \"%{req:CF-Connecting-IP}\"", tunnel);
        Assert.Contains("Header always set X-Robots-Tag", tunnel);
        Assert.Contains("Header always unset X-Powered-By", tunnel);   // la de PHP por FastCGI va en la tabla de "always"
        Assert.DoesNotContain("SSLEngine", tunnel);
        AssertSinTildes(tunnel);

        var lan = ShareConf.RenderApache(new ShareConf.Input(new ShareListener("tienda", ShareKind.Lan, "192.168.1.20", 8443), site, "php-8.4",
            CertFile: @"C:\c\tienda.crt", KeyFile: @"C:\c\tienda.key", CaCertFile: @"C:\c\roost-ca.crt")).ReplaceLineEndings("\n");
        Assert.Contains("Listen 192.168.1.20:8443\n<VirtualHost 192.168.1.20:8443>", lan);
        Assert.Contains("SSLEngine on", lan);
        Assert.Contains("SSLCertificateFile \"C:/c/tienda.crt\"", lan);
        Assert.Contains($"Alias \"{ShareConf.CaPath}\" \"C:/c/roost-ca.crt\"", lan);
        Assert.DoesNotContain("HTTPS \"on\"", lan);
        AssertSinTildes(lan);
    }

    [Fact]
    public void ShareConf_LaRedLocalSinCertificado_NoSeEscribe() =>
        Assert.Throws<ArgumentException>(() => ShareConf.RenderNginx(new ShareConf.Input(
            new ShareListener("tienda", ShareKind.Lan, "192.168.1.20", 8443), NewSite(), "php-8.4", "fastcgi_params")));

    [Fact]
    public void NginxConf_ConLaCarpetaDeCompartir_LaIncluyeTrasLosSitios_YSinEllaNoCambia()
    {
        var input = new NginxConfInput("mime.types", "fastcgi_params", @"C:\r\run", @"C:\r\logs", @"C:\r\www", 8080,
            [new NginxUpstream("php-8.4", [9840])], "php-8.4", @"C:\r\etc\nginx\sites");
        var without = NginxConf.Render(input).ReplaceLineEndings("\n");
        var with = NginxConf.Render(input with { SharesDirectory = @"C:\r\etc\nginx\share" }).ReplaceLineEndings("\n");

        Assert.DoesNotContain("share/*.conf", without);
        var sites = with.IndexOf("include \"C:/r/etc/nginx/sites/*.conf\";", StringComparison.Ordinal);
        var shares = with.IndexOf("include \"C:/r/etc/nginx/share/*.conf\";", StringComparison.Ordinal);
        Assert.True(sites > 0 && shares > sites);
        Assert.Equal(without, with.Replace("\n\n    # Lo que estas compartiendo (roost share), solo mientras dura: lo escribe y lo borra Roost.\n    include \"C:/r/etc/nginx/share/*.conf\";", ""));
        Assert.EndsWith("share/*.conf\";\n}\n", with);
    }

    [Fact]
    public void ApacheConf_ConLaCarpetaDeCompartir_LaIncluye()
    {
        var input = new ApacheConfInput(@"C:\r\bin\apache\2.4.69", @"C:\r\run", @"C:\r\logs", @"C:\r\www", 8080, 8443,
            [new NginxUpstream("php-8.4", [9840])], "php-8.4", @"C:\r\etc\apache\sites");
        var without = ApacheConf.Render(input).ReplaceLineEndings("\n");
        var with = ApacheConf.Render(input with { SharesDirectory = @"C:\r\etc\apache\share" }).ReplaceLineEndings("\n");

        Assert.DoesNotContain("share/*.conf", without);
        Assert.EndsWith("IncludeOptional \"C:/r/etc/apache/share/*.conf\"\n", with);
    }

    [Fact]
    public void Prepare_ConUnaEscucha_LaEscribe_YBorraLaQueYaNoSeComparte()
    {
        var (php, nginx) = FakeStack();
        var site = NewSite();
        new SiteRegistry(Paths).Upsert(site);
        var config = new RoostConfig { HttpPort = 8766, HttpsPort = 8444, PhpPool = 1, DbEngine = RoostConfig.NoDb };
        var stale = Path.Combine(Paths.NginxShares, "viejo-tunnel.conf");
        Directory.CreateDirectory(Paths.NginxShares);
        File.WriteAllText(stale, "listen 127.0.0.1:1;");

        var listener = new ShareListener("tienda", ShareKind.Tunnel, "127.0.0.1", 49731);
        var plan = new StackPlanner(Paths, new NginxTestOk()) { Shares = [listener, new ShareListener("borrado", ShareKind.Tunnel, "127.0.0.1", 49732)] }
            .Prepare(config, php, nginx, sites: [site], ca: new LocalCa(Paths));

        Assert.Equal([listener], plan.Shares);   // la del sitio que ya no está no va
        Assert.Contains(49731, plan.Ports);
        Assert.DoesNotContain(49732, plan.Ports);
        Assert.Contains("listen 127.0.0.1:49731;", File.ReadAllText(Path.Combine(Paths.NginxShares, "tienda-tunnel.conf")));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Path.Combine(Paths.NginxShares, "borrado-tunnel.conf")));
        Assert.Contains("share/*.conf\";", File.ReadAllText(Path.Combine(Paths.Etc, "nginx", "nginx.conf")));
    }

    [Fact]
    public void Prepare_SinNadaCompartido_LaCarpetaExisteVacia()
    {
        var (php, nginx) = FakeStack();
        var config = new RoostConfig { HttpPort = 8766, PhpPool = 1, DbEngine = RoostConfig.NoDb };

        new StackPlanner(Paths, new NginxTestOk()).Prepare(config, php, nginx);

        Assert.True(Directory.Exists(Paths.NginxShares));   // el include con comodín la necesita en Windows
        Assert.Empty(Directory.EnumerateFiles(Paths.NginxShares));
    }

    // cloudflared: lo que se le pasa y cómo se lee lo que dice

    [Fact]
    public void QuickTunnel_LosArgumentos_AislanDelConfigDelUsuario_YNoSeActualizaSolo()
    {
        Assert.Equal(
            ["tunnel", "--config", @"C:\roost\state\tunnel\cloudflared.yml", "--no-autoupdate", "--metrics", "127.0.0.1:0", "--label", "roost", "--url", "http://127.0.0.1:49731"],
            QuickTunnel.Arguments(49731, @"C:\roost\state\tunnel\cloudflared.yml"));
    }

    [Fact]
    public void QuickTunnel_ElEntorno_QuitaLasVariablesQueCambiarianElTunel()
    {
        var env = QuickTunnel.Environment(["PATH", "TUNNEL_TOKEN", "tunnel_url", "TUNNEL_ORIGIN_CA_POOL", "HTTPS_PROXY", "USERPROFILE"]);

        Assert.Equal(["CFDPATH", "NO_AUTOUPDATE", "NO_TLS_VERIFY", "TUNNEL_ORIGIN_CA_POOL", "TUNNEL_TOKEN", "tunnel_url"], env.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(env.Values, Assert.Null);   // null = quitarla; el proxy y lo demás se heredan
    }

    [Fact]
    public void QuickTunnel_ElFicheroDeConfiguracion_EsUnYamlVacio()
    {
        var file = QuickTunnel.EnsureConfigFile(Paths);

        Assert.Equal(Path.Combine(Paths.State, "tunnel", "cloudflared.yml"), file);
        Assert.EndsWith("\n{}\n", File.ReadAllText(file));
        Assert.Equal(file, QuickTunnel.EnsureConfigFile(Paths));
    }

    // Lo que escribe cloudflared 2026.10.3 (cmd/cloudflared/tunnel/quick_tunnel.go y cliutil.LogTable).
    private static readonly string[] Created =
    [
        "2026-10-24T05:32:17Z INF Thank you for trying Cloudflare Tunnel. Doing so, without a Cloudflare account, is a quick way to experiment and try it out. However, be aware that these account-less Tunnels have no uptime guarantee, are subject to the Cloudflare Online Services Terms of Use (https://www.cloudflare.com/website-terms/), and Cloudflare reserves the right to investigate your use of Tunnels for violations of such terms. If you intend to use Tunnels in production you should use a pre-created named tunnel by following: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps",
        "2026-10-24T05:32:17Z INF Requesting new quick Tunnel on trycloudflare.com...",
        "2026-10-24T05:32:19Z INF +--------------------------------------------------------------------------------------------+",
        "2026-10-24T05:32:19Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |",
        "2026-10-24T05:32:19Z INF |  https://seasons-hollow-zoning-tall.trycloudflare.com                                      |",
        "2026-10-24T05:32:19Z INF +--------------------------------------------------------------------------------------------+",
        "2026-10-24T05:32:19Z INF Version 2026.10.3 (Checksum 76b28)",
        "2026-10-24T05:32:20Z INF Registered tunnel connection connIndex=0 connection=8c1f4b2e event=0 ip=198.41.192.7 location=mad01 protocol=quic",
    ];

    [Fact]
    public void QuickTunnelOutput_LaUrl_EsLaDelRecuadro_YLaConexion_LaPrimeraRegistrada()
    {
        var output = new QuickTunnelOutput();
        var events = Created.Select(output.Feed).ToList();

        Assert.Equal("https://seasons-hollow-zoning-tall.trycloudflare.com", output.Url);
        Assert.True(output.Connected);
        Assert.Null(output.LastError);
        Assert.Equal([false, false, false, false, true, false, false, true], events);
    }

    [Fact]
    public void QuickTunnelOutput_LasUrlsDeAntes_NiLaDeLaApiEnUnError_NoSonLaDelTunel()
    {
        var output = new QuickTunnelOutput();
        output.Feed(Created[0]);
        output.Feed("failed to request quick Tunnel: Post \"https://api.trycloudflare.com/tunnel\": dial tcp 127.0.0.1:65011: connectex: No connection could be made because the target machine actively refused it.");

        Assert.Null(output.Url);
        Assert.StartsWith("failed to request quick Tunnel: Post", output.LastError);
        Assert.StartsWith("No se ha podido hablar con trycloudflare.com", QuickTunnelOutput.Explain(output.LastError, 1));
    }

    [Fact]
    public void QuickTunnelOutput_ElProtegido_TambienDaSuUrl()
    {
        var output = new QuickTunnelOutput();
        output.Feed("2026-10-24T05:32:19Z INF |  Your protected quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |");
        output.Feed("2026-10-24T05:32:19Z INF |  https://a-b-c.trycloudflare.com                    |");

        Assert.Equal("https://a-b-c.trycloudflare.com", output.Url);
    }

    [Fact]
    public void QuickTunnelOutput_LosErrores_SeCuentanConSuMotivo()
    {
        var output = new QuickTunnelOutput();
        output.Feed("2026-10-24T05:40:00Z ERR Failed to dial a quic connection error=\"timeout: no recent network activity\" connIndex=0");

        Assert.Equal("Failed to dial a quic connection error=\"timeout: no recent network activity\" connIndex=0", output.LastError);
        Assert.StartsWith("Cloudflare no da mas tuneles rapidos", QuickTunnelOutput.Explain("quick tunnel provisioning failed with status 429", 1));
        Assert.Equal("cloudflared ha terminado (codigo 1) sin dar la URL.", QuickTunnelOutput.Explain(null, 1));
        Assert.Equal("cloudflared no ha dado la URL a tiempo.", QuickTunnelOutput.Explain(null, null));
        Assert.StartsWith("cloudflared no ha podido abrir el tunel: ", QuickTunnelOutput.Explain("algo raro", 1));
    }

    // La IP y el puerto de la red local

    private static LanCandidate Nic(string ip, string name, NetworkInterfaceType type = NetworkInterfaceType.Wireless80211, bool gateway = true, int index = 5, string description = "Intel(R) Wi-Fi 6E AX211") =>
        new(IPAddress.Parse(ip), index, name, description, type, gateway);

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("192.169.1.20", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", false)]
    public void LanNetwork_SoloLasRedesPrivadas(string ip, bool expected) =>
        Assert.Equal(expected, LanNetwork.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void LanNetwork_PrefiereLaWifiOElCable_ConPuertaDeEnlace_YNuncaUnAdaptadorVirtual()
    {
        var candidates = new[]
        {
            Nic("172.29.48.1", "vEthernet (WSL (Hyper-V firewall))", NetworkInterfaceType.Ethernet, gateway: false, index: 40, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("192.168.56.1", "Ethernet 3", NetworkInterfaceType.Ethernet, gateway: false, index: 12, description: "VirtualBox Host-Only Ethernet Adapter"),
            Nic("10.8.0.2", "OpenVPN", NetworkInterfaceType.Unknown, gateway: true, index: 30, description: "TAP-Windows Adapter V9"),
            Nic("192.168.1.20", "Wi-Fi", index: 5),
            Nic("192.168.1.33", "Ethernet", NetworkInterfaceType.Ethernet, gateway: false, index: 7, description: "Realtek PCIe GbE"),
            Nic("8.8.4.4", "Rara", index: 3),
        };

        Assert.Equal(["192.168.1.20", "192.168.1.33"], LanNetwork.Usable(candidates).Select(c => c.Address.ToString()));
        Assert.Equal("192.168.1.20", LanNetwork.Choose(candidates, null).Address.ToString());
        // Con --ip vale una del equipo, aunque sea de un adaptador virtual.
        Assert.Equal("192.168.56.1", LanNetwork.Choose(candidates, "192.168.56.1").Address.ToString());
    }

    [Theory]
    [InlineData("192.168.1.99", "no es una direccion de este equipo")]
    [InlineData("8.8.4.4", "no es una direccion de red local")]
    [InlineData("0.0.0.0", "no es una direccion de red local")]
    [InlineData("fe80::1", "no es una direccion IPv4")]
    [InlineData("tienda.test", "no es una direccion IPv4")]
    public void LanNetwork_ConIp_SoloUnaPrivadaDeEsteEquipo(string wanted, string message)
    {
        var error = Assert.Throws<RoostException>(() => LanNetwork.Choose([Nic("192.168.1.20", "Wi-Fi"), Nic("8.8.4.4", "Rara")], wanted));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void LanNetwork_SinRedLocal_LoDice_YNombraLasVirtuales()
    {
        var error = Assert.Throws<RoostException>(() => LanNetwork.Choose(
            [Nic("172.29.48.1", "vEthernet (WSL)", NetworkInterfaceType.Ethernet, gateway: false, description: "Hyper-V Virtual Ethernet Adapter")], null));
        Assert.Contains("ninguna direccion de red local", error.Message);
        Assert.Contains("172.29.48.1 (vEthernet (WSL))", error.Message);
        Assert.Contains("--ip", error.Message);
    }

    [Fact]
    public void LanNetwork_ElPuerto_EsElHttpsSiEstaLibre_SiNoElSiguiente_SaltandoLosQueNoSeAbren()
    {
        Assert.Equal(8443, LanNetwork.ChoosePort(8443, _ => false));
        Assert.Equal(8445, LanNetwork.ChoosePort(8443, p => p is 8443 or 8444));
        Assert.Equal(443, LanNetwork.ChoosePort(443, _ => false));
        Assert.Equal(3390, LanNetwork.ChoosePort(3389, _ => false));   // el de escritorio remoto, nunca
        Assert.Throws<RoostException>(() => LanNetwork.ChoosePort(8443, _ => true));
        Assert.Equal("https://192.168.1.20:8443/", LanNetwork.Url("192.168.1.20", 8443));
        Assert.Equal("https://192.168.1.20/", LanNetwork.Url("192.168.1.20", 443));
    }

    [Fact]
    public void LanProbe_LeeElTipoDeCadaRed_YLasReglasDeRoost()
    {
        // Lo que dio el script de verdad (solo lee) en esta máquina, con una regla de Roost añadida a mano.
        var result = LanProbe.Parse("""{"profiles":[{"i":51,"c":"Private"},{"i":26,"c":"Public"},{"i":5,"c":"Private"}],"rules":["Roost LAN 8443","Roost LAN 3389","Otra"]}""");

        Assert.Equal(NetworkCategory.Private, result.CategoryOf(51));
        Assert.Equal(NetworkCategory.Public, result.CategoryOf(26));
        Assert.Equal(NetworkCategory.Unknown, result.CategoryOf(99));
        Assert.Equal([8443], result.RulePorts);
    }

    [Fact]
    public void LanProbe_AguantaElProgresoDePowerShell_UnSoloObjeto_YLosNumeros()
    {
        var result = LanProbe.Parse("#< CLIXML\r\n{\"profiles\":{\"i\":7,\"c\":1},\"rules\":\"Roost LAN 443\"}\r\n<Objs Version=\"1.1.0.1\"></Objs>");

        Assert.Equal(NetworkCategory.Private, result.CategoryOf(7));
        Assert.Equal([443], result.RulePorts);
        Assert.Same(LanProbeResult.Empty, LanProbe.Parse("no es json"));
        Assert.Empty(LanProbe.Parse("{\"profiles\":[],\"rules\":[]}").RulePorts);
    }

    // El certificado con la IP

    [Fact]
    public void LocalCa_ElDeLaRedLocal_LlevaElNombreYLaIp_EncadenaConLaCa_YSeReemiteSiCambiaLaIp()
    {
        var ca = new LocalCa(Paths);
        var site = NewSite();

        var (certFile, keyFile) = ca.EnsureLanCert(site, IPAddress.Parse("192.168.1.20"));

        Assert.Equal(Path.Combine(Paths.Certs, "lan", "tienda.crt"), certFile);
        Assert.True(File.Exists(keyFile));
        using (var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certFile)))
        {
            Assert.True(cert.MatchesHostname("192.168.1.20"));
            Assert.True(cert.MatchesHostname("tienda.test"));
            Assert.False(cert.MatchesHostname("otro.test"));
            Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
            using var root = X509Certificate2.CreateFromPem(File.ReadAllText(ca.CaCertFile));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(cert));
            Assert.True(cert.NotAfter - DateTime.Now <= LocalCa.SiteValidity);
        }

        // La misma IP: se reutiliza; otra IP: se reemite.
        var first = File.ReadAllText(certFile);
        ca.EnsureLanCert(site, IPAddress.Parse("192.168.1.20"));
        Assert.Equal(first, File.ReadAllText(certFile));
        ca.EnsureLanCert(site, IPAddress.Parse("10.0.0.7"));
        using (var again = X509Certificate2.CreateFromPem(File.ReadAllText(certFile)))
        {
            Assert.True(again.MatchesHostname("10.0.0.7"));
            Assert.False(again.MatchesHostname("192.168.1.20"));
        }

        // El del sitio no se toca, y quitar el de la red local no toca el del sitio.
        Assert.False(File.Exists(ca.SiteCertFile("tienda")));
        ca.RemoveLanCert("tienda");
        Assert.False(File.Exists(certFile));
        Assert.False(File.Exists(keyFile));
    }

    // Instalar cloudflared

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task Cloudflared_Install_DejaElExeEnSuCarpeta_YLoEncuentra()
    {
        var exe = "MZ cloudflared de pega"u8.ToArray();
        var build = new CloudflaredBuild("2026.10.3", new Uri("https://example.test/cloudflared-windows-amd64.exe"), Convert.ToHexStringLower(SHA256.HashData(exe)));
        var installer = new CloudflaredInstaller(Paths, new HttpClient(new FakeHandler(exe)));

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.CloudflaredBin, "2026.10.3", "cloudflared.exe"), installed.Executable);
        Assert.Equal(exe, File.ReadAllBytes(installed.Executable));
        Assert.Equal(installed, installer.Find(build));
        Assert.Equal("2026.10.3", Assert.Single(installer.ListInstalled()).Version);
        Assert.True(File.Exists(Path.Combine(Paths.Downloads, "cloudflared-2026.10.3-windows-amd64.exe")));
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));
    }

    [Fact]
    public async Task Cloudflared_Install_ConUnShaQueNoCuadra_NoInstalaNada()
    {
        var build = new CloudflaredBuild("2026.10.3", new Uri("https://example.test/cloudflared-windows-amd64.exe"), new string('a', 64));
        var installer = new CloudflaredInstaller(Paths, new HttpClient(new FakeHandler("MZ otro"u8.ToArray())));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Null(installer.Locate());
        Assert.False(Directory.Exists(Path.Combine(Paths.CloudflaredBin, "2026.10.3")));
    }

    [Fact]
    public void CloudflaredCatalog_EsElExeDeLaReleaseDeGitHub_ConSuSha()
    {
        var build = CloudflaredCatalog.Windows;
        Assert.Equal("https://github.com/cloudflare/cloudflared/releases/download/2026.10.3/cloudflared-windows-amd64.exe", build.Url.ToString());
        Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
        Assert.Equal("cloudflared-2026.10.3-windows-amd64.exe", build.FileName);
    }

    // Lo que conviene saber antes de compartir

    [Fact]
    public void ShareCheck_Laravel_ConAppDebug_LoDice_YDaSuAppUrl()
    {
        var site = NewSite("tienda", root: null);
        File.WriteAllText(Path.Combine(site.Folder, "artisan"), "");
        File.WriteAllText(Path.Combine(site.Folder, ".env"), "APP_NAME=Tienda\nAPP_DEBUG=true\nAPP_URL=https://tienda.test\n");

        var check = ShareCheck.Inspect(site);

        Assert.Equal(ProjectType.Laravel, check.Type);
        Assert.True(check.Debug);
        Assert.Equal("https://tienda.test", check.AppUrl);

        File.WriteAllText(Path.Combine(site.Folder, ".env"), "APP_DEBUG=false\n");
        Assert.False(ShareCheck.Inspect(site).Debug);
    }

    [Fact]
    public void ShareCheck_Symfony_ElEnvLocalManda()
    {
        var site = NewSite("api", root: null);
        Directory.CreateDirectory(Path.Combine(site.Folder, "bin"));
        File.WriteAllText(Path.Combine(site.Folder, "bin", "console"), "");
        File.WriteAllText(Path.Combine(site.Folder, "symfony.lock"), "{}");
        File.WriteAllText(Path.Combine(site.Folder, ".env"), "APP_ENV=dev\n");

        Assert.True(ShareCheck.Inspect(site).Debug);
        File.WriteAllText(Path.Combine(site.Folder, ".env.local"), "APP_ENV=prod\n");
        Assert.False(ShareCheck.Inspect(site).Debug);
    }

    [Fact]
    public void ShareCheck_WordPress_ConWpDebug()
    {
        var site = NewSite("blog", root: null);
        File.WriteAllText(Path.Combine(site.Folder, "wp-config.php"), "<?php\ndefine( 'WP_DEBUG', true );\n");

        var check = ShareCheck.Inspect(site);

        Assert.Equal(ProjectType.WordPress, check.Type);
        Assert.True(check.Debug);
        File.WriteAllText(Path.Combine(site.Folder, "wp-config.php"), "<?php\ndefine('WP_DEBUG', false);\n");
        Assert.False(ShareCheck.Inspect(site).Debug);
    }

    [Fact]
    public void ShareCheck_UnaCarpetaQueNoExiste_NoLanza() =>
        Assert.Equal(ProjectType.Plain, ShareCheck.Inspect(new Site { Name = "nada", Folder = Path.Combine(_root, "no-existe") }).Type);

    // Dobles

    private sealed class NginxTestOk : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "nginx: configuration file test is successful";
    }

    private (InstalledPhp Php, InstalledNginx Nginx) FakeStack()
    {
        var dir = Path.Combine(Paths.PhpBin, "8.4.27-nts-x64");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "php.exe"), "");
        File.WriteAllText(Path.Combine(dir, "php-cgi.exe"), "");
        return (new InstalledPhp("8.4.27-nts-x64", dir, Path.Combine(dir, "php.exe")), new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6")));
    }

    /// <summary>Lo que genera Roost va sin tildes (como el resto de su configuración).</summary>
    private static void AssertSinTildes(string text) =>
        Assert.DoesNotMatch("[áéíóúñÁÉÍÓÚÑ]", text);
}
