using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Roost.Core.Laragon;
using Roost.Core.Scanning;
using Roost.Desktop.Services;
using Roost.Desktop.Tests;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

// BD-2: ningún test lee el C:\laragon de verdad (la bienvenida lo buscaría por la ruta de los PHP
// de la auditoría): la variable apunta a una carpeta que no existe salvo que un test ponga la suya.
[assembly: AssemblyFixture(typeof(SinLaragonDeVerdad))]

namespace Roost.Desktop.Tests;

/// <summary>Pone <see cref="LaragonInstall.RootVariable"/> para todo el ensamblado de tests.</summary>
public sealed class SinLaragonDeVerdad : IDisposable
{
    public static readonly string Nowhere = Path.Combine(Path.GetTempPath(), "roost-tests-sin-laragon-" + Environment.ProcessId);

    public SinLaragonDeVerdad() => Environment.SetEnvironmentVariable(LaragonInstall.RootVariable, Nowhere);

    public void Dispose() => Environment.SetEnvironmentVariable(LaragonInstall.RootVariable, null);
}

/// <summary>
/// El servidor de base de datos de las vistas (BD-2): contesta a listar, a las colaciones, a mirar
/// una base de datos y a crear y borrar, como uno con las bases de datos que se le digan. Nada se ejecuta.
/// </summary>
internal sealed partial class FakeDbServer : IProcessRunner
{
    public List<string> Databases { get; } = ["blog", "tienda"];
    public Dictionary<string, int> Tables { get; } = new(StringComparer.OrdinalIgnoreCase) { ["blog"] = 0, ["tienda"] = 12 };
    public HashSet<string> Collations { get; } = new(["utf8mb4_unicode_ci", "utf8mb4_general_ci", "utf8mb4_bin"], StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    public string? Run(string executablePath, string arguments, TimeSpan timeout)
    {
        lock (Calls) Calls.Add(arguments);
        if (arguments.Contains("SHOW DATABASES", StringComparison.Ordinal)) return string.Join("\n", Databases) + "\nroost-ok\n";
        if (arguments.Contains("SHOW COLLATION", StringComparison.Ordinal)) return string.Join("\n", Collations.Select(c => $"{c}\tutf8mb4\t1\t\tYes\t1")) + "\nroost-ok\n";
        if (SchemaName().Match(arguments) is { Success: true } inspect)
        {
            var name = inspect.Groups[1].Value;
            var exists = Databases.Contains(name, StringComparer.OrdinalIgnoreCase);
            return $"{(exists ? 1 : 0)}\n{(exists ? Tables.GetValueOrDefault(name) : 0)}\nroost-ok\n";
        }
        if (CreateDb().Match(arguments) is { Success: true } create)
        {
            if (!Databases.Contains(create.Groups[1].Value)) Databases.Add(create.Groups[1].Value);
            return "roost-ok\n";
        }
        if (DropDb().Match(arguments) is { Success: true } drop)
        {
            Databases.Remove(drop.Groups[1].Value);
            Tables.Remove(drop.Groups[1].Value);
            return "roost-ok\n";
        }
        return "roost-ok\n";
    }

    [GeneratedRegex("SCHEMA_NAME = '([^']+)'")]
    private static partial Regex SchemaName();

    [GeneratedRegex("CREATE DATABASE IF NOT EXISTS `([^`]+)`")]
    private static partial Regex CreateDb();

    [GeneratedRegex("DROP DATABASE `([^`]+)`")]
    private static partial Regex DropDb();
}

/// <summary>
/// El volcador y el cliente de las vistas: escribe un volcado de pega en el StdoutStream o lee el
/// StdinStream (hasta <see cref="StopAfter"/> bytes) y se queda «en marcha» hasta
/// <see cref="Finish"/> o hasta que se cancela, como un proceso de verdad.
/// </summary>
internal sealed class StreamingRunner : ICommandRunner
{
    private readonly TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<CommandSpec> Calls { get; } = [];
    public int DumpBytes { get; init; } = 3 << 20;
    public long StopAfter { get; init; } = long.MaxValue;
    public bool Waiting { get; private set; }
    public string Stderr { get; init; } = "";
    public Action<CommandSpec>? OnFinish { get; init; }
    public long Received { get; private set; }

