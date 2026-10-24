using Microsoft.Win32;

namespace Roost.Desktop.Services;

/// <summary>La parte del registro donde se mira (sin depender del enum de Windows, que solo existe allí).</summary>
public enum RegHive
{
    LocalMachine,
    CurrentUser,
}

/// <summary>Un programa que Roost sabe abrir: un editor, una terminal o un navegador.</summary>
/// <param name="Id">Lo que se guarda en las preferencias (<c>vscode</c>, <c>wt</c>, <c>chrome.exe</c>).</param>
public sealed record ShellApp(string Id, string Name, string Exe);

/// <summary>
/// Lo que se mira para encontrar los programas: ficheros, variables de entorno, el registro (solo
/// lectura) y las subcarpetas de una carpeta. Los tests ponen uno de pega.
/// </summary>
public sealed record AppProbe(
    Func<string, bool> FileExists,
    Func<string, string?> Env,
    Func<RegHive, string, string?, string?> ReadRegistry,
    Func<RegHive, string, IReadOnlyList<string>> RegistrySubKeys,
    Func<string, IReadOnlyList<string>> Directories)
{
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
/// J3 + C4: los editores (VS Code, Cursor, PhpStorm, Sublime Text), las terminales (Windows
/// Terminal, PowerShell 7, Windows PowerShell, Git Bash, cmd) y los navegadores que hay en el
/// equipo, por sus rutas típicas y el registro de Windows (solo lectura, como
/// <see cref="DbClients"/>). Las preferencias guardan el <see cref="ShellApp.Id"/>; vacío es
/// «automático» (el primer editor que haya, o el programa asociado; PowerShell; el navegador
/// predeterminado).
/// </summary>
public static class ShellApps
{
    /// <summary>La preferencia «automático».</summary>
    public const string Auto = "";

    /// <summary>Editor: el programa asociado de Windows siempre, aunque haya un editor.</summary>
    public const string AssociatedEditor = "asociado";

    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // Editores

    /// <summary>Los editores que hay, en orden de preferencia para «automático».</summary>
    public static IReadOnlyList<ShellApp> Editors(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
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
                .Where(d => Path.GetFileName(d).StartsWith("PhpStorm", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin", exe)));
        candidates.Add(Path.Combine(Expand(p, @"%LocalAppData%\Programs\PhpStorm"), "bin", exe));
        foreach (var channel in p.Directories(Expand(p, @"%LocalAppData%\JetBrains\Toolbox\apps\PhpStorm")))
            candidates.AddRange(p.Directories(channel).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "bin", exe)));
        foreach (var key in new[] { @"SOFTWARE\JetBrains\PhpStorm", @"SOFTWARE\WOW6432Node\JetBrains\PhpStorm" })
            foreach (var build in p.RegistrySubKeys(RegHive.LocalMachine, key).OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase))
                if (p.ReadRegistry(RegHive.LocalMachine, $@"{key}\{build}", null) is { Length: > 0 } dir)
                    candidates.Add(Path.Combine(dir.Trim('"'), "bin", exe));
        return candidates.FirstOrDefault(p.FileExists) ?? FromUninstall(p, "PhpStorm", exe, "bin");
    }

    // Terminales

    /// <summary>Las terminales que hay. Windows Terminal abre PowerShell (7 si está) dentro.</summary>
    public static IReadOnlyList<ShellApp> Terminals(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
        var found = new List<ShellApp>();
        Add(found, "wt", "Windows Terminal", First(p, [@"%LocalAppData%\Microsoft\WindowsApps\wt.exe"]));
        Add(found, "pwsh", "PowerShell 7", OnPath(p, "pwsh.exe") ?? First(p, [@"%ProgramFiles%\PowerShell\7\pwsh.exe"]));
        Add(found, "powershell", "Windows PowerShell", First(p, [@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"]) ?? OnPath(p, "powershell.exe"));
        var gitRoot = p.ReadRegistry(RegHive.LocalMachine, @"SOFTWARE\GitForWindows", "InstallPath")
                      ?? p.ReadRegistry(RegHive.CurrentUser, @"SOFTWARE\GitForWindows", "InstallPath");
        Add(found, "gitbash", "Git Bash", First(p,
            [.. gitRoot is null ? Array.Empty<string>() : [Path.Combine(gitRoot, "git-bash.exe")], @"%ProgramFiles%\Git\git-bash.exe", @"%LocalAppData%\Programs\Git\git-bash.exe"]));
        Add(found, "cmd", "Símbolo del sistema (cmd)", p.Env("ComSpec") is { Length: > 0 } comspec && p.FileExists(comspec) ? comspec : First(p, [@"%SystemRoot%\System32\cmd.exe"]));
        return found;
    }

    /// <summary>La de «automático»: PowerShell 7 si está, y si no Windows PowerShell (lo de siempre).</summary>
    public static ShellApp? AutoTerminal(IReadOnlyList<ShellApp> terminals) =>
        terminals.FirstOrDefault(t => t.Id == "pwsh") ?? terminals.FirstOrDefault(t => t.Id == "powershell");

    // Navegadores

    /// <summary>
    /// Los navegadores registrados (<c>Clients\StartMenuInternet</c>, de la máquina y del usuario),
    /// por el nombre de su ejecutable: <c>chrome.exe</c>, <c>msedge.exe</c>, <c>firefox.exe</c>.
    /// </summary>
    public static IReadOnlyList<ShellApp> Browsers(AppProbe? probe = null)
    {
        var p = probe ?? AppProbe.Machine;
        var found = new List<ShellApp>();
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            foreach (var root in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                foreach (var name in p.RegistrySubKeys(hive, root))
                {
                    var command = p.ReadRegistry(hive, $@"{root}\{name}\shell\open\command", null);
                    if (ExeOf(command) is not { } exe || !p.FileExists(exe)) continue;
                    var id = Path.GetFileName(exe).ToLowerInvariant();
                    if (found.Any(b => b.Id == id)) continue;
                    var display = p.ReadRegistry(hive, $@"{root}\{name}", null) is { Length: > 0 } d ? d : name;
                    found.Add(new ShellApp(id, display, exe));
                }
            }
        }
        return found.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
        candidates.Select(c => Expand(p, c)).FirstOrDefault(c => Path.IsPathFullyQualified(c) && p.FileExists(c));

    private static string? OnPath(AppProbe p, string exe) =>
        (p.Env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, exe))
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
                        var candidate = subfolder is null ? Path.Combine(dir, exe) : Path.Combine(dir, subfolder, exe);
                        if (p.FileExists(candidate)) return candidate;
                    }
                    if (p.ReadRegistry(hive, $@"{root}\{key}", "DisplayIcon") is { Length: > 0 } icon)
                    {
                        var path = icon.Split(',')[0].Trim('"');
                        if (Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase) && p.FileExists(path)) return path;
                    }
                }
            }
        }
        return null;
    }
}
