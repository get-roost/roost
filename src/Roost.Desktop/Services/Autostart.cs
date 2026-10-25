using System.Text;
using Microsoft.Win32;

namespace Roost.Desktop.Services;

/// <summary>
/// «Abrir Roost al iniciar sesión»: abre la app en la bandeja con <c>--minimized</c>, solo para este
/// usuario y sin administrador.
/// <list type="bullet">
/// <item>Windows: una entrada en <c>HKCU\…\Run</c>.</item>
/// <item>Linux: un <c>.desktop</c> en <c>~/.config/autostart</c> (<c>$XDG_CONFIG_HOME</c>), que es lo que
/// leen GNOME, KDE, XFCE y los demás al entrar en la sesión (Desktop Application Autostart). El del
/// stack sin la app (<c>roost autostart</c>, una unidad de <c>systemd --user</c>) es otra cosa.</item>
/// </list>
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Roost";

    /// <summary>El fichero de autostart de Linux, en la carpeta <c>autostart</c> de la configuración del usuario.</summary>
    public const string DesktopFileName = "roost-desktop.desktop";

    /// <summary>Para los tests de Linux: otra carpeta de configuración en vez de <c>$XDG_CONFIG_HOME</c>.</summary>
    internal static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Linux: <c>$XDG_CONFIG_HOME/autostart/roost-desktop.desktop</c> (por defecto, <c>~/.config</c>).</summary>
    public static string LinuxFile()
    {
        var config = Env("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "autostart", DesktopFileName);
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var file = LinuxFile();
            return File.Exists(file) && IsEntryEnabled(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppServices.AppDirectory, "roost-desktop.exe");
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return;
        }
        if (!OperatingSystem.IsLinux()) return;

        var file = LinuxFile();
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, DesktopEntry(Environment.ProcessPath ?? Path.Combine(AppServices.AppDirectory, "roost-desktop")));
        }
        else if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <summary>PURO: el <c>.desktop</c> de autostart que abre <paramref name="exe"/> en la bandeja.</summary>
    public static string DesktopEntry(string exe) =>
        "[Desktop Entry]\n"
        + "Type=Application\n"
        + "Name=Roost\n"
        + "Comment=Abre Roost en la bandeja al iniciar la sesión\n"
        + $"Exec={QuoteExec(exe)} --minimized\n"
        + "Icon=roost\n"
        + "Terminal=false\n"
        + "StartupNotify=false\n"
        + "X-GNOME-Autostart-enabled=true\n";

    /// <summary>PURO: true salvo que el fichero diga <c>Hidden=true</c> o <c>X-GNOME-Autostart-enabled=false</c> (el escritorio lo apagó).</summary>
    public static bool IsEntryEnabled(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>PURO: la ruta como la quiere la clave <c>Exec</c> (entre comillas si hace falta; dentro, <c>\</c>, <c>"</c>, <c>$</c> y <c>`</c> escapados).</summary>
    public static string QuoteExec(string exe)
    {
        if (exe.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ':' or '@')) return exe;
        var sb = new StringBuilder("\"");
        foreach (var c in exe)
        {
            if (c is '\\' or '"' or '$' or '`') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
