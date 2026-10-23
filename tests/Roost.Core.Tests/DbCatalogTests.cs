using Roost.Core.Install;
using Roost.Core.Models;

namespace Roost.Core.Tests;

/// <summary>
/// Testea como se elige base de datos: el <c>motor@version</c> que escribe el usuario, el
/// parseo de la API de MariaDB (recortes reales del 22-oct-2026) y el catalogo fijado de MySQL.
/// </summary>
public class DbCatalogTests
{
    private const string MajorsJson = """
        {"major_releases": [
          {"release_id": "13.1", "release_name": "13.1", "release_status": "RC", "release_support_type": "Rolling", "release_eol_date": null},
          {"release_id": "12.3", "release_name": "12.3", "release_status": "Stable", "release_support_type": "Long Term Support", "release_eol_date": "2029-06-12"},
          {"release_id": "11.4", "release_name": "11.4", "release_status": "Stable", "release_support_type": "Long Term Support", "release_eol_date": "2029-05-29"}
        ]}
        """;

    private const string LatestJson = """
        {"releases": {"11.4.13": {"release_id": "11.4.13", "files": [
          {"file_name": "mariadb-11.4.13-winx64.msi", "checksum": {"sha256sum": "aaaa"},
           "file_download_url": "http://downloads.mariadb.org/rest-api/mariadb/11.4.13/mariadb-11.4.13-winx64.msi"},
          {"file_name": "mariadb-11.4.13-winx64.zip", "package_type": "ZIP file",
           "checksum": {"md5sum": "922fc670c1a538dcf2d7b3a6fa7b47ac", "sha256sum": "D62986D433EEEBFDE218560B276103831604A61E929E87F1A17F5AEBD80257E2"},
           "file_download_url": "http://downloads.mariadb.org/rest-api/mariadb/11.4.13/mariadb-11.4.13-winx64.zip"}
        ]}}}
        """;

    private static DbBuild Fake(DbEngine engine, string version, bool lts = false)
    {
        ComponentVersion.TryParse(version, out var v);
        return new DbBuild
        {
            Engine = engine, Version = v, Branch = $"{v.Major}.{v.Minor}", Lts = lts,
            FileName = "x.zip", Urls = [new Uri("https://example.test/x.zip")], Sha256 = new string('0', 64),
        };
    }

    // Catalogo de prueba: lo que habia el 22-oct-2026, sin las ramas intermedias.
    private static readonly DbBuild[] Catalog =
    [
        Fake(DbEngine.MariaDb, "13.0.2"),
        Fake(DbEngine.MariaDb, "12.3.3", lts: true),
        Fake(DbEngine.MariaDb, "11.4.13", lts: true),
        .. MySqlCatalog.Windows,
    ];

    [Theory]
    [InlineData("mariadb@11.4", DbEngine.MariaDb, "11.4")]
    [InlineData("MySQL@8", DbEngine.MySql, "8")]
    [InlineData("mysql@5.7.44", DbEngine.MySql, "5.7.44")]
    [InlineData("mariadb", DbEngine.MariaDb, null)]
    public void DbSpec_Parse_LeeMotorYVersion(string text, DbEngine engine, string? version)
    {
        var spec = DbSpec.Parse(text);

        Assert.Equal(engine, spec.Engine);
        Assert.Equal(version, spec.Version);
    }

    [Theory]
    [InlineData("postgres@16")]
    [InlineData("mariadb@latest")]
    [InlineData("")]
    public void DbSpec_Parse_TextoQueNoVale_Falla(string text)
    {
        Assert.Throws<RoostException>(() => DbSpec.Parse(text));
    }

    [Theory]
    [InlineData("mariadb", "12.3.3")]      // sin version: la LTS mas nueva, no la 13.0 rolling
    [InlineData("mariadb@13", "13.0.2")]
    [InlineData("mariadb@11.4", "11.4.13")]
    [InlineData("mysql", "8.4.11")]        // la LTS
    [InlineData("mysql@8", "8.4.11")]      // la rama 8.x mas nueva
    [InlineData("mysql@8.0", "8.0.46")]
    [InlineData("mysql@5.7", "5.7.44")]
    [InlineData("5.7", "5.7.44")]          // sin motor, si no es ambiguo
    public void Resolve_EligeLaRamaQueToca(string spec, string expected)
    {
        Assert.Equal(expected, DbCatalog.Resolve(Catalog, DbSpec.Parse(spec)).Version.ToString());
    }

    [Fact]
    public void Resolve_VersionQueNoEsLaUltima_DiceCualHay()
    {
        var ex = Assert.Throws<RoostException>(() => DbCatalog.Resolve(Catalog, DbSpec.Parse("mysql@8.4.3")));

        Assert.Contains("8.4.11", ex.Message);
    }

    [Fact]
    public void Resolve_VersionSinMotorQueValeParaLosDos_PideElMotor()
    {
        DbBuild[] catalog = [Fake(DbEngine.MariaDb, "10.6.28"), Fake(DbEngine.MySql, "10.6.1")];

        var ex = Assert.Throws<RoostException>(() => DbCatalog.Resolve(catalog, DbSpec.Parse("10.6")));

        Assert.Contains("mariadb@10.6", ex.Message);
    }

    [Fact]
    public void MariaDb_ParseMajors_LeeEstadoSoporteYEol()
    {
        var majors = MariaDbCatalog.ParseMajors(MajorsJson);

        Assert.Equal(["13.1", "12.3", "11.4"], majors.Select(m => m.Branch));
        Assert.False(majors[0].Stable);
        Assert.True(majors[1].Stable && majors[1].Lts);
        Assert.Equal(new DateOnly(2029, 6, 12), majors[1].Eol);
    }

    [Fact]
    public void MariaDb_ParseLatest_DaElZipConShaYUrlsHttps()
    {
        var major = new MariaDbCatalog.Major("11.4", "Stable", "Long Term Support", new DateOnly(2029, 5, 29));

        var build = MariaDbCatalog.ParseLatest(LatestJson, major)!;

        Assert.Equal("11.4.13", build.Version.ToString());
        Assert.Equal("mariadb-11.4.13-winx64.zip", build.FileName);   // el zip, no el msi
        Assert.Equal("d62986d433eeebfde218560b276103831604a61e929e87f1a17f5aebd80257e2", build.Sha256);
        Assert.True(build.Lts);
        Assert.Equal("https://downloads.mariadb.org/rest-api/mariadb/11.4.13/mariadb-11.4.13-winx64.zip", build.Urls[0].ToString());
        Assert.Equal("https://archive.mariadb.org/mariadb-11.4.13/winx64-packages/mariadb-11.4.13-winx64.zip", build.Urls[1].ToString());
    }

    [Fact]
    public void MySql_CatalogoFijado_TieneLasTresRamasConUrlDeRespaldo()
    {
        Assert.Equal(["8.4", "8.0", "5.7"], MySqlCatalog.Windows.Select(b => b.Branch));
        Assert.All(MySqlCatalog.Windows, b =>
        {
            Assert.Matches("^[0-9a-f]{64}$", b.Sha256);
            Assert.Equal(2, b.Urls.Count);
            Assert.Contains("/archives/", b.Urls[1].ToString());
        });
        Assert.True(MySqlCatalog.Windows[0].Lts);
    }
}
