using System.Globalization;
using System.Text.Json;
using Roost.Core.Platform;

namespace Roost.Core.Updates;

/// <summary>Qué pasó con una actualización pendiente, visto desde la versión que corre ahora.</summary>
public enum UpdateOutcome
{
    /// <summary>La versión que corre es la nueva (o más): se actualizó.</summary>
    Updated,

    /// <summary>La versión que corre sigue siendo la vieja: el instalador no llegó a actualizar.</summary>
    NotUpdated,
}

/// <summary>
/// La nota que deja quien lanza el instalador (la app o <c>roost update --install</c>) en
/// <c>state/update-pending.json</c>: de qué versión a cuál y si el stack estaba en marcha. La app,
/// al volver a abrirse, la lee, la borra y dice «Roost actualizado a X» (y arranca el stack si
/// tocaba) o «No se ha actualizado», según la versión que corra.
/// </summary>
public sealed record UpdateMarker(string From, string To, bool StackWasRunning, DateTimeOffset At, string? SetupLog = null)
{
    public const string FileName = "update-pending.json";

    private const int Format = 1;

    public static string FilePath(RoostPaths paths) => Path.Combine(paths.State, FileName);

    /// <summary>Comparado con <paramref name="currentVersion"/>: actualizada si corre la nueva (o una posterior).</summary>
    public UpdateOutcome Outcome(string currentVersion) =>
        SemanticVersion.TryParse(currentVersion, out var current) && SemanticVersion.TryParse(To, out var to) && current >= to
            ? UpdateOutcome.Updated
            : UpdateOutcome.NotUpdated;

    /// <summary>Escribe el marcador (atómico). Lanza <see cref="RoostException"/> si no se puede.</summary>
    public static void Write(RoostPaths paths, UpdateMarker marker)
    {
        try
        {
            Directory.CreateDirectory(paths.State);
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("format", Format);
                w.WriteString("from", marker.From);
                w.WriteString("to", marker.To);
                w.WriteBoolean("stackWasRunning", marker.StackWasRunning);
                w.WriteString("at", marker.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                if (marker.SetupLog is not null) w.WriteString("setupLog", marker.SetupLog);
                w.WriteEndObject();
            }
            var file = FilePath(paths);
            var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tmp, buffer.ToArray());
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo apuntar la actualizacion pendiente en {FilePath(paths)}: {ex.Message}", ex);
        }
    }

    /// <summary>El marcador, o null si no hay o no se entiende. Nunca lanza.</summary>
    public static UpdateMarker? TryRead(RoostPaths paths)
    {
        try
        {
            var file = FilePath(paths);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != Format) return null;
            var from = Str(root, "from");
            var to = Str(root, "to");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;
            var running = root.TryGetProperty("stackWasRunning", out var r) && r.ValueKind == JsonValueKind.True;
            var at = DateTimeOffset.TryParse(Str(root, "at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue;
            return new UpdateMarker(from, to, running, at, Str(root, "setupLog"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Quita el marcador (si no se puede, no pasa nada: se volverá a intentar al abrir la app).</summary>
    public static void Delete(RoostPaths paths)
    {
        try { File.Delete(FilePath(paths)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
