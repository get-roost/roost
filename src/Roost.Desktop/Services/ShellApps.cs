using Microsoft.Win32;

namespace Roost.Desktop.Services;

/// <summary>La parte del registro donde se mira (sin depender del enum de Windows, que solo existe allí).</summary>
public enum RegHive
{
    LocalMachine,
    CurrentUser,
}

/// <summary>Un programa que Roost sabe abrir: un editor, una terminal o un navegador.</summary>
/// <param name="Id">Lo que se guarda en las preferencias (<c>vscode</c>, <c>wt</c>, <c>chrome.exe</c>; en Linux, <c>gnome-terminal</c>, <c>firefox</c>).</param>
public sealed record ShellApp(string Id, string Name, string Exe);

/// <summary>
/// Lo que se mira para encontrar los programas: ficheros, variables de entorno, el registro (solo
/// lectura) y las subcarpetas de una carpeta. Los tests ponen uno de pega. <see cref="Posix"/> dice si
/// las rutas son de Linux (<c>/usr/bin</c>, el <c>PATH</c> con <c>:</c>) o de Windows (<c>%ProgramFiles%</c>, el registro):
/// así los tests prueban las dos listas en cualquier sistema.
/// </summary>
public sealed record AppProbe(
    Func<string, bool> FileExists,
    Func<string, string?> Env,
    Func<RegHive, string, string?, string?> ReadRegistry,
    Func<RegHive, string, IReadOnlyList<string>> RegistrySubKeys,
    Func<string, IReadOnlyList<string>> Directories)
{
    /// <summary>true si el equipo es Linux o macOS (rutas con <c>/</c>, sin registro).</summary>
    public bool Posix { get; init; } = !OperatingSystem.IsWindows();

    /// <summary>El equipo de verdad.</summary>
    public static readonly AppProbe Machine = new(
        File.Exists,
        Environment.GetEnvironmentVariable,
        RealRegistry,
        RealSubKeys,
        dir =>
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        });

    private static string? RealRegistry(RegHive hive, string key, string? value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> RealSubKeys(RegHive hive, string key)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
            using var sub = root.OpenSubKey(key);
            return sub?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// J3 + C4: los editores (VS Code, Cursor, PhpStorm, Sublime Text), las terminales y los navegadores
/// que hay en el equipo. En Windows, por sus rutas típicas y el registro (solo lectura, como
/// <see cref="DbClients"/>): Windows Terminal, PowerShell 7, Windows PowerShell, Git Bash, cmd y los
/// navegadores registrados. En Linux, por el <c>PATH</c> y las carpetas de siempre (<c>/opt</c>, snap,
/// Toolbox): VS Code, VSCodium, Cursor, PhpStorm, Sublime Text; gnome-terminal, konsole, xfce4-terminal,
/// kitty, alacritty, wezterm, tilix, mate-terminal, x-terminal-emulator y xterm; Chrome, Chromium,
/// Firefox, Brave, Edge, Vivaldi y Opera. Las preferencias guardan el <see cref="ShellApp.Id"/>; vacío
/// es «automático» (el primer editor que haya, o el programa asociado; PowerShell o la primera terminal;
/// el navegador predeterminado, que en Linux es <c>xdg-open</c>).
/// </summary>
public static class ShellApps
{
    /// <summary>La preferencia «automático».</summary>
    public const string Auto = "";

    /// <summary>Editor: el programa asociado del sistema siempre, aunque haya un editor.</summary>
    public const string AssociatedEditor = "asociado";

    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // Editores

    /// <summary>Los editores que hay, en orden de preferencia para «automático».</summary>
    public static IReadOnlyList<ShellApp> Editors(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
        if (p.Posix) return LinuxEditors(p);
        var found = new List<ShellApp>();
        Add(found, "vscode", "Visual Studio Code", First(p,
            [@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe", @"%ProgramFiles%\Microsoft VS Code\Code.exe", @"%ProgramFiles(x86)%\Microsoft VS Code\Code.exe"])
            ?? FromUninstall(p, "Microsoft Visual Studio Code", "Code.exe"));
        Add(found, "cursor", "Cursor", First(p, [@"%LocalAppData%\Programs\cursor\Cursor.exe", @"%ProgramFiles%\Cursor\Cursor.exe"])
            ?? FromUninstall(p, "Cursor", "Cursor.exe"));
        Add(found, "phpstorm", "PhpStorm", PhpStorm(p));
        Add(found, "sublime", "Sublime Text", First(p,
            [@"%ProgramFiles%\Sublime Text\sublime_text.exe", @"%ProgramFiles%\Sublime Text 4\sublime_text.exe", @"%ProgramFiles%\Sublime Text 3\sublime_text.exe", @"%LocalAppData%\Programs\Sublime Text\sublime_text.exe"])
            ?? FromUninstall(p, "Sublime Text", "sublime_text.exe"));
        return found;
    }

    /// <summary>Linux: por el PATH (el <c>code</c> de un .deb o de snap, <c>phpstorm</c> de Toolbox…) y las carpetas de siempre.</summary>
    private static IReadOnlyList<ShellApp> LinuxEditors(AppProbe p)
    {
        var found = new List<ShellApp>();
        var home = p.Env("HOME") ?? "";
        Add(found, "vscode", "Visual Studio Code", OnPosixPath(p, "code") ?? FirstPosix(p, ["/usr/share/code/bin/code", "/snap/bin/code", "/opt/visual-studio-code/bin/code"]));
        Add(found, "codium", "VSCodium", OnPosixPath(p, "codium") ?? FirstPosix(p, ["/usr/share/codium/bin/codium", "/snap/bin/codium"]));
        Add(found, "cursor", "Cursor", OnPosixPath(p, "cursor") ?? FirstPosix(p, ["/opt/cursor/cursor", $"{home}/.local/bin/cursor"]));
        Add(found, "phpstorm", "PhpStorm", OnPosixPath(p, "phpstorm") ?? OnPosixPath(p, "phpstorm.sh")
            ?? FirstPosix(p, [$"{home}/.local/share/JetBrains/Toolbox/apps/phpstorm/bin/phpstorm.sh", $"{home}/.local/share/JetBrains/Toolbox/apps/phpstorm/bin/phpstorm",
                "/opt/phpstorm/bin/phpstorm.sh", "/opt/PhpStorm/bin/phpstorm.sh", "/snap/bin/phpstorm", "/usr/local/bin/phpstorm"]));
        Add(found, "sublime", "Sublime Text", OnPosixPath(p, "subl") ?? FirstPosix(p, ["/opt/sublime_text/sublime_text", "/snap/bin/subl"]));
        return found;
    }

    /// <summary>
    /// PhpStorm: la instalación normal (<c>JetBrains\PhpStorm 2026.2\bin</c>), la de Toolbox (en
    /// <c>Programs</c> o en <c>Toolbox\apps</c>) y la clave de JetBrains del registro. La más nueva.
    /// </summary>
    private static string? PhpStorm(AppProbe p)
    {
        const string exe = "phpstorm64.exe";
        var candidates = new List<string>();
        foreach (var jetbrains in new[] { @"%ProgramFiles%\JetBrains", @"%ProgramFiles(x86)%\JetBrains" })
            candidates.AddRange(p.Directories(Expand(p, jetbrains))
                .Where(d => WindowsFileName(d).StartsWith("PhpStorm", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Join(d, "bin", exe)));
        candidates.Add(Join(Expand(p, @"%LocalAppData%\Programs\PhpStorm"), "bin", exe));
        foreach (var channel in p.Directories(Expand(p, @"%LocalAppData%\JetBrains\Toolbox\apps\PhpStorm")))
            candidates.AddRange(p.Directories(channel).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).Select(d => Join(d, "bin", exe)));
        foreach (var key in new[] { @"SOFTWARE\JetBrains\PhpStorm", @"SOFTWARE\WOW6432Node\JetBrains\PhpStorm" })
            foreach (var build in p.RegistrySubKeys(RegHive.LocalMachine, key).OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase))
                if (p.ReadRegistry(RegHive.LocalMachine, $@"{key}\{build}", null) is { Length: > 0 } dir)
                    candidates.Add(Join(dir.Trim('"'), "bin", exe));
        return candidates.FirstOrDefault(p.FileExists) ?? FromUninstall(p, "PhpStorm", exe, "bin");
    }

    // Terminales

    /// <summary>Las terminales que hay. Windows Terminal abre PowerShell (7 si está) dentro.</summary>
    public static IReadOnlyList<ShellApp> Terminals(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
        if (p.Posix) return LinuxTerminals(p);
        var found = new List<ShellApp>();
        Add(found, "wt", "Windows Terminal", First(p, [@"%LocalAppData%\Microsoft\WindowsApps\wt.exe"]));
        Add(found, "pwsh", "PowerShell 7", OnPath(p, "pwsh.exe") ?? First(p, [@"%ProgramFiles%\PowerShell\7\pwsh.exe"]));
        Add(found, "powershell", "Windows PowerShell", First(p, [@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"]) ?? OnPath(p, "powershell.exe"));
        var gitRoot = p.ReadRegistry(RegHive.LocalMachine, @"SOFTWARE\GitForWindows", "InstallPath")
                      ?? p.ReadRegistry(RegHive.CurrentUser, @"SOFTWARE\GitForWindows", "InstallPath");
        Add(found, "gitbash", "Git Bash", First(p,
            [.. gitRoot is null ? Array.Empty<string>() : [Join(gitRoot, "git-bash.exe")], @"%ProgramFiles%\Git\git-bash.exe", @"%LocalAppData%\Programs\Git\git-bash.exe"]));
        Add(found, "cmd", "Símbolo del sistema (cmd)", p.Env("ComSpec") is { Length: > 0 } comspec && p.FileExists(comspec) ? comspec : First(p, [@"%SystemRoot%\System32\cmd.exe"]));
        return found;
    }

    /// <summary>
    /// Las terminales de Linux que Roost sabe abrir en una carpeta y con una orden dentro, en el orden de
    /// preferencia para «automática». <c>x-terminal-emulator</c> es la elegida por la distro (Debian y
    /// derivadas), que puede ser cualquiera de las otras.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name)> LinuxTerminalKinds =
    [
        ("gnome-terminal", "Terminal de GNOME"),
        ("konsole", "Konsole"),
        ("xfce4-terminal", "Terminal de XFCE"),
        ("kitty", "kitty"),
        ("alacritty", "Alacritty"),
        ("wezterm", "WezTerm"),
        ("tilix", "Tilix"),
        ("mate-terminal", "Terminal de MATE"),
        ("x-terminal-emulator", "La terminal de la distro (x-terminal-emulator)"),
        ("xterm", "xterm"),
    ];

    private static IReadOnlyList<ShellApp> LinuxTerminals(AppProbe p)
    {
        var found = new List<ShellApp>();
        foreach (var (id, name) in LinuxTerminalKinds) Add(found, id, name, OnPosixPath(p, id));
        return found;
    }

    /// <summary>La de «automático»: en Windows, PowerShell 7 si está, y si no Windows PowerShell; en Linux, la primera de <see cref="LinuxTerminalKinds"/> que haya.</summary>
    public static ShellApp? AutoTerminal(IReadOnlyList<ShellApp> terminals) =>
        terminals.FirstOrDefault(t => t.Id == "pwsh") ?? terminals.FirstOrDefault(t => t.Id == "powershell")
        ?? terminals.FirstOrDefault(t => LinuxTerminalKinds.Any(k => k.Id == t.Id));

    // Navegadores

    /// <summary>
    /// Los navegadores: en Windows, los registrados (<c>Clients\StartMenuInternet</c>, de la máquina y del
    /// usuario), por el nombre de su ejecutable (<c>chrome.exe</c>, <c>msedge.exe</c>, <c>firefox.exe</c>);
    /// en Linux, los del PATH por su orden (<c>google-chrome</c>, <c>chromium</c>, <c>firefox</c>…).
    /// </summary>
    public static IReadOnlyList<ShellApp> Browsers(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
        if (p.Posix) return LinuxBrowsers(p);
        var found = new List<ShellApp>();
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            foreach (var root in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                foreach (var name in p.RegistrySubKeys(hive, root))
                {
                    var command = p.ReadRegistry(hive, $@"{root}\{name}\shell\open\command", null);
                    if (ExeOf(command) is not { } exe || !p.FileExists(exe)) continue;
                    var id = WindowsFileName(exe).ToLowerInvariant();
                    if (found.Any(b => b.Id == id)) continue;
                    var display = p.ReadRegistry(hive, $@"{root}\{name}", null) is { Length: > 0 } d ? d : name;
                    found.Add(new ShellApp(id, display, exe));
                }
            }
        }
        return found.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Los navegadores de Linux que se buscan en el PATH, con su nombre.</summary>
    public static readonly IReadOnlyList<(string Command, string Name)> LinuxBrowserKinds =
    [
        ("google-chrome-stable", "Google Chrome"),
        ("google-chrome", "Google Chrome"),
        ("chromium", "Chromium"),
        ("chromium-browser", "Chromium"),
        ("firefox", "Firefox"),
        ("brave-browser", "Brave"),
        ("microsoft-edge-stable", "Microsoft Edge"),
        ("microsoft-edge", "Microsoft Edge"),
        ("vivaldi", "Vivaldi"),
        ("opera", "Opera"),
    ];

    private static IReadOnlyList<ShellApp> LinuxBrowsers(AppProbe p)
    {
        var found = new List<ShellApp>();
        foreach (var (command, name) in LinuxBrowserKinds)
        {
            if (found.Any(b => b.Name == name)) continue;   // google-chrome-stable y google-chrome son el mismo
            Add(found, command, name, OnPosixPath(p, command));
        }
        return found;
    }

    /// <summary>PURO: el ejecutable de una orden del registro (<c>"C:\…\chrome.exe" --flag</c> o sin comillas).</summary>
    public static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }

    // Lo común

    private static void Add(List<ShellApp> found, string id, string name, string? exe)
    {
        if (exe is not null) found.Add(new ShellApp(id, name, exe));
    }

    private static string Expand(AppProbe p, string path)
    {
        // %Variable% con lo que diga la sonda (los tests no dependen del equipo).
        var result = path;
        foreach (var name in new[] { "LocalAppData", "ProgramFiles", "ProgramFiles(x86)", "SystemRoot", "AppData" })
            if (result.Contains($"%{name}%", StringComparison.OrdinalIgnoreCase))
                result = result.Replace($"%{name}%", p.Env(name) ?? "", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string? First(AppProbe p, IEnumerable<string> candidates) =>
        // Una variable que no está (en los tests, o un equipo raro) deja la ruta a medias: se salta.
        candidates.Select(c => Expand(p, c)).FirstOrDefault(c => IsWindowsFullPath(c) && p.FileExists(c));

    private static string? OnPath(AppProbe p, string exe) =>
        (p.Env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Join(dir, exe))
            .FirstOrDefault(p.FileExists);

    // Las rutas de Windows se montan a mano (no con Path.Combine): así el equipo de pega de Windows de los
    // tests vale también en el runner de Linux, donde Path.Combine junta con / y «C:\» no es una ruta completa.

    private static bool IsWindowsFullPath(string path) =>
        path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static string Join(params string[] parts) => string.Join('\\', parts.Select(p => p.TrimEnd('\\')));

    /// <summary>El nombre de fichero de una ruta de Windows (<c>C:\a\b.exe</c> → <c>b.exe</c>), valga el test donde valga.</summary>
    private static string WindowsFileName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>Linux: el primero de los candidatos que existe (rutas con <c>/</c>, escritas enteras).</summary>
    private static string? FirstPosix(AppProbe p, IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(c => c.StartsWith('/') && p.FileExists(c));

    /// <summary>Linux: <paramref name="command"/> en alguna carpeta del PATH (separado por <c>:</c>; las rutas se juntan con <c>/</c>, valga el test donde valga).</summary>
    private static string? OnPosixPath(AppProbe p, string command) =>
        (p.Env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(dir => dir.StartsWith('/'))
            .Select(dir => dir.TrimEnd('/') + "/" + command)
            .FirstOrDefault(p.FileExists);

    /// <summary>Por el <c>DisplayName</c> de las claves de desinstalación: su carpeta (o la del icono) y el exe.</summary>
    private static string? FromUninstall(AppProbe p, string displayName, string exe, string? subfolder = null)
    {
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            foreach (var root in new[] { Uninstall, Uninstall32 })
            {
                foreach (var key in p.RegistrySubKeys(hive, root))
                {
                    var display = p.ReadRegistry(hive, $@"{root}\{key}", "DisplayName") ?? "";
                    if (!display.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.ReadRegistry(hive, $@"{root}\{key}", "InstallLocation") is { Length: > 0 } location)
                    {
                        var dir = location.Trim('"');
                        var candidate = subfolder is null ? Join(dir, exe) : Join(dir, subfolder, exe);
                        if (p.FileExists(candidate)) return candidate;
                    }
                    if (p.ReadRegistry(hive, $@"{root}\{key}", "DisplayIcon") is { Length: > 0 } icon)
                    {
                        var path = icon.Split(',')[0].Trim('"');
                        if (WindowsFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase) && p.FileExists(path)) return path;
                    }
                }
            }
        }
        return null;
    }
}
