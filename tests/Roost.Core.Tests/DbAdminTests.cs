using Roost.Core.Install;
using Roost.Core.Platform;
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

    /// <summary>Contesta según lo que se le pide (varias llamadas seguidas, como el cambio de contraseña).</summary>
    private sealed class ScriptedClient(Func<string, string?> answer) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{Path.GetFileName(executablePath)} {arguments}");
            return answer(arguments);
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

    // --- Borrar ---

    [Fact]
    public void Drop_BorraLaBaseDeDatos_YConfirma()
    {
        var client = new FakeClient("roost-ok\n");

        DbAdmin.Drop(MariaDb, "my.ini", "tienda", client);

        Assert.Contains("DROP DATABASE `tienda`; SELECT 'roost-ok'", Assert.Single(client.Calls));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("information_schema")]
    [InlineData("SYS")]
    [InlineData("tienda; DROP DATABASE mysql")]
    public void Drop_NuncaLasDelSistema_NiNombresRaros(string name)
    {
        var client = new FakeClient("roost-ok");

        Assert.Throws<RoostException>(() => DbAdmin.Drop(MariaDb, "my.ini", name, client));
        Assert.Empty(client.Calls);
    }

    // --- La contraseña ---

    [Theory]
    [InlineData("", true)]
    [InlineData("root", true)]
    [InlineData("S3cret!#%&(){}[]<>?,.;:~^|@$*+-=_/", true)]
    [InlineData("con espacio", false)]
    [InlineData("comilla'simple", false)]
    [InlineData("comilla\"doble", false)]
    [InlineData("barra\\invertida", false)]
    [InlineData("acento`grave", false)]
    [InlineData("contraseña", false)]
    [InlineData("tab\there", false)]
    public void IsValidPassword_SoloAsciiImprimibleSinLoQueHabriaQueEscapar(string password, bool valid)
    {
        Assert.Equal(valid, DbAdmin.IsValidPassword(password));
        Assert.False(DbAdmin.IsValidPassword(new string('a', 65)));
        Assert.True(DbAdmin.IsValidPassword(new string('a', 64)));
    }

    [Fact]
    public void SetRootPassword_CambiaTodasLasCuentasRoot_EnUnaLlamada()
    {
        var client = new ScriptedClient(args => args.Contains("SELECT host FROM mysql.user", StringComparison.Ordinal)
            ? "localhost\r\n127.0.0.1\r\n::1\r\nroost-ok\r\n"
            : "roost-ok\r\n");

        DbAdmin.SetRootPassword(MariaDb, "my.ini", "S3cret!", client);

        Assert.Equal(2, client.Calls.Count);
        Assert.Contains("WHERE user = 'root'", client.Calls[0]);
        Assert.Contains("ALTER USER 'root'@'localhost' IDENTIFIED BY 'S3cret!'; ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY 'S3cret!'; ALTER USER 'root'@'::1' IDENTIFIED BY 'S3cret!'; SELECT 'roost-ok'", client.Calls[1]);
    }

    [Fact]
    public void SetRootPassword_Vacia_QuitaLaContrasena()
    {
        var client = new ScriptedClient(args => args.Contains("SELECT host", StringComparison.Ordinal) ? "localhost\nroost-ok\n" : "roost-ok");

        DbAdmin.SetRootPassword(MariaDb, "my.ini", "", client);

        Assert.Contains("ALTER USER 'root'@'localhost' IDENTIFIED BY ''", client.Calls[1]);
    }

    [Fact]
    public void SetRootPassword_InvalidaOSinCuentas_NoTocaElServidor()
    {
        var client = new FakeClient("roost-ok");
        Assert.Throws<RoostException>(() => DbAdmin.SetRootPassword(MariaDb, "my.ini", "con espacio", client));
        Assert.Empty(client.Calls);

        // Solo el roost-ok, sin hosts: no hay a quién cambiársela.
        var ex = Assert.Throws<RoostException>(() => DbAdmin.SetRootPassword(MariaDb, "my.ini", "x", new FakeClient("roost-ok\n")));
        Assert.Contains("root", ex.Message);
    }

    [Fact]
    public void SetRootPassword_AccesoDenegado_SugiereSaveOnly()
    {
        var client = new ScriptedClient(args => args.Contains("SELECT host", StringComparison.Ordinal)
            ? "localhost\nroost-ok\n"
            : "ERROR 1045 (28000): Access denied for user 'root'@'localhost' (using password: NO)");

        var ex = Assert.Throws<RoostException>(() => DbAdmin.SetRootPassword(MariaDb, "my.ini", "nueva", client));

        Assert.Contains("--save-only", ex.Message);
    }

    [Fact]
    public void ChangeRootPassword_GuardaLaContrasena_YReescribeElMyIni()
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-dbpw-" + Guid.NewGuid().ToString("N"));
        var paths = new RoostPaths(root, Path.Combine(root, "cache"));
        var db = new InstalledDb(DbEngine.MariaDb, "11.4.13", Path.Combine(root, "bin", "mariadb", "11.4.13"));
        var client = new ScriptedClient(args => args.Contains("SELECT host", StringComparison.Ordinal) ? "localhost\n127.0.0.1\nroost-ok\n" : "roost-ok\n");
        try
        {
            DbAdmin.ChangeRootPassword(paths, db, 3407, "S3cret!", saveOnly: false, client);

            Assert.Equal("S3cret!", new DbPasswords(paths).For(db));
            var ini = File.ReadAllText(DbIni.PathFor(paths, db));
            Assert.Contains("password = \"S3cret!\"", ini);
            Assert.Contains("port = 3407", ini);
            Assert.Contains("!include " + DbIni.Slashes(DbIni.UserFilePath(paths, db)), ini);
            Assert.True(File.Exists(DbIni.UserFilePath(paths, db)));
            // Dos ALTER USER (uno por cuenta) y, con el my.ini nuevo, el SELECT de comprobación.
            Assert.Contains(client.Calls, c => c.Contains("ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY 'S3cret!'", StringComparison.Ordinal));
            Assert.EndsWith("-N -e \"SELECT 'roost-ok'\"", client.Calls[^1]);

            // Con --save-only no hay ALTER USER: solo se guarda y se comprueba.
            client.Calls.Clear();
            DbAdmin.ChangeRootPassword(paths, db, 3407, "otra", saveOnly: true, client);
            Assert.DoesNotContain(client.Calls, c => c.Contains("ALTER USER", StringComparison.Ordinal));
            Assert.Equal("otra", new DbPasswords(paths).For(db));

            // La vacía vuelve a sin contraseña: desaparece del my.ini y del fichero.
            DbAdmin.ChangeRootPassword(paths, db, 3407, "", saveOnly: false, client);
            Assert.Null(new DbPasswords(paths).For(db));
            Assert.DoesNotContain("password =", File.ReadAllText(DbIni.PathFor(paths, db)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ChangeRootPassword_SiRootNoEntraConLaNueva_DejaTodoComoEstaba()
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-dbpw-" + Guid.NewGuid().ToString("N"));
        var paths = new RoostPaths(root, Path.Combine(root, "cache"));
        var db = new InstalledDb(DbEngine.MySql, "8.4.11", Path.Combine(root, "bin", "mysql", "8.4.11"));
        // Solo guardar una contraseña que no es la de verdad: el SELECT de comprobación falla.
        var client = new ScriptedClient(_ => "ERROR 1045 (28000): Access denied for user 'root'@'localhost' (using password: YES)");
        try
        {
            var ex = Assert.Throws<RoostException>(() => DbAdmin.ChangeRootPassword(paths, db, 3306, "equivocada", saveOnly: true, client));

            Assert.Contains("no entra", ex.Message);
            Assert.Null(new DbPasswords(paths).For(db));
            Assert.DoesNotContain("password =", File.ReadAllText(DbIni.PathFor(paths, db)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // --- Usuarios ---

    [Fact]
    public void ListUsers_QuitaRootYLasDelSistema_YLeeSuAcceso()
    {
        var client = new FakeClient(
            "admin\t%\tY\t\r\n"
            + "mariadb.sys\tlocalhost\tN\t\r\n"
            + "mysql.session\tlocalhost\tN\t\r\n"
            + "PUBLIC\t\tN\t\r\n"
            + "root\tlocalhost\tY\t\r\n"
            + "root\t127.0.0.1\tY\t\r\n"
            + "sinnada\t%\tN\t\r\n"
            + "tienda\t%\tN\tblog,tienda\r\n"
            + "roost-ok\r\n");

        var users = DbAdmin.ListUsers(MariaDb, "my.ini", client);

        Assert.Equal(["admin", "sinnada", "tienda"], users.Select(u => u.Name));
        Assert.True(users[0].AllDatabases);
        Assert.Equal("todas", users[0].AccessText);
        Assert.Equal("ninguna", users[1].AccessText);
        Assert.Equal(["blog", "tienda"], users[2].Databases);
        Assert.Equal("blog, tienda", users[2].AccessText);
        Assert.Equal("tienda@%", users[2].Account);
        Assert.Contains("FROM mysql.user u", Assert.Single(client.Calls));
        Assert.Contains("FROM mysql.db d", client.Calls[0]);
    }

    [Fact]
    public void CreateUser_ConBaseDeDatos_DaTodosLosPrivilegiosSoloSobreElla()
    {
        var client = new FakeClient("roost-ok");

        DbAdmin.CreateUser(MariaDb, "my.ini", "tienda", "S3cret!", "tienda", client);

        Assert.Contains("CREATE USER 'tienda'@'%' IDENTIFIED BY 'S3cret!'; GRANT ALL PRIVILEGES ON `tienda`.* TO 'tienda'@'%'; SELECT 'roost-ok'", Assert.Single(client.Calls));
    }

    [Fact]
    public void CreateUser_SinBaseDeDatos_DaTodasLasBasesDeDatos()
    {
        var client = new FakeClient("roost-ok");

        DbAdmin.CreateUser(MariaDb, "my.ini", "admin", "S3cret!", null, client);

        Assert.Contains("GRANT ALL PRIVILEGES ON *.* TO 'admin'@'%'", Assert.Single(client.Calls));
    }

    [Theory]
    [InlineData("mi-usuario", "x", "tienda")]
    [InlineData("root", "x", "tienda")]
    [InlineData("tienda", "", "tienda")]
    [InlineData("tienda", "con espacio", "tienda")]
    [InlineData("tienda", "x", "mysql")]
    [InlineData("tienda", "x", "ti enda")]
    [InlineData("usuario_de_mas_de_treinta_y_dos_caracteres", "x", null)]
    public void CreateUser_LoQueNoVale_NoLlegaAlServidor(string user, string password, string? database)
    {
        var client = new FakeClient("roost-ok");

        Assert.Throws<RoostException>(() => DbAdmin.CreateUser(MariaDb, "my.ini", user, password, database, client));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public void SetUserPassword_YDropUser_UsanLaCuentaExacta()
    {
        var client = new FakeClient("roost-ok");

        DbAdmin.SetUserPassword(MariaDb, "my.ini", "tienda", "%", "Nuev4!", client);
        DbAdmin.DropUser(MariaDb, "my.ini", "tienda", "%", client);

        Assert.Contains("ALTER USER 'tienda'@'%' IDENTIFIED BY 'Nuev4!'; SELECT 'roost-ok'", client.Calls[0]);
        Assert.Contains("DROP USER 'tienda'@'%'; SELECT 'roost-ok'", client.Calls[1]);
    }

    [Theory]
    [InlineData("root", "localhost")]
    [InlineData("mariadb.sys", "localhost")]
    [InlineData("tienda", "%'; DROP USER 'root'@'localhost")]
    [InlineData("tienda", "")]
    public void DropUser_NiRootNiElSistema_NiHostsRaros(string user, string host)
    {
        var client = new FakeClient("roost-ok");

        Assert.Throws<RoostException>(() => DbAdmin.DropUser(MariaDb, "my.ini", user, host, client));
        Assert.Throws<RoostException>(() => DbAdmin.SetUserPassword(MariaDb, "my.ini", user, host, "x", client));
        Assert.Empty(client.Calls);
    }

    // --- Tras instalar ---

    [Fact]
    public void SmokeTest_SiElServidorContesta_DaSuVersion()
    {
        var client = new FakeClient("C:\\roost\\bin\\mariadb\\11.4.13\\bin\\mariadbd.exe  Ver 11.4.13-MariaDB for Win64 on AMD64\n");

        var smoke = DbAdmin.SmokeTest(MariaDb, client);

        Assert.True(smoke.Ok);
        Assert.Contains("Ver 11.4.13", smoke.VersionLine);
        Assert.EndsWith("mariadbd.exe --version", Assert.Single(client.Calls));
    }

    [Fact]
    public void SmokeTest_SiNoContesta_NombraElRedistributableQueToca()
    {
        var modern = DbAdmin.SmokeTest(MariaDb, new FakeClient(null));
        var old = DbAdmin.SmokeTest(new InstalledDb(DbEngine.MySql, "5.7.44", @"C:\roost\bin\mysql\5.7.44"), new FakeClient(""));

        Assert.False(modern.Ok);
        Assert.Equal("Visual C++ v14 (x64)", modern.MissingRedistributable);
        Assert.Equal("https://aka.ms/vc14/vc_redist.x64.exe", modern.RedistributableUrl);
        Assert.Equal("Visual C++ 2013 (x64)", old.MissingRedistributable);
    }
}
