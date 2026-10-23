using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>Testea DbAdmin con un cliente falso: qué se le pide al servidor y cómo se lee lo que contesta.</summary>
public class DbAdminTests
{
    private static readonly InstalledDb MariaDb = new(DbEngine.MariaDb, "11.4.13", @"C:\roost\bin\mariadb\11.4.13");

    private sealed class FakeClient(string? output) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{Path.GetFileName(executablePath)} {arguments}");
            return output;
        }
    }

    [Fact]
    public void Create_UsaElClienteDeLaInstalacionConSuMyIni_EnUtf8mb4()
    {
        var client = new FakeClient("roost-ok\n");

        DbAdmin.Create(MariaDb, @"C:\roost\etc\mariadb\11.4\my.ini", "tienda", client);

        var call = Assert.Single(client.Calls);
        Assert.StartsWith(@"mariadb.exe --defaults-file=""C:\roost\etc\mariadb\11.4\my.ini""", call);
        Assert.Contains("CREATE DATABASE IF NOT EXISTS `tienda` CHARACTER SET utf8mb4", call);
    }

    [Theory]
    [InlineData("mi-tienda")]
    [InlineData("tienda; DROP DATABASE mysql")]
    [InlineData("")]
    public void Create_NombreInvalido_NoLlegaAlServidor(string name)
    {
        var client = new FakeClient("roost-ok");

        Assert.Throws<RoostException>(() => DbAdmin.Create(MariaDb, "my.ini", name, client));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public void Create_SinConfirmacion_FallaConLoQueDijoElCliente()
    {
        var ex = Assert.Throws<RoostException>(() =>
            DbAdmin.Create(MariaDb, "my.ini", "tienda", new FakeClient("ERROR 2002 (HY000): Can't connect")));

        Assert.Contains("Can't connect", ex.Message);
    }

    [Fact]
    public void List_QuitaLasDelSistema_YOrdena()
    {
        var client = new FakeClient("information_schema\r\nmysql\r\ntienda\r\nperformance_schema\r\nlegacy_crm\r\nsys\r\nroost-ok\r\n");

        Assert.Equal(["legacy_crm", "tienda"], DbAdmin.List(MariaDb, "my.ini", client));
    }

    [Fact]
    public void List_SinRespuesta_Lanza()
    {
        Assert.Throws<RoostException>(() => DbAdmin.List(MariaDb, "my.ini", new FakeClient(null)));
    }
}
