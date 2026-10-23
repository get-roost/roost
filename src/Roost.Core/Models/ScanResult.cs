namespace Roost.Core.Models;

/// <summary>Resultado de un escaneo completo del entorno.</summary>
public sealed record ScanResult
{
    /// <summary>Descripción del SO, p.ej. "Microsoft Windows 10.0.26200".</summary>
    public required string OsDescription { get; init; }

    /// <summary>RID de .NET, p.ej. "win-x64", "linux-x64".</summary>
    public required string RuntimeId { get; init; }

    public required IReadOnlyList<DetectedComponent> Components { get; init; }

    public IEnumerable<DetectedComponent> OfKind(ComponentKind kind) =>
        Components.Where(c => c.Kind == kind);
}
