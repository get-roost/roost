using System.Net;
using System.Net.Sockets;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Testea las piezas puras del stack: puertos del pool, php.ini, nginx.conf y la elección del
/// PHP global. Nada arranca procesos ni toca la red.
/// </summary>
public class StackConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-stack-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LaCarpetaDeProyectos_EsWwwDentroDeLaRaiz_SalvoLaCompartidaDeLinux()
    {
        // C:\roost\www, no C:\www: todo lo de Roost cuelga de su raíz (con ROOST_HOME o sin él). En Linux, sin
        // ROOST_HOME y con /var/www/roost del usuario (roost www init), los proyectos van ahí.
        var paths = RoostPaths.Default();
        var shared = OperatingSystem.IsLinux()
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RoostPaths.HomeVariable))
            && Directory.Exists(RoostPaths.LinuxSharedWww) && Posix.CanWrite(RoostPaths.LinuxSharedWww);
        if (!shared && paths.WwwDir is not null) return;   // el roost.toml de esta máquina fija [paths] www
        Assert.Equal(shared ? RoostPaths.LinuxSharedWww : Path.Combine(paths.Root, "www"), paths.Www);
        Assert.Equal(Path.Combine(_dir, "www"), RoostPaths.ForRoot(_dir).Www);
    }

    [Fact]
    public void RoostPaths_ConRoostHome_LaWwwVaDebajo_SalvoQueSuTomlDigaOtra()
    {
        Directory.CreateDirectory(_dir);
        Func<string, string?> env = v => v == RoostPaths.HomeVariable ? _dir : null;

        // Aunque /var/www/roost fuera escribible, con ROOST_HOME no se mira: todo va bajo esa carpeta.
        var paths = RoostPaths.Default(env, _ => true);
        Assert.Equal(Path.Combine(_dir, "www"), paths.Www);
        Assert.True(paths.WwwIsFallback);

        var www = Path.Combine(_dir, "proyectos");
        RoostConfig.SetWww(Path.Combine(_dir, "roost.toml"), www);
        var custom = RoostPaths.Default(env, _ => true);
        Assert.Equal(www, custom.Www);
        Assert.False(custom.WwwIsFallback);
        Assert.Equal(_dir, custom.Root);
    }

    [LinuxFact]
    public void Linux_SinRoostHome_LaWwwCompartida_SoloSiEsEscribible()
    {
        Func<string, string?> env = v => v == "XDG_CONFIG_HOME" ? Path.Combine(_dir, "config") : null;

        Assert.Equal(RoostPaths.LinuxSharedWww, RoostPaths.Default(env, dir => dir == RoostPaths.LinuxSharedWww).Www);
        var fallback = RoostPaths.Default(env, _ => false);
        Assert.Equal(Path.Combine(fallback.Root, "www"), fallback.Www);
        Assert.True(fallback.WwwIsFallback);
    }

    [Fact]
    public void NginxConf_FueraDeWindows_LlevaDaemonOff_ParaQueElSupervisorSeaSuPadre()
    {
        var input = new NginxConfInput("/r/bin/nginx/1.30.6/conf/mime.types", "/r/bin/nginx/1.30.6/conf/fastcgi_params", "/r/run", "/r/logs", "/r/www",
            8765, [new NginxUpstream("php-8.4", [9840])], "php-8.4", "/r/etc/nginx/sites");
        Assert.Equal(!OperatingSystem.IsWindows(), input.DaemonOff);

        var linux = NginxConf.Render(input with { DaemonOff = true });
        Assert.StartsWith("# Generado por Roost: NO lo edites, se regenera en cada `roost up`.\ndaemon off;\nworker_processes 1;\n", linux);
        Assert.Contains("pid \"/r/run/nginx.pid\";", linux);
        Assert.DoesNotContain("user ", linux);   // sin `user`: corre como el usuario, sin root

        var windows = NginxConf.Render(input with { DaemonOff = false });
        Assert.DoesNotContain("daemon", windows);
        Assert.StartsWith("# Generado por Roost: NO lo edites, se regenera en cada `roost up`.\nworker_processes 1;\n", windows);
    }

    [Fact]
    public void PuertosDenegados_ElMensajeNoDiceOcupado_YSugiereElAlto()
    {
        var message = StackPlanner.DeniedPortMessage(80, "[web] port", 8080, @"C:\x\roost.toml");
        Assert.StartsWith("El puerto 80 esta por debajo del 1024 y en Linux un usuario normal no puede abrirlo (no es que este ocupado).", message);
        Assert.Contains(@"[web] port = 8080 en C:\x\roost.toml", message);
        Assert.Contains("setcap", message);

        Assert.Equal(80, StackPlanner.DeniedBind("nginx: [emerg] bind() to 127.0.0.1:80 failed (13: Permission denied)\nnginx: configuration file x test failed"));
        Assert.Null(StackPlanner.DeniedBind("nginx: [emerg] bind() to 127.0.0.1:80 failed (98: Address already in use)"));
        Assert.Null(StackPlanner.DeniedBind(null));

        var config = new RoostConfig { HttpPort = 80, HttpsPort = 443 };
        Assert.Equal(("[web] port", 8080), StackPlanner.SuggestionFor(80, config));
        Assert.Equal(("[web] https_port", 8443), StackPlanner.SuggestionFor(443, config));
    }

    [Fact]
    public void Probe_UnPuertoLibre_YUnoQueTenemosNosotros()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Equal(PortState.Busy, StackPlanner.Probe(port));
            Assert.Equal([port], StackPlanner.BusyPorts([port]));
            Assert.Empty(StackPlanner.DeniedPorts([port]));
        }
        finally
        {
            listener.Stop();
        }
    }

    [LinuxFact]
    public void Linux_Probe_UnPuertoBajoComoUsuario_EsDenegado_NoOcupado()
    {
        if (Posix.Uid == 0) return;   // root sí puede
        // El 1 nunca lo tiene nadie: como usuario normal, EACCES.
        Assert.Equal(PortState.Denied, StackPlanner.Probe(1));
        Assert.Equal([1], StackPlanner.DeniedPorts([1]));
        Assert.Equal([1], StackPlanner.BusyPorts([1]));   // también cuenta como «no se puede arrancar»
    }

    [Fact]
    public void PhpIni_ConElBundleDeCa_ParaCurl_YOpensslConElAlmacenDeWindows()
    {
        var available = new HashSet<string>(["curl", "openssl"], StringComparer.OrdinalIgnoreCase);
        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\roost\bin\php\8.4.27-nts-x64", available, @"C:\roost\etc\php\conf.d",
            @"C:\roost\logs\php-8.4-error.log", caBundle: @"C:\roost\etc\php\cacert.pem");
        Assert.Contains("curl.cainfo = \"C:\\roost\\etc\\php\\cacert.pem\"", ini);
        Assert.DoesNotContain("openssl.cafile", ini);   // los streams siguen con el almacén de Windows

        Assert.DoesNotContain("curl.cainfo", PhpIni.Render("8.4.27-nts-x64", @"C:\x", available, @"C:\x", @"C:\x.log"));
    }

    [Fact]
    public void CaBundle_LasDeMozilla_YLaDeRoostAlFinalSiExiste()
    {
        var mozilla = CaBundle.Mozilla();
        Assert.True(mozilla.Split("BEGIN CERTIFICATE").Length > 100);   // la lista de curl.se, empaquetada

        Directory.CreateDirectory(_dir);
        var ca = Path.Combine(_dir, "roost-ca.crt");
        Assert.DoesNotContain("Roost Local CA", CaBundle.Compose(mozilla, ca));

        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
        var bundle = CaBundle.Compose(mozilla, ca);
        Assert.EndsWith("Roost Local CA\n==============\n-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n", bundle);
    }

    [Fact]
    public void InstanceTag_VacioSinRoostHome_YUnoPorRaizConEl()
    {
        // Sin ROOST_HOME, los nombres de la tubería y los mutex no cambian (la Roost de verdad).
        Assert.Equal("", RoostPaths.InstanceTag(_ => null));

        var a = RoostPaths.InstanceTag(_ => Path.Combine(_dir, "a"));
        Assert.Matches("^-[0-9a-f]{8}$", a);
        Assert.Equal(a, RoostPaths.InstanceTag(_ => Path.Combine(_dir, "a") + Path.DirectorySeparatorChar));
        Assert.NotEqual(a, RoostPaths.InstanceTag(_ => Path.Combine(_dir, "b")));

        // Con unas rutas: las de siempre, sin etiqueta; otra raíz (la de un test), con la suya, aunque
        // no haya ROOST_HOME. Así el cliente de un test nunca habla con el daemon de verdad.
        if (Environment.GetEnvironmentVariable(RoostPaths.HomeVariable) is null)
            Assert.Equal("", RoostPaths.InstanceTag(RoostPaths.Default()));
        Assert.Equal(a, RoostPaths.InstanceTag(new RoostPaths(Path.Combine(_dir, "a"), Path.Combine(_dir, "cache"))));
    }

    [Fact]
    public void PhpPool_PuertosSeparadosPorRama_ComoEnRoostMd()
    {
        Assert.Equal([9830, 9831, 9832, 9833], PhpPool.Ports(new ComponentVersion(8, 3, 36, "8.3.36"), 4));
        Assert.Equal([9740], PhpPool.Ports(new ComponentVersion(7, 4, 33, "7.4.33"), 1));
        Assert.Equal("php-8.3", PhpPool.UpstreamName(new ComponentVersion(8, 3, 36, "8.3.36")));
    }

    [Fact]
    public void PhpPool_CadaPhpCgiSeReciclaTrasUnNumeroDistinto_ParaQueNoSalganTodosALaVez()
    {
        var max = Enumerable.Range(0, 10).Select(PhpPool.MaxRequests).ToList();
        Assert.Equal(500, max[0]);
        Assert.Equal(max.Count, max.Distinct().Count());
    }

    [WindowsFact("Pendiente en Linux: Builds y PHP (L1).")]
    public void PhpIni_SoloActivaLasExtensionesQueTraeElBuild()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curl", "mbstring", "opcache", "xdebug" };

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\roost\bin\php\8.4.27-nts-x64", available, @"C:\roost\etc\php\conf.d", @"C:\roost\logs\php-8.4-error.log");

        Assert.Contains("extension=curl\n", ini);
        Assert.Contains("extension=mbstring\n", ini);
        Assert.DoesNotContain("extension=intl", ini);   // el build no la trae
        Assert.DoesNotContain("xdebug", ini);           // la trae, pero no se activa sola
        Assert.Contains("zend_extension=opcache", ini);
        Assert.Contains(@"extension_dir = ""C:\roost\bin\php\8.4.27-nts-x64\ext""", ini);
        Assert.Contains(@"error_log = ""C:\roost\logs\php-8.4-error.log""", ini);
    }

    [Fact]
    public void PhpIni_SinDllDeOpcache_NoLaCarga_PeroLaConfigura()
    {
        // PHP 8.5 trae OPcache dentro del binario: no hay php_opcache.dll.
        var ini = PhpIni.Render("8.5.12-nts-x64", @"C:\php", new HashSet<string>(), @"C:\confd", @"C:\log");

        Assert.DoesNotContain("zend_extension=opcache", ini);
        Assert.Contains("opcache.enable = 1", ini);
    }

    [Fact]
    public void PhpIni_ConEleccionDelUsuario_ActivaYQuitaSoloLoQueTraeElBuild()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curl", "mbstring", "opcache", "soap", "xsl" };
        var choice = PhpExtensionChoice.None
            .With("soap", enabled: true)
            .With("ldap", enabled: true)       // el build no la trae: no se carga
            .With("mbstring", enabled: false);

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\php", available, @"C:\confd", @"C:\log", choice);

        Assert.Contains("extension=curl\n", ini);
        Assert.Contains("extension=soap\n", ini);
        Assert.DoesNotContain("extension=mbstring", ini);
        Assert.DoesNotContain("ldap", ini);
        Assert.DoesNotContain("xsl", ini);
        Assert.Contains("zend_extension=opcache", ini);
    }

    [Fact]
    public void PhpIni_QuitarOpcache_LaDescargaYLaApaga()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "opcache" };

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\php", available, @"C:\confd", @"C:\log", PhpExtensionChoice.None.With("opcache", enabled: false));

        Assert.DoesNotContain("zend_extension=opcache", ini);
        Assert.Contains("opcache.enable = 0", ini);
    }

    [Fact]
    public void PhpExtensionChoice_SoloGuardaLoQueSeApartaDeLoDeSerie_YSobreviveAlDisco()
    {
        var file = Path.Combine(_dir, "8.4.27-nts-x64", PhpExtensionChoice.FileName);
        var choice = PhpExtensionChoice.None
            .With("soap", enabled: true)
            .With("curl", enabled: true)      // ya es de serie: no se apunta
            .With("intl", enabled: false)
            .With("soap", enabled: false);    // vuelve a lo de serie

        choice.Save(file);
        var loaded = PhpExtensionChoice.Load(file);

        Assert.Empty(loaded.On);
        Assert.Equal(["intl"], loaded.Off);
        Assert.Same(PhpExtensionChoice.None, PhpExtensionChoice.Load(Path.Combine(_dir, "no-existe.json")));
    }

    [Fact]
    public void PhpIni_AvailableExtensions_LeeLasDllDeExt()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ext"));
        File.WriteAllText(Path.Combine(_dir, "ext", "php_curl.dll"), "");
        File.WriteAllText(Path.Combine(_dir, "ext", "php_pdo_mysql.dll"), "");
        File.WriteAllText(Path.Combine(_dir, "ext", "libsasl.dll"), ""); // no es una extension
        File.WriteAllText(Path.Combine(_dir, "ext", "php_zend_test.dll"), ""); // solo para los tests de PHP

        var available = PhpIni.AvailableExtensions(_dir);

        Assert.Equal(["curl", "pdo_mysql"], available.Order());
    }

    [Fact]
    public void NginxConf_UpstreamPuertoYRutasConBarrasNormales()
    {
        var conf = NginxConf.Render(new NginxConfInput(
            @"C:\roost\bin\nginx\1.30.6\conf\mime.types",
            @"C:\roost\bin\nginx\1.30.6\conf\fastcgi_params",
            @"C:\roost\run",
            @"C:\roost\logs",
            @"C:\www",
            8765,
            [new NginxUpstream("php-8.4", [9840, 9841])],
            "php-8.4",
            @"C:\roost\etc\nginx\sites"));

        // max_fails=0: al reiniciar el pool uno a uno, ninguno queda «caído» y no hay 502 (E2).
        Assert.Contains("upstream php-8.4 {\n        server 127.0.0.1:9840 max_fails=0;\n        server 127.0.0.1:9841 max_fails=0;\n    }", conf);
        Assert.Contains("        listen 127.0.0.1:8765;\n        listen [::1]:8765;\n        server_name localhost;", conf);   // solo loopback, en las dos
        Assert.DoesNotContain("listen 8765", conf);
        Assert.Contains("fastcgi_read_timeout 600s;", conf);   // Xdebug y tareas largas, sin 504 a los 60 s
        Assert.Contains("fastcgi_pass php-8.4;", conf);
        Assert.Contains("root \"C:/www\";", conf);
        Assert.Contains("include \"C:/roost/bin/nginx/1.30.6/conf/fastcgi_params\";", conf);
        Assert.Contains("include \"C:/roost/etc/nginx/sites/*.conf\";", conf);
        Assert.DoesNotContain("\\", conf.Replace(@"\.php$", ""));   // ninguna '\' en rutas
    }

    [Fact]
    public void NginxConf_SinIpv6EnLaMaquina_SoloEscuchaEn127()
    {
        var conf = NginxConf.Render(new NginxConfInput(
            @"C:\x\mime.types", @"C:\x\fastcgi_params", @"C:\roost\run", @"C:\roost\logs", @"C:\www", 8765,
            [new NginxUpstream("php-8.4", [9840])], "php-8.4", @"C:\roost\etc\nginx\sites", Ipv6: false));

        Assert.Contains("listen 127.0.0.1:8765;", conf);
        Assert.DoesNotContain("[::1]", conf);
    }

    [Fact]
    public void BusyPorts_VeLoQueEscuchaEnIpv4_YTambienLoQueSoloEscuchaEnIpv6()
    {
        // Un puerto libre en las dos no está ocupado.
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        Assert.Empty(StackPlanner.BusyPorts([port]));

        // Cogido solo en [::1] (como un servidor en [::]:80): la sonda de IPv4 no lo vería.
        if (!Loopback.HasIpv6) return;
        var onlyV6 = new TcpListener(IPAddress.IPv6Loopback, port);
        onlyV6.Start();
        try
        {
            Assert.Equal([port], StackPlanner.BusyPorts([port]));
        }
        finally
        {
            onlyV6.Stop();
        }
    }

    private static InstalledPhp Installed(string name) => new(name, @"C:\roost\bin\php\" + name, @"C:\roost\bin\php\" + name + @"\php.exe");

    private StackPlanner Planner() => new(new RoostPaths(_dir, Path.Combine(_dir, "cache")), ProcessRunner.Default);

    [Fact]
    public void ResolvePhp_SinVersion_DaLaMasNueva_YPrefiereNts()
    {
        var installed = new[] { Installed("8.4.27-ts-x64"), Installed("8.4.27-nts-x64"), Installed("8.3.36-nts-x64") };

        var php = Planner().ResolvePhp(new RoostConfig(), installed);

        Assert.Equal("8.4.27-nts-x64", php.Name);
    }

    [Fact]
    public void ResolvePhp_ConRama_DaLaDeEsaRama()
    {
        var installed = new[] { Installed("8.4.27-nts-x64"), Installed("8.3.36-nts-x64") };

        var php = Planner().ResolvePhp(new RoostConfig { PhpVersion = "8.3" }, installed);

        Assert.Equal("8.3.36-nts-x64", php.Name);
    }

    [Fact]
    public void ResolvePhp_SinNadaQueCase_FallaDiciendoComoInstalarlo()
    {
        var ex = Assert.Throws<RoostException>(() =>
            Planner().ResolvePhp(new RoostConfig { PhpVersion = "7.4" }, [Installed("8.4.27-nts-x64")]));

        Assert.Contains("roost php install 7.4", ex.Message);
        Assert.Throws<RoostException>(() => Planner().ResolvePhp(new RoostConfig(), []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
