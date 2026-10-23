using System.Text.Json;
using System.Text.RegularExpressions;
using Roost.Core.Install;

namespace Roost.Core.Sites;

/// <summary>
/// Lo que Roost lee del <c>composer.json</c> de un proyecto: el PHP que pide (<c>require.php</c>).
/// Lo usan el alta de un sitio y la bienvenida, para proponer el PHP de cada carpeta.
/// </summary>
public static partial class ComposerJson
{
    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex MinimumPattern();

    /// <summary>El <c>require.php</c> de la carpeta («^8.2», «&gt;=7.4»); null si no hay composer.json, no lo pide o no se puede leer.</summary>
    public static string? PhpConstraint(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "composer.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("require", out var require)
                   && require.ValueKind == JsonValueKind.Object
                   && require.TryGetProperty("php", out var php)
                   && php.ValueKind == JsonValueKind.String
                ? php.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// El PHP que cumple <paramref name="constraint"/>: el más nuevo de los instalados (NTS) que llega a
    /// la versión mínima y, con <c>^</c>, de su misma versión mayor. null si no hay ninguno.
    /// </summary>
    public static InstalledPhp? Recommended(string constraint, IReadOnlyList<InstalledPhp> installed)
    {
        var min = MinimumPattern().Match(constraint);
        if (!min.Success) return null;
        var major = int.Parse(min.Groups[1].Value);
        var minor = int.Parse(min.Groups[2].Value);
        var caret = constraint.TrimStart().StartsWith('^');
        return installed
            .Where(p => !p.ThreadSafe && p.Version is { } v && (v.Major > major || v.Major == major && v.Minor >= minor) && (!caret || v.Major == major))
            .OrderByDescending(p => p.Version)
            .FirstOrDefault();
    }
}
