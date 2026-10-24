using System.Diagnostics;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

namespace Roost.Desktop.Tests;

/// <summary>
/// Ajustes y sistema (oleada 5): los atajos, los programas preferidos (editor, terminal y
/// navegador), el log de la app, roostd y el DNS local en Ajustes. Nada se lanza de verdad:
/// <see cref="Shell.Launcher"/> apunta lo que se abriría y los programas salen de un equipo de pega
/// (<see cref="FakeMachine"/>). Las capturas van a <c>TestResults/snapshots/ajustes-sistema-*.png</c>.
/// </summary>
public class AjustesSistemaTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    internal static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake)> OpenAsync(bool running = true)
    {
        var fake = new FakeRoost();
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: FakeRoost.Version));
        main.Stack.Show(running ? FakeRoost.Running : null);
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.Current.ShownAsync();
        main.NotifyConfigChanged();
        return (main, window, fake);
    }

    internal static void Snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(SnapshotDir);
#pragma warning disable CS0618 // el PNG por defecto basta para revisar la captura
        frame.Save(Path.Combine(SnapshotDir, name + ".png"));
#pragma warning restore CS0618
    }

    /// <summary>Baja la pantalla actual (las capturas solo enseñan lo que cabe en la ventana).</summary>
    internal static void ScrollTo(MainWindow window, double y)
    {
        Dispatcher.UIThread.RunJobs();
        var scroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ScrollViewer>().First();
        scroll.Offset = new Avalonia.Vector(0, y);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Un equipo con VS Code, PhpStorm (Toolbox), Windows Terminal, PowerShell 7, Git Bash, Chrome y Firefox.</summary>
    internal static FakeMachine Machine()
    {
        var m = new FakeMachine();
        m.Files.Add(@"C:\Users\ana\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        m.Dirs[@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm"] = [@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0"];
        m.Dirs[@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0"] =
            [@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0\252.1", @"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0\261.4"];
        m.Files.Add(@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0\261.4\bin\phpstorm64.exe");
        m.Files.Add(@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0\252.1\bin\phpstorm64.exe");
        m.Files.Add(@"C:\Users\ana\AppData\Local\Microsoft\WindowsApps\wt.exe");
        m.Files.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");
        m.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        m.Files.Add(@"C:\Windows\System32\cmd.exe");
        m.Values[(RegHive.LocalMachine, @"SOFTWARE\GitForWindows", "InstallPath")] = @"D:\Git";
        m.Files.Add(@"D:\Git\git-bash.exe");
        m.SubKeys[(RegHive.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet")] = ["Google Chrome", "FIREFOX.EXE"];
        m.Values[(RegHive.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet\Google Chrome", null)] = "Google Chrome";
        m.Values[(RegHive.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet\Google Chrome\shell\open\command", null)] = "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"";
        m.Files.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        m.Values[(RegHive.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE", null)] = "Firefox";
        m.Values[(RegHive.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE\shell\open\command", null)] = "C:\\Program Files\\Mozilla Firefox\\firefox.exe -osint -url \"%1\"";
        m.Files.Add(@"C:\Program Files\Mozilla Firefox\firefox.exe");
        return m;
    }

    /// <summary>Pone el equipo de pega y apunta lo que se lanzaría; al acabar, lo deja como estaba.</summary>
    internal sealed class FakeShell : IDisposable
    {
        private readonly AppProbe _probe = Shell.Probe;
        private readonly Action<ProcessStartInfo> _launcher = Shell.Launcher;

        public FakeShell(FakeMachine machine)
        {
            Shell.Probe = machine.Probe;
            Shell.Launcher = Started.Add;
        }

        public List<ProcessStartInfo> Started { get; } = [];

        public void Dispose()
        {
            Shell.Probe = _probe;
            Shell.Launcher = _launcher;
        }
    }

    // J5: los atajos

    [AvaloniaFact]
    public async Task Atajos_LlevanASuPantalla_YConUnDialogoAbiertoNoHacenNada()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;

        window.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, "l");
        Assert.Same(main.Logs, main.Current);
        window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, ",");
        Assert.Same(main.Settings, main.Current);

        // F5 vuelve a leer la pantalla: en Registros aparece un log nuevo.
        await main.NavigateToAsync(main.Logs);
        File.WriteAllText(Path.Combine(fake.Paths.Logs, "nuevo.log"), "hola" + Environment.NewLine);
        Assert.DoesNotContain(main.Logs.Files, f => f.Display == "nuevo.log");
        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        await (main.RefreshPageCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Contains(main.Logs.Files, f => f.Display == "nuevo.log");
        main.Stack.Show(FakeRoost.Running);

        // Ctrl+N: Sitios y el diálogo de Nuevo sitio.
        window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(main.Sites, main.Current);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        // Con el diálogo abierto, Ctrl+L no se lleva la pantalla de debajo.
        window.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, "l");
        Assert.Same(main.Sites, main.Current);
        dialog.Close(null);

        Assert.Equal("Registros · Ctrl+L", main.RoostNav.Single(n => n.Page == main.Logs).Tip);
        Assert.Equal("Ajustes · Ctrl+,", main.RoostNav.Single(n => n.Page == main.Settings).Tip);
        Assert.Equal("Sitios · Ctrl+N: nuevo sitio", main.MainNav.Single(n => n.Page == main.Sites).Tip);
        Assert.Null(main.MainNav.Single(n => n.Page == main.Home).Tip);
    }

    // J3 + C4: los programas

    [Fact]
    public void ShellApps_EncuentraEditoresTerminalesYNavegadores_PorRutasYRegistro()
    {
        var probe = Machine().Probe;

        var editors = ShellApps.Editors(probe);
        Assert.Equal(["vscode", "phpstorm"], editors.Select(e => e.Id));
        // De Toolbox, la más nueva.
        Assert.Equal(@"C:\Users\ana\AppData\Local\JetBrains\Toolbox\apps\PhpStorm\ch-0\261.4\bin\phpstorm64.exe", editors[1].Exe);

        var terminals = ShellApps.Terminals(probe);
        Assert.Equal(["wt", "pwsh", "powershell", "gitbash", "cmd"], terminals.Select(t => t.Id));
        Assert.Equal(@"D:\Git\git-bash.exe", terminals.Single(t => t.Id == "gitbash").Exe);
        Assert.Equal("pwsh", ShellApps.AutoTerminal(terminals)!.Id);

        var browsers = ShellApps.Browsers(probe);
        Assert.Equal(["firefox.exe", "chrome.exe"], browsers.Select(b => b.Id));
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", browsers[0].Exe);

        Assert.Equal(@"C:\a b\x.exe", ShellApps.ExeOf("\"C:\\a b\\x.exe\" --flag \"%1\""));
        Assert.Equal(@"C:\Programs\x.exe", ShellApps.ExeOf(@"C:\Programs\x.exe -osint"));
        Assert.Null(ShellApps.ExeOf("  "));
    }

    [Fact]
    public void ShellApps_SinNada_NoEncuentraNingunEditor()
    {
        var probe = new FakeMachine().Probe;
        Assert.Empty(ShellApps.Editors(probe));
        Assert.Empty(ShellApps.Browsers(probe));
        Assert.Null(ShellApps.AutoTerminal(ShellApps.Terminals(probe)));
    }

    [Fact]
    public void Shell_AbreConElEditorLaTerminalYElNavegadorElegidos()
    {
        using var shell = new FakeShell(Machine());
        var prefs = new DesktopPrefs();
        var before = Shell.Preferences;
        Shell.Preferences = () => prefs;
        try
        {
            // Automático: el primer editor que hay (VS Code), con la ruta como argumento.
            Shell.OpenFile(@"C:\roost\etc\php\conf.d\99-usuario.ini");
            Assert.EndsWith("Code.exe", shell.Started[^1].FileName);
            Assert.Equal([@"C:\roost\etc\php\conf.d\99-usuario.ini"], shell.Started[^1].ArgumentList);
            Assert.False(shell.Started[^1].UseShellExecute);

            prefs = prefs with { Editor = "phpstorm" };
            Shell.OpenInEditor(Path.GetTempPath());
            Assert.EndsWith("phpstorm64.exe", shell.Started[^1].FileName);

            // El programa asociado: lo abre Windows.
            prefs = prefs with { Editor = ShellApps.AssociatedEditor };
            Shell.OpenFile(@"C:\roost\logs\roostd.log");
            Assert.True(shell.Started[^1].UseShellExecute);
            Assert.Equal(@"C:\roost\logs\roostd.log", shell.Started[^1].FileName);

            // Un navegador elegido solo para las URL web; services.msc sigue yendo por Windows.
            prefs = prefs with { Browser = "firefox.exe" };
            Shell.OpenUrl("https://tienda.test/");
            Assert.EndsWith("firefox.exe", shell.Started[^1].FileName);
            Assert.Equal(["https://tienda.test/"], shell.Started[^1].ArgumentList);
            Shell.OpenUrl("services.msc");
            Assert.True(shell.Started[^1].UseShellExecute);
            prefs = prefs with { Browser = "brave.exe" };   // ya no está: el predeterminado
            Shell.OpenUrl("http://localhost/");
            Assert.True(shell.Started[^1].UseShellExecute);
        }
        finally
        {
            Shell.Preferences = before;
        }
    }

    [Fact]
    public void Shell_Terminal_WindowsTerminalPoneElPathConEncodedCommand_YGitBashLaCarpeta()
    {
        var terminals = ShellApps.Terminals(Machine().Probe);
        var extra = new[] { @"C:\roost\bin\php\8.4.27-nts-x64", @"C:\roost\bin\mariadb\11.4.13\bin" };
        var env = new Dictionary<string, string> { ["ROOST_PHP_VERSION"] = "8.3.36-nts-x64" };

        var wt = Shell.TerminalStart(terminals.Single(t => t.Id == "wt"), terminals, @"C:\roost\www\tienda", extra, env, @"C:\Windows");
        Assert.EndsWith("wt.exe", wt.FileName);
        Assert.Equal(["-d", @"C:\roost\www\tienda", @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoExit", "-EncodedCommand"], wt.ArgumentList.Take(5));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(wt.ArgumentList[5]));
        Assert.Contains(@"$env:PATH = 'C:\roost\bin\php\8.4.27-nts-x64;C:\roost\bin\mariadb\11.4.13\bin;' + $env:PATH", script);
        Assert.Contains("$env:ROOST_PHP_VERSION = '8.3.36-nts-x64'", script);
        Assert.DoesNotContain(';', wt.ArgumentList[5]);   // wt toma el ; por un separador suyo

        var bash = Shell.TerminalStart(terminals.Single(t => t.Id == "gitbash"), terminals, @"C:\roost\www", extra, env, @"C:\Windows");
        Assert.Equal([@"--cd=C:\roost\www"], bash.ArgumentList);
        Assert.StartsWith(@"C:\roost\bin\php\8.4.27-nts-x64;", bash.Environment["PATH"]);
        Assert.Equal("8.3.36-nts-x64", bash.Environment["ROOST_PHP_VERSION"]);

        var cmd = Shell.TerminalStart(terminals.Single(t => t.Id == "cmd"), terminals, @"C:\roost\www", extra, env, @"C:\Windows");
        Assert.Empty(cmd.ArgumentList);
        Assert.Equal(@"C:\roost\www", cmd.WorkingDirectory);

        // Automática: PowerShell 7 con -NoExit, como siempre.
        var auto = Shell.TerminalStart(ShellApps.AutoTerminal(terminals), terminals, @"C:\roost\www", extra, env, @"C:\Windows");
        Assert.EndsWith("pwsh.exe", auto.FileName);
        Assert.Equal(["-NoExit"], auto.ArgumentList);
        Assert.EndsWith(@";C:\Windows", auto.Environment["PATH"]);

        // Las comillas simples se doblan (PowerShell).
        Assert.Contains("'C:\\o''brien\\php;'", Encoding.Unicode.GetString(Convert.FromBase64String(Shell.PowerShellPrelude([@"C:\o'brien\php"], new Dictionary<string, string>()))));
    }

    // J6: el log de la app

    [Fact]
    public void AppLog_TapaContrasenasYTokens()
    {
        Assert.Equal("clonar https://***@github.com/acme/tienda", AppLog.Scrub("clonar https://ana:ghp_1234@github.com/acme/tienda"));
        Assert.Equal("clonar https://***@gitlab.com/x", AppLog.Scrub("clonar https://oauth2@gitlab.com/x"));
        Assert.Equal("password=*** y token: ***", AppLog.Scrub("password=secreta y token: abc123"));
        Assert.Equal("PASSWORD=***", AppLog.Scrub("PASSWORD=\"con espacios\""));
        Assert.Equal("Puerto 3306 ocupado por mysqld (PID 42)", AppLog.Scrub("Puerto 3306 ocupado por mysqld (PID 42)"));
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \[error\] pwd=\*\*\*$", AppLog.Format(DateTime.Now, "error", "pwd=x"));
    }

    [Fact]
    public void AppLog_EscribeEnLogs_YRotaAlPasarDelLimite()
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-applog-" + Guid.NewGuid().ToString("N"));
        var paths = new Roost.Core.Platform.RoostPaths(root, Path.Combine(root, "cache"));
        try
        {
            AppLog.Start(paths, "0.1.7", ["--minimized"]);
            AppLog.Error("No se pudo abrir el vhost", new IOException("en uso"));
            var file = Path.Combine(paths.Logs, AppLog.FileName);
            var text = File.ReadAllText(file);
            Assert.Contains("[info] Roost 0.1.7 se abre · raíz " + root + " · --minimized", text);
            Assert.Contains("[error] No se pudo abrir el vhost: IOException: en uso", text);

            // En pausa (mover la raíz) no se escribe nada.
            using (AppLog.Pause()) AppLog.Info("esto no va");
            Assert.DoesNotContain("esto no va", File.ReadAllText(file));

            File.AppendAllText(file, new string('x', (int)AppLog.MaxBytes));
            AppLog.Info("tras rotar");
            Assert.True(File.Exists(file + ".1"));
            Assert.Contains("tras rotar", File.ReadAllText(file));
            Assert.True(new FileInfo(file).Length < 1000);
        }
        finally
        {
            AppLog.Stop();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AppLog_SinEmpezar_NoEscribe_YAvaloniaRellenaLaPlantilla()
    {
        AppLog.Stop();
        AppLog.Info("nada");   // sin Start (los tests de vistas) no hay fichero
        Assert.Null(AppLog.FilePath);
        Assert.Equal("Error in binding Foo to Bar", AppLog.AvaloniaSink.Fill("Error in binding {Property} to {Target}", ["Foo", "Bar"]));
        Assert.Equal("sin {Valores}", AppLog.AvaloniaSink.Fill("sin {Valores}", []));
    }

    // Ajustes: Programas, roostd y la app, el DNS local

    [AvaloniaFact]
    public async Task Ajustes_ProgramasRoostdYAvisosDeLaBandeja()
    {
        using var shell = new FakeShell(Machine());
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;

        await main.NavigateToAsync(main.Settings);
        var settings = main.Settings;
        Assert.Equal(["Automático (Visual Studio Code)", "El programa asociado de Windows", "Visual Studio Code", "PhpStorm"], settings.EditorChoices.Select(c => c.Name));
        Assert.Equal("Automática (PowerShell 7)", settings.TerminalChoice!.Name);
        Assert.Contains(settings.TerminalChoices, c => c.Name == "Windows Terminal (con PowerShell 7)");
        Assert.Equal("El predeterminado de Windows", settings.BrowserChoice!.Name);
        Assert.Contains("VS Code", settings.ProgramsHint.Replace("Visual Studio Code", "VS Code"));
        Assert.StartsWith("roostd en marcha · PID 70324 · el stack, en marcha desde las", settings.DaemonText);
        Assert.Equal(Roost.Desktop.Controls.Tone.Ok, settings.DaemonTone);
        Assert.Equal("El DNS local no está en marcha (no hace falta: los .test van por el hosts).", settings.DnsProbeText);
        Assert.True(settings.NotifyServiceDown);
        Assert.True(settings.NotifyAuditRed);
        Snapshot(window, "ajustes-sistema");
        ScrollTo(window, 620);
        Snapshot(window, "ajustes-sistema-abajo");
        ScrollTo(window, 0);

        // Elegir se guarda en desktop.json, y Shell lo usa.
        settings.EditorChoice = settings.EditorChoices.Single(c => c.Id == "phpstorm");
        settings.TerminalChoice = settings.TerminalChoices.Single(c => c.Id == "wt");
        settings.BrowserChoice = settings.BrowserChoices.Single(c => c.Id == "chrome.exe");
        Assert.Equal(("phpstorm", "wt", "chrome.exe"), (main.App.Prefs.Editor, main.App.Prefs.Terminal, main.App.Prefs.Browser));
        Assert.Equal("phpstorm", DesktopPrefs.Load(fake.Paths).Editor);
        await settings.TryEditorCommand.ExecuteAsync(null);
        Assert.EndsWith("phpstorm64.exe", shell.Started[^1].FileName);
        Assert.Equal([fake.Paths.Www], shell.Started[^1].ArgumentList);

        settings.NotifyAuditRed = false;
        Assert.False(DesktopPrefs.Load(fake.Paths).NotifyAuditRed);

        // Lo elegido sigue elegido al volver a entrar.
        await main.NavigateToAsync(main.Home);
        await main.NavigateToAsync(main.Settings);
        Assert.Equal("phpstorm", settings.EditorChoice!.Id);
        Assert.False(settings.NotifyAuditRed);

        // Parado: roostd no está.
        main.Stack.Show(null);
        Assert.Equal("roostd no está en marcha: arranca con Iniciar todo (o roost up).", settings.DaemonText);
    }

    // E4: la tarea al iniciar sesión

    /// <summary>Un schtasks de pega: recuerda si la tarea «está» y apunta lo que se le pide.</summary>
    private sealed class FakeSchtasks : Roost.Core.Scanning.ICommandRunner
    {
        public bool Exists { get; set; }
        public bool Deny { get; set; }
        public List<string> Calls { get; } = [];

        public Task<Roost.Core.Scanning.CommandResult> RunAsync(Roost.Core.Scanning.CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Calls.Add(spec.Arguments[0]);
            var result = spec.Arguments[0] switch
            {
                "/query" => Exists ? new Roost.Core.Scanning.CommandResult(0, Roost.Core.Platform.LogonTask.Xml(@"C:\Program Files\Roost\roost.exe", "PC\\ana", "conhost.exe"), "", false) : new(1, "", "ERROR: no existe", false),
                "/create" when Deny => new(1, "", "ERROR: Acceso denegado.", false),
                "/create" => Ok(() => Exists = true),
                "/delete" => Ok(() => Exists = false),
                _ => new Roost.Core.Scanning.CommandResult(1, "", "", false),
            };
            return Task.FromResult(result);
        }

        private static Roost.Core.Scanning.CommandResult Ok(Action action)
        {
            action();
            return new(0, "CORRECTO", "", false);
        }
    }

    [AvaloniaFact]
    public async Task Ajustes_IniciarTodoAlIniciarSesion_ConLaTareaProgramada()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        var schtasks = new FakeSchtasks();
        main.App.LogonTask = new Roost.Core.Platform.LogonTask(schtasks, "schtasks.exe", _ => null);

        await main.NavigateToAsync(main.Settings);
        var settings = main.Settings;
        Assert.True(settings.CanStackAtLogon);
        Assert.False(settings.StackAtLogon);
        Assert.StartsWith("Una tarea programada de tu usuario", settings.StackAtLogonHint);

        settings.StackAtLogon = true;
        await WaitAsync(() => schtasks.Exists && settings.StackAtLogonHint.StartsWith("Lanza roost up", StringComparison.Ordinal));
        Assert.Contains("/create", schtasks.Calls);
        Assert.True(settings.StackAtLogon);

        settings.StackAtLogon = false;
        await WaitAsync(() => !schtasks.Exists && settings.StackAtLogonHint.StartsWith("Una tarea", StringComparison.Ordinal));
        Assert.Contains("/delete", schtasks.Calls);

        // Si Windows no la deja crear, lo dice y la casilla vuelve atrás.
        schtasks.Deny = true;
        settings.StackAtLogon = true;
        await WaitAsync(() => main.Dialog is MessageDialogViewModel);
        var error = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("No se pudo crear la tarea al iniciar sesión", error.Title);
        Assert.Contains("Acceso denegado", error.Text);
        error.Close(null);
        await WaitAsync(() => !settings.StackAtLogon);

        // Con ROOST_HOME no se puede (la tarea arrancaría la Roost de siempre).
        main.App.LogonTask = new Roost.Core.Platform.LogonTask(schtasks, "schtasks.exe", name => name == "ROOST_HOME" ? @"C:\pruebas" : null);
        await main.NavigateToAsync(main.Home);
        await main.NavigateToAsync(main.Settings);
        Assert.False(settings.CanStackAtLogon);
        Assert.Contains("ROOST_HOME", settings.StackAtLogonHint);
        Snapshot(window, "ajustes-sistema-tarea");
    }

    // J2: cambiar la raíz

    [Fact]
    public void CambiarRaiz_QueCarpetasValen()
    {
        (string? Problem, string? Info) C(string target, bool move = true, bool exists = false, bool roost = false) =>
            ChangeRootDialogViewModel.Check(@"C:\roost", target, move, _ => exists, _ => exists, _ => roost);

        Assert.Null(C(@"D:\roost").Problem);
        Assert.Null(C(@"D:\dev\roost-2").Problem);
        Assert.Equal("Elige una carpeta.", C("  ").Problem);
        Assert.StartsWith("Tiene que ser una ruta completa", C(@"roost").Problem);
        Assert.StartsWith("Tiene que estar en un disco de este equipo", C(@"\\nas\roost").Problem);
        Assert.StartsWith("Sin espacios, tildes", C(@"D:\mis proyectos\roost").Problem);
        Assert.StartsWith("Sin espacios, tildes", C(@"D:\Ángel\roost").Problem);
        Assert.StartsWith("Un disco entero no", C(@"D:\").Problem);
        Assert.Equal("Es la que ya usa Roost.", C(@"C:\ROOST\").Problem);
        Assert.StartsWith("No puede estar dentro", C(@"C:\roost\otra").Problem);
        // (Archivos de programa ya lo para el espacio.)
        Assert.StartsWith("Ni en Windows", C(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "roost")).Problem);
        Assert.StartsWith("Para mover, la carpeta tiene que estar vacía", C(@"D:\roost", exists: true).Problem);
        Assert.StartsWith("Tiene cosas que no son de Roost", C(@"D:\roost", move: false, exists: true).Problem);
        Assert.Equal((null, "Ahí ya hay una Roost: se usará la que hay (sus PHP, sus datos y su CA)."), C(@"D:\roost", move: false, exists: true, roost: true));
        Assert.StartsWith("Roost empezará de cero ahí", C(@"D:\roost", move: false).Info);
    }

    [AvaloniaFact]
    public async Task CambiarRaiz_MoverLoQueHay_LoCopiaApuntaYDejaElMarcador()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var target = Path.Combine(Path.GetDirectoryName(fake.Paths.Root)!, "raiz-nueva");

        await main.NavigateToAsync(main.Settings);
        var changing = main.Settings.ChangeRootCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<ChangeRootDialogViewModel>(main.Dialog);
        Assert.Equal(fake.Paths.Root, dialog.From);
        Assert.False(dialog.CanStart);
        await WaitAsync(() => dialog.Size is not null);
        Assert.StartsWith("Ahora ocupa", dialog.SizeText);
        dialog.Target = target;
        Assert.True(dialog.CanStart);
        Assert.Equal("Mover", dialog.StartLabel);
        Snapshot(window, "ajustes-sistema-raiz");

        await dialog.StartCommand.ExecuteAsync(null);
        Assert.Null(dialog.Error);
        Assert.True(dialog.Done);
        Assert.True(File.Exists(Path.Combine(target, "bin", "php", "8.4.27-nts-x64", "php.exe")));
        Assert.True(File.Exists(Path.Combine(target, "state", "sites.json")));
        Assert.Equal(target, Roost.Core.Config.RoostConfig.ReadRoot(fake.Paths.ConfigFile));
        // El roost.toml vivía dentro de la raíz (como con ROOST_HOME): no se copia y la de antes no se renombra.
        Assert.False(File.Exists(Path.Combine(target, "roost.toml")));
        Assert.True(Directory.Exists(fake.Paths.Root));
        Assert.Contains("donde también están el roost.toml", dialog.ResultText);
        var marker = RootChangeMarker.Take(new Roost.Core.Platform.RoostPaths(target, fake.Paths.Cache));
        Assert.NotNull(marker);
        Assert.True(marker.Moved);
        Assert.False(marker.StackWasRunning);
        Snapshot(window, "ajustes-sistema-raiz-hecho");

        dialog.FinishCommand.Execute(null);
        await changing;   // sin App.Relaunch (los tests): lo dice en la barra de estado
        Assert.Equal("Cierra Roost y vuelve a abrirlo para usar la raíz nueva", main.FlashText);
        Roost.Core.Platform.RootMover.DeleteTree(target);
    }

    [AvaloniaFact]
    public async Task CambiarRaiz_EmpezarDeCero_SoloApunta()
    {
        var (main, _, fake) = await OpenAsync(running: false);
        using var __ = fake;
        var target = Path.Combine(Path.GetDirectoryName(fake.Paths.Root)!, "de-cero");
        var dialog = new ChangeRootDialogViewModel(main) { Target = target, Fresh = true };
        Assert.False(dialog.Move);
        Assert.Equal("Cambiar", dialog.StartLabel);
        Assert.StartsWith("Roost empezará de cero ahí", dialog.Info);
        await dialog.StartCommand.ExecuteAsync(null);
        Assert.True(dialog.Done);
        Assert.True(Directory.Exists(target));
        Assert.Equal(["state"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));   // solo el marcador
        Assert.Equal(target, Roost.Core.Config.RoostConfig.ReadRoot(fake.Paths.ConfigFile));
        Assert.True(File.Exists(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "php.exe")));   // la de antes, intacta
        Assert.StartsWith($"Roost usará {target}", dialog.ResultText);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Sitios_AbrirEnElEditor_ConLaCarpetaDelSitio()
    {
        using var shell = new FakeShell(Machine());
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var tienda = main.Sites.Rows.Single(r => r.Site.Name == "tienda");
        main.Sites.ChooseCommand.Execute(tienda);
        await main.Sites.OpenInEditorCommand.ExecuteAsync(null);
        Assert.EndsWith("Code.exe", shell.Started[^1].FileName);
        Assert.Equal([tienda.Site.Folder], shell.Started[^1].ArgumentList);
        Snapshot(window, "ajustes-sistema-sitios-editor");
    }
}

/// <summary>Un equipo de pega para <see cref="ShellApps"/>: ficheros, variables, registro y carpetas.</summary>
internal sealed class FakeMachine
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = @"C:\Users\ana\AppData\Local",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["SystemRoot"] = @"C:\Windows",
        ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
        ["PATH"] = @"C:\Windows\System32",
    };

    public Dictionary<(RegHive, string, string?), string> Values { get; } = [];

    public Dictionary<(RegHive, string), List<string>> SubKeys { get; } = [];

    public Dictionary<string, List<string>> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AppProbe Probe => new(
        Files.Contains,
        name => Env.GetValueOrDefault(name),
        (hive, key, value) => Values.GetValueOrDefault((hive, key, value)),
        (hive, key) => SubKeys.GetValueOrDefault((hive, key)) ?? [],
        dir => Dirs.GetValueOrDefault(dir) ?? []);
}
