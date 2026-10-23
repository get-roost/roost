using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>El my.ini generado: con y sin contrasena de root, el usuario.cnf incluido y la escritura en disco.</summary>
public class DbIniTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-dbini-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static readonly ComponentVersion Maria = new(11, 4, 13, "11.4.13");

    [Fact]
    public void Render_SinContrasena_ElClienteVaSoloConRoot()
    {
        var ini = DbIni.Render(DbEngine.MariaDb, Maria, @"C:\b", @"C:\d", 3306);

        Assert.Contains("[client]\nhost = 127.0.0.1\nport = 3306\nuser = root\n", ini.Replace("\r\n", "\n"));
        Assert.DoesNotContain("password", ini);
        Assert.DoesNotContain("!include", ini);
    }

    [Fact]
    public void Render_ConContrasena_LaEscribeEntreComillasEnElCliente()
    {
        var ini = DbIni.Render(DbEngine.MariaDb, Maria, @"C:\b", @"C:\d", 3306, password: "S3cret!#");

        // Entre comillas, el # no abre un comentario.
        Assert.Contains("user = root\npassword = \"S3cret!#\"\n", ini.Replace("\r\n", "\n"));
        // Y solo en [client]: el servidor no la necesita.
        Assert.True(ini.IndexOf("password =", StringComparison.Ordinal) > ini.IndexOf("[client]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ConFicheroDelUsuario_LoIncluyeAlFinalConBarrasNormales()
    {
        var ini = DbIni.Render(DbEngine.MySql, new ComponentVersion(8, 4, 11, "8.4.11"), @"C:\b", @"C:\d", 3306,
            userFile: @"C:\roost\etc\mysql\8.4\usuario.cnf");

        Assert.EndsWith("!include C:/roost/etc/mysql/8.4/usuario.cnf\n", ini.Replace("\r\n", "\n"));
        Assert.Contains("mysqlx = OFF", ini);
    }

    [Fact]
    public void Write_DejaElMyIni_YCreaUsuarioCnfSoloLaPrimeraVez()
    {
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", Path.Combine(_root, "bin", "mariadb", "11.4.13"));

        var ini = DbIni.Write(Paths, db, 3407, "S3cret!");

        Assert.Equal(Path.Combine(_root, "etc", "mariadb", "11.4", "my.ini"), ini);
        Assert.Equal(ini, DbIni.PathFor(Paths, db));
        var text = File.ReadAllText(ini);
        Assert.Contains("port = 3407", text);
        Assert.Contains("password = \"S3cret!\"", text);
        Assert.Contains($"datadir = \"{DbIni.Slashes(Path.Combine(_root, "data", "mariadb", "11.4"))}\"", text);
        Assert.Contains("!include " + DbIni.Slashes(Path.Combine(_root, "etc", "mariadb", "11.4", "usuario.cnf")), text);

        var userFile = DbIni.UserFilePath(Paths, db);
        Assert.Equal(DbIni.UserFileTemplate, File.ReadAllText(userFile));
        Assert.Contains("# sql_mode = NO_ENGINE_SUBSTITUTION", File.ReadAllText(userFile));

        // Lo que escriba el usuario se respeta en los arranques siguientes.
        File.WriteAllText(userFile, "[mysqld]\nmax_allowed_packet = 256M\n");
        DbIni.Write(Paths, db, 3407, null);
        Assert.Equal("[mysqld]\nmax_allowed_packet = 256M\n", File.ReadAllText(userFile));
        Assert.DoesNotContain("password", File.ReadAllText(ini));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
