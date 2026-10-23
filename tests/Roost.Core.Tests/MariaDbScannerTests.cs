using Roost.Core.Scanning.Scanners;

namespace Roost.Core.Tests;

/// <summary>
/// Testea que una MariaDB moderna (mariadbd + mysqld en la misma carpeta) cuente como
/// UNA instalación. Se crean ficheros vacíos en una carpeta temporal: no se ejecuta nada.
/// </summary>
public class MariaDbScannerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ext = OperatingSystem.IsWindows() ? ".exe" : "";

    public MariaDbScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "roost-maria-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name + _ext);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void SkipLegacyAliases_DescartaMysqld_SiHayMariadbdEnLaMismaCarpeta()
    {
        var mariadbd = Touch("mariadbd");
        var mysqld = Touch("mysqld");

        var result = MariaDbScanner.SkipLegacyAliases(
            [new Candidate(mariadbd, "Program Files"), new Candidate(mysqld, "Program Files")]).ToList();

        Assert.Single(result);
        Assert.Equal(mariadbd, result[0].Path);
    }

    [Fact]
    public void SkipLegacyAliases_ConservaMysqldSolo_DeUnaMariaDbAntigua()
    {
        var mysqld = Touch("mysqld");

        var result = MariaDbScanner.SkipLegacyAliases([new Candidate(mysqld, "Laragon")]).ToList();

        Assert.Single(result);
        Assert.Equal(mysqld, result[0].Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