    public void Finish(int exitCode = 0) => _gate.TrySetResult(exitCode);

    public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        Calls.Add(spec);
        if (spec.StdoutStream is { } output)
        {
            var chunk = Encoding.ASCII.GetBytes(new string('x', 1023) + "\n");
            for (var written = 0; written < DumpBytes; written += chunk.Length) await output.WriteAsync(chunk, cancellation);
        }
        var buffer = new byte[1 << 16];
        if (spec.StdinStream is { } input)
        {
            int n;
            while (Received < StopAfter && (n = await input.ReadAsync(buffer, cancellation)) > 0) Received += n;
        }
        Waiting = true;
        int code;
        using (cancellation.Register(() => _gate.TrySetCanceled(cancellation)))
            code = await _gate.Task;
        Waiting = false;
        if (spec.StdinStream is { } rest)
        {
            int n;
            while ((n = await rest.ReadAsync(buffer, cancellation)) > 0) Received += n;
        }
        OnFinish?.Invoke(spec);
        if (Stderr.Length > 0) lines?.Report(Stderr);
        return new CommandResult(code, "", Stderr, false);
    }
}

/// <summary>
/// BD-2 en la app: «Exportar…» e «Importar…» en Bases de datos, con el diálogo de progreso y
/// Cancelar, y las bases de datos de Laragon en la bienvenida (con un Laragon de pega en una
/// carpeta temporal). Deja sus capturas junto a las de <see cref="ViewSnapshotTests"/>.
/// </summary>
public class BdDumpViewTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake, FakeDbServer Server)> OpenAsync()
    {
        var fake = new FakeRoost();
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: FakeRoost.Version));
        main.Stack.Show(FakeRoost.Running);
        var server = new FakeDbServer();
        main.Databases.DbClient = server;
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.Current.ShownAsync();
        main.NotifyConfigChanged();
        return (main, window, fake, server);
    }

    private static void Snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(SnapshotDir);
#pragma warning disable CS0618 // el PNG por defecto basta para revisar la captura
        frame.Save(Path.Combine(SnapshotDir, name + ".png"));
