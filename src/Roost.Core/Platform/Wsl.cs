namespace Roost.Core.Platform;

/// <summary>
/// Si Roost corre en un Linux de WSL (dentro de Windows). Ahí el navegador está en Windows: el
/// <c>hosts</c> y la CA que importan son los de Windows (WSL copia el hosts de Windows a
/// <c>/etc/hosts</c> en cada arranque), y puede no haber systemd ni polkit.
/// </summary>
public static class Wsl
{
    /// <summary>
    /// true en un Linux de WSL 1 o 2. <c>ROOST_WSL=0</c> hace que Roost se porte como en un Linux
    /// cualquiera (el hosts y la CA de esta distro, no los de Windows) y <c>ROOST_WSL=1</c> fuerza lo
    /// de WSL: así se prueba cada camino en una distro de WSL sin tocar el Windows de verdad.
    /// </summary>
    public static bool IsWsl { get; } = Detect();

    /// <summary>El hosts de Windows visto desde WSL (con la unidad C: montada donde la monta WSL por defecto); null fuera de WSL.</summary>
    public static string? WindowsHostsFile => IsWsl ? "/mnt/c/Windows/System32/drivers/etc/hosts" : null;

    private static bool Detect()
    {
        if (!OperatingSystem.IsLinux()) return false;
        switch (Environment.GetEnvironmentVariable("ROOST_WSL"))
        {
            case "0": return false;
            case "1": return true;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))) return true;
        try
        {
            return File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop")
                   || File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
