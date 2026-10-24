using Microsoft.Win32;

namespace Roost.Core.Platform;

/// <summary>
/// Si está el Visual C++ Redistributable x64 (2015-2022, el runtime 14.x que comparten todas sus
/// versiones) y cuál. <see cref="Checked"/> es false fuera de Windows o si no se pudo leer el
/// registro: entonces no se sabe, y no se avisa.
/// </summary>
public sealed record VcRedistStatus(bool Installed, Version? Version, bool Checked = true)
{
    /// <summary>Apache de Apache Lounge (VS18) necesita el 14.50 o posterior; PHP y las bases de datos valen con cualquier 14.x.</summary>
    public static readonly Version ApacheMinimum = new(14, 50);

    /// <summary>Microsoft lo publica siempre en la misma URL (el «latest supported»).</summary>
    public const string DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public bool SatisfiesApache => Installed && Version is not null && Version >= ApacheMinimum;

    /// <summary>«14.44.35211», o «no instalado».</summary>
    public string Describe() => !Installed ? "no instalado" : Version?.ToString() ?? "instalado";
}

/// <summary>
/// Detecta el VC++ Redistributable leyendo <b>solo</b>
/// <c>HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64</c> (vista de 64 bits), que es
/// donde lo apunta el propio redistribuible: <c>Installed</c>, <c>Major</c>, <c>Minor</c> y
/// <c>Bld</c>. <see cref="Evaluate"/> es la parte pura, sobre los valores leídos.
/// </summary>
public static class VcRedist
{
    public const string RegistryPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";

    public static VcRedistStatus Detect()
    {
        if (!OperatingSystem.IsWindows()) return new VcRedistStatus(false, null, Checked: false);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(RegistryPath);
            if (key is null) return new VcRedistStatus(false, null);
            return Evaluate(key.GetValue("Installed"), key.GetValue("Major"), key.GetValue("Minor"), key.GetValue("Bld"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new VcRedistStatus(false, null, Checked: false);
        }
    }

    /// <summary>PURO: los cuatro valores del registro (DWORD, o null si faltan) a un estado.</summary>
    public static VcRedistStatus Evaluate(object? installed, object? major, object? minor, object? build)
    {
        if (installed is not int flag || flag == 0) return new VcRedistStatus(false, null);
        if (major is not int ma || minor is not int mi) return new VcRedistStatus(true, null);
        return new VcRedistStatus(true, build is int b and >= 0 ? new Version(ma, mi, b) : new Version(ma, mi));
    }

    /// <summary>
    /// PURO: el aviso para el usuario (sin tildes: lo enseña la CLI), o null si no falta nada.
    /// Con <paramref name="forApache"/>, también si el instalado es anterior al 14.50.
    /// </summary>
    public static string? Warning(VcRedistStatus status, bool forApache)
    {
        if (!status.Checked) return null;
        if (!status.Installed)
            return "Falta el Visual C++ Redistributable (x64): PHP, MariaDB, MySQL y Apache lo necesitan para arrancar. "
                + $"Descargalo e instalalo: {VcRedistStatus.DownloadUrl}";
        if (forApache && !status.SatisfiesApache)
            return $"El Visual C++ Redistributable instalado es el {status.Describe()} y Apache necesita el {VcRedistStatus.ApacheMinimum} o posterior. "
                + $"Actualizalo: {VcRedistStatus.DownloadUrl}";
        return null;
    }
}
