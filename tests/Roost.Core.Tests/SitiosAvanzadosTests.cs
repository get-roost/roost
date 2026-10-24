using System.Security.Cryptography.X509Certificates;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Sitios avanzados (oleada 6): lo que puede escribir el helper elevado en el hosts con subdominios
/// (entradas hostiles incluidas), los alias, los registros por vhost, renombrar y mover, el
/// .roost.toml y las carpetas aparcadas. Todo sobre una raíz temporal: nada toca el hosts real, ni
/// el almacén de certificados, ni ejecuta nada.
/// </summary>
public class SitiosAvanzadosTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-sitios-av-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* lo que quede */ }
    }

    private string Folder(params string[] parts)
    {
        var dir = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Php(params string[] names)
    {
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(Paths.PhpBin, name));
            File.WriteAllText(Path.Combine(Paths.PhpBin, name, "php.exe"), "");
            File.WriteAllText(Path.Combine(Paths.PhpBin, name, "php-cgi.exe"), "");
        }
    }

    // C7: el hosts. HostsBlock va compilado DENTRO del helper elevado: estas son las entradas que
    // decide lo que llega al hosts del sistema.

    [Theory]
    [InlineData("tienda.test")]
    [InlineData("api.tienda.test")]
    [InlineData("v1.api.tienda.test")]
    [InlineData("a.b.c.d.test")]                    // 4 etiquetas, el máximo
    [InlineData("tienda-alias.test")]
    [InlineData("1-2.test")]
    [InlineData("xn--tinda-4ra.test")]              // punycode: ASCII
    public void HostsBlock_AdmiteSubdominiosTest(string name)
    {
        Assert.True(HostsBlock.IsValidHost(name));
        Assert.Contains($"127.0.0.1 {name}\r\n", HostsBlock.Apply("", [name]));
    }

    public static TheoryData<string> Hostiles() => new()
    {
        "tienda.test\n",                            // el $ de .NET casaba antes de un \n final
        "tienda.test\r\n",
        "tienda.test\r\n127.0.0.1 banco.com",       // otra línea
        "tienda.test\n0.0.0.0 update.microsoft.com",
        "api.tienda.test 1.2.3.4",                  // otra columna
        "api.tienda.test\t8.8.8.8",
        "tienda.test#comentario",
        "tienda.test\0",
        "localhost",
        "localhost.localdomain",
        "127.0.0.1",
        "::1",
        "1.2.3.4",
        "google.com",
        "api.google.com",
        "tienda.test.com",
        "tienda.test.",                             // FQDN con punto final
        ".test",
        "test",
        "..test",
        "a..test",
        "-a.test",
        "a-.test",
        "api.-a.test",
        "a_b.test",
        "*.tienda.test",                            // nada de comodines en el hosts
        "TIENDA.TEST",
        "Api.tienda.test",
        "tiénda.test",                              // unicode
        "tienda.tеst",                              // la «е» es cirílica
        "ｔienda.test",                             // ancho completo
        "tienda\u200b.test",                        // espacio de anchura cero
        "a.b.c.d.e.test",                           // 5 etiquetas
        "",
        " ",
        " tienda.test",
    };

    [Theory]
    [MemberData(nameof(Hostiles))]
    public void HostsBlock_RechazaEntradasHostiles(string name)
    {
        Assert.False(HostsBlock.IsValidHost(name));
        Assert.Throws<ArgumentException>(() => HostsBlock.Apply("", [name]));
    }

    [Fact]
    public void HostsBlock_LongitudesMaximas()
    {
        var label63 = new string('a', 63);
        Assert.True(HostsBlock.IsValidHost($"{label63}.test"));
        Assert.False(HostsBlock.IsValidHost($"{new string('a', 64)}.test"));   // una etiqueta de 64

        // 4 etiquetas de 63: 4·64 + 4 = 260 > 253.
        var tooLong = string.Join('.', Enumerable.Repeat(label63, 4)) + ".test";
        Assert.False(HostsBlock.IsValidHost(tooLong));
        var justFits = string.Join('.', Enumerable.Repeat(label63, 3)) + "." + new string('b', 56) + ".test";
        Assert.Equal(HostsBlock.MaxHostLength, justFits.Length);
        Assert.True(HostsBlock.IsValidHost(justFits));
    }

    [Fact]
    public void HostsBlock_ConSubdominios_EscribeUnaLineaPorNombre_YSoloEsas()
    {
        var hosts = "# Copyright (c) Microsoft\r\n127.0.0.1 localhost\r\n";
        var updated = HostsBlock.Apply(hosts, ["tienda.test", "api.tienda.test", "tienda-alias.test"]);

        Assert.StartsWith(hosts, updated);
        Assert.Equal(["api.tienda.test", "tienda-alias.test", "tienda.test"], HostsBlock.ReadHosts(updated));
        var block = updated[updated.IndexOf("# roost:begin", StringComparison.Ordinal)..];
        Assert.Equal(5, block.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // marcas + 3
    }

    // C7: alias

    private static readonly Site Tienda = new() { Name = "tienda", Folder = @"C:\www\tienda", Root = "public" };
    private static readonly Site Blog = new() { Name = "blog", Folder = @"C:\www\blog", Aliases = ["diario.test"] };

    [Theory]
    [InlineData("tienda-alias", "tienda-alias.test")]
    [InlineData(" Tienda-Alias.TEST. ", "tienda-alias.test")]
    [InlineData("api.tienda.test", "api.tienda.test")]
    [InlineData("midominio.com", "midominio.com")]     // no se convierte en midominio.com.test: no vale
    public void SiteAliases_Normaliza(string input, string expected) => Assert.Equal(expected, SiteAliases.Normalize(input));

    [Theory]
    [InlineData("tienda.test", "ya es el nombre")]
    [InlineData("blog.test", "ya es de blog.test")]
    [InlineData("diario.test", "ya es de blog.test")]
    [InlineData("api.blog.test", "subdominio de blog.test")]
    [InlineData("x.diario.test", "subdominio de diario.test")]
    [InlineData("google.com.", "no vale")]
    [InlineData("tienda.test\n", "no vale")]
    public void SiteAliases_WhyNot_DiceLoQueNoVale(string alias, string why)
    {
        var reason = SiteAliases.WhyNot(Tienda, alias, [Tienda, Blog]);
        Assert.NotNull(reason);
        Assert.Contains(why, reason);
    }

    [Fact]
    public void SiteAliases_WhyNot_ValenUnAliasYUnSubdominioPropio()
    {
        Assert.Null(SiteAliases.WhyNot(Tienda, "tienda-alias.test", [Tienda, Blog]));
        Assert.Null(SiteAliases.WhyNot(Tienda, "api.tienda.test", [Tienda, Blog]));
        // Un alias que dejaría debajo el nombre de otro sitio, no.
        var otro = new Site { Name = "otro", Folder = @"C:\www\otro", Aliases = ["x.foo.test"] };
        Assert.Contains("Debajo de foo.test", SiteAliases.WhyNot(Tienda, "foo.test", [Tienda, otro]));
        // Ni un nombre de sitio que ya es alias de otro.
        Assert.Contains("ya es un alias de blog.test", SiteAliases.WhyNotName("diario", [Tienda, Blog]));
    }

    [Fact]
    public void SiteAliases_ServerNamesYCertificado()
    {
        var site = Tienda with { Aliases = ["api.tienda.test", "v1.api.tienda.test", "tienda-alias.test", "x.tienda-alias.test"] };

        Assert.Equal(["tienda.test", "*.tienda.test", "tienda-alias.test", "*.tienda-alias.test"], SiteAliases.ServerNames(site));
        // El *.tienda.test de un certificado no vale para dos niveles: v1.api.tienda.test va aparte.
        Assert.Equal(["tienda.test", "*.tienda.test", "tienda-alias.test", "*.tienda-alias.test", "v1.api.tienda.test"], SiteAliases.CertNames(site));
        Assert.Equal(["tienda.test", "api.tienda.test", "v1.api.tienda.test", "tienda-alias.test", "x.tienda-alias.test"], site.HostNames);
    }

    private static SiteConf.Input Nginx(Site site) =>
        new(site, "php-8.4", 8765, 8443, @"C:\roost\certs\sites\tienda.crt", @"C:\roost\certs\sites\tienda.key", @"C:\roost\bin\nginx\conf\fastcgi_params");

    [Fact]
    public void SiteConf_ConAliasYRegistros()
    {
        var site = Tienda with { Aliases = ["tienda-alias.test", "api.tienda.test"] };
        var conf = SiteConf.RenderNew(Nginx(site) with { LogsDirectory = @"C:\roost\logs\sites" });

        Assert.Contains("server_name tienda.test *.tienda.test tienda-alias.test *.tienda-alias.test;", conf);
        Assert.Contains("    access_log \"C:/roost/logs/sites/tienda-access.log\";", conf);
        Assert.Contains("    error_log \"C:/roost/logs/sites/tienda-error.log\" warn;", conf);
        // Dentro del bloque de Roost, y antes del root.
        var end = conf.IndexOf(SiteConf.End, StringComparison.Ordinal);
        Assert.True(conf.IndexOf("access_log", StringComparison.Ordinal) < end);
        Assert.True(conf.IndexOf("access_log", StringComparison.Ordinal) < conf.IndexOf("    root ", StringComparison.Ordinal));
        // Sin carpeta de registros, como antes.
        Assert.DoesNotContain("access_log", SiteConf.RenderNew(Nginx(Tienda)));
    }

    [Fact]
    public void ApacheSiteConf_ConAliasYRegistros_EnLosDosVirtualHost()
    {
        var site = Tienda with { Aliases = ["tienda-alias.test"] };
        var input = new ApacheSiteConf.Input(site, "php-8.4", 8765, 8443, @"C:\c.crt", @"C:\c.key") { LogsDirectory = @"C:\roost\logs\sites" };
        var conf = ApacheSiteConf.RenderNew(input);

        Assert.Equal(2, conf.Split("ServerName tienda.test").Length - 1);
        Assert.Equal(2, conf.Split("ServerAlias *.tienda.test tienda-alias.test *.tienda-alias.test").Length - 1);
        Assert.Equal(2, conf.Split("ErrorLog \"C:/roost/logs/sites/tienda-error.log\"").Length - 1);
        Assert.Equal(2, conf.Split("CustomLog \"C:/roost/logs/sites/tienda-access.log\" combined").Length - 1);
        Assert.DoesNotContain("CustomLog", ApacheSiteConf.RenderNew(input with { LogsDirectory = null }));
    }

    [Fact]
    public void LocalCa_ConAlias_LosLlevaEnElSan_YSeReemiteSiCambian()
    {
        var ca = new LocalCa(Paths);
        ca.EnsureSiteCert(Tienda);
        var first = File.ReadAllText(ca.SiteCertFile("tienda"));

        var withAlias = Tienda with { Aliases = ["tienda-alias.test", "v1.api.tienda.test"] };
        ca.EnsureSiteCert(withAlias);
        Assert.NotEqual(first, File.ReadAllText(ca.SiteCertFile("tienda")));
        using (var cert = X509Certificate2.CreateFromPem(File.ReadAllText(ca.SiteCertFile("tienda"))))
        {
            Assert.True(cert.MatchesHostname("tienda-alias.test"));
            Assert.True(cert.MatchesHostname("www.tienda-alias.test"));
            Assert.True(cert.MatchesHostname("v1.api.tienda.test"));
            Assert.False(cert.MatchesHostname("v2.api.tienda.test"));
        }
        Assert.Equal(SiteAliases.CertNames(withAlias), ca.SiteCertNames("tienda"));

        // Con los mismos alias no se reemite; al quitarlos, sí.
        var second = File.ReadAllText(ca.SiteCertFile("tienda"));
        ca.EnsureSiteCert(withAlias);
        Assert.Equal(second, File.ReadAllText(ca.SiteCertFile("tienda")));
        ca.EnsureSiteCert(Tienda);
        Assert.Equal(["tienda.test", "*.tienda.test"], ca.SiteCertNames("tienda"));
    }

    [Fact]
    public void SiteManager_Alias_AnadirQuitarYElHosts()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        sites.Add("blog");

        var site = sites.AddAlias("tienda", "tienda-alias");
        sites.AddAlias("tienda.test", "api.tienda.test");
        Assert.Equal(["tienda-alias.test", "api.tienda.test"], sites.Find("tienda")!.Aliases);
        Assert.Throws<RoostException>(() => sites.AddAlias("blog", "tienda-alias.test"));
        Assert.Throws<RoostException>(() => sites.AddAlias("tienda", "tienda-alias.test"));
        Assert.Equal(["api.tienda.test", "blog.test", "tienda-alias.test", "tienda.test"], SiteManager.HostNames(sites.Registry.Load()));

        sites.RemoveAlias("tienda", "tienda-alias.test");
        Assert.Equal(["api.tienda.test"], sites.Find("tienda")!.Aliases);
        Assert.Throws<RoostException>(() => sites.RemoveAlias("tienda", "otro.test"));
        sites.RemoveAlias("tienda", "api.tienda.test");
        Assert.Null(sites.Find("tienda")!.Aliases);
        Assert.Equal("tienda", site.Name);
        // Un sitio no puede llamarse como el alias de otro.
        sites.AddAlias("blog", "diario");
        Assert.Throws<RoostException>(() => sites.Add("diario"));
    }

    // C5: los registros

    [Fact]
    public void SiteLogs_DeQueSitioEsCadaFichero()
    {
        Assert.Equal("tienda", SiteLogs.SiteOf("tienda-access.log"));
        Assert.Equal("mi-blog", SiteLogs.SiteOf("mi-blog-error.log"));
        Assert.Null(SiteLogs.SiteOf("roostd.log"));   // solo los de logs/sites
        Assert.Null(SiteLogs.SiteOf("-access.log"));
        Assert.Equal(Path.Combine(Paths.Logs, "sites", "tienda-access.log"), SiteLogs.Access(Paths, "tienda"));
    }

    [Fact]
    public void Prepare_EscribeLosRegistrosPorSitio_YCreaSuCarpeta()
    {
        Php("8.4.27-nts-x64");
        var php = new InstalledPhp("8.4.27-nts-x64", Path.Combine(Paths.PhpBin, "8.4.27-nts-x64"), Path.Combine(Paths.PhpBin, "8.4.27-nts-x64", "php.exe"));
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        Site[] sites = [new() { Name = "tienda", Folder = Folder("www", "tienda"), Aliases = ["api.tienda.test"] }];

        new StackPlanner(Paths, new NginxOk()).Prepare(new RoostConfig { HttpPort = 8765, HttpsPort = 8443, PhpPool = 1 }, php, nginx, null, sites, [php], new LocalCa(Paths));

        Assert.True(Directory.Exists(SiteLogs.Directory(Paths)));
        var conf = File.ReadAllText(Path.Combine(Paths.NginxSites, "tienda.conf"));
        Assert.Contains($"access_log \"{NginxConf.Nginx(SiteLogs.Directory(Paths))}/tienda-access.log\";", conf);
        // Lo de localhost, como estaba: su access_log es el general.
        Assert.Contains("nginx-access.log", File.ReadAllText(Path.Combine(Paths.Etc, "nginx", "nginx.conf")));
    }

    private sealed class NginxOk : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "nginx: configuration file test is successful";
    }

    // C2: renombrar y mover

    [Fact]
    public void Rename_LlevaTodoAlNombreNuevo_YLaBaseDeDatosSeQuedaFijada()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        sites.AddAlias("tienda", "api.tienda.test");
        sites.AddAlias("tienda", "tienda-alias.test");
        var ca = new LocalCa(Paths);
        ca.EnsureSiteCert(sites.Find("tienda")!);
        Directory.CreateDirectory(Paths.NginxSites);
        Directory.CreateDirectory(Paths.ApacheSites);
        File.WriteAllText(Path.Combine(Paths.NginxSites, "tienda.conf"), "# mio");
        File.WriteAllText(Path.Combine(Paths.ApacheSites, "tienda.conf"), "# mio");
        new GitHistory(Paths).Append("tienda", new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Pull, "main", "a", "b", true, 1, null));
        Directory.CreateDirectory(Paths.Logs);
        File.WriteAllText(new GitLog(Paths).FilePath("tienda"), "=== git");
        Directory.CreateDirectory(SiteLogs.Directory(Paths));
        File.WriteAllText(SiteLogs.Access(Paths, "tienda"), "GET /");

        var renamed = sites.Rename("tienda.test", "Shop");

        Assert.Null(sites.Find("tienda"));
        var shop = sites.Find("shop")!;
        Assert.Equal(renamed.Site, shop with { Aliases = renamed.Site.Aliases });
        Assert.Equal("tienda", shop.Database);                     // la de antes, fijada
        Assert.Equal(["api.shop.test", "tienda-alias.test"], shop.Aliases);
        Assert.Equal([("api.tienda.test", "api.shop.test")], renamed.AliasesMoved);
        Assert.Equal(Path.Combine(Paths.Www, "tienda"), shop.Folder);   // la carpeta no cambia
        Assert.True(File.Exists(Path.Combine(Paths.NginxSites, "tienda.conf.removed")));
        Assert.True(File.Exists(Path.Combine(Paths.ApacheSites, "tienda.conf.removed")));
        Assert.Equal(2, renamed.ConfsKept.Count);
        Assert.False(File.Exists(ca.SiteCertFile("tienda")));
        Assert.Equal(["shop.test", "*.shop.test", "tienda-alias.test", "*.tienda-alias.test"], ca.SiteCertNames("shop"));   // el nuevo, ya emitido
        Assert.Single(new GitHistory(Paths).Load("shop"));
        Assert.True(File.Exists(new GitLog(Paths).FilePath("shop")));
        Assert.True(File.Exists(SiteLogs.Access(Paths, "shop")));
    }

    [Fact]
    public void Rename_NoPisaNiAOtroSitioNiAUnAlias()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        sites.Add("blog");
        sites.AddAlias("blog", "diario");

        Assert.Contains("Ya hay un sitio blog.test", Assert.Throws<RoostException>(() => sites.Rename("tienda", "blog")).Message);
        Assert.Contains("alias de blog.test", Assert.Throws<RoostException>(() => sites.Rename("tienda", "diario")).Message);
        Assert.Throws<RoostException>(() => sites.Rename("tienda", "Mi Tienda"));
        Assert.Throws<RoostException>(() => sites.Rename("nada", "otra"));
        Assert.NotNull(sites.Find("tienda"));   // no se ha tocado
    }

    [Fact]
    public void Move_SoloApuntar_YMoverLaCarpeta()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        var other = Folder("proyectos", "tienda");
        Directory.CreateDirectory(Path.Combine(other, "public"));

        // Solo apuntar: la carpeta tiene que existir, y la de antes no se toca.
        Assert.Throws<RoostException>(() => sites.Move("tienda", Path.Combine(_root, "no-existe"), moveFiles: false));
        var pointed = sites.Move("tienda", other, moveFiles: false);
        Assert.False(pointed.FilesMoved);
        Assert.Equal(other, pointed.Site.Folder);
        Assert.Equal("public", pointed.Site.Root);
        Assert.True(Directory.Exists(Path.Combine(Paths.Www, "tienda")));

        // Mover: la de destino tiene que no existir (o estar vacía) y no estar dentro.
        File.WriteAllText(Path.Combine(other, "public", "index.php"), "<?php");
        Assert.Throws<RoostException>(() => sites.Move("tienda", Path.Combine(other, "sub"), moveFiles: true));
        var full = Folder("ocupada");
        File.WriteAllText(Path.Combine(full, "x.txt"), "");
        Assert.Throws<RoostException>(() => sites.Move("tienda", full, moveFiles: true));

        var target = Path.Combine(_root, "nuevo", "tienda");
        var moved = sites.Move("tienda", target, moveFiles: true);
        Assert.True(moved.FilesMoved);
        Assert.False(Directory.Exists(other));
        Assert.True(File.Exists(Path.Combine(target, "public", "index.php")));
        Assert.Equal("public", sites.Find("tienda")!.Root);
    }

    [Fact]
    public void Move_ElGitSoloSigueSiSeMueveLaCarpeta()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        sites.Registry.Upsert(sites.Find("tienda")! with { Git = new SiteGit { Remote = "https://github.com/acme/tienda", Branch = "main" } });

        var moved = sites.Move("tienda", Path.Combine(_root, "a", "tienda"), moveFiles: true);
        Assert.NotNull(moved.Site.Git);
        var pointed = sites.Move("tienda", Folder("b"), moveFiles: false);
        Assert.Null(pointed.Site.Git);
    }

    // C11: el .roost.toml

    [Fact]
    public void ProjectFile_LeeYEscribeConservandoLoDemas()
    {
        var text = "# mi proyecto\nphp = \"7.4\"   # el del CRM\nroot = \"web\"\nfoo = 1\n\n[extra]\nbar = \"x\"\n";
        var parsed = ProjectFile.Parse(text, ".roost.toml");
        Assert.Equal("7.4", parsed.Php);
        Assert.Equal("web", parsed.Root);
        Assert.Null(parsed.Aliases);

        var updated = ProjectFile.Render(text, parsed with { Php = "8.3", Root = null, Aliases = ["api.tienda.test"] });
        Assert.Contains("# mi proyecto", updated);
        Assert.Contains("php = \"8.3\"", updated);
        Assert.DoesNotContain("root", updated);
        Assert.Contains("aliases = [\"api.tienda.test\"]", updated);
        Assert.Contains("foo = 1", updated);
        Assert.Contains("[extra]", updated);
        Assert.True(updated.IndexOf("aliases", StringComparison.Ordinal) < updated.IndexOf("[extra]", StringComparison.Ordinal));
        Assert.Equal(["api.tienda.test"], ProjectFile.Parse(updated, "x").Aliases);

        var fresh = ProjectFile.Parse(ProjectFile.Render(null, new ProjectSettings { Php = "8.4", Db = "tienda", Aliases = ["a.test", "b.test"] }), "x");
        Assert.Equal(("8.4", "tienda"), (fresh.Php, fresh.Db));
        Assert.Equal(["a.test", "b.test"], fresh.Aliases);
        // Sin salto final, una clave nueva va en su línea.
        Assert.Equal("8.3", ProjectFile.Parse(ProjectFile.Render("root = \"public\"", new ProjectSettings { Root = "public", Php = "8.3" }), "x").Php);
    }

    [Theory]
    [InlineData("php = 7.4", "php tiene que ser un texto")]
    [InlineData("aliases = \"api.tienda.test\"", "aliases tiene que ser una lista")]
    [InlineData("aliases = [1, 2]", "lista de textos")]
    [InlineData("php = \"7.4", "no es TOML valido")]
    public void ProjectFile_LoQueNoEsDelTipoQueToca_SeDice(string text, string why)
    {
        Assert.Contains(why, Assert.Throws<RoostException>(() => ProjectFile.Parse(text, "x")).Message);
    }

    [Fact]
    public void Add_LeeElRoostToml_YLoDeLosArgumentosManda()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64");
        var dir = Folder("www", "crm");
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        File.WriteAllText(ProjectFile.PathIn(dir), "php = \"7.4\"\nroot = \"web\"\ndb = \"crm_viejo\"\naliases = [\"api.crm.test\"]\n");
        var sites = new SiteManager(Paths);

        var change = sites.Add("crm");
        Assert.Equal(("7.4", "web", "crm_viejo"), (change.Site.Php, change.Site.Root, change.Site.Database));
        Assert.Equal(["api.crm.test"], change.Site.Aliases);
        Assert.NotNull(change.Project);

        Assert.Null(sites.Add("crm", php: "global").Site.Php);
        // Un PHP que no está instalado dice de dónde viene.
        File.WriteAllText(ProjectFile.PathIn(dir), "php = \"5.6\"\n");
        Assert.Contains(".roost.toml", Assert.Throws<RoostException>(() => sites.Add("crm")).Message);
    }

    [Fact]
    public void SyncProjectFiles_PoneAlDiaElSitio_YDiceLoQueNoPuede()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64");
        var sites = new SiteManager(Paths);
        sites.Add("tienda");
        sites.Add("blog");
        var tienda = sites.Find("tienda")!;
        Assert.False(sites.SyncProjectFiles().Changed);   // sin .roost.toml no hace nada

        File.WriteAllText(ProjectFile.PathIn(tienda.Folder), "php = \"7.4\"\naliases = [\"api.tienda.test\"]\n");
        File.WriteAllText(ProjectFile.PathIn(sites.Find("blog")!.Folder), "php = \"5.6\"\nroot = \"no-existe\"\n");

        var sync = sites.SyncProjectFiles();
        Assert.True(sync.NamesChanged);
        Assert.Equal(["PHP 7.4", "alias api.tienda.test"], sync.Changes.Select(c => c.What));
        Assert.Equal(2, sync.Warnings.Count);
        Assert.Contains(sync.Warnings, w => w.Contains("roost php install 5.6"));
        Assert.Equal("7.4", sites.Find("tienda")!.Php);
        Assert.Null(sites.Find("blog")!.Php);
        Assert.False(sites.SyncProjectFiles().Changed);   // idempotente
    }

    [Fact]
    public void KeepProjectFile_SoloCambiaLasClavesQueTiene()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64");
        var sites = new SiteManager(Paths);
        var site = sites.Add("tienda").Site;
        File.WriteAllText(ProjectFile.PathIn(site.Folder), "# del equipo\nphp = \"7.4\"\n");
        sites.SyncProjectFiles();

        // Pasar al global desde la app no lo deshace el siguiente up: el fichero dice "global".
        var global = sites.Find("tienda")! with { Php = null };
        sites.Registry.Upsert(global);
        sites.KeepProjectFile(global, ProjectKeys.Php);
        var text = File.ReadAllText(ProjectFile.PathIn(site.Folder));
        Assert.Contains("# del equipo", text);
        Assert.Contains("php = \"global\"", text);
        Assert.DoesNotContain("root", text);   // lo que no tenía, no lo añade
        Assert.False(sites.SyncProjectFiles().Changed);

        // Lo que el fichero dice y aún no se ha aplicado no lo pisa otro cambio (aquí, un alias).
        File.WriteAllText(ProjectFile.PathIn(site.Folder), "php = \"7.4\"\naliases = []\n");
        sites.AddAlias("tienda", "api.tienda.test");
        var after = ProjectFile.Read(site.Folder)!;
        Assert.Equal("7.4", after.Php);
        Assert.Equal(["api.tienda.test"], after.Aliases);

        // SaveProjectFile sí lo escribe todo, lo que tiene el registro.
        File.WriteAllText(ProjectFile.PathIn(site.Folder), "# del equipo\n");
        sites.SaveProjectFile("tienda");
        var saved = ProjectFile.Read(site.Folder)!;
        Assert.Equal(["api.tienda.test"], saved.Aliases);
        Assert.Null(saved.Php);   // el global: sin clave
        Assert.Contains("# del equipo", File.ReadAllText(ProjectFile.PathIn(site.Folder)));
    }

    [Fact]
    public void TerminalPhp_ElRoostTomlValeComoUnPhpVersion()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64", "8.3.36-nts-x64");
        var project = Folder("www", "crm");
        var src = Folder("www", "crm", "src");
        File.WriteAllText(ProjectFile.PathIn(project), "php = \"7.4\"\n");

        var choice = TerminalPhp.Resolve(Paths, src, null);
        Assert.Equal("7.4.33-nts-x64", choice.Php.Name);
        Assert.Contains(".roost.toml pide 7.4", choice.Reason);

        // En la misma carpeta, el .php-version primero; más cerca, el que esté más cerca.
        File.WriteAllText(Path.Combine(project, TerminalPhp.VersionFile), "8.3");
        Assert.Equal("8.3.36-nts-x64", TerminalPhp.Resolve(Paths, src, null).Php.Name);
        File.WriteAllText(ProjectFile.PathIn(src), "php = \"global\"\n");
        Assert.Equal("8.4.27-nts-x64", TerminalPhp.Resolve(Paths, src, null).Php.Name);
        // Uno sin php no cuenta.
        File.WriteAllText(ProjectFile.PathIn(src), "root = \"public\"\n");
        Assert.Equal("8.3.36-nts-x64", TerminalPhp.Resolve(Paths, src, null).Php.Name);
    }

    // C3: carpetas aparcadas

    [Fact]
    public void Park_CadaSubcarpetaEsUnSitio_YLasQueNoValenSeDicen()
    {
        var parked = Folder("proyectos");
        Folder("proyectos", "tienda");
        Folder("proyectos", "Blog");
        Folder("proyectos", "crm-2024");
        Folder("proyectos", "Mi Proyecto");          // espacio: no vale
        Folder("proyectos", "ñandu");                // no ASCII: no vale
        Folder("proyectos", ".git");                 // de trabajo: ni se dice
        var named = Folder("proyectos", "Cosa_Rara");
        File.WriteAllText(ProjectFile.PathIn(named), "name = \"cosa\"\n");
        var sites = new SiteManager(Paths);

        var sync = sites.Park(parked);

        Assert.Equal(["blog", "cosa", "crm-2024", "tienda"], sync.Added.Select(s => s.Name).Order());
        Assert.All(sync.Added, s => Assert.Equal(parked, s.ParkedIn));
        Assert.Equal(2, sync.Skipped.Count);
        Assert.Contains(sync.Skipped, s => s.Folder.EndsWith("Mi Proyecto") && s.Reason.Contains("no vale"));
        Assert.Equal([parked], sites.Parked.Load());
        Assert.False(sites.SyncParked().Changed);   // idempotente
        Assert.Equal(2, sites.SyncParked().Skipped.Count);
    }

    [Fact]
    public void SyncParked_UnaCarpetaNuevaEntra_YUnaBorradaSale()
    {
        var parked = Folder("proyectos");
        Folder("proyectos", "tienda");
        var sites = new SiteManager(Paths);
        sites.Park(parked);
        var cert = new LocalCa(Paths);
        cert.EnsureSiteCert(sites.Find("tienda")!);

        Folder("proyectos", "nuevo");
        Directory.Delete(Path.Combine(parked, "tienda"));
        var sync = sites.SyncParked();

        Assert.Equal(["nuevo"], sync.Added.Select(s => s.Name));
        Assert.Equal(["tienda"], sync.Removed.Select(s => s.Name));
        Assert.Null(sites.Find("tienda"));
        Assert.False(File.Exists(cert.SiteCertFile("tienda")));
    }

    [Fact]
    public void SyncParked_ConflictosYLaCarpetaQueNoEsta()
    {
        var sites = new SiteManager(Paths);
        sites.Add("tienda");   // www/tienda, a mano
        var parked = Folder("proyectos");
        Folder("proyectos", "tienda");
        Folder("proyectos", "blog");

        var sync = sites.Park(parked);
        Assert.Equal(["blog"], sync.Added.Select(s => s.Name));
        Assert.Contains(sync.Skipped, s => s.Reason.Contains("ya hay un sitio tienda.test"));
        Assert.Null(sites.Find("tienda")!.ParkedIn);   // el de a mano se queda como estaba

        // Un disco que no está: sus sitios se quedan.
        Directory.Move(parked, parked + "-fuera");
        var away = sites.SyncParked();
        Assert.Empty(away.Removed);
        Assert.NotNull(sites.Find("blog"));
        Assert.Contains(away.Skipped, s => s.Reason.Contains("no esta ahora"));
        Directory.Move(parked + "-fuera", parked);

        // Renombrado a mano, no se vuelve a dar de alta con el nombre de su carpeta.
        sites.Rename("blog", "diario");
        Assert.False(sites.SyncParked().Changed);
    }

    [Fact]
    public void Park_NoAnida_YUnparkSoloQuitaLosSuyos()
    {
        var parked = Folder("proyectos");
        Folder("proyectos", "tienda", "public");
        var sites = new SiteManager(Paths);
        sites.Add("blog");
        sites.Park(parked);

        Assert.Throws<RoostException>(() => sites.Park(Path.Combine(parked, "tienda")));
        Assert.Throws<RoostException>(() => sites.Park(_root));
        Assert.Throws<RoostException>(() => sites.Park(Path.Combine(_root, "no-existe")));
        Assert.Equal("public", sites.Find("tienda")!.Root);

        var removed = sites.Unpark(parked + Path.DirectorySeparatorChar);
        Assert.Equal(["tienda"], removed.Select(s => s.Name));
        Assert.NotNull(sites.Find("blog"));
        Assert.True(Directory.Exists(Path.Combine(parked, "tienda")));   // la carpeta no se toca
        Assert.Empty(sites.Parked.Load());
        Assert.Throws<RoostException>(() => sites.Unpark(parked));
    }

    [Fact]
    public async Task ParkWatcher_AvisaUnaVezPorRafaga()
    {
        var parked = Folder("vigilada");
        using var watcher = new ParkWatcher(TimeSpan.FromMilliseconds(300));
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);
        watcher.Watch([parked, Path.Combine(_root, "no-existe")]);
        Assert.Equal([parked], watcher.Folders);

        for (var i = 0; i < 5; i++) Directory.CreateDirectory(Path.Combine(parked, $"sitio{i}"));
        for (var i = 0; i < 50 && Volatile.Read(ref count) == 0; i++) await Task.Delay(100);
        await Task.Delay(600);
        Assert.Equal(1, Volatile.Read(ref count));

        Directory.Delete(Path.Combine(parked, "sitio0"));
        for (var i = 0; i < 50 && Volatile.Read(ref count) == 1; i++) await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref count));

        watcher.Watch([]);
        Assert.Empty(watcher.Folders);
    }
}
