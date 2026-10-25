using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Tests;

/// <summary>
/// La app en Linux (L2): lo que la app hace distinto fuera de Windows. Lo puro (las listas de
/// programas de un equipo Linux de pega, las terminales, el .desktop de autostart, las reglas POSIX de
/// la raíz, los textos por sistema, la cultura sin ICU) se prueba en cualquier sistema; lo que toca el
/// sistema de verdad (el bloqueo y el socket de la instancia única, el fichero de autostart) solo en Linux.
/// </summary>
public class LinuxAppTests
{
    /// <summary>Un Ubuntu de pega: VS Code del .deb, PhpStorm de Toolbox, gnome-terminal y xterm, Firefox y Chromium, DBeaver y Beekeeper.</summary>
    internal static FakeMachine Ubuntu()
    {
        var m = new FakeMachine { Posix = true };
        m.Env.Clear();
        m.Env["HOME"] = "/home/ana";
        m.Env["PATH"] = "/home/ana/.local/bin:/usr/local/bin:/usr/bin:/bin:/snap/bin";
        m.Files.Add("/usr/bin/code");
        m.Files.Add("/home/ana/.local/share/JetBrains/Toolbox/apps/phpstorm/bin/phpstorm.sh");
        m.Files.Add("/usr/bin/gnome-terminal");
        m.Files.Add("/usr/bin/xterm");
        m.Files.Add("/usr/bin/x-terminal-emulator");
        m.Files.Add("/usr/bin/firefox");
        m.Files.Add("/usr/bin/chromium");
        m.Files.Add("/usr/bin/dbeaver");
        m.Files.Add("/snap/bin/beekeeper-studio");
        return m;
    }

    [Fact]
    public void ShellApps_Linux_EncuentraEditoresTerminalesYNavegadores_PorElPath()
    {
        var probe = Ubuntu().Probe;

        var editors = ShellApps.Editors(probe);
        Assert.Equal(["vscode", "phpstorm"], editors.Select(e => e.Id));
        Assert.Equal("/usr/bin/code", editors[0].Exe);
        Assert.Equal("/home/ana/.local/share/JetBrains/Toolbox/apps/phpstorm/bin/phpstorm.sh", editors[1].Exe);

        var terminals = ShellApps.Terminals(probe);
        Assert.Equal(["gnome-terminal", "x-terminal-emulator", "xterm"], terminals.Select(t => t.Id));
        Assert.Equal("gnome-terminal", ShellApps.AutoTerminal(terminals)!.Id);

        var browsers = ShellApps.Browsers(probe);
        Assert.Equal(["chromium", "firefox"], browsers.Select(b => b.Id));
        Assert.Equal(["Chromium", "Firefox"], browsers.Select(b => b.Name));

        // Un equipo sin nada: listas vacías y ninguna terminal automática.
        var empty = new FakeMachine { Posix = true }.Probe;
        Assert.Empty(ShellApps.Editors(empty));
        Assert.Empty(ShellApps.Browsers(empty));
        Assert.Null(ShellApps.AutoTerminal(ShellApps.Terminals(empty)));
    }

