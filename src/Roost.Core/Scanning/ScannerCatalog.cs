using System.Runtime.InteropServices;
using Roost.Core.Scanning.Scanners;

namespace Roost.Core.Scanning;

/// <summary>
/// Catálogo de scanners por defecto para el SO actual.
///
/// Los cinco scanners (PHP, MySQL, MariaDB, nginx y Apache) son multiplataforma: cada uno elige sus
/// rutas candidatas según el SO (Windows vs POSIX) y se desactiva solo si el SO no está soportado.
/// Por eso se registran los mismos cinco en Windows, Linux y macOS.
/// </summary>
public static class ScannerCatalog
{
    public static IReadOnlyList<IComponentScanner> CreateDefault()
    {
        var runner = ProcessRunner.Default;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return
            [
                new PhpScanner(runner),
                new MySqlScanner(runner),
                new MariaDbScanner(runner),
                new NginxScanner(runner),
                new ApacheScanner(runner),
            ];
        }

        // SO desconocido: nada que escanear con seguridad.
        return Array.Empty<IComponentScanner>();
    }
}
