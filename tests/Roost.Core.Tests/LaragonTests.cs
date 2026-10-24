using System.Text;
using Roost.Core.Install;
using Roost.Core.Laragon;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// BD-2: las bases de datos de Laragon, con un Laragon de pega en una carpeta temporal (la misma
/// estructura que el de verdad: <c>bin\mysql\…</c>, <c>data\mysql-8</c>, <c>usr\laragon.ini</c>) y
/// un runner que hace de cliente, de volcador y de servidor. Nada arranca de verdad y nunca se
/// mira el <c>C:\laragon</c> real.
/// </summary>
public sealed class LaragonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-laragon-" + Guid.NewGuid().ToString("N"));
    private string Laragon => Path.Combine(_root, "laragon");
    private RoostPaths Paths => new(Path.Combine(_root, "roost"), Path.Combine(_root, "cache"));

    public LaragonTests()
    {
        // MySQL 8.4 (la que usa Laragon, con su my.ini) y una MariaDB 10.4 vieja, sin my.ini y solo con mysqld.exe.
        Touch(@"bin\mysql\mysql-8.4.11-winx64\bin\mysqld.exe");
        Touch(@"bin\mysql\mysql-8.4.11-winx64\bin\mysql.exe");
        Touch(@"bin\mysql\mysql-8.4.11-winx64\bin\mysqldump.exe");
        Write(@"bin\mysql\mysql-8.4.11-winx64\my.ini", $"[client]\nport = 3419\n\n[mysqld]\ndatadir={Laragon.Replace('\\', '/')}/data/mysql-8\nport		= 3419\n");
        Touch(@"bin\mysql\mariadb-10.4.32-winx64\bin\mysqld.exe");
        Touch(@"bin\mysql\mariadb-10.4.32-winx64\bin\mysql.exe");
        Touch(@"bin\mysql\mariadb-10.4.32-winx64\bin\mysqldump.exe");
        Touch(@"bin\mysql\vacia-sin-servidor\README");
        Write(@"usr\laragon.ini", "[laragon]\nVersion=8.0.0\n\n[mysql]\nVersion=mysql-8.4.11-winx64\nPort=3306\n");

        Write(@"data\mysql-8\ibdata1", new string('i', 4096));
        Write(@"data\mysql-8\mysql.ibd", "m");
        Write(@"data\mysql-8\auto.cnf", "[auto]");
        Write(@"data\mysql-8\PC.pid", "123");
        Write(@"data\mysql-8\mysql\db.opt", "x");
        Write(@"data\mysql-8\sys\sys_config.ibd", "x");
        Write(@"data\mysql-8\#innodb_redo\#ib_redo10", "r");
        Write(@"data\mysql-8\blog_laragon\posts.ibd", new string('p', 3000));
        Write(@"data\mysql-8\blog_laragon\etiquetas.ibd", new string('e', 1000));
        Write(@"data\mysql-8\crm\contactos.ibd", "c");
        Write(@"data\mysql-8\mi@002dtienda\t.ibd", "t");
        Write(@"data\mariadb\mysql\user.MYD", "u");
        Write(@"data\mariadb\viejo\t.frm", "v");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string relative) => Write(relative, "");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Laragon, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Todo lo que hay en el Laragon de pega, con su tamaño y su fecha: no puede cambiar.</summary>
    private string Snapshot() => string.Join("\n", Directory.EnumerateFiles(Laragon, "*", SearchOption.AllDirectories).Order()
        .Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f):O}"));

    private LaragonDbServer MySql84 => new LaragonInstall(Laragon).DbServers().First();

    // --- Dónde está y qué tiene ---

    [WindowsFact("Solo en Windows: Laragon.")]
    public void Locate_LaVariableManda_YSiNoLaRutaDeUnBinario()
    {
        var fromVariable = LaragonInstall.Locate([], name => name == LaragonInstall.RootVariable ? Laragon : null);
        var fromPath = LaragonInstall.Locate([@"C:\Program Files\PHP\php.exe", @"D:\laragon\bin\php\php-8.3.18-nts-Win32-vs16-x64\php.exe"], _ => null);
        var missing = LaragonInstall.Locate([@"C:\laragon\bin\php\php.exe"], name => name == LaragonInstall.RootVariable ? Path.Combine(_root, "no-existe") : null);

        Assert.Equal(Path.GetFullPath(Laragon), fromVariable!.Root);
        Assert.Equal(@"D:\laragon", fromPath!.Root);
        Assert.Equal(@"D:\laragon\www", fromPath.Www);
        Assert.Null(missing);   // la variable apunta a otra parte: no se busca el de verdad
        Assert.Null(LaragonInstall.Locate([@"C:\xampp\php\php.exe"], _ => null));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public void DbServers_LaQueUsaLaragonPrimero_ConSuDatadirYSuPuerto()
    {
        var servers = new LaragonInstall(Laragon).DbServers();

        Assert.Equal(["mysql-8.4.11-winx64", "mariadb-10.4.32-winx64"], servers.Select(s => s.Name));
        var mysql = servers[0];
        Assert.Equal(DbEngine.MySql, mysql.Engine);
        Assert.Equal("8.4.11", mysql.Version.ToString());
        Assert.True(mysql.Active);
        Assert.Equal(3419, mysql.Port);   // el de su my.ini, que es el que usa
        Assert.Equal(Path.Combine(Laragon, "data", "mysql-8"), mysql.DataDir);
        Assert.Equal(Path.Combine(mysql.Folder, "bin", "mysqld.exe"), mysql.Server);

        var maria = servers[1];
        Assert.Equal(DbEngine.MariaDb, maria.Engine);   // por el nombre de la carpeta: la 10.4 aún lo llama mysqld
        Assert.False(maria.Active);
        Assert.Equal(LaragonInstall.DefaultPort, maria.Port);
        Assert.Equal(Path.Combine(Laragon, "data", "mariadb"), maria.DataDir);   // sin my.ini: la de siempre que tiene mysql dentro
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public void Databases_LasDelUsuario_ConSuNombreDecodificado_YSiValenEnRoost()
    {
        var dbs = LaragonInstall.Databases(MySql84);

        Assert.Equal(["blog_laragon", "crm", "mi-tienda"], dbs.Select(d => d.Name));
        Assert.Equal(4000, dbs[0].Bytes);
        Assert.True(dbs[0].Importable);
        Assert.False(dbs[2].Importable);   // con guion: no vale como nombre en Roost
        Assert.Equal("mi-tienda", LaragonInstall.DecodeName("mi@002dtienda"));
        Assert.Empty(LaragonInstall.Databases(MySql84 with { DataDir = Path.Combine(_root, "no-existe") }));
    }

    [Fact]
    public void ParseIni_SeccionesClavesYComentarios()
    {
        var ini = LaragonInstall.ParseIni("\uFEFF; comentario\n[mysql]\nVersion = mysql-8.0.30-winx64\n# otro\nPort=3307\nraro\n[MySQLD]\ndatadir=C:/laragon/data/mysql-8\r\n");

        Assert.Equal("mysql-8.0.30-winx64", ini["mysql"]["version"]);
        Assert.Equal("3307", ini["MYSQL"]["Port"]);
        Assert.Equal("C:/laragon/data/mysql-8", ini["mysqld"]["datadir"]);
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public void CopyServerCommand_SinFicherosDeOpciones_SoloEn127_YSinBinlogNiEventos()
    {
        var spec = LaragonDumper.CopyServerCommand(MySql84, @"C:\roost\run\laragon-1\data", @"C:\roost\run\laragon-1", 51234);

        Assert.Equal(MySql84.Server, spec.Exe);
        Assert.Equal("--no-defaults", spec.Arguments[0]);   // si no, leería su my.ini, que apunta a los datos de verdad
        Assert.Contains(@"--datadir=C:\roost\run\laragon-1\data", spec.Arguments);
        Assert.Contains("--port=51234", spec.Arguments);
        Assert.Contains("--bind-address=127.0.0.1", spec.Arguments);
        Assert.Contains("--event-scheduler=OFF", spec.Arguments);
        Assert.Contains("--skip-log-bin", spec.Arguments);
        Assert.Contains("--mysqlx=OFF", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains(Path.Combine(Laragon, "data"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--skip-log-bin", LaragonDumper.CopyServerCommand(new LaragonInstall(Laragon).DbServers()[1], "d", "w", 1).Arguments);
    }

    // --- Volcar ---

    /// <summary>El cliente, el volcador y el servidor de Laragon, de pega.</summary>
    private sealed class FakeLaragon(string dataDir) : ICommandRunner
    {
        public List<CommandSpec> Calls { get; } = [];
        public bool ServerRunning { get; private set; }
        public bool ServerKilled { get; private set; }
        public string? ServerCrash { get; init; }
        public string AccessDenied { get; init; } = "";
        public string? DataDirAnswer { get; set; }
        public List<string> CopyContents { get; } = [];

        public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Calls.Add(spec);
            var exe = Path.GetFileName(spec.Exe);
            if (exe == "mysqld.exe")
            {
                if (ServerCrash is not null)
                {
                    lines?.Report(ServerCrash);
                    return new CommandResult(1, "", ServerCrash, false);
                }
                var copy = spec.Arguments.First(a => a.StartsWith("--datadir=", StringComparison.Ordinal))["--datadir=".Length..];
                CopyContents.AddRange(Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(copy, f)).Order());
                ServerRunning = true;
                lines?.Report("[Server] ready for connections.");
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                finally
                {
                    ServerRunning = false;
                    ServerKilled = true;
                }
            }

            var cnf = File.ReadAllText(spec.Arguments[0]["--defaults-file=".Length..]);
            if (AccessDenied.Length > 0 && !cnf.Contains($"password = \"{AccessDenied}\"", StringComparison.Ordinal))
                return new CommandResult(1, "", "ERROR 1045 (28000): Access denied for user 'root'@'localhost' (using password: NO)", false);
            if (exe == "mysql.exe")
                return new CommandResult(0, (DataDirAnswer ?? dataDir.Replace("\\", "\\\\") + "\\\\") + "\n", "", false);
            if (exe == "mysqldump.exe")
            {
                spec.StdoutStream!.Write(Encoding.UTF8.GetBytes($"-- MySQL dump 10.13  Distrib 8.4.11\n-- volcado de {spec.Arguments[^1]} por el puerto {cnf.Split('\n').First(l => l.StartsWith("port", StringComparison.Ordinal))}\n"));
                return new CommandResult(0, "", "", false);
            }
            throw new InvalidOperationException($"Sin guion para {spec.Display}");
        }
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_ConLaragonEnMarcha_VuelcaDeSuServidor_YNoEscribeEnSusCarpetas()
    {
        var before = Snapshot();
        var runner = new FakeLaragon(MySql84.DataDir!);
        var dumper = new LaragonDumper(Paths, runner) { AcceptsConnections = (port, _) => Task.FromResult(port == 3419), DataDirInUse = _ => true };
        var stages = new List<LaragonStage>();

        using (var dumps = await dumper.DumpAsync(MySql84, ["blog_laragon", "crm"], null, new SyncProgress<LaragonProgress>(p => stages.Add(p.Stage)), null, CancellationToken.None))
        {
            Assert.Equal(LaragonMode.Live, dumps.Mode);
            Assert.Equal(["blog_laragon", "crm"], dumps.Files.Keys.Order());
            Assert.Contains("volcado de crm por el puerto port = 3419", File.ReadAllText(dumps.Files["crm"]));
            Assert.StartsWith(Paths.Run, dumps.Folder);
            Assert.DoesNotContain(runner.Calls, c => Path.GetFileName(c.Exe) == "mysqld.exe");   // nada que arrancar
            Assert.Equal([LaragonStage.Checking, LaragonStage.Dumping], stages.Distinct());
            // Su volcador, con root y sin contraseña en la línea de órdenes.
            var dump = runner.Calls.Single(c => c.Arguments[^1] == "crm");
            Assert.Equal(Path.Combine(MySql84.BinDir, "mysqldump.exe"), dump.Exe);
            Assert.Contains("--single-transaction", dump.Arguments);
            Assert.DoesNotContain(dump.Arguments, a => a.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("SELECT @@datadir", runner.Calls[0].Arguments);
            Assert.Equal(before, Snapshot());
            Assert.False(Directory.Exists(Path.Combine(dumps.Folder, "data")));
            var folder = dumps.Folder;
            dumps.Dispose();
            Assert.False(Directory.Exists(folder));
        }
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_ConContrasena_VaEnElFicheroDeOpciones_YSinElla_LoPide()
    {
        var runner = new FakeLaragon(MySql84.DataDir!) { AccessDenied = "S3cret!" };
        var dumper = new LaragonDumper(Paths, runner) { AcceptsConnections = (_, _) => Task.FromResult(true) };

        var ex = await Assert.ThrowsAsync<LaragonPasswordException>(() => dumper.DumpAsync(MySql84, ["crm"], null, null, null, CancellationToken.None));
        using var dumps = await dumper.DumpAsync(MySql84, ["crm"], "S3cret!", null, null, CancellationToken.None);

        Assert.Equal("El MySQL 8.4.11 de Laragon pide la contrasena de root.", ex.Message);
        Assert.Single(dumps.Files);
        Assert.Single(Directory.GetDirectories(Paths.Run));   // el intento fallido no ha dejado su carpeta
        await Assert.ThrowsAsync<LaragonPasswordException>(() => dumper.DumpAsync(MySql84, ["crm"], "con espacio", null, null, CancellationToken.None));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_ConLaragonParado_CopiaSusDatos_ArrancaSuServidorSobreLaCopia_YLaBorra()
    {
        var before = Snapshot();
        var runner = new FakeLaragon(MySql84.DataDir!);
        var dumper = new LaragonDumper(Paths, runner)
        {
            AcceptsConnections = (port, _) => Task.FromResult(port == 52001 && runner.ServerRunning),
            DataDirInUse = _ => false,
            FreePort = () => 52001,
        };
        var stages = new List<LaragonProgress>();

        using var dumps = await dumper.DumpAsync(MySql84, ["blog_laragon"], null, new SyncProgress<LaragonProgress>(stages.Add), null, CancellationToken.None);

        Assert.Equal(LaragonMode.Copy, dumps.Mode);
        Assert.Contains("por el puerto port = 52001", File.ReadAllText(dumps.Files["blog_laragon"]));
        // La copia tenía sus datos (sin el .pid), el servidor se ha parado y la copia ya no está.
        Assert.Contains(Path.Combine("blog_laragon", "posts.ibd"), runner.CopyContents);
        Assert.Contains("ibdata1", runner.CopyContents);
        Assert.DoesNotContain("PC.pid", runner.CopyContents);
        Assert.True(runner.ServerKilled);
        Assert.False(Directory.Exists(Path.Combine(dumps.Folder, "data")));
        Assert.Equal([LaragonStage.Checking, LaragonStage.Copying, LaragonStage.Starting, LaragonStage.Dumping, LaragonStage.Cleaning], stages.Select(s => s.Stage).Distinct());
        Assert.Equal(52001, stages.First(s => s.Stage == LaragonStage.Starting).Port);
        Assert.Equal(before, Snapshot());
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_EnSuPuertoContestaOtroServidor_NoSeVuelcaDeEse()
    {
        var runner = new FakeLaragon(MySql84.DataDir!) { DataDirAnswer = @"C:\\ProgramData\\MySQL\\MySQL Server 8.0\\Data\\" };
        var dumper = new LaragonDumper(Paths, runner)
        {
            AcceptsConnections = (port, _) => Task.FromResult(port == 3419 || (port == 52002 && runner.ServerRunning)),
            DataDirInUse = _ => false,
            FreePort = () => 52002,
        };

        using var dumps = await dumper.DumpAsync(MySql84, ["crm"], null, null, null, CancellationToken.None);

        Assert.Equal(LaragonMode.Copy, dumps.Mode);   // el del 3419 no era el de Laragon: se usa la copia
        Assert.DoesNotContain(runner.Calls, c => Path.GetFileName(c.Exe) == "mysqldump.exe" && File.ReadAllText(c.Arguments[0]["--defaults-file=".Length..]).Contains("3419"));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_ParadoPeroConSusFicherosAbiertos_NoSeCopia()
    {
        var runner = new FakeLaragon(MySql84.DataDir!);
        var dumper = new LaragonDumper(Paths, runner) { AcceptsConnections = (_, _) => Task.FromResult(false), DataDirInUse = _ => true };

        var ex = await Assert.ThrowsAsync<RoostException>(() => dumper.DumpAsync(MySql84, ["crm"], null, null, null, CancellationToken.None));

        Assert.Contains("parece en marcha", ex.Message);
        Assert.Empty(runner.Calls);
        Assert.Empty(Directory.GetDirectories(Paths.Run));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_SiLaCopiaNoArranca_LoDice_YNoDejaNada()
    {
        var runner = new FakeLaragon(MySql84.DataDir!) { ServerCrash = "2026-10-24T03:49:29Z 0 [ERROR] [MY-010119] [Server] Aborting" };
        var dumper = new LaragonDumper(Paths, runner) { AcceptsConnections = (_, _) => Task.FromResult(false), DataDirInUse = _ => false, FreePort = () => 52003 };

        var ex = await Assert.ThrowsAsync<RoostException>(() => dumper.DumpAsync(MySql84, ["crm"], null, null, null, CancellationToken.None));

        Assert.Contains("no ha arrancado: 2026-10-24T03:49:29Z 0 [ERROR] [MY-010119] [Server] Aborting", ex.Message);
        Assert.Empty(Directory.GetDirectories(Paths.Run));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_Cancelado_ParaLaCopiaYNoDejaNada()
    {
        var runner = new FakeLaragon(MySql84.DataDir!);
        using var cts = new CancellationTokenSource();
        var dumper = new LaragonDumper(Paths, runner)
        {
            // Contesta cuando ya está en marcha, y justo entonces se cancela.
            AcceptsConnections = (port, _) =>
            {
                if (runner.ServerRunning) cts.Cancel();
                return Task.FromResult(false);
            },
            DataDirInUse = _ => false,
            FreePort = () => 52004,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dumper.DumpAsync(MySql84, ["crm"], null, null, null, cts.Token));

        Assert.True(runner.ServerKilled);
        Assert.Empty(Directory.GetDirectories(Paths.Run));
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public async Task Dump_NombresQueNoValen_NoLleganANada()
    {
        var runner = new FakeLaragon(MySql84.DataDir!);
        var dumper = new LaragonDumper(Paths, runner);

        await Assert.ThrowsAsync<RoostException>(() => dumper.DumpAsync(MySql84, ["mi-tienda"], null, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<RoostException>(() => dumper.DumpAsync(MySql84, ["mysql"], null, null, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [WindowsFact("Solo en Windows: Laragon.")]
    public void IsDataDirInUse_SiOtroLoTieneAbiertoParaEscribir()
    {
        var dir = Path.Combine(Laragon, "data", "mysql-8");
        Assert.False(LaragonDumper.IsDataDirInUse(dir));

        using (new FileStream(Path.Combine(dir, "ibdata1"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.True(LaragonDumper.IsDataDirInUse(dir));

        Assert.False(LaragonDumper.IsDataDirInUse(dir));
    }

    /// <summary>Un IProgress que avisa en el acto (el de .NET va por el contexto de sincronización y llegaría tarde).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
