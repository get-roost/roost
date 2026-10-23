using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el instalador de bases de datos SIN red, la eleccion de la base de datos global y
/// el my.ini. Un servidor HTTP falso sirve zips hechos en memoria.
/// </summary>
public class DbInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-db-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    /// <summary>Sirve <c>body</c>, salvo en las URLs que contengan <c>missing</c>, que dan 404.</summary>
    private sealed class FakeHandler(byte[] body, string? missing = null) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(missing is not null && request.RequestUri!.ToString().Contains(missing)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("x");
        return ms.ToArray();
    }

    private static DbBuild Build(DbEngine engine, string version, byte[] zip, params string[] urls)
    {
        ComponentVersion.TryParse(version, out var v);
        return new DbBuild
        {
            Engine = engine, Version = v, Branch = $"{v.Major}.{v.Minor}",
            FileName = $"{engine.Key()}-{version}-winx64.zip",
            Urls = urls.Select(u => new Uri(u)).ToList(),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)),
        };
    }

    private static byte[] MariaDbZip(string version) =>
        Zip($"mariadb-{version}-winx64/bin/mariadbd.exe", $"mariadb-{version}-winx64/bin/mariadb-admin.exe");

    private static byte[] MySqlZip(string version) =>
        Zip($"mysql-{version}-winx64/bin/mysqld.exe", $"mysql-{version}-winx64/bin/mysqladmin.exe");

    [Fact]
    public async Task Install_QuitaLaCarpetaEnvolvente_YQuedaEnBinMotorVersion()
    {
        var zip = MariaDbZip("11.4.13");
        var installer = new DbInstaller(Paths, new HttpClient(new FakeHandler(zip)));

        var db = await installer.InstallAsync(Build(DbEngine.MariaDb, "11.4.13", zip, "https://example.test/a.zip"));

        Assert.Equal(Path.Combine(_root, "bin", "mariadb", "11.4.13", "bin", "mariadbd.exe"), db.Server);
        Assert.True(File.Exists(db.Server));
        Assert.Equal("mariadb@11.4.13", db.Spec);
    }

    [Fact]
    public async Task Install_SiLaPrimeraUrlDa404_UsaLaSiguiente()
    {
        // Lo que pasa con MySQL cuando sale una release nueva: la anterior se va de Downloads/ a archives/.
        var zip = MySqlZip("8.4.11");
        var handler = new FakeHandler(zip, missing: "/Downloads/");
        var installer = new DbInstaller(Paths, new HttpClient(handler));

        var db = await installer.InstallAsync(Build(DbEngine.MySql, "8.4.11", zip,
            "https://cdn.example.test/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
            "https://cdn.example.test/archives/mysql-8.4/mysql-8.4.11-winx64.zip"));

        Assert.True(File.Exists(db.Server));
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task ListInstalled_MariaDbPrimero_YMatchFiltraPorMotorYVersion()
    {
        foreach (var (engine, version, zip) in new[]
                 {
                     (DbEngine.MySql, "8.4.11", MySqlZip("8.4.11")),
                     (DbEngine.MariaDb, "10.11.19", MariaDbZip("10.11.19")),
                     (DbEngine.MariaDb, "11.4.13", MariaDbZip("11.4.13")),
                 })
        {
            await new DbInstaller(Paths, new HttpClient(new FakeHandler(zip))).InstallAsync(Build(engine, version, zip, "https://example.test/x"));
        }

        var installed = new DbInstaller(Paths).ListInstalled();

        Assert.Equal(["mariadb@11.4.13", "mariadb@10.11.19", "mysql@8.4.11"], installed.Select(d => d.Spec));
        Assert.Single(DbInstaller.Match(installed, DbSpec.Parse("mysql@8")));
        Assert.Equal(2, DbInstaller.Match(installed, DbSpec.Parse("mariadb")).Count);
    }

    [Fact]
    public async Task Remove_QuitaLosBinarios_PeroNuncaLosDatos()
    {
        var zip = MySqlZip("5.7.44");
        var installer = new DbInstaller(Paths, new HttpClient(new FakeHandler(zip)));
        var db = await installer.InstallAsync(Build(DbEngine.MySql, "5.7.44", zip, "https://example.test/x"));
        var data = Path.Combine(Paths.Data, "mysql", "5.7", "mysql");
        Directory.CreateDirectory(data);

        installer.Remove(db);

        Assert.False(Directory.Exists(db.Directory));
        Assert.True(Directory.Exists(data));
    }

    private static InstalledDb Db(DbEngine engine, string version) => new(engine, version, @"C:\roost\bin\" + engine.Key() + @"\" + version);

    private StackPlanner Planner() => new(Paths, ProcessRunner.Default);

    private static readonly InstalledDb[] Installed = [Db(DbEngine.MariaDb, "11.4.13"), Db(DbEngine.MySql, "8.4.11"), Db(DbEngine.MySql, "5.7.44")];

    [Fact]
    public void ResolveDb_SinConfig_DaLaPrimera_YConNone_Ninguna()
    {
        Assert.Equal("mariadb@11.4.13", Planner().ResolveDb(new RoostConfig(), Installed)!.Spec);
        Assert.Null(Planner().ResolveDb(new RoostConfig { DbEngine = RoostConfig.NoDb }, Installed));
        Assert.Null(Planner().ResolveDb(new RoostConfig(), []));
    }

    [Fact]
    public void ResolveDb_ConMotorYVersion_DaLaQueCasa()
    {
        Assert.Equal("mysql@8.4.11", Planner().ResolveDb(new RoostConfig { DbEngine = "mysql" }, Installed)!.Spec);
        Assert.Equal("mysql@5.7.44", Planner().ResolveDb(new RoostConfig { DbEngine = "mysql", DbVersion = "5.7" }, Installed)!.Spec);
    }

    [Fact]
    public void ResolveDb_SinNadaQueCase_FallaDiciendoComoInstalarla()
    {
        var ex = Assert.Throws<RoostException>(() =>
            Planner().ResolveDb(new RoostConfig { DbEngine = "mariadb", DbVersion = "10.6" }, Installed));

        Assert.Contains("roost db install mariadb@10.6", ex.Message);
    }

    [Fact]
    public void DbIni_SoloLocal_ConBarrasNormales_YSinProtocoloXEnMySql8()
    {
        var mysql8 = DbIni.Render(DbEngine.MySql, new ComponentVersion(8, 4, 11, "8.4.11"), @"C:\roost\bin\mysql\8.4.11", @"C:\roost\data\mysql\8.4", 3407);
        var mysql57 = DbIni.Render(DbEngine.MySql, new ComponentVersion(5, 7, 44, "5.7.44"), @"C:\b", @"C:\d", 3306);
        var maria = DbIni.Render(DbEngine.MariaDb, new ComponentVersion(11, 4, 13, "11.4.13"), @"C:\b", @"C:\d", 3306);

        Assert.Contains("bind-address = 127.0.0.1", mysql8);
        Assert.Contains("datadir = \"C:/roost/data/mysql/8.4\"", mysql8);   // '\' es escape en los ficheros de opciones
        Assert.Contains("port = 3407", mysql8);
        Assert.Contains("user = root", mysql8);
        Assert.Contains("mysqlx = OFF", mysql8);
        Assert.DoesNotContain("mysqlx", mysql57);   // la 5.7 no conoce la opcion y no arrancaria
        Assert.DoesNotContain("mysqlx", maria);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
