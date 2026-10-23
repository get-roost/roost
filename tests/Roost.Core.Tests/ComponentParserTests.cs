using Roost.Core.Models;
using Roost.Core.Scanning;

namespace Roost.Core.Tests;

/// <summary>
/// El parser es la pieza pura (no ejecuta binarios), así que es la que más se testea:
/// se le dan salidas de ejemplo de <c>php -v</c> / <c>mysqld --version</c> y se comprueba
/// el <see cref="DetectedComponent"/> que produce.
/// </summary>
public class ComponentParserTests
{
    // ---- PHP -------------------------------------------------------------

    [Fact]
    public void ParsePhp_BannerWindows_SacaVersionSapiArchYThreadSafety()
    {
        const string salida =
            "PHP 8.3.14 (cli) (built: Dec 17 2024 22:59:40) (ZTS Visual C++ 2019 x64)\r\n" +
            "Copyright (c) The PHP Group\r\n" +
            "Zend Engine v4.3.14, Copyright (c) Zend Technologies\r\n";

        var c = ComponentParser.ParsePhp(salida, @"C:\laragon\bin\php\php-8.3.14\php.exe", "Laragon", isOnPath: true);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.Php, c!.Kind);
        Assert.Equal(new[] { 8, 3, 14 }, new[] { c.Version.Major, c.Version.Minor, c.Version.Patch });
        Assert.Equal("cli", c.Sapi);
        Assert.Equal("x64", c.Architecture);
        Assert.Equal("Laragon", c.Source);
        Assert.True(c.IsOnPath);
        Assert.Equal("ts", c.Extra["threadSafety"]); // ZTS = thread-safe
    }

    [Fact]
    public void ParsePhp_BannerLinux_SinArquitectura_MarcaNts()
    {
        const string salida =
            "PHP 8.3.6 (cli) (built: Apr 15 2024 19:21:47) (NTS)\n" +
            "Copyright (c) The PHP Group\n";

        var c = ComponentParser.ParsePhp(salida, "/usr/bin/php8.3", "system", isOnPath: false);

        Assert.NotNull(c);
        Assert.Equal(8, c!.Version.Major);
        Assert.Equal(3, c.Version.Minor);
        Assert.Equal(6, c.Version.Patch);
        Assert.Equal("cli", c.Sapi);
        Assert.Null(c.Architecture); // el banner de Linux no trae arquitectura
        Assert.Equal("nts", c.Extra["threadSafety"]);
        Assert.False(c.IsOnPath);
    }

    [Fact]
    public void ParsePhp_UsaElNombreDeCarpetaComoReservaParaArchYTs()
    {
        // Banner sin arquitectura ni NTS/ZTS: la info sale del nombre de la carpeta.
        const string salida = "PHP 8.2.10 (cli) (built: Jan 1 2024)\n";

        var c = ComponentParser.ParsePhp(
            salida,
            @"C:\laragon\bin\php\php-8.2.10-nts-Win32-vs16-x64\php.exe",
            "Laragon",
            isOnPath: false,
            folderHint: "php-8.2.10-nts-Win32-vs16-x64");

        Assert.NotNull(c);
        Assert.Equal("x64", c!.Architecture);
        Assert.Equal("nts", c.Extra["threadSafety"]);
    }

    [Fact]
    public void ParsePhp_DetectaSapiFpm()
    {
        var c = ComponentParser.ParsePhp(
            "PHP 8.3.6 (fpm-fcgi) (built: Apr 15 2024) (NTS)",
            "/usr/sbin/php-fpm8.3", "system", isOnPath: false);

        Assert.NotNull(c);
        Assert.Equal("fpm", c!.Sapi);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("este binario no responde a -v")]
    public void ParsePhp_SinVersion_DevuelveNull(string? salida)
    {
        Assert.Null(ComponentParser.ParsePhp(salida, "php", "PATH", false));
    }

    // ---- MySQL / MariaDB -------------------------------------------------

    [Fact]
    public void ParseDatabase_MySql_SacaVersionYArquitectura()
    {
        const string salida =
            @"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe  Ver 8.0.36 for Win64 on x86_64 (MySQL Community Server - GPL)";

        var c = ComponentParser.ParseDatabase(
            salida, ComponentKind.MySql, @"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe", "system", isOnPath: true);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.MySql, c!.Kind);
        Assert.Equal(8, c.Version.Major);
        Assert.Equal(0, c.Version.Minor);
        Assert.Equal(36, c.Version.Patch);
        Assert.Equal("x64", c.Architecture);
        Assert.Null(c.Sapi); // las bases de datos no tienen SAPI
    }

    [Fact]
    public void ParseDatabase_MariaDb_SacaVersionDesdeElSufijo()
    {
        const string salida = "mariadbd  Ver 11.4.2-MariaDB for Linux on x86_64 (Source distribution)";

        var c = ComponentParser.ParseDatabase(
            salida, ComponentKind.MariaDb, "/usr/sbin/mariadbd", "system", isOnPath: true);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.MariaDb, c!.Kind);
        Assert.Equal(11, c.Version.Major);
        Assert.Equal(4, c.Version.Minor);
        Assert.Equal(2, c.Version.Patch);
        Assert.Equal("x64", c.Architecture);
    }

    [Fact]
    public void ParseDatabase_MariaDbConNombreHeredadoMysqld_SeIdentificaPorElBanner()
    {
        // Ramas viejas de MariaDB se instalan como "mysqld" pero el banner dice MariaDB.
        const string salida = "mysqld  Ver 10.4.32-MariaDB for Win64 on AMD64";

        var c = ComponentParser.ParseDatabase(
            salida, ComponentKind.MariaDb, @"C:\laragon\bin\mariadb\mariadb-10.4.32\bin\mysqld.exe", "Laragon", false);

        Assert.NotNull(c);
        Assert.Equal(ComponentKind.MariaDb, c!.Kind);
        Assert.Equal(new[] { 10, 4, 32 }, new[] { c.Version.Major, c.Version.Minor, c.Version.Patch });
        Assert.Equal("x64", c.Architecture);
    }

    [Fact]
    public void ParseDatabase_MySqlEsperadoPeroSalidaMariaDb_DevuelveNull()
    {
        // Cruce de coherencia: no contar un MariaDB como MySQL.
        var c = ComponentParser.ParseDatabase(
            "mariadbd  Ver 11.4.2-MariaDB for Linux on x86_64",
            ComponentKind.MySql, "/usr/sbin/mariadbd", "system", false);

        Assert.Null(c);
    }

    [Fact]
    public void ParseDatabase_MariaDbEsperadoPeroSalidaMySql_DevuelveNull()
    {
        var c = ComponentParser.ParseDatabase(
            "mysqld  Ver 8.0.36 for Linux on x86_64 (MySQL Community Server - GPL)",
            ComponentKind.MariaDb, "/usr/sbin/mysqld", "system", false);

        Assert.Null(c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin version")]
    public void ParseDatabase_SinVersion_DevuelveNull(string? salida)
    {
        Assert.Null(ComponentParser.ParseDatabase(salida, ComponentKind.MySql, "mysqld", "PATH", false));
    }

    [Fact]
    public void ParseDatabase_TipoQueNoEsBaseDeDatos_DevuelveNull()
    {
        // Solo MySql/MariaDb son válidos para este parser.
        Assert.Null(ComponentParser.ParseDatabase(
            "mysqld  Ver 8.0.36 for Linux on x86_64", ComponentKind.Php, "x", "PATH", false));
    }
}
