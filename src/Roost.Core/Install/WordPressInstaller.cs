using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Una versión de WordPress: la que dice la API de wordpress.org y dónde está su zip.</summary>
/// <param name="MinPhp">El PHP mínimo que pide (<c>php_version</c> de la API), o null.</param>
public sealed record WordPressRelease(string Version, Uri Download, string? MinPhp, string? MinMySql)
{
    public string FileName => $"wordpress-{Version}.zip";

    /// <summary>El SHA-1 que publica wordpress.org junto al zip (<c>….zip.sha1</c>), en el mismo servidor y por HTTPS.</summary>
    public Uri ChecksumUrl => new(Download + ".sha1");
}

/// <summary>
/// La versión actual de WordPress, de <c>api.wordpress.org/core/version-check/1.7/</c> (la que usa el
/// propio WordPress para avisar de las actualizaciones). wordpress.org no publica SHA-256: publica el
/// SHA-1 (y el MD5) de cada zip junto al zip. Roost usa el SHA-1, bajado por HTTPS del mismo origen:
/// protege de un zip corrupto o cortado y de una caché que no cuadra; frente a alguien que controlase
/// wordpress.org no protege más que el propio HTTPS, que es lo mismo en lo que se apoya WordPress al
/// actualizarse solo. No se fija a mano como nginx o MySQL porque WordPress saca versiones de seguridad
/// a menudo y una plantilla tiene que dar la última.
/// </summary>
public static partial class WordPressCatalog
{
    public static readonly Uri VersionCheckUrl = new("https://api.wordpress.org/core/version-check/1.7/");

