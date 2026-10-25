using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Roost.Core.Platform;

/// <summary>
/// El PATH del usuario (no el del sistema: no hace falta ser administrador), para poner delante la
/// carpeta de los shims. En Windows vive en <c>HKCU\Environment\Path</c>, a menudo como
/// <c>REG_EXPAND_SZ</c> con entradas tipo <c>%USERPROFILE%\…</c>: se lee y se escribe sin expandir y
/// con el mismo tipo, para no estropearlas (<see cref="Environment.SetEnvironmentVariable(string, string?, EnvironmentVariableTarget)"/>
/// las expandiría y las guardaría como texto plano).
/// </summary>
public static class UserPath
{
    /// <summary>PURO: <paramref name="path"/> con <paramref name="entry"/> al principio (y sin repetirla).</summary>
    public static string WithEntryFirst(string path, string entry) =>
        string.Join(';', new[] { entry }.Concat(Entries(path).Where(e => !Same(e, entry))));

    /// <summary>PURO: <paramref name="path"/> sin <paramref name="entry"/>.</summary>
    public static string WithoutEntry(string path, string entry) =>
        string.Join(';', Entries(path).Where(e => !Same(e, entry)));

    public static bool Contains(string path, string entry) => Entries(path).Any(e => Same(e, entry));

    /// <summary>El PATH del usuario tal cual está guardado (sin expandir).</summary>
    public static string Read()
    {
        if (!OperatingSystem.IsWindows()) return "";
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    /// <summary>Pone <paramref name="entry"/> al principio del PATH del usuario. true si ha cambiado algo.</summary>
    public static bool AddFirst(string entry) => Update(path => WithEntryFirst(path, entry));

    /// <summary>Quita <paramref name="entry"/> del PATH del usuario. true si ha cambiado algo.</summary>
    public static bool Remove(string entry) => Update(path => WithoutEntry(path, entry));

    private static bool Update(Func<string, string> change)
    {
        if (!OperatingSystem.IsWindows())
            throw new RoostException("El PATH del usuario del registro es de Windows. En Linux los shims llegan a la terminal por los enlaces de ~/.local/bin (roost shims install; ShimInstaller.Enable).");

        using var key = Registry.CurrentUser.CreateSubKey("Environment");
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
        var updated = change(current);
        if (updated == current) return false;

        key.SetValue("Path", updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        Broadcast();
        return true;
    }

    private static IEnumerable<string> Entries(string path) =>
        path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Avisa a Explorer y a las terminales nuevas de que el entorno ha cambiado (las abiertas no se enteran).
    private static void Broadcast()
    {
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x001A;
        const int SMTO_ABORTIFHUNG = 0x0002;
        _ = SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
