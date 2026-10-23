using Microsoft.Win32;

namespace Roost.Desktop.Services;

/// <summary>
/// «Abrir Roost al iniciar sesión»: una entrada en HKCU\...\Run (solo el usuario, sin
/// administrador) que abre la app en la bandeja con <c>--minimized</c>.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Roost";

    public static bool Supported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
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
    }
}
