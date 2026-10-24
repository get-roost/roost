using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Audit;
using Roost.Core.Platform;

namespace Roost.Desktop.Services;

/// <summary>Un hallazgo tal como se guarda para comparar con la auditoría siguiente.</summary>
/// <param name="Key">Qué componente es: el tipo y la ruta del ejecutable (<see cref="AuditHistory.KeyOf"/>).</param>
public sealed record AuditMark(string Key, string Kind, string Version, string Source, string Path, AuditColor Color, string Verdict);

/// <summary>Lo último que se vio: <c>state/audit-last.json</c>.</summary>
public sealed record AuditRecord(DateTime When, IReadOnlyList<AuditMark> Findings);

/// <summary>Un hallazgo ignorado (K1): sale aparte y no cuenta en rojo ni avisa en la bandeja.</summary>
/// <param name="Id">El componente y su versión (<see cref="AuditHistory.IgnoreIdOf"/>): si cambia de versión, deja de estar ignorado.</param>
public sealed record IgnoredFinding(string Id, string Kind, string Version, string Source, string Path, DateTime When);

/// <summary>
/// Qué ha cambiado desde la auditoría anterior (K1): lo que es nuevo o ha empeorado (no estaba,
/// estaba en verde o ha pasado de ámbar a rojo) y lo resuelto (estaba en rojo o ámbar y ya no está
/// o está en verde). Lo ignorado no cuenta.
/// </summary>
public sealed record AuditChanges(DateTime Since, IReadOnlyList<AuditMark> New, IReadOnlyList<AuditMark> Resolved)
{
    /// <summary>Lo que ha pasado a rojo: el aviso de la bandeja (K5).</summary>
    public IReadOnlyList<AuditMark> NewRed => New.Where(m => m.Color == AuditColor.Red).ToList();

    public bool Any => New.Count > 0 || Resolved.Count > 0;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AuditRecord))]
[JsonSerializable(typeof(List<IgnoredFinding>))]
internal sealed partial class AuditHistoryJson : JsonSerializerContext;

/// <summary>
/// K1: lo que se recuerda de la auditoría entre un escaneo y otro (y entre sesiones), en
/// <c>state/</c>: el último resultado (<c>audit-last.json</c>) para decir qué ha cambiado, y lo
/// ignorado (<c>audit-ignored.json</c>). Lo que no se puede leer o escribir se trata como vacío:
/// nunca impide auditar.
/// </summary>
public static class AuditHistory
{
    public static string LastFile(RoostPaths paths) => Path.Combine(paths.State, "audit-last.json");

    public static string IgnoredFile(RoostPaths paths) => Path.Combine(paths.State, "audit-ignored.json");

    /// <summary>PURO: el componente, sin la versión (si se actualiza en su sitio, es el mismo y se ve el cambio).</summary>
    public static string KeyOf(AuditFinding finding) =>
        $"{finding.Component.Kind}|{finding.Component.ExecutablePath.ToUpperInvariant()}";

    /// <summary>PURO: el componente y su versión, para ignorar solo esa versión.</summary>
    public static string IgnoreIdOf(AuditFinding finding) => $"{KeyOf(finding)}|{finding.Component.Version}";

    public static AuditMark MarkOf(AuditFinding f) => new(
        KeyOf(f), f.Component.Kind.ToString(), f.Component.Version.ToString(), f.Component.Source, f.Component.ExecutablePath, f.Color, f.VerdictLabel);

    /// <summary>
    /// PURO: qué ha cambiado de <paramref name="previous"/> a <paramref name="current"/>, sin lo que
    /// sea <paramref name="isIgnored"/>. null sin auditoría anterior (la primera vez no hay con qué comparar).
    /// </summary>
    public static AuditChanges? Compare(AuditRecord? previous, IReadOnlyList<AuditFinding> current, Func<AuditFinding, bool> isIgnored)
    {
        if (previous is null) return null;
        var before = previous.Findings.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Max(m => m.Color));
        var active = current.Where(f => !isIgnored(f)).ToList();
        var ignoredKeys = current.Where(isIgnored).Select(KeyOf).ToHashSet();
        var now = active.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.Max(f => f.Color));

        var news = active
            .Where(f => f.Color != AuditColor.Green && (!before.TryGetValue(KeyOf(f), out var was) || was < f.Color))
            .Select(MarkOf)
            .DistinctBy(m => m.Key)
            .ToList();
        var resolved = previous.Findings
            .Where(m => m.Color != AuditColor.Green && !ignoredKeys.Contains(m.Key) && (!now.TryGetValue(m.Key, out var color) || color == AuditColor.Green))
            .DistinctBy(m => m.Key)
            .ToList();
        return new AuditChanges(previous.When, news, resolved);
    }

    public static AuditRecord? LoadLast(RoostPaths paths)
    {
        try
        {
            var file = LastFile(paths);
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), AuditHistoryJson.Default.AuditRecord) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static void SaveLast(RoostPaths paths, AuditRecord record) =>
        Write(LastFile(paths), JsonSerializer.Serialize(record, AuditHistoryJson.Default.AuditRecord));

    public static List<IgnoredFinding> LoadIgnored(RoostPaths paths)
    {
        try
        {
            var file = IgnoredFile(paths);
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), AuditHistoryJson.Default.ListIgnoredFinding) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    public static void SaveIgnored(RoostPaths paths, List<IgnoredFinding> ignored) =>
        Write(IgnoredFile(paths), JsonSerializer.Serialize(ignored, AuditHistoryJson.Default.ListIgnoredFinding));

    /// <summary>Escritura atómica (a un temporal y renombrar), como los demás JSON de <c>state/</c>.</summary>
    private static void Write(string file, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin historial se sigue auditando; solo no se sabrá qué ha cambiado.
        }
    }
}