    /// <summary>
    /// PURO: la oferta «upgrade» (la última versión) de la respuesta de la API, con su zip completo.
    /// Lanza <see cref="RoostException"/> si no se entiende o si el zip no es de wordpress.org por HTTPS.
    /// </summary>
    public static WordPressRelease Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("offers", out var offers) || offers.ValueKind != JsonValueKind.Array)
                throw new RoostException("La API de wordpress.org no ha devuelto ninguna version (falta offers).");

            JsonElement? best = null;
            foreach (var offer in offers.EnumerateArray())
            {
                if (offer.ValueKind != JsonValueKind.Object) continue;
                best ??= offer;
                if (Text(offer, "response") == "upgrade") { best = offer; break; }
            }
            if (best is not { } chosen)
                throw new RoostException("La API de wordpress.org no ha devuelto ninguna version.");

            var version = Text(chosen, "version") ?? Text(chosen, "current");
            if (version is null || !VersionPattern().IsMatch(version))
                throw new RoostException($"La API de wordpress.org da una version que no se entiende ({version ?? "ninguna"}).");

            var url = chosen.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object ? Text(packages, "full") : null;
            url ??= Text(chosen, "download");
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var download) || !IsWordPressOrg(download))
                throw new RoostException($"El zip de WordPress {version} no es de wordpress.org por https ({url ?? "sin direccion"}): no se descarga.");

            return new WordPressRelease(version, download, Text(chosen, "php_version"), Text(chosen, "mysql_version"));
        }
        catch (JsonException ex)
        {
            throw new RoostException($"La respuesta de la API de wordpress.org no se entiende: {ex.Message}");
        }
    }

    /// <summary>PURO: el SHA-1 de un <c>.sha1</c> de wordpress.org (40 hexadecimales, quizá con el nombre detrás), en minúsculas.</summary>
    public static string ParseSha1(string text)
    {
        var match = Sha1Pattern().Match(text);
        if (!match.Success)
            throw new RoostException("El .sha1 de wordpress.org no trae un SHA-1: no se instala nada.");
        return match.Value.ToLowerInvariant();
    }

    /// <summary>true si la dirección es de wordpress.org (o de su CDN, w.org) por HTTPS.</summary>
    public static bool IsWordPressOrg(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps
        && (url.Host is "wordpress.org" or "w.org" || url.Host.EndsWith(".wordpress.org", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".w.org", StringComparison.OrdinalIgnoreCase));

    /// <summary>La versión actual, preguntando a la API (red).</summary>
    public static async Task<WordPressRelease> LatestAsync(HttpClient http, CancellationToken cancellation)
    {
        string json;
        try { json = await http.GetStringAsync(VersionCheckUrl, cancellation); }
        catch (HttpRequestException ex) { throw new RoostException($"No se pudo preguntar a wordpress.org por la ultima version: {ex.Message}", ex); }
        return Parse(json);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{40}\b")]
    private static partial Regex Sha1Pattern();
}

/// <summary>
/// Deja WordPress en la carpeta de un sitio nuevo (la plantilla de Nuevo sitio y <c>roost new
/// wordpress</c>): el zip de la versión actual a la caché (<c>cache/downloads</c>, reutilizado si su
/// SHA-1 cuadra), comprobado con el SHA-1 de wordpress.org antes de descomprimir nada, y extraído
/// como el resto de instaladores: a una carpeta <c>.tmp-…</c> que se renombra al final, quitando la
/// carpeta <c>wordpress/</c> de dentro del zip. La carpeta tiene que no existir.
/// </summary>
public sealed class WordPressInstaller(RoostPaths paths, HttpClient http)
{
    /// <summary>Descarga, comprueba y extrae. Lo que cuenta va a <paramref name="lines"/> (el progreso, en porcentaje).</summary>
    public async Task<WordPressRelease> InstallAsync(string folder, IProgress<string>? lines, CancellationToken cancellation)
    {
        if (Directory.Exists(folder) || File.Exists(folder))
            throw new RoostException($"{folder} ya existe: WordPress se descomprime en una carpeta nueva.");

        lines?.Report($"Preguntando a {WordPressCatalog.VersionCheckUrl.Host} por la ultima version...");
        var release = await WordPressCatalog.LatestAsync(http, cancellation);
        lines?.Report($"WordPress {release.Version} (pide PHP {release.MinPhp ?? "?"} y MySQL {release.MinMySql ?? "?"} o mas nuevos): {release.Download}");

        string sha1;
        try { sha1 = WordPressCatalog.ParseSha1(await http.GetStringAsync(release.ChecksumUrl, cancellation)); }
        catch (HttpRequestException ex) { throw new RoostException($"No se pudo leer el SHA-1 de WordPress ({release.ChecksumUrl}): {ex.Message}. No se instala nada.", ex); }
        lines?.Report($"SHA-1 publicado: {sha1}");

        var zip = await DownloadVerifiedAsync(release, sha1, lines, cancellation);
        lines?.Report($"Descomprimiendo en {folder}...");
        InstallFiles.ExtractAtomic(zip, folder, "wp-load.php", stripSingleRoot: true, $"WordPress {release.Version}");
        lines?.Report($"WordPress {release.Version} listo en {folder}.");
        return release;
    }

    /// <summary>
    /// El zip verificado en la caché: si ya estaba y su SHA-1 cuadra, ese; si no, se baja a un
    /// <c>.part-…</c>, se comprueba y solo entonces pasa a su nombre. Si no cuadra, no queda nada.
    /// </summary>
    private async Task<string> DownloadVerifiedAsync(WordPressRelease release, string sha1, IProgress<string>? lines, CancellationToken cancellation)
    {
        Directory.CreateDirectory(paths.Downloads);
        var final = Path.Combine(paths.Downloads, Path.GetFileName(release.FileName));
        if (File.Exists(final))
        {
            if (await Sha1OfAsync(final, cancellation) == sha1)
            {
                lines?.Report($"{release.FileName} ya estaba en la cache y su SHA-1 cuadra.");
                return final;
            }
            InstallFiles.TryDeleteFile(final);
        }

        var part = final + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var file = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    done += read;
                    if (total is > 0 && lines is not null)
                    {
                        var percent = (int)(done * 100 / total.Value);
                        if (percent / 5 != lastPercent / 5 || percent == 100 && lastPercent != 100)
                        {
                            lastPercent = percent;
                            lines.Report(string.Create(CultureInfo.GetCultureInfo("es-ES"),
                                $"Descargando {release.FileName}: {percent}% ({done / 1048576.0:0.0} de {total.Value / 1048576.0:0.0} MB)"));
                        }
                    }
                }
            }

            var actual = await Sha1OfAsync(part, cancellation);
            if (actual != sha1)
                throw new RoostException($"El SHA-1 de {release.FileName} no coincide con el que publica wordpress.org (esperado {sha1}, obtenido {actual}). No se instala nada.");
            lines?.Report($"SHA-1 comprobado: {actual}");
            File.Move(part, final, overwrite: true);
            return final;
        }
        catch (Exception ex)
        {
            InstallFiles.TryDeleteFile(part);
            if (ex is RoostException or OperationCanceledException) throw;
            throw new RoostException($"No se pudo descargar {release.Download}: {ex.Message}", ex);
        }
    }

    private static async Task<string> Sha1OfAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
#pragma warning disable CA5350 // es el hash que publica wordpress.org: se comprueba, no se usa para firmar nada
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, cancellation));
#pragma warning restore CA5350
    }
}