    [Fact]
    public void Shell_Linux_LaTerminalAbreElShellConElPathPorEnv_YLaConsolaLanzaLaOrden()
    {
        var terminals = ShellApps.Terminals(Ubuntu().Probe);
        var extra = new[] { "/home/ana/.local/share/roost/bin/php/8.4.27-nts-x64/bin", "/home/ana/.local/share/roost/bin/mariadb/11.4.13/bin" };
        var env = new Dictionary<string, string> { ["ROOST_PHP_VERSION"] = "8.3.36-nts-x64" };

        // gnome-terminal es un cliente de un servidor que ya estaba: el PATH va por env, no solo por el entorno del proceso.
        var gnome = Shell.PosixTerminalStart(terminals.Single(t => t.Id == "gnome-terminal"), "/var/www/roost/tienda", extra, env, "/usr/bin:/bin", "/usr/bin/zsh");
        Assert.Equal("/usr/bin/gnome-terminal", gnome.FileName);
        Assert.Equal(["--working-directory=/var/www/roost/tienda", "--", "env", $"PATH={extra[0]}:{extra[1]}:/usr/bin:/bin", "ROOST_PHP_VERSION=8.3.36-nts-x64", "/usr/bin/zsh"], gnome.ArgumentList);
        Assert.Equal($"{extra[0]}:{extra[1]}:/usr/bin:/bin", gnome.Environment["PATH"]);
        Assert.Equal("/var/www/roost/tienda", gnome.WorkingDirectory);

        // Sin SHELL, bash; xterm no sabe de carpetas: se lanza desde ella con -e.
        var xterm = Shell.PosixTerminalStart(terminals.Single(t => t.Id == "xterm"), "/home/ana/www", [], new Dictionary<string, string>(), "/usr/bin", null);
        Assert.Equal(["-e", "env", "PATH=/usr/bin", "/bin/bash"], xterm.ArgumentList);
        Assert.Equal("/home/ana/www", xterm.WorkingDirectory);

        // La consola SQL: la orden dentro de la terminal, con los argumentos sueltos (las comillas de la línea se quitan).
        var console = Shell.PosixCommandStart(new ShellApp("konsole", "Konsole", "/usr/bin/konsole"), "/home/ana/www",
            ["/home/ana/.local/share/roost/bin/mariadb/11.4.13/bin/mariadb", .. Shell.SplitArguments("--defaults-file=\"/home/ana/mi casa/my.ini\" tienda")]);
        Assert.Equal(["--workdir", "/home/ana/www", "-e", "/home/ana/.local/share/roost/bin/mariadb/11.4.13/bin/mariadb", "--defaults-file=/home/ana/mi casa/my.ini", "tienda"], console.ArgumentList);
        var tilix = Shell.PosixCommandStart(new ShellApp("tilix", "Tilix", "/usr/bin/tilix"), "/home/ana/www", ["redis-cli", "-h", "127.0.0.1", "-p", "6379"]);
        Assert.Equal(["--working-directory=/home/ana/www", "-e", "redis-cli -h 127.0.0.1 -p 6379"], tilix.ArgumentList);
        Assert.Equal(["start", "--cwd", "/x", "--", "bash"], Shell.PosixCommandStart(new ShellApp("wezterm", "WezTerm", "/usr/bin/wezterm"), "/x", ["bash"]).ArgumentList);
        Assert.Equal(["--directory", "/x", "bash"], Shell.PosixCommandStart(new ShellApp("kitty", "kitty", "/usr/bin/kitty"), "/x", ["bash"]).ArgumentList);

        Assert.Equal(["a", "b c", "--x=d e", "f"], Shell.SplitArguments("a \"b c\" --x=\"d e\"  f "));
        Assert.Empty(Shell.SplitArguments("   "));
    }

    [Fact]
    public void Shell_Linux_AbreConElEditorYElNavegadorElegidos()
    {
        var machine = Ubuntu();
        using var shell = new AjustesSistemaTests.FakeShell(machine);
        var prefs = new DesktopPrefs();
        var before = Shell.Preferences;
        Shell.Preferences = () => prefs;
        try
        {
            // Automático: el primer editor que hay (VS Code), con la ruta como argumento.
            Shell.OpenFile("/home/ana/.local/share/roost/etc/php/conf.d/99-usuario.ini");
            Assert.Equal("/usr/bin/code", shell.Started[^1].FileName);
            Assert.Equal(["/home/ana/.local/share/roost/etc/php/conf.d/99-usuario.ini"], shell.Started[^1].ArgumentList);

            // El programa asociado: xdg-open (UseShellExecute fuera de Windows).
            prefs = prefs with { Editor = ShellApps.AssociatedEditor };
            Shell.OpenFile("/home/ana/.local/share/roost/logs/roostd.log");
            Assert.True(shell.Started[^1].UseShellExecute);

            // Un navegador elegido solo para las URL web; uno que ya no está, el predeterminado.
            prefs = prefs with { Browser = "firefox" };
            Shell.OpenUrl("https://tienda.test/");
            Assert.Equal("/usr/bin/firefox", shell.Started[^1].FileName);
            Assert.Equal(["https://tienda.test/"], shell.Started[^1].ArgumentList);
            prefs = prefs with { Browser = "brave-browser" };
            Shell.OpenUrl("http://localhost:8080/");
            Assert.True(shell.Started[^1].UseShellExecute);
        }
        finally
        {
            Shell.Preferences = before;
        }
    }

