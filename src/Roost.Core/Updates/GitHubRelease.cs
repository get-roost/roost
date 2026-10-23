using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Roost.Core.Updates;

/// <summary>Un fichero de una release (<c>roost-0.2.0-win-x64.zip</c>, <c>SHA256SUMS.txt</c>…).</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>Una release publicada de Roost, tal como la cuenta GitHub.</summary>
public sealed record ReleaseInfo
{
    /// <summary>La etiqueta tal cual: <c>v0.2.0</c>.</summary>
    public required string Tag { get; init; }

    public required SemanticVersion Version { get; init; }

    /// <summary>El título de la release (<c>Roost 0.2.0</c>), o la etiqueta si no tiene.</summary>
    public required string Name { get; init; }

    /// <summary>Su página en GitHub, con las notas y los ficheros.</summary>
    public required Uri Page { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Las notas de la release, en Markdown (pueden estar vacías).</summary>
    public string Notes { get; init; } = "";

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>
    /// El paquete para este sistema: <c>roost-&lt;versión&gt;-win-x64.zip</c> en Windows y
    /// <c>roost-&lt;versión&gt;-linux-x64.tar.gz</c> en Linux; null si la release no lo trae.
    /// </summary>
    public ReleaseAsset? PackageFor(bool windows) =>
        Assets.FirstOrDefault(a => a.Name.StartsWith("roost-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(windows ? "-win-x64.zip" : "-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase));

    /// <summary>El instalador de Windows de esta release (<c>roost-0.2.0-win-x64-setup.exe</c>), si lo trae.</summary>
    public ReleaseAsset? Installer =>
        Assets.FirstOrDefault(a => a.Name.StartsWith("roost-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// La API de releases de GitHub del repo de Roost. PURO: aquí solo se parsea; la red la pone
/// <see cref="UpdateChecker"/>. Se usa <c>/releases/latest</c>, que ya excluye borradores y
/// prereleases y da 404 si aún no hay ninguna; el parser las descarta igualmente, y también
/// acepta la lista de <c>/releases</c> (se queda con la estable más nueva).
/// </summary>
public static class GitHubReleases
{
    public static readonly Uri LatestUrl = new("https://api.github.com/repos/get-roost/roost/releases/latest");

    /// <summary>
    /// Parsea la respuesta. True si es JSON con la forma esperada; <paramref name="release"/> es
    /// null si no hay ninguna release estable (todo borradores o prereleases, o la lista vacía).
    /// False, con el motivo, si la respuesta no se entiende: así no se confunde con «no hay».
    /// </summary>
    public static bool TryParse(string json, out ReleaseInfo? release, out string? error)
    {
        release = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!root.TryGetProperty("tag_name", out _))
                    {
                        error = "la respuesta de GitHub no es una release (no trae tag_name)";
                        return false;
                    }
                    if (!TryRead(root, out release, out error)) return false;
                    return true;

                case JsonValueKind.Array:
                    foreach (var item in root.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (TryRead(item, out var candidate, out _) && candidate is not null
                            && (release is null || candidate.Version > release.Version))
                            release = candidate;
                    }
                    return true;

                default:
                    error = "la respuesta de GitHub no es un objeto JSON";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "la respuesta de GitHub no es JSON valido";
            return false;
        }
    }

    /// <summary>
    /// Lee una release. True con null si es un borrador o una prerelease (no cuentan); false si
    /// le falta algo imprescindible o su etiqueta no es una versión.
    /// </summary>
    internal static bool TryRead(JsonElement e, out ReleaseInfo? release, out string? error)
    {
        release = null;
        error = null;
        if (Bool(e, "draft") || Bool(e, "prerelease")) return true;

        var tag = Str(e, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version))
        {
            error = $"la etiqueta de la release no es una version ({tag ?? "vacia"})";
            return false;
        }
        // Una etiqueta con guion es una prerelease aunque no la marcaran como tal (contrato: v0.2.0-beta.1).
        if (version.IsPrerelease) return true;

        if (!Uri.TryCreate(Str(e, "html_url"), UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
        {
            error = "la release no trae su pagina (html_url)";
            return false;
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(Str(e, "published_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var p) ? p : null;

        var assets = new List<ReleaseAsset>();
        if (e.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Solo https; http únicamente en loopback, para probar el actualizador con una release
                // falsa servida en local (ROOST_UPDATE_URL con py -3 -m http.server).
                if (!Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url)
                    || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))) continue;
                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        var title = Str(e, "name");
        release = new ReleaseInfo
        {
            Tag = tag!,
            Version = version,
            Name = string.IsNullOrWhiteSpace(title) ? tag! : title.Trim(),
            Page = page,
            PublishedAt = published,
            Notes = Str(e, "body") ?? "",
            Assets = assets,
        };
        return true;
    }

    /// <summary>
    /// Escribe una release con los mismos nombres de campo que GitHub, para que la caché se lea
    /// con el mismo parser.
    /// </summary>
    internal static void Write(Utf8JsonWriter w, ReleaseInfo release)
    {
        w.WriteStartObject();
        w.WriteString("tag_name", release.Tag);
        w.WriteString("name", release.Name);
        w.WriteString("html_url", release.Page.ToString());
        if (release.PublishedAt is { } published) w.WriteString("published_at", published.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("body", release.Notes);
        w.WriteBoolean("draft", false);
        w.WriteBoolean("prerelease", false);
        w.WriteStartArray("assets");
        foreach (var a in release.Assets)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            w.WriteString("browser_download_url", a.Url.ToString());
            w.WriteNumber("size", a.Size);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// Las notas en corto, para la terminal o la app: las primeras líneas con texto, sin títulos
    /// ni la sintaxis de Markdown que estorba al leerlas tal cual (negritas, código, enlaces). PURO.
    /// </summary>
    public static IReadOnlyList<string> Summarize(string? notes, int maxLines = 6, int maxLineLength = 110)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return lines;
        var fence = false;
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence || line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal)) continue;
            if (line.All(c => c is '-' or '=' or '*' or '_')) continue;   // separadores
            if (line.StartsWith('#')) continue;   // los títulos («## Novedades»): quien enseña las notas ya pone el suyo

            var bullet = line.Length > 1 && (line[0] is '-' or '*' or '+') && line[1] == ' ';
            if (bullet) line = line[2..].Trim();
            line = Plain(line);
            if (line.Length == 0) continue;
            if (lines.Count == maxLines)
            {
                lines.Add("…");   // hay más: están en la página de la release
                break;
            }
            if (line.Length > maxLineLength) line = line[..(maxLineLength - 1)].TrimEnd() + "…";
            lines.Add(bullet ? "- " + line : line);
        }
        return lines;
    }

    // [texto](url) → texto; ![alt](url) → alt; **negrita**, __negrita__ y `código` → el texto.
    internal static string Plain(string line)
    {
        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '!' && i + 1 < line.Length && line[i + 1] == '[') continue;
            if (c == '[' && line.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0)
            {
                var end = line.IndexOf(')', mid);
                if (end > mid)
                {
                    sb.Append(line, i + 1, mid - i - 1);
                    i = end;
                    continue;
                }
            }
            if (c is '`') continue;
            if (c is '*' or '_' && i + 1 < line.Length && line[i + 1] == c)
            {
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
