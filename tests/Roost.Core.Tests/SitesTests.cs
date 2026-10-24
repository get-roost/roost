using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Testea los sitios *.test: el bloque del hosts (lo que puede escribir roost-helper), el .conf
/// de nginx de cada sitio, el registro, la CA local y un Prepare con dos PHP. Nada eleva
/// privilegios, ni toca el hosts real, ni el almacen de certificados.
/// </summary>
public class SitesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-sites-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private const string SystemHosts =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n192.168.1.10 nas.casa  # mi NAS\r\n";

    [Fact]
    public void HostsBlock_Apply_AnadeElBloqueAlFinal_SinTocarLoDemas()
    {
        var updated = HostsBlock.Apply(SystemHosts, ["tienda.test", "blog.test"]);

        Assert.StartsWith(SystemHosts, updated);   // lo de antes, intacto
        Assert.Contains("\r\n127.0.0.1 blog.test\r\n127.0.0.1 tienda.test\r\n# roost:end\r\n", updated);   // ordenado, con CRLF
        Assert.Equal(["blog.test", "tienda.test"], HostsBlock.ReadHosts(updated));
    }

    [Fact]
    public void HostsBlock_Apply_ReescribeElBloque_YSinNombresLoQuita()
    {
        var once = HostsBlock.Apply(SystemHosts, ["tienda.test"]);
        var twice = HostsBlock.Apply(once + "10.0.0.1 otro.lan\r\n", ["blog.test"]);

        Assert.Equal(["blog.test"], HostsBlock.ReadHosts(twice));
        Assert.Contains("10.0.0.1 otro.lan", twice);   // lo escrito despues del bloque tambien se respeta
        Assert.DoesNotContain("roost", HostsBlock.Apply(twice, []));
        Assert.Equal(once, HostsBlock.Apply(once, ["tienda.test"]));   // idempotente
    }

    [Theory]
    [InlineData("google.com")]                     // nada de dominios reales
    [InlineData("tienda.test 1.2.3.4")]            // ni inyectar otra columna
    [InlineData("tienda.test\r\n127.0.0.1 x.com")] // ni lineas de mas
    [InlineData("-tienda.test")]
    [InlineData(".test")]
    public void HostsBlock_Apply_RechazaLoQueNoEsUnPuntoTest(string name)
    {
        Assert.Throws<ArgumentException>(() => HostsBlock.Apply(SystemHosts, [name]));
    }

    [Fact]
    public void HostsBlock_BloqueSinCierre_NoSeToca()
    {
        Assert.Throws<InvalidDataException>(() => HostsBlock.Apply(SystemHosts + "# roost:begin\r\n127.0.0.1 a.test\r\n", ["b.test"]));
    }

    private static SiteConf.Input Input(Site site) =>
        new(site, "php-8.3", 8765, 8443, @"C:\roost\certs\sites\tienda.crt", @"C:\roost\certs\sites\tienda.key", @"C:\roost\bin\nginx\1.30.6\conf\fastcgi_params");

    private static readonly Site Tienda = new() { Name = "tienda", Folder = @"C:\www\tienda", Root = "public" };

    [WindowsFact("Pendiente en Linux: Privilegios y confianza (L1).")]
    public void SiteConf_Nuevo_TieneHttpYHttpsSoloLocalYElFrontalFueraDeLasMarcas()
    {
        var conf = SiteConf.RenderNew(Input(Tienda));

        Assert.Contains("    listen 127.0.0.1:8765;\n    listen [::1]:8765;\n", conf);
        Assert.Contains("    listen 127.0.0.1:8443 ssl;\n    listen [::1]:8443 ssl;\n    http2 on;", conf);
        Assert.DoesNotContain("[::1]", SiteConf.RenderNew(Input(Tienda) with { Ipv6 = false }));   // máquina sin IPv6
        Assert.Contains("server_name tienda.test *.tienda.test;", conf);
        Assert.Contains("root \"C:/www/tienda/public\";", conf);
        Assert.Contains("fastcgi_pass php-8.3;", conf);
        Assert.Contains("ssl_certificate \"C:/roost/certs/sites/tienda.crt\";", conf);
        var end = conf.IndexOf(SiteConf.End, StringComparison.Ordinal);
        Assert.True(conf.IndexOf("try_files $uri $uri/ /index.php", StringComparison.Ordinal) > end); // editable
    }

    [Fact]
    public void SiteConf_Merge_RegeneraSoloElBloque_YRespetaLoDelUsuario()
    {
        var edited = SiteConf.RenderNew(Input(Tienda)).Replace(
            "location / {", "client_max_body_size 1g; # mio\n    location / {");

        var merged = SiteConf.Merge(edited, Input(Tienda with { Php = "8.4" }) with { Upstream = "php-8.4" }, "tienda.conf");

        Assert.Contains("fastcgi_pass php-8.4;", merged);
        Assert.DoesNotContain("php-8.3", merged);
        Assert.Contains("client_max_body_size 1g; # mio", merged);
        Assert.Equal(1, merged.Split(SiteConf.Begin).Length - 1);   // un solo bloque
    }

    [Fact]
    public void SiteConf_Merge_SinMarcas_NoAdivina()
    {
        Assert.Throws<RoostException>(() => SiteConf.Merge("server { }", Input(Tienda), "tienda.conf"));
    }

    [Fact]
    public void SiteRegistry_UpsertSustituye_YRemoveQuita()
    {
        var registry = new SiteRegistry(Paths);

        registry.Upsert(Tienda);
        registry.Upsert(Tienda with { Php = "7.4" });
        registry.Upsert(new Site { Name = "blog", Folder = @"C:\www\blog" });

        Assert.Equal(["blog", "tienda"], registry.Load().Select(s => s.Name));
        Assert.Equal("7.4", registry.Find("tienda")!.Php);
        Assert.True(registry.Remove("blog"));
        Assert.False(registry.Remove("blog"));
        Assert.Single(registry.Load());
    }

    [Theory]
    [InlineData("tienda", true)]
    [InlineData("mi-blog2", true)]
    [InlineData("Tienda", false)]
    [InlineData("-x", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void Site_IsValidName_SoloEtiquetasDns(string name, bool valid)
    {
        Assert.Equal(valid, Site.IsValidName(name));
    }

    [Fact]
    public void LocalCa_EmiteCertificadoDeSitio_ConSanCadenaYCaducidad()
    {
        var ca = new LocalCa(Paths);

        var (certFile, keyFile) = ca.EnsureSiteCert(Tienda);

        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(ca.CaCertFile));
        using var cert = X509Certificate2.CreateFromPemFile(certFile, keyFile);
        Assert.True(cert.HasPrivateKey);
        Assert.True(cert.MatchesHostname("tienda.test"));
        Assert.True(cert.MatchesHostname("api.tienda.test"));   // *.tienda.test
        Assert.False(cert.MatchesHostname("otra.test"));
        Assert.InRange((cert.NotAfter - DateTime.Now).TotalDays, 390, 398);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(cert));
        Assert.Contains(root.Extensions.OfType<X509BasicConstraintsExtension>(), e => e.CertificateAuthority);
    }

    [Fact]
    public void LocalCa_ReutilizaElCertificado_YSoloLoRenuevaSiSeLePide()
    {
        var ca = new LocalCa(Paths);
        ca.EnsureSiteCert(Tienda);
        var first = File.ReadAllText(ca.SiteCertFile("tienda"));

        ca.EnsureSiteCert(Tienda);
        Assert.Equal(first, File.ReadAllText(ca.SiteCertFile("tienda")));

        ca.EnsureSiteCert(Tienda, force: true);
        Assert.NotEqual(first, File.ReadAllText(ca.SiteCertFile("tienda")));
    }

    [Fact]
    public void LocalCa_LaClaveDeLaCa_SoloLaPuedeLeerElUsuario()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ca = new LocalCa(Paths);
        ca.LoadOrCreate().Dispose();

        var acl = new FileInfo(ca.CaKeyFile).GetAccessControl();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(acl.AreAccessRulesProtected);   // sin herencia
        Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rules[0].IdentityReference);
    }

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

    [Fact]
    public void QuitarPhp_NiElGlobal_NiElQueNecesitaUnSitio_SalvoQueQuedeOtroDeSuRama()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var php74 = FakePhp("7.4.33-nts-x64");
        var planner = new StackPlanner(Paths, new NginxTestOk());
        var config = new RoostConfig { PhpVersion = "8.4" };
        Site[] sites = [new() { Name = "legacy", Folder = @"C:\www\legacy", Php = "7.4" }, new() { Name = "blog", Folder = @"C:\www\blog" }];

        Assert.Contains("PHP global", planner.WhyNotRemove(php84, config, [php84, php74], sites));
        Assert.Contains("legacy", planner.WhyNotRemove(php74, config, [php84, php74], sites));

        // Con otro 7.4 instalado, legacy pasa a ese y se puede quitar.
        var php7430 = FakePhp("7.4.30-nts-x64");
        Assert.Null(planner.WhyNotRemove(php74, config, [php84, php74, php7430], sites));
    }

    [Fact]
    public void Prepare_ConSitios_UnPoolPorRama_UnConfPorSitio_YElPuertoHttps()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var php74 = FakePhp("7.4.33-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        Site[] sites =
        [
            new() { Name = "blog", Folder = Path.Combine(_root, "www", "blog") },
            new() { Name = "legacy", Folder = Path.Combine(_root, "www", "legacy"), Php = "7.4" },
        ];
        var config = new RoostConfig { HttpPort = 8765, HttpsPort = 8443, PhpPool = 2 };

        var plan = new StackPlanner(Paths, new NginxTestOk())
            .Prepare(config, php84, nginx, null, sites, [php84, php74], new LocalCa(Paths));

        Assert.Equal(["php-8.4-0", "php-8.4-1", "php-7.4-0", "php-7.4-1", "nginx"], plan.Services.Select(s => s.Name));
        Assert.Equal("7.4.33-nts-x64", plan.Sites.Single(s => s.Site.Name == "legacy").Php.Name);
        Assert.Equal("https://blog.test:8443/", plan.Sites.Single(s => s.Site.Name == "blog").Url);
        Assert.Contains(8443, plan.Ports);
        Assert.Contains("fastcgi_pass php-7.4;", File.ReadAllText(Path.Combine(Paths.NginxSites, "legacy.conf")));
        Assert.True(File.Exists(new LocalCa(Paths).SiteCertFile("blog")));
    }

    [Fact]
    public void Prepare_ConServiciosDeExtensiones_VanTrasLaBaseDeDatos_YAntesDePhp()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var redis = new Services.ServiceDefinition { Name = "redis", Executable = OsNames.Exe("redis-server"), Port = 6379 };
        var config = new RoostConfig { HttpPort = 8765, PhpPool = 1 };

        var plan = new StackPlanner(Paths, new NginxTestOk()).Prepare(config, php84, nginx, extraServices: [redis]);

        Assert.Equal(["redis", "php-8.4-0", "nginx"], plan.Services.Select(s => s.Name));
        Assert.Contains(6379, plan.Ports);   // se comprueba que esté libre antes de arrancar
    }

    [Fact]
    public void Prepare_UnaExtensionNoPuedeRepetirElNombreDeUnServicio()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var impostor = new Services.ServiceDefinition { Name = "nginx", Executable = "otro.exe" };

        var ex = Assert.Throws<RoostException>(() =>
            new StackPlanner(Paths, new NginxTestOk()).Prepare(new RoostConfig { PhpPool = 1 }, php84, nginx, extraServices: [impostor]));
        Assert.Contains("'nginx'", ex.Message);
    }

    [Fact]
    public void Prepare_SitioQuePideUnPhpNoInstalado_FallaDiciendoComoInstalarlo()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        Site[] sites = [new() { Name = "viejo", Folder = Path.Combine(_root, "www", "viejo"), Php = "5.6" }];

        var ex = Assert.Throws<RoostException>(() => new StackPlanner(Paths, new NginxTestOk())
            .Prepare(new RoostConfig(), php84, nginx, null, sites, [php84], new LocalCa(Paths)));

        Assert.Contains("roost php install 5.6", ex.Message);
    }

    [Fact]
    public void SiteManager_Add_CreaLaCarpetaConIndex_YRepetirConservaLoQueNoSeDice()
    {
        var sites = new SiteManager(Paths);

        var first = sites.Add("Tienda");
        Assert.True(first.Created);
        Assert.True(first.FolderCreated);
        Assert.Equal(Path.Combine(Paths.Www, "tienda"), first.Site.Folder);
        Assert.True(File.Exists(Path.Combine(first.Site.Folder, "index.php")));
        Assert.True(File.Exists(new LocalCa(Paths).CaCertFile));

        Directory.CreateDirectory(Path.Combine(first.Site.Folder, "public"));
        var again = sites.Add("tienda", root: "public");
        Assert.False(again.Created);
        Assert.False(again.FolderCreated);
        Assert.Equal("public", sites.Add("tienda").Site.Root); // sin decirlo, se queda la de antes
    }

    [Fact]
    public void SiteManager_Add_ConUnPhpQueNoEstaInstalado_FallaSinRegistrarlo()
    {
        var sites = new SiteManager(Paths);

        var ex = Assert.Throws<RoostException>(() => sites.Add("legacy", php: "7.4"));
        Assert.Contains("roost php install 7.4", ex.Message);
        Assert.Empty(sites.Registry.Load());
    }

    [Fact]
    public void SiteManager_Remove_ApartaLosDosConf_YNoTocaLaCarpeta()
    {
        var sites = new SiteManager(Paths);
        var site = sites.Add("blog").Site;
        Directory.CreateDirectory(Paths.NginxSites);
        Directory.CreateDirectory(Paths.ApacheSites);
        var nginxConf = Path.Combine(Paths.NginxSites, "blog.conf");
        var apacheConf = Path.Combine(Paths.ApacheSites, "blog.conf");
        File.WriteAllText(nginxConf, "# mi regla\n");
        File.WriteAllText(apacheConf, "# mi otra regla\n");

        var removed = sites.Remove("blog.test");

        Assert.Equal("blog", removed?.Name);
        Assert.Empty(sites.Registry.Load());
        // Los dos, no solo el del servidor activo: el otro cargaría un vhost con un certificado ya borrado.
        Assert.False(File.Exists(nginxConf));
        Assert.False(File.Exists(apacheConf));
        Assert.True(File.Exists(nginxConf + ".removed"));
        Assert.True(File.Exists(apacheConf + ".removed"));
        Assert.True(Directory.Exists(site.Folder));
        Assert.Null(sites.Remove("blog"));
    }

    [Fact]
    public void SiteManager_ConfFile_EsElDelServidorActivo()
    {
        var sites = new SiteManager(Paths);
        var site = sites.Add("blog").Site;

        Assert.Equal(Path.Combine(Paths.NginxSites, "blog.conf"), sites.ConfFile(site));   // por defecto, nginx
        RoostConfig.SetWebServer(Paths.ConfigFile, RoostConfig.WebApache);
        Assert.Equal(Path.Combine(Paths.ApacheSites, "blog.conf"), sites.ConfFile(site));
        Assert.Equal(Path.Combine(Paths.NginxSites, "blog.conf"), sites.ConfFile(site, new RoostConfig()));
    }

    [Fact]
    public void SiteManager_Add_ConOtraCarpeta_VuelveADetectarLaRaiz()
    {
        var sites = new SiteManager(Paths);
        var tienda = sites.Add("tienda").Site;
        Directory.CreateDirectory(Path.Combine(tienda.Folder, "public"));
        Assert.Equal("public", sites.Add("tienda", root: "public").Site.Root);

        // La misma carpeta sin decir raíz: se queda la de antes.
        Assert.Equal("public", sites.Add("tienda", php: "global").Site.Root);

        // Otra carpeta sin public/: la raíz de antes no vale para ella.
        var plain = Path.Combine(_root, "proyectos", "tienda-v2");
        Directory.CreateDirectory(plain);
        var moved = sites.Add("tienda", plain).Site;
        Assert.Equal(plain, moved.Folder);
        Assert.Null(moved.Root);

        // Y otra con public/: se detecta.
        var laravel = Path.Combine(_root, "proyectos", "tienda-v3");
        Directory.CreateDirectory(Path.Combine(laravel, "public"));
        Assert.Equal("public", sites.Add("tienda", laravel).Site.Root);
    }

    [Fact]
    public void SiteManager_Add_UnaCarpetaRelativa_CuelgaDeLaWww()
    {
        var sites = new SiteManager(Paths);

        var site = sites.Add("blog", "blog-src").Site;

        // No del directorio desde el que se ejecute el comando.
        Assert.Equal(Path.Combine(Paths.Www, "blog-src"), site.Folder);
        Assert.True(File.Exists(Path.Combine(site.Folder, "index.php")));
    }

    [Fact]
    public void SiteManager_Add_ConUnaRaizQueNoVale_NoCreaNiRegistraNada()
    {
        var sites = new SiteManager(Paths);

        var ex = Assert.Throws<RoostException>(() => sites.Add("nueva", root: "public"));

        Assert.Contains("No existe", ex.Message);
        Assert.Empty(sites.Registry.Load());
        Assert.False(Directory.Exists(Path.Combine(Paths.Www, "nueva")));   // --root se valida antes de crear la carpeta
    }

    [Fact]
    public void SiteManager_SetRoot_SubcarpetaValida_YVolverALaCarpeta()
    {
        var sites = new SiteManager(Paths);
        var tienda = sites.Add("tienda").Site;
        Directory.CreateDirectory(Path.Combine(tienda.Folder, "public"));

        var changed = sites.SetRoot("tienda.test", "public");
        Assert.Equal("public", changed.Root);
        Assert.Equal(Path.Combine(tienda.Folder, "public"), changed.DocumentRoot);
        Assert.Equal("public", sites.Find("tienda")!.Root);   // guardada

        Assert.Null(sites.SetRoot("tienda", null).Root);
        Assert.Equal("public", sites.SetRoot("tienda", "public/").Root);   // la barra final no cuenta
        Assert.Null(sites.SetRoot("tienda", ".").Root);
        Assert.Equal("public", sites.SetRoot("tienda", "./public").Root);
        Assert.Null(sites.SetRoot("tienda", "").Root);
        Assert.Equal(tienda.Folder, sites.Find("tienda")!.DocumentRoot);
    }

    [Fact]
    public void SiteManager_SetRoot_RechazaLoQueQuedaFuera_YLoQueNoExiste()
    {
        var sites = new SiteManager(Paths);
        var tienda = sites.Add("tienda").Site;
        var otra = Path.Combine(_root, "otra");
        Directory.CreateDirectory(otra);

        Assert.Contains("fuera", Assert.Throws<RoostException>(() => sites.SetRoot("tienda", "..")).Message);
        Assert.Contains("fuera", Assert.Throws<RoostException>(() => sites.SetRoot("tienda", "../otra")).Message);
        Assert.Contains("fuera", Assert.Throws<RoostException>(() => sites.SetRoot("tienda", otra)).Message);   // absoluta, de otro sitio
        Assert.Contains("No existe", Assert.Throws<RoostException>(() => sites.SetRoot("tienda", "dist")).Message);
        Assert.Contains("No existe", Assert.Throws<RoostException>(() => sites.SetRoot("tienda", "public/../dist")).Message);
        Assert.Throws<RoostException>(() => sites.SetRoot("nadie", "public"));

        Assert.Null(sites.Find("tienda")!.Root);   // nada de eso se guardó
        Assert.False(Directory.Exists(Path.Combine(tienda.Folder, "dist")));
    }

    [Fact]
    public void SiteManager_SetRoot_UnaRutaAbsolutaDentro_SeGuardaRelativa()
    {
        var sites = new SiteManager(Paths);
        var tienda = sites.Add("tienda").Site;
        var build = Path.Combine(tienda.Folder, "public", "build");
        Directory.CreateDirectory(build);

        var changed = sites.SetRoot("tienda", build);

        Assert.Equal("public/build", changed.Root);   // relativa y con barras, también en el sites.json
        Assert.Contains("\"root\": \"public/build\"", File.ReadAllText(sites.Registry.FilePath));
        Assert.Equal(build, changed.DocumentRoot);
    }

    [Fact]
    public void Prepare_ApartaLosConfDeSitiosQueYaNoExisten()
    {
        var php84 = FakePhp("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        Site[] sites = [new() { Name = "blog", Folder = Path.Combine(_root, "www", "blog") }];
        Directory.CreateDirectory(Paths.NginxSites);
        var orphan = Path.Combine(Paths.NginxSites, "viejo.conf");   // lo dejó una versión anterior al quitar el sitio
        File.WriteAllText(orphan, "server { ssl_certificate \"ya-no-existe.crt\"; }\n");

        new StackPlanner(Paths, new NginxTestOk()).Prepare(new RoostConfig { HttpPort = 8765, PhpPool = 1 }, php84, nginx, null, sites, [php84], new LocalCa(Paths));

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(orphan + ".removed"));
        Assert.True(File.Exists(Path.Combine(Paths.NginxSites, "blog.conf")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