    [Fact]
    public void DbClients_Linux_DBeaverYBeekeeper_YComoSeAbren()
    {
        var clients = DbClients.Detect(Ubuntu().Probe);
        Assert.Equal([DbClientKind.DBeaver, DbClientKind.Beekeeper], clients.Select(c => c.Kind));
        Assert.Equal(["/usr/bin/dbeaver", "/snap/bin/beekeeper-studio"], clients.Select(c => c.Exe));
        Assert.All(clients, c => Assert.True(c.AcceptsConnection));
        Assert.Empty(DbClients.Detect(new FakeMachine { Posix = true }.Probe));

        var connection = new DbConnection(DbEngine.MariaDb, "127.0.0.1", 3409, "root", "", "tienda", "MariaDB 11.4 de Roost");
        var beekeeper = DbClients.StartInfo(clients[1], connection);
        Assert.Equal(["mysql://root@127.0.0.1:3409/tienda"], beekeeper.ArgumentList);
        var dbeaver = DbClients.StartInfo(clients[0], connection);
        Assert.Equal("-con", dbeaver.ArgumentList[0]);
        Assert.Contains("driver=mariadb|host=127.0.0.1|port=3409|user=root|", dbeaver.ArgumentList[1]);
        Assert.Contains("database=tienda", dbeaver.ArgumentList[1]);
    }

    [Fact]
    public void Autostart_ElDesktopDeAutostart_YLoQueSeConsideraPuesto()
    {
        var entry = Autostart.DesktopEntry("/usr/lib/roost/roost-desktop");
        Assert.StartsWith("[Desktop Entry]\nType=Application\nName=Roost\n", entry);
        Assert.Contains("\nExec=/usr/lib/roost/roost-desktop --minimized\n", entry);
        Assert.Contains("\nX-GNOME-Autostart-enabled=true\n", entry);
        Assert.Contains("\nExec=\"/home/ana/mis cosas/roost-desktop\" --minimized\n", Autostart.DesktopEntry("/home/ana/mis cosas/roost-desktop"));
        Assert.Equal("\"/opt/a\\$b/roost\"", Autostart.QuoteExec("/opt/a$b/roost"));

        Assert.True(Autostart.IsEntryEnabled(entry));
        Assert.False(Autostart.IsEntryEnabled(entry + "Hidden=true\n"));
        Assert.False(Autostart.IsEntryEnabled(entry.Replace("X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false")));
    }

    [LinuxFact]
    public void Autostart_Linux_PoneYQuitaElDesktopEnXdgConfigHome()
    {
        var config = Path.Combine(Path.GetTempPath(), "roost-autostart-" + Guid.NewGuid().ToString("N"));
        var before = Autostart.Env;
        Autostart.Env = name => name == "XDG_CONFIG_HOME" ? config : Environment.GetEnvironmentVariable(name);
        try
        {
            Assert.True(Autostart.Supported);
            Assert.Equal(Path.Combine(config, "autostart", Autostart.DesktopFileName), Autostart.LinuxFile());
            Assert.False(Autostart.IsEnabled());
            Autostart.Set(true);
            Assert.True(Autostart.IsEnabled());
            var text = File.ReadAllText(Autostart.LinuxFile());
            Assert.Contains("--minimized", text);
            Assert.Contains(Environment.ProcessPath!.Length > 0 ? Path.GetFileName(Environment.ProcessPath) : "roost-desktop", text);
            Autostart.Set(false);
            Assert.False(Autostart.IsEnabled());
            Assert.False(File.Exists(Autostart.LinuxFile()));
        }
        finally
        {
            Autostart.Env = before;
            try { Directory.Delete(config, recursive: true); } catch { /* best effort */ }
        }
    }

