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
/// <see cref="DesktopPrefs.Browser"/>; J3 + C4). En Linux, «el programa asociado» y «el navegador
/// predeterminado» son <c>xdg-open</c> (lo que hace .NET con <c>UseShellExecute</c> fuera de Windows).
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
    /// una URL web (<c>services.msc</c>) va siempre por el sistema.
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

    /// <summary>Selecciona el fichero en el explorador (en Linux, abre su carpeta).</summary>
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
            extra.Add(BinOf(phpDirectory));
        }
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            if (extra.Count == 0) extra.Add(BinOf(planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled()).Directory));
            if (planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()) is { } db)
                extra.Add(Path.Combine(db.Directory, "bin"));
        }
        catch (RoostException)
        {
            // Sin PHP instalado (o con roost.toml roto) la terminal se abre igual, sin nada extra.
        }

        var folder = workingDirectory ?? paths.Www;
        Directory.CreateDirectory(folder);
        var terminals = ShellApps.Terminals(Probe);
        var terminal = TerminalFor(Preferences(), terminals);
        if (!OperatingSystem.IsWindows())
        {
            if (terminal is null) throw new RoostException("No se ha encontrado ninguna terminal (gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, wezterm, tilix, xterm…): instala una o elige otra en Ajustes › Programas.");
            Start(PosixTerminalStart(terminal, folder, extra, environment, Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetEnvironmentVariable("SHELL")));
            return;
        }
        Start(TerminalStart(terminal, terminals, folder, extra, environment, Environment.GetEnvironmentVariable("PATH") ?? ""));
    }

    /// <summary>La carpeta con el ejecutable de un PHP: la propia en Windows (<c>php.exe</c> en la raíz) y <c>bin/</c> en los de roost-builds.</summary>
    private static string BinOf(string phpDirectory)
    {
        var bin = Path.Combine(phpDirectory, "bin");
        return Directory.Exists(bin) && File.Exists(Path.Combine(bin, OsNames.Exe("php"))) ? bin : phpDirectory;
    }

    /// <summary>
    /// Un programa de consola en su propia ventana (la consola SQL de la base de datos, redis-cli). En
    /// Windows, desde una app sin consola el hijo recibe una ventana de consola propia; en Linux hay
    /// que abrirle una terminal (la elegida en Ajustes) y lanzarlo dentro.
    /// </summary>
    public static void OpenConsole(string exe, string arguments, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = workingDirectory });
            return;
        }
        var terminals = ShellApps.Terminals(Probe);
        var terminal = TerminalFor(Preferences(), terminals)
            ?? throw new RoostException("No se ha encontrado ninguna terminal en la que abrir la consola: instala una (gnome-terminal, konsole, xterm…) o elige otra en Ajustes › Programas.");
        Start(PosixCommandStart(terminal, workingDirectory, [exe, .. SplitArguments(arguments)]));
    }

    // Qué programa toca

    /// <summary>El editor de las preferencias: el elegido si sigue instalado; con «automático», el primero que haya; null = el programa asociado.</summary>
    public static ShellApp? EditorFor(DesktopPrefs prefs)
    {
        if (prefs.Editor == ShellApps.AssociatedEditor) return null;
        var editors = ShellApps.Editors(Probe);
        return editors.FirstOrDefault(e => e.Id == prefs.Editor) ?? editors.FirstOrDefault();
    }

    /// <summary>La terminal de las preferencias, o la automática (PowerShell 7 o Windows PowerShell; en Linux, la primera que haya) si no está la elegida.</summary>
    public static ShellApp? TerminalFor(DesktopPrefs prefs, IReadOnlyList<ShellApp>? terminals = null)
    {
        terminals ??= ShellApps.Terminals(Probe);
        return terminals.FirstOrDefault(t => t.Id == prefs.Terminal) ?? ShellApps.AutoTerminal(terminals);
    }

    /// <summary>El navegador elegido si sigue instalado; null = el predeterminado del sistema.</summary>
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
    /// PURO: la terminal de Linux en <paramref name="folder"/> con el shell del usuario dentro y
    /// <paramref name="extra"/> delante del PATH. Las variables van por <c>env</c>, no solo por el entorno
    /// del proceso: gnome-terminal (y tilix, mate-terminal) es un cliente de un servidor que ya estaba en
    /// marcha, y la pestaña nueva nace con el entorno de ese servidor, no con el nuestro.
    /// </summary>
    internal static ProcessStartInfo PosixTerminalStart(ShellApp terminal, string folder, IReadOnlyList<string> extra,
        IReadOnlyDictionary<string, string> environment, string inheritedPath, string? shell)
    {
        var path = string.Join(':', extra.Append(inheritedPath));
        var command = new List<string> { "env", $"PATH={path}" };
        foreach (var (name, value) in environment) command.Add($"{name}={value}");
        command.Add(string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell);
        var start = PosixCommandStart(terminal, folder, command);
        start.Environment["PATH"] = path;
        foreach (var (name, value) in environment) start.Environment[name] = value;
        return start;
    }

    /// <summary>
    /// PURO: la terminal de Linux abierta en <paramref name="folder"/> ejecutando <paramref name="command"/>,
    /// con los argumentos de cada una: gnome-terminal <c>--working-directory= -- cmd</c>; konsole
    /// <c>--workdir -e cmd</c>; xfce4-terminal <c>--working-directory= -x cmd</c>; kitty <c>--directory cmd</c>;
    /// alacritty <c>--working-directory -e cmd</c>; wezterm <c>start --cwd -- cmd</c>; tilix y mate-terminal
    /// <c>--working-directory= -e "cmd"</c> (una sola cadena); x-terminal-emulator y xterm <c>-e cmd</c>
    /// desde la carpeta.
    /// </summary>
    internal static ProcessStartInfo PosixCommandStart(ShellApp terminal, string folder, IReadOnlyList<string> command)
    {
        var start = new ProcessStartInfo(terminal.Exe) { UseShellExecute = false, WorkingDirectory = folder };
        IEnumerable<string> args = terminal.Id switch
        {
            "gnome-terminal" => [$"--working-directory={folder}", "--", .. command],
            "konsole" => ["--workdir", folder, "-e", .. command],
            "xfce4-terminal" => [$"--working-directory={folder}", "-x", .. command],
            "kitty" => ["--directory", folder, .. command],
            "alacritty" => ["--working-directory", folder, "-e", .. command],
            "wezterm" => ["start", "--cwd", folder, "--", .. command],
            "tilix" or "mate-terminal" => [$"--working-directory={folder}", "-e", string.Join(' ', command.Select(Elevation.ShellQuote))],
            _ => ["-e", .. command],   // x-terminal-emulator, xterm y cualquier otra: desde la carpeta
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>PURO: una línea de argumentos (<c>--defaults-file="C:\a b\my.ini" tienda</c>) en argumentos sueltos, sin las comillas.</summary>
    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
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
