using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>state/db-passwords.json: una contrasena de root por instancia (motor y rama).</summary>
public class DbPasswordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-dbpwd-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static readonly InstalledDb Maria = new(DbEngine.MariaDb, "11.4.13", @"C:\roost\bin\mariadb\11.4.13");
    private static readonly InstalledDb MariaTwin = new(DbEngine.MariaDb, "11.4.12", @"C:\roost\bin\mariadb\11.4.12");
    private static readonly InstalledDb MySql = new(DbEngine.MySql, "8.4.11", @"C:\roost\bin\mysql\8.4.11");

    [Fact]
    public void SinFichero_NadieTieneContrasena()
    {
        var store = new DbPasswords(Paths);

        Assert.Null(store.For(Maria));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Set_GuardaPorInstancia_YLaCompartenLasVersionesDeLaMismaRama()
    {
        var store = new DbPasswords(Paths);

        store.Set(Maria, "S3cret!");
        store.Set(MySql, "otra");

        Assert.Equal("S3cret!", store.For(Maria));
        Assert.Equal("S3cret!", store.For(MariaTwin));   // los datos van por rama, y la contrasena con ellos
        Assert.Equal("otra", store.For(MySql));
        Assert.Equal("mariadb-11.4", DbPasswords.InstanceKey(Maria));
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"mariadb-11.4\": \"S3cret!\"", json);
        Assert.Contains("\"mysql-8.4\": \"otra\"", json);
    }

    [Fact]
    public void Set_VaciaONull_QuitaLaEntrada_YSinEntradasBorraElFichero()
    {
        var store = new DbPasswords(Paths);
        store.Set(Maria, "S3cret!");
        store.Set(MySql, "otra");

        store.Set(Maria, "");
        Assert.Null(store.For(Maria));
        Assert.Equal("otra", store.For(MySql));
        Assert.True(File.Exists(store.FilePath));

        store.Set(MySql, null);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void FicheroRoto_LoDiceConSuRuta()
    {
        var store = new DbPasswords(Paths);
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(store.FilePath, "{ esto no es json");

        var ex = Assert.Throws<RoostException>(() => store.For(Maria));

        Assert.Contains("db-passwords.json", ex.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
