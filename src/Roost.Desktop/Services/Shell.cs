using System.Diagnostics;
using System.Text;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Stack;

namespace Roost.Desktop.Services;

/// <summary>
/// Abrir cosas fuera de la app: el navegador, el explorador, el editor, una terminal. Todo lo que
/// abre un fichero o una terminal pasa por aquí, así que usa los programas elegidos en Ajustes
/// (<see cref="DesktopPrefs.Editor"/>, <see cref="DesktopPrefs.Terminal"/>,
/// <see cref="DesktopPrefs.Browser"/>; J3 + C4).
/// </summary>
public static class Shell
{
    /// <summary>Las preferencias de la app (las pone <see cref="AppServices"/> al crearse y al guardarlas).</summary>
    public static Func<DesktopPrefs> Preferences { get; set; } = () => new DesktopPrefs();

    /// <summary>Lo que lanza los procesos: los tests lo cambian para ver qué se abriría sin abrir nada.</summary>
    internal static Action<ProcessStartInfo> Launcher { get; set; } = start => Process.Start(start)?.Dispose();

    /// <summary>Dónde se buscan los programas (los tests ponen una sonda de pega).</summary>
    internal static AppProbe Probe { get; set; } = AppProbe.Machine;

    /// <summary>
    /// Abre la URL en el navegador elegido o, si no hay ninguno, en el predeterminado. Lo que no es
    /// una URL web (<c>services.msc</c>) va siempre por Windows.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (IsWeb(url) && BrowserFor(Preferences()) is { } browser)
            Start(BrowserStart(browser, url));
        else
            Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Abre la carpeta en el explorador (creándola si aún no existe, como C:\www el primer día).</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// Abre el fichero (un log, roost.toml, un php.ini, un vhost, el usuario.cnf) en el editor elegido;
    /// sin editor, con su programa asociado.
    /// </summary>
    public static void OpenFile(string path)
    {
        if (EditorFor(Preferences()) is { } editor)
            Start(EditorStart(editor, path));
        else
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>La carpeta de un proyecto en el editor; sin editor, en el explorador.</summary>
    public static void OpenInEditor(string folder)
    {
        if (EditorFor(Preferences()) is { } editor)
        {
            Directory.CreateDirectory(folder);
            Start(EditorStart(editor, folder));
        }
        else
        {
            OpenFolder(folder);
        }
    }

    /// <summary>Selecciona el fichero en el explorador.</summary>
    public static void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        else
            OpenFolder(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Una terminal (la elegida en Ajustes) en la carpeta www con el PHP global (y el cliente de
    /// la base de datos global, si la hay) delante en el PATH: lo que en Laragon es «Terminal».
    /// </summary>
    /// <param name="phpDirectory">Otro PHP en vez del global (el botón Terminal de la pantalla PHP).</param>
    /// <param name="workingDirectory">Otra carpeta en vez de www (la de un sitio).</param>
    public static void OpenTerminal(RoostPaths paths, string? phpDirectory = null, string? workingDirectory = null)
    {
        var extra = new List<string>();
        var environment = new Dictionary<string, string>();
        var shims = new ShimInstaller(paths);
        if (shims.IsInstalled)
        {
            // Con los shims, php sigue la carpeta; un PHP concreto se pide por la variable.
            extra.Add(shims.Directory);
            if (phpDirectory is not null) environment[TerminalPhp.VersionVariable] = Path.GetFileName(phpDirectory);
        }
        else if (phpDirectory is not null)
        {
            extra.Add(phpDirectory);
        }
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            if (extra.Count == 0) extra.Add(planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled()).Directory);
            if (planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()) is { } db)
                extra.Add(Path.Combine(db.Directory, "bin"));
        }
        catch (RoostException)
        {
            // Sin PHP instalado (o con roost.toml roto) la terminal se abre igual, sin nada extra.
        }

        var folder = workingDirectory ?? paths.Www;
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo("x-terminal-emulator") { UseShellExecute = false, WorkingDirectory = folder });
            return;
        }
        var terminals = ShellApps.Terminals(Probe);
        Start(TerminalStart(TerminalFor(Preferences(), terminals), terminals, folder, extra, environment, Environment.GetEnvironmentVariable("PATH") ?? ""));
    }

    /// <summary>Un programa de consola en su propia ventana (la consola SQL de la base de datos).</summary>
    public static void OpenConsole(string exe, string arguments, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        // Desde una app sin consola, Windows le da al hijo una ventana de consola propia.
        Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = workingDirectory });
    }

    // Qué programa toca

    /// <summary>El editor de las preferencias: el elegido si sigue instalado; con «automático», el primero que haya; null = el programa asociado.</summary>
    public static ShellApp? EditorFor(DesktopPrefs prefs)
    {
        if (prefs.Editor == ShellApps.AssociatedEditor) return null;
        var editors = ShellApps.Editors(Probe);
        return editors.FirstOrDefault(e => e.Id == prefs.Editor) ?? editors.FirstOrDefault();
    }

    /// <summary>La terminal de las preferencias, o la automática (PowerShell 7 o Windows PowerShell) si no está la elegida.</summary>
    public static ShellApp? TerminalFor(DesktopPrefs prefs, IReadOnlyList<ShellApp>? terminals = null)
    {
        terminals ??= ShellApps.Terminals(Probe);
        return terminals.FirstOrDefault(t => t.Id == prefs.Terminal) ?? ShellApps.AutoTerminal(terminals);
    }

    /// <summary>El navegador elegido si sigue instalado; null = el predeterminado de Windows.</summary>
    public static ShellApp? BrowserFor(DesktopPrefs prefs) =>
        prefs.Browser == ShellApps.Auto ? null : ShellApps.Browsers(Probe).FirstOrDefault(b => b.Id == prefs.Browser);

    private static bool IsWeb(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Qué se lanza (PURO: los tests lo comprueban sin lanzar nada)

    /// <summary>El editor con la carpeta o el fichero (VS Code, Cursor, PhpStorm y Sublime aceptan la ruta tal cual).</summary>
    internal static ProcessStartInfo EditorStart(ShellApp editor, string path)
    {
        var start = new ProcessStartInfo(editor.Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(editor.Exe) ?? "" };
        start.ArgumentList.Add(path);
        return start;
    }

    internal static ProcessStartInfo BrowserStart(ShellApp browser, string url)
    {
        var start = new ProcessStartInfo(browser.Exe) { UseShellExecute = false };
        start.ArgumentList.Add(url);
        return start;
    }

    /// <summary>
    /// La terminal en <paramref name="folder"/> con <paramref name="extra"/> delante del PATH y las
    /// variables de <paramref name="environment"/>:
    /// <list type="bullet">
    /// <item>PowerShell (7 o el de Windows): <c>-NoExit</c>, con el entorno del proceso.</item>
    /// <item>Windows Terminal: <c>wt -d &lt;carpeta&gt;</c> con PowerShell dentro, que pone el PATH con
    /// <c>-EncodedCommand</c> (si Windows Terminal ya estaba abierto, la pestaña nueva nace con el
    /// entorno de su proceso, no con el nuestro; y en Base64 no hay <c>;</c> que wt tome por suyo).</item>
    /// <item>Git Bash: <c>git-bash.exe --cd=&lt;carpeta&gt;</c>, con el entorno (bash convierte el PATH).</item>
    /// <item>cmd: en su propia consola, con el entorno.</item>
    /// </list>
    /// </summary>
    internal static ProcessStartInfo TerminalStart(ShellApp? terminal, IReadOnlyList<ShellApp> terminals, string folder,
        IReadOnlyList<string> extra, IReadOnlyDictionary<string, string> environment, string inheritedPath)
    {
        var path = string.Join(Path.PathSeparator, extra.Append(inheritedPath));
        ProcessStartInfo start;
        switch (terminal?.Id)
        {
            case "wt":
                var inner = ShellApps.AutoTerminal(terminals)?.Exe ?? "powershell.exe";
                start = new ProcessStartInfo(terminal.Exe) { UseShellExecute = false, WorkingDirectory = folder };
                foreach (var arg in new[] { "-d", folder, inner, "-NoExit", "-EncodedCommand", PowerShellPrelude(extra, environment) })
                    start.ArgumentList.Add(arg);
                break;
            case "gitbash":
                start = new ProcessStartInfo(terminal.Exe) { UseShellExecute = false, WorkingDirectory = folder };
                start.ArgumentList.Add($"--cd={folder}");
                break;
            case "cmd":
                start = new ProcessStartInfo(terminal.Exe) { UseShellExecute = false, WorkingDirectory = folder };
                break;
            default:
                start = new ProcessStartInfo(terminal?.Exe ?? "powershell.exe") { UseShellExecute = false, WorkingDirectory = folder };
                start.ArgumentList.Add("-NoExit");
                break;
        }
        start.Environment["PATH"] = path;
        foreach (var (name, value) in environment) start.Environment[name] = value;
        return start;
    }

    /// <summary>
    /// PURO: lo que ejecuta PowerShell dentro de Windows Terminal, en Base64 (UTF-16LE, lo que pide
    /// <c>-EncodedCommand</c>): <paramref name="extra"/> delante del PATH (tras el perfil del usuario,
    /// que puede tocarlo) y las variables, entre comillas simples.
    /// </summary>
    internal static string PowerShellPrelude(IReadOnlyList<string> extra, IReadOnlyDictionary<string, string> environment)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = new StringBuilder();
        if (extra.Count > 0) script.Append("$env:PATH = ").Append(Quote(string.Join(Path.PathSeparator, extra) + Path.PathSeparator)).Append(" + $env:PATH").Append('\n');
        foreach (var (name, value) in environment) script.Append("$env:").Append(name).Append(" = ").Append(Quote(value)).Append('\n');
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
    }

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            Launcher(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo abrir {start.FileName}: {ex.Message}");
        }
    }
}