#pragma warning restore CS0618
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private static void ScrollToDatabases(MainWindow window, DatabasesViewModel db)
    {
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.DataContext == db);
        scroll.Offset = new Avalonia.Vector(0, 1450);
    }

    private static string Temp(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "roost-bd2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>Un volcado de MySQL 8 de unos MB, con una colación que la MariaDB de pega no conoce.</summary>
    private static string MySqlDump(int megabytes)
    {
        var file = Temp("blog-mysql8.sql");
        var text = new StringBuilder("""
            -- MySQL dump 10.13  Distrib 8.4.11, for Win64 (x86_64)
            --
            -- Host: 127.0.0.1    Database: blog
            -- Server version	8.4.11
            CREATE TABLE `posts` (`id` int NOT NULL, `titulo` varchar(200) COLLATE utf8mb4_es_0900_ai_ci, PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

            """);
        var row = "INSERT INTO `posts` VALUES " + string.Join(",", Enumerable.Range(0, 100).Select(i => $"({i},'post {i}')")) + ";\n";
        while (text.Length < megabytes << 20) text.Append(row);
        File.WriteAllText(file, text.ToString());
        return file;
    }

    // --- Bases de datos ---

    [AvaloniaFact]
    public async Task BasesDeDatos_ExportarEImportar_EnLaLista()
    {
        var (main, window, fake, _) = await OpenAsync();
        using var _f = fake;

        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        Assert.Equal(["blog", "tienda"], db.Databases.Select(d => d.Name));
        Assert.True(db.ExportCommand.CanExecute(db.Databases[0]));
        ScrollToDatabases(window, db);
        Snapshot(window, "bd2-bases-de-datos-exportar-importar");
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_Exportar_ConProgreso_CancelarNoDejaFichero_YLuegoBien()
    {
        var (main, window, fake, _) = await OpenAsync();
        using var _f = fake;
        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        var file = Temp("tienda.sql.gz");
        db.PickExportFile = _ => Task.FromResult<string?>(file);
        var runner = new StreamingRunner { DumpBytes = 3 << 20 };
        db.Runner = runner;

        var exporting = db.ExportCommand.ExecuteAsync(db.Databases.Single(d => d.Name == "tienda"));
        await UntilAsync(() => main.Dialog is DbTransferDialogViewModel && runner.Waiting);
        Dispatcher.UIThread.RunJobs();   // los avisos de progreso que ya estaban en cola
        var dialog = (DbTransferDialogViewModel)main.Dialog!;

        // Su volcador con su my.ini, y el progreso en lo volcado (antes de comprimir).
        var spec = runner.Calls.Single();
        Assert.EndsWith("mariadb-dump.exe", spec.Exe);
        Assert.StartsWith("--defaults-file=", spec.Arguments[0]);
        Assert.Equal("tienda", spec.Arguments[^1]);
        Assert.True(dialog.Running);
        Assert.False(dialog.CanDismiss);
        Assert.Equal("Volcando", dialog.Phase);
        Assert.EndsWith("volcados", dialog.BytesText);
        Snapshot(window, "bd2-exportando");

        dialog.CancelCommand.Execute(null);
        await UntilAsync(() => dialog.Done);
        Assert.True(dialog.Canceled);
        Assert.StartsWith("Cancelado: se ha parado el volcado y no ha quedado ningún fichero a medias", dialog.ResultText);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!));   // ni el .gz ni su temporal
        Snapshot(window, "bd2-exportar-cancelado");
        dialog.FinishCommand.Execute(null);
        await exporting;

        // Otra vez, ahora hasta el final.
        var done = new StreamingRunner { DumpBytes = 200_000 };
        done.Finish();
        db.Runner = done;
        exporting = db.ExportCommand.ExecuteAsync(db.Databases.Single(d => d.Name == "tienda"));
        await UntilAsync(() => main.Dialog is DbTransferDialogViewModel { Done: true });
        dialog = (DbTransferDialogViewModel)main.Dialog!;
        Assert.True(dialog.Succeeded);
        Assert.StartsWith("Exportada tienda a tienda.sql.gz:", dialog.ResultText);
        Assert.True(File.Exists(file));
        Snapshot(window, "bd2-exportado");
        dialog.FinishCommand.Execute(null);
        await exporting;
        Assert.StartsWith("Exportada tienda a", main.FlashText);
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_Importar_UnVolcadoDeMySql8_OfreceAdaptarlo_ConProgresoEnVivo()
    {
        var (main, window, fake, server) = await OpenAsync();
        using var _f = fake;
        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        var file = MySqlDump(6);
        db.PickImportFile = () => Task.FromResult<string?>(file);
        var runner = new StreamingRunner { StopAfter = 3 << 20, OnFinish = _ => server.Tables["blog_mysql"] = 1 };
        db.Runner = runner;
        server.Databases.Add("desde_la_cli");   // creada después de cargar la pantalla

        var importing = db.ImportCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is DbImportDialogViewModel);
        var choose = (DbImportDialogViewModel)main.Dialog!;
        // La lista es la del servidor al importar, no la que cargó la pantalla.
        Assert.Contains("desde_la_cli", choose.Existing);
        // «blog» ya existe: propone importar encima de ella, y avisa de las colaciones de MySQL 8.
        Assert.True(choose.ToExisting);
        Assert.Equal("blog", choose.SelectedExisting);
        Assert.StartsWith("6,0 MB · un volcado de MySQL 8.4.11", choose.FileDetail);
        Assert.True(choose.HasIssues);
        Assert.Contains("utf8mb4_es_0900_ai_ci -> utf8mb4_unicode_ci", choose.Issues[0]);
        Assert.True(choose.Compat);
        Snapshot(window, "bd2-importar-dialogo");

        // A una nueva: el nombre se valida al teclear.
        choose.ToNew = true;
        choose.NewName = "tienda";
        Assert.Equal("tienda ya existe: elige «Una que ya existe» o cambia el nombre.", choose.NameHint);
        Assert.False(choose.CanConfirm);
        choose.NewName = "blog_mysql";
        Assert.True(choose.CanConfirm);
        choose.ConfirmCommand.Execute(null);

        await UntilAsync(() => main.Dialog is DbTransferDialogViewModel && runner.Waiting);
        var dialog = (DbTransferDialogViewModel)main.Dialog!;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(server.Calls, c => c.Contains("CREATE DATABASE IF NOT EXISTS `blog_mysql`", StringComparison.Ordinal));
        var spec = runner.Calls.Single();
        Assert.EndsWith("mariadb.exe", spec.Exe);
        Assert.Contains("--sandbox", spec.Arguments);
        Assert.Equal("blog_mysql", spec.Arguments[^1]);
        Assert.False(dialog.Indeterminate);
        Assert.InRange(dialog.Percent, 30, 70);
        Assert.EndsWith("de 6,0 MB", dialog.BytesText);
        Snapshot(window, "bd2-importando");

        runner.Finish();
        await UntilAsync(() => dialog.Done);
        Assert.True(dialog.Succeeded, dialog.ResultText);
        Assert.StartsWith("Importado blog-mysql8.sql en blog_mysql: 1 tabla", dialog.ResultText);
        Assert.Contains(dialog.Notes, n => n.Contains("utf8mb4_es_0900_ai_ci cambiada por utf8mb4_unicode_ci", StringComparison.Ordinal));
        Snapshot(window, "bd2-importado");
        dialog.FinishCommand.Execute(null);
        await importing;
        Assert.Contains("blog_mysql", db.Databases.Select(d => d.Name));
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_ImportarEncimaDeUnaConTablas_PideConfirmacion()
    {
        var (main, window, fake, _) = await OpenAsync();
        using var _f = fake;
        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        var file = Temp("tienda-20261024-1830.sql");
        File.WriteAllText(file, "-- MariaDB dump 10.19-11.4.13-MariaDB\nCREATE TABLE t (id int);\n");
        db.PickImportFile = () => Task.FromResult<string?>(file);
        var runner = new StreamingRunner();
        db.Runner = runner;

        var importing = db.ImportCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is DbImportDialogViewModel);
        var choose = (DbImportDialogViewModel)main.Dialog!;
        Assert.Equal("tienda", choose.SelectedExisting);   // del nombre del fichero, sin la fecha de Exportar
        Assert.False(choose.HasIssues);
        choose.ConfirmCommand.Execute(null);

        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var confirm = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal("tienda ya tiene 12 tablas", confirm.Title);
        Snapshot(window, "bd2-importar-encima");
        confirm.Close(false);
        await importing;
        Assert.Empty(runner.Calls);   // no se ha importado nada
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_SiFallaUnaNueva_SeBorra_YLoExplica()
    {
        var (main, window, fake, server) = await OpenAsync();
        using var _f = fake;
        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        db.PickImportFile = () => Task.FromResult<string?>(MySqlDump(1));
        var runner = new StreamingRunner { Stderr = "ERROR 1273 (HY000) at line 5: Unknown collation: 'utf8mb4_es_0900_ai_ci'" };
        runner.Finish(1);
        db.Runner = runner;

        var importing = db.ImportCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is DbImportDialogViewModel);
        var choose = (DbImportDialogViewModel)main.Dialog!;
        choose.ToNew = true;
        choose.NewName = "nueva";
        choose.Compat = false;
        Assert.StartsWith("Sin adaptarlo", choose.CompatHint);
        choose.ConfirmCommand.Execute(null);

        await UntilAsync(() => main.Dialog is DbTransferDialogViewModel { Done: true });
        var dialog = (DbTransferDialogViewModel)main.Dialog!;
        Assert.True(dialog.Failed);
        Assert.Contains("colacion que MariaDB 11.4.13 no conoce (linea 5 del fichero)", dialog.ResultText);
        Assert.Contains("Vuelve a importarlo con «Adaptarlo al vuelo» marcado.", dialog.Notes);
        Assert.Contains("Se ha borrado nueva, que se había creado para esto: no queda nada a medias.", dialog.Notes);
        Assert.DoesNotContain("nueva", server.Databases);
        Snapshot(window, "bd2-importar-fallo");
        dialog.FinishCommand.Execute(null);
        await importing;
    }

    // --- Laragon en la bienvenida ---

    /// <summary>Un Laragon de pega con MySQL 8.4 (en marcha en el 3419, para el runner de pega) y dos sitios.</summary>
    private sealed class FakeLaragon : IDisposable
    {
        public FakeLaragon()
        {
            Root = Path.Combine(Path.GetTempPath(), "roost-laragon-app-" + Guid.NewGuid().ToString("N"), "laragon");
            var version = Path.Combine(Root, "bin", "mysql", "mysql-8.4.11-winx64");
            foreach (var exe in new[] { "mysqld.exe", "mysql.exe", "mysqldump.exe" }) Write(Path.Combine(version, "bin", exe), "");
            Write(Path.Combine(version, "my.ini"), $"[mysqld]\ndatadir={Root.Replace('\\', '/')}/data/mysql-8\nport = 3419\n");
            Write(Path.Combine(Root, "usr", "laragon.ini"), "[mysql]\nVersion=mysql-8.4.11-winx64\nPort=3419\n");
            Write(Path.Combine(Root, "data", "mysql-8", "mysql", "db.opt"), "");
            Write(Path.Combine(Root, "data", "mysql-8", "blog_laragon", "posts.ibd"), new string('p', 180_000));
            Write(Path.Combine(Root, "data", "mysql-8", "crm", "contactos.ibd"), new string('c', 98_304));
            Write(Path.Combine(Root, "data", "mysql-8", "mi@002dtienda", "t.ibd"), "t");
            Directory.CreateDirectory(Path.Combine(Root, "data", "mysql-8", "vacia"));
            Directory.CreateDirectory(Path.Combine(Root, "www", "blog", "public"));
            Directory.CreateDirectory(Path.Combine(Root, "www", "crm"));
            Environment.SetEnvironmentVariable(LaragonInstall.RootVariable, Root);
        }

        public string Root { get; }

        public string DataDir => Path.Combine(Root, "data", "mysql-8");

        private static void Write(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(LaragonInstall.RootVariable, SinLaragonDeVerdad.Nowhere);
            try { Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>El cliente y el volcador del Laragon de pega: contesta a @@datadir y vuelca un .sql pequeño.</summary>
    private sealed class LaragonRunner(string dataDir) : ICommandRunner
    {
        public List<CommandSpec> Calls { get; } = [];

        public Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Calls.Add(spec);
            if (spec.StdoutStream is { } output)
            {
                output.Write(Encoding.UTF8.GetBytes($"-- MySQL dump 10.13  Distrib 8.4.11\nCREATE TABLE `t` (`id` int) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;\n-- {spec.Arguments[^1]}\n"));
                return Task.FromResult(new CommandResult(0, "", "", false));
            }
            return Task.FromResult(new CommandResult(0, dataDir.Replace("\\", "\\\\") + "\\\\\n", "", false));
        }
    }

    [AvaloniaFact]
    public async Task Bienvenida_ConLaragon_OfreceSusBasesDeDatos()
    {
        using var laragon = new FakeLaragon();
        var (main, window, fake, _) = await OpenAsync();
        using var _f = fake;

        var welcome = new WelcomeDialogViewModel(main);
        var showing = main.ShowDialogAsync(welcome);
        await welcome.LoadAsync();
        Assert.True(welcome.HasLaragon);
        Assert.Equal("MySQL 8.4.11", welcome.LaragonServer!.Label);
        Assert.Equal(["blog_laragon", "crm", "mi-tienda", "vacia"], welcome.LaragonDatabases.Select(d => d.Name));
        Assert.False(welcome.LaragonDatabases.Single(d => d.Name == "mi-tienda").Checked);   // con guion: no se puede traer
        Assert.Equal("vacía", welcome.LaragonDatabases.Single(d => d.Name == "vacia").Detail);
        Assert.True(welcome.ImportDatabases);
        Assert.Equal("Traer a Roost 3 bases de datos del MySQL 8.4.11 de Laragon", welcome.DatabasesText);
        Assert.True(welcome.ShowNext);
        Snapshot(window, "bd2-bienvenida-laragon");

        welcome.NextCommand.Execute(null);
        Assert.True(welcome.ShowDatabasesStep2);
        Assert.True(welcome.ShowSitesStep2);
        welcome.LaragonDatabases.Single(d => d.Name == "vacia").Checked = false;
        Snapshot(window, "bd2-bienvenida-laragon-paso-2");

        // Sin carpetas que registrar, el paso 2 sigue para las bases de datos.
        welcome.RegisterSites = false;
        Assert.True(welcome.HasStep2);
        Assert.False(welcome.ShowSitesStep2);

        welcome.LaragonPassword = "S3cret!";
        welcome.StartCommand.Execute(null);
        var plan = Assert.IsType<WelcomePlan>(await showing);
        Assert.Equal(["blog_laragon", "crm"], plan.LaragonDatabases!.Select(d => d.Name));
        Assert.Equal("S3cret!", plan.LaragonPassword);
        Assert.Empty(plan.Sites);
    }

    [AvaloniaFact]
    public async Task Bienvenida_TraeLasDeLaragon_ConSuProgreso_YSaltaLasQueYaEstan()
    {
        using var laragon = new FakeLaragon();
        var (main, window, fake, server) = await OpenAsync();
        using var _f = fake;
        server.Databases.Add("crm");
        var dbs = LaragonInstall.Databases(new LaragonInstall(laragon.Root).DbServers()[0]).Where(d => d.Importable && d.Name != "vacia").ToList();
        var laragonRunner = new LaragonRunner(laragon.DataDir);
        main.Databases.LaragonDumperFactory = () => new LaragonDumper(fake.Paths, laragonRunner)
        {
            AcceptsConnections = (port, _) => Task.FromResult(port == 3419),
            DataDirInUse = _ => true,
        };
        var import = new StreamingRunner { OnFinish = spec => server.Tables[spec.Arguments[^1]] = 1 };
        main.Databases.Runner = import;
        await main.NavigateToAsync(main.Databases);

        var bringing = main.Databases.ImportFromLaragonAsync(dbs, null);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var conflict = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal("crm ya existe en Roost", conflict.Title);
        Snapshot(window, "bd2-bienvenida-laragon-ya-existe");
        conflict.Close("skip");

        await UntilAsync(() => main.Dialog is DbTransferDialogViewModel && import.Waiting);
        var dialog = (DbTransferDialogViewModel)main.Dialog!;
        Assert.Equal(["Ver si el MySQL 8.4.11 de Laragon está en marcha", "Volcar blog_laragon", "Importar blog_laragon en Roost"], dialog.Steps.Select(s => s.Label));
        Assert.Contains("Laragon estaba en marcha: se ha volcado de su servidor, solo leyendo.", dialog.Notes);
        Snapshot(window, "bd2-bienvenida-laragon-trayendo");

        import.Finish();
        await UntilAsync(() => dialog.Done);
        Assert.True(dialog.Succeeded, dialog.ResultText);
        Assert.StartsWith("Traídas de Laragon: blog_laragon (1 tabla). Saltadas, porque ya estaban: crm", dialog.ResultText);
        Assert.Equal("blog_laragon", import.Calls.Single().Arguments[^1]);
        // Su volcador, con root y su puerto, sin contraseña en la línea de órdenes; y la carpeta de trabajo ya no está.
        var dump = laragonRunner.Calls.Single(c => c.StdoutStream is not null);
        Assert.Equal(Path.Combine(laragon.Root, "bin", "mysql", "mysql-8.4.11-winx64", "bin", "mysqldump.exe"), dump.Exe);
        Assert.Empty(Directory.Exists(fake.Paths.Run) ? Directory.GetDirectories(fake.Paths.Run) : []);
        Snapshot(window, "bd2-bienvenida-laragon-traidas");
        dialog.FinishCommand.Execute(null);
        Assert.True(await bringing);
    }

    [AvaloniaFact]
    public async Task Bienvenida_SinLaBaseDeDatosDeRoostEnMarcha_LoDiceYSigue()
    {
        using var laragon = new FakeLaragon();
        var (main, window, fake, _) = await OpenAsync();
        using var _f = fake;
        main.Stack.Show(null);
        var dbs = LaragonInstall.Databases(new LaragonInstall(laragon.Root).DbServers()[0]).Where(d => d.Importable).ToList();

        var bringing = main.Databases.ImportFromLaragonAsync(dbs, null);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var info = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal("No se han traído las bases de datos de Laragon", info.Title);
        Snapshot(window, "bd2-bienvenida-laragon-sin-bd");
        info.Close();
        Assert.False(await bringing);
    }
}
