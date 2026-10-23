using System.Runtime.InteropServices;
using Roost.Core.Models;

namespace Roost.Core.Scanning;

/// <summary>
/// Motor de escaneo: ejecuta los scanners registrados, junta sus resultados y
/// elimina duplicados por ruta (dos scanners pueden ver el mismo binario).
/// </summary>
public sealed class EnvironmentScanner
{
    private readonly IReadOnlyList<IComponentScanner> _scanners;

    public EnvironmentScanner(IEnumerable<IComponentScanner> scanners)
        => _scanners = scanners.ToList();

    /// <summary>Crea el motor con los scanners por defecto del SO actual, más los de las extensiones.</summary>
    public static EnvironmentScanner CreateDefault(IEnumerable<IComponentScanner>? extra = null)
        => new(ScannerCatalog.CreateDefault().Concat(extra ?? []));

    public ScanResult ScanAll()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<DetectedComponent>();

        foreach (var scanner in _scanners)
        {
            if (!scanner.SupportsCurrentOs) continue;

            IEnumerable<DetectedComponent> partial;
            try { partial = scanner.Scan(); }
            catch { continue; } // un scanner roto no tumba el escaneo entero

            foreach (var c in partial)
            {
                if (seen.Add(NormalizePath(c.ExecutablePath)))
                    found.Add(c);
            }
        }

        return new ScanResult
        {
            OsDescription = RuntimeInformation.OSDescription,
            RuntimeId = RuntimeInformation.RuntimeIdentifier,
            Components = found,
        };
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