    [LinuxFact]
    public void SingleInstance_Linux_LaSegundaNoEntra_YLaPrimeraRecibeElAviso()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roost-instancia-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = SingleInstance.TryAcquirePosix(dir, "-prueba");
            Assert.NotNull(first);
            var (lockFile, socket) = SingleInstance.PosixFiles(dir, "-prueba");
            Assert.True(File.Exists(lockFile));
            Assert.True(File.Exists(socket));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));

            var shown = new ManualResetEventSlim();
            first.Listen(shown.Set);
            // La segunda app: no se queda con la instancia, y la primera recibe «enséñate».
            Assert.Null(SingleInstance.TryAcquirePosix(dir, "-prueba"));
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));

            // Otra raíz (otro tag) no se cruza con esta.
            using var other = SingleInstance.TryAcquirePosix(dir, "-otra");
            Assert.NotNull(other);

            // Al soltar la primera, la siguiente entra (y el socket viejo no estorba).
            first.Dispose();
            using var third = SingleInstance.TryAcquirePosix(dir, "-prueba");
            Assert.NotNull(third);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void CambiarRaiz_Linux_QueCarpetasValen()
    {
        var system = new ChangeRootDialogViewModel.SystemFolders(false,
            ["/usr", "/etc", "/bin", "/sbin", "/lib", "/boot", "/dev", "/proc", "/sys", "/run", "/root", "/var/lib", "/var/log", "/snap"],
            "/home/ana");
        (string? Problem, string? Info) C(string target, bool move = true, bool exists = false, bool roost = false) =>
            ChangeRootDialogViewModel.Check("/home/ana/.local/share/roost", target, move, _ => exists, _ => exists, _ => roost, system);

        Assert.Null(C("/srv/roost").Problem);
        Assert.Null(C("/opt/roost").Problem);
        Assert.Null(C("/home/ana/dev/roost").Problem);
        Assert.Null(C("/home/ana/.local/lib/roost").Problem);   // debajo de la carpeta personal vale aunque se llame lib
        Assert.Equal("Elige una carpeta.", C("  ").Problem);
        Assert.StartsWith("Tiene que ser una ruta completa, como /srv/roost", C("roost").Problem);
        Assert.StartsWith("Tiene que ser una ruta completa", C("~/roost").Problem);
        Assert.StartsWith("Sin espacios, tildes", C("/home/ana/mis proyectos/roost").Problem);
        Assert.StartsWith("Sin espacios, tildes", C("/home/ana/Ángel/roost").Problem);
        Assert.StartsWith("La raíz del disco no", C("/").Problem);
        Assert.Equal("Es la que ya usa Roost.", C("/home/ana/.local/share/roost/").Problem);
        Assert.Equal("Es la que ya usa Roost.", C("/home/ana/.local/share/../share/roost").Problem);
        Assert.StartsWith("No puede estar dentro", C("/home/ana/.local/share/roost/otra").Problem);
        Assert.StartsWith("No puede estar dentro", C("/home/ana/.local").Problem);
        Assert.StartsWith("Ahí no (/usr es del sistema)", C("/usr/local/roost").Problem);
        Assert.StartsWith("Ahí no (/var/lib es del sistema)", C("/var/lib/roost").Problem);
        Assert.StartsWith("Ahí no (/etc es del sistema)", C("/etc").Problem);
        Assert.StartsWith("Para mover, la carpeta tiene que estar vacía", C("/srv/roost", exists: true).Problem);
        Assert.StartsWith("Tiene cosas que no son de Roost", C("/srv/roost", move: false, exists: true).Problem);
        Assert.Equal((null, "Ahí ya hay una Roost: se usará la que hay (sus PHP, sus datos y su CA)."), C("/srv/roost", move: false, exists: true, roost: true));
        Assert.StartsWith("Roost empezará de cero ahí", C("/srv/roost", move: false).Info);

        Assert.Equal("/srv/roost", ChangeRootDialogViewModel.NormalizePosix("/srv//roost/./"));
        Assert.Equal("/roost", ChangeRootDialogViewModel.NormalizePosix("/a/../../roost"));
    }

    [Fact]
    public void LoPuroDeLinux_TextosBandejaAvisosSshAgentYCultura()
    {
        // Por qué no hay bandeja, según el escritorio.
        Assert.StartsWith("GNOME no tiene bandeja de serie: instala la extensión AppIndicator", TrayHost.Explain("ubuntu:GNOME"));
        Assert.StartsWith("No hay ningún panel con bandeja", TrayHost.Explain(null));
        Assert.StartsWith("El escritorio (XFCE) no tiene", TrayHost.Explain("XFCE"));

        // El notify-send de respaldo.
        var send = FreedesktopNotifier.NotifySend(new TrayAlert(TrayAlertKind.ServiceDown, "nginx se ha caído", "Mira Registros"));
        Assert.Equal("notify-send", send.FileName);
        Assert.Equal(["--app-name=Roost", "--icon=roost", "--urgency=critical", "nginx se ha caído", "Mira Registros"], send.ArgumentList);
        Assert.Contains("--urgency=normal", FreedesktopNotifier.NotifySend(new TrayAlert(TrayAlertKind.AuditRed, "a", "b")).ArgumentList);

        // El ssh-agent por SSH_AUTH_SOCK.
        Assert.Equal("no está", GitContext.AgentStateFromSocket(null, _ => true));
        Assert.Equal("en marcha", GitContext.AgentStateFromSocket("/run/user/1000/keyring/ssh", _ => true));
        Assert.Equal("parado", GitContext.AgentStateFromSocket("/tmp/ssh-x/agent.1", _ => false));

        // La cultura sin ICU: números y fechas en castellano, iguales en los dos sistemas.
        Assert.Equal("1.234,5 MB", string.Format(Es.Culture, "{0:#,0.0} MB", 1234.5));
        Assert.Equal("12.345", 12345.ToString("N0", Es.Culture));
        Assert.Equal("22-oct, 18:05", new DateTime(2026, 10, 22, 18, 5, 0).ToString("d-MMM, HH:mm", Es.Culture));
        Assert.Equal("1,5 s", string.Format(Es.Culture, "{0:0.0} s", 1.5));

        // Los textos por sistema: los de Windows siguen diciendo Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Windows pedirá permiso de administrador", OsTexts.WillAskAdmin);
            Assert.Equal("El predeterminado de Windows", OsTexts.DefaultBrowser);
        }
        else
        {
            Assert.Equal("Se pedirá permiso de administrador (polkit, o sudo)", OsTexts.WillAskAdmin);
            Assert.Equal("El predeterminado del sistema (xdg-open)", OsTexts.DefaultBrowser);
            Assert.Equal("Desactívalo (sudo systemctl disable --now mariadb.service) para que no se lo quede.", OsTexts.LeaveServiceManual("mariadb.service"));
        }
    }

    [AvaloniaFact]
    public async Task SinFormaDePedirPermiso_ElDialogoOfreceCopiarLaOrden()
    {
        var (main, window, fake) = await AjustesSistemaTests.OpenAsync();
        using var _ = fake;
        var command = "sudo /usr/libexec/roost/roost-helper hosts-sync < '/home/ana/.local/share/roost/run/hosts-request.txt'";
        var error = new ElevationNeededException(
            $"No se ha podido pedir permiso de administrador (pkexec no ha podido autorizar (no hay agente de polkit); sudo pide la contrasena y no hay terminal). Ejecuta esto en una terminal y repite la orden:\n  {command}",
            command);

        var showing = main.ShowElevationErrorAsync("No se pudo actualizar el hosts", error);
        var dialog = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("No se pudo actualizar el hosts", dialog.Title);
        Assert.Equal("Roost no ha podido pedir permiso de administrador desde aquí.", dialog.Subtitle);
        Assert.Contains("pkexec no ha podido autorizar", dialog.Text);
        Assert.EndsWith("Ejecuta esto en una terminal y repite la operación:", dialog.Text);
        Assert.Equal(command, dialog.Detail);
        Assert.Equal(["Cerrar", "Copiar la orden"], dialog.Buttons.Select(b => b.Label));
        AjustesSistemaTests.Snapshot(window, "linux-permiso-copiar");

        dialog.ChooseCommand.Execute(dialog.Buttons.Single(b => b.Label == "Copiar la orden"));
        await showing;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Copiada: pégala en una terminal", main.FlashText);
        Assert.Null(main.Dialog);

        // Un error normal va al diálogo de siempre, sin «Copiar».
        var plain = main.ShowElevationErrorAsync("No se pudo", new Roost.Core.RoostException("algo"));
        var info = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal(["Cerrar"], info.Buttons.Select(b => b.Label));
        info.Close(null);
        await plain;
    }

    [AvaloniaFact]
    public async Task Ajustes_SinBandeja_LoDiceYLaCasillaNoSePuedeTocar()
    {
        var (main, window, fake) = await AjustesSistemaTests.OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Settings);
        // Con bandeja (lo que se da por hecho hasta que TrayHost.DetectAsync diga otra cosa): nada que avisar.
        Assert.False(main.Settings.TrayUnavailable);
        Assert.Equal(TrayHost.Status.Available, TrayHost.CanCloseToTray(new DesktopPrefs()));
        Assert.False(TrayHost.CanCloseToTray(new DesktopPrefs { CloseToTray = false }));
        Assert.Equal(Autostart.Supported, main.Settings.CanAutostart);
    }
}
