using Roost.Core.Models;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Base compartida de MySQL y MariaDB: los dos se leen con <c>--version</c> y se
/// parsean igual (<see cref="ComponentParser.ParseDatabase"/>), que además hace el
/// cruce de coherencia MySQL/MariaDB. Lo único que cambia entre ambos son las rutas
/// candidatas y los nombres de ejecutable, que quedan abstractos.
/// </summary>
public abstract class DatabaseScannerBase : ComponentScannerBase
{
    protected DatabaseScannerBase(IProcessRunner runner) : base(runner) { }

    protected override string VersionArguments => "--version";

    protected override DetectedComponent? Build(Candidate candidate, string? versionOutput, bool isOnPath) =>
        ComponentParser.ParseDatabase(
            versionOutput,
            Kind,
            candidate.Path,
            candidate.Source,
            isOnPath,
            FolderName(candidate.Path));

    /// <summary>Nombre de la carpeta contenedora (reserva para la arquitectura).</summary>
    protected static string? FolderName(string path)
    {
        try { return Path.GetFileName(Path.GetDirectoryName(path)); }
        catch { return null; }
    }
}
