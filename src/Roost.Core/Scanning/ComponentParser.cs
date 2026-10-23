using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Scanning;

/// <summary>
/// Convierte la salida de <c>php -v</c> / <c>mysqld --version</c> / <c>mariadbd --version</c>
/// en un <see cref="DetectedComponent"/>. Es PURO (no ejecuta nada, no toca disco), así que
/// es lo que cubren los tests: se le pasan salidas de ejemplo y se comprueba el resultado.
/// Los scanners se encargan de la parte sucia (descubrir rutas y ejecutar los binarios).
/// </summary>
public static partial class ComponentParser
{
    [GeneratedRegex(@"PHP\s+[\d.]+\s*\(([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex PhpSapiPattern();

    [GeneratedRegex(@"\b(ZTS|NTS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThreadSafetyInline();

    [GeneratedRegex(@"(?:^|[-_.])(nts|zts|ts)(?:[-_.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ThreadSafetyInName();

    /// <summary>
    /// Parsea la salida de <c>php -v</c>. Devuelve <c>null</c> si no hay versión legible.
    /// <paramref name="folderHint"/> es el nombre de la carpeta contenedora (p.ej.
    /// <c>php-8.3.14-nts-Win32-vs16-x64</c>): sirve de reserva para arquitectura y thread-safety.
    /// </summary>
    public static DetectedComponent? ParsePhp(
        string? output,
        string executablePath,
        string source,
        bool isOnPath,
        string? folderHint = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var firstLine = FirstLine(output);
        if (!ComponentVersion.TryParse(firstLine, out var version)) return null;

        var sapi = ParseSapi(output) ?? "cli"; // corremos el CLI, así que ese es el defecto razonable
        var threadSafety = ParseThreadSafety(output) ?? ParseThreadSafetyFromName(folderHint);
        var architecture = ParseArchitecture(output) ?? ParseArchitecture(folderHint);

        var extra = new Dictionary<string, string>();
        if (threadSafety is not null) extra["threadSafety"] = threadSafety;

        return new DetectedComponent
        {
            Kind = ComponentKind.Php,
            Version = version,
            ExecutablePath = executablePath,
            Source = source,
            Sapi = sapi,
            IsOnPath = isOnPath,
            Architecture = architecture,
            Extra = extra,
        };
    }

    /// <summary>
    /// Parsea la salida de <c>mysqld --version</c> / <c>mariadbd --version</c>. El
    /// <paramref name="expectedKind"/> debe ser <see cref="ComponentKind.MySql"/> o
    /// <see cref="ComponentKind.MariaDb"/>; se usa un cruce de coherencia: MariaDB anuncia
    /// "MariaDB" en su banner, así que si la salida y el tipo esperado no casan, se descarta
    /// (así un <c>mysqld.exe</c> que en realidad es MariaDB no se cuenta dos veces mal).
    /// </summary>
    public static DetectedComponent? ParseDatabase(
        string? output,
        ComponentKind expectedKind,
        string executablePath,
        string source,
        bool isOnPath,
        string? folderHint = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (expectedKind is not (ComponentKind.MySql or ComponentKind.MariaDb)) return null;

        var announcesMariaDb = output.Contains("mariadb", StringComparison.OrdinalIgnoreCase);
        // Coherencia: no clasificar un binario con el tipo equivocado.
        if (expectedKind == ComponentKind.MariaDb && !announcesMariaDb) return null;
        if (expectedKind == ComponentKind.MySql && announcesMariaDb) return null;

        var firstLine = FirstLine(output);
        if (!ComponentVersion.TryParse(firstLine, out var version)) return null;

        var architecture = ParseArchitecture(output) ?? ParseArchitecture(folderHint);

        return new DetectedComponent
        {
            Kind = expectedKind,
            Version = version,
            ExecutablePath = executablePath,
            Source = source,
            Sapi = null,
            IsOnPath = isOnPath,
            Architecture = architecture,
        };
    }

    /// <summary>SAPI de PHP a partir del primer paréntesis tras la versión: cli / fpm / cgi.</summary>
    private static string? ParseSapi(string output)
    {
        var m = PhpSapiPattern().Match(output);
        if (!m.Success) return null;

        var raw = m.Groups[1].Value.ToLowerInvariant();
        if (raw.Contains("fpm")) return "fpm";
        if (raw.Contains("cgi")) return "cgi";
        if (raw.Contains("cli")) return "cli";
        return raw.Trim();
    }

    /// <summary>Thread-safety desde el banner: ZTS = "ts" (seguro), NTS = "nts" (no seguro).</summary>
    private static string? ParseThreadSafety(string output)
    {
        var m = ThreadSafetyInline().Match(output);
        if (!m.Success) return null;
        return m.Groups[1].Value.Equals("NTS", StringComparison.OrdinalIgnoreCase) ? "nts" : "ts";
    }

    /// <summary>Thread-safety desde el nombre de carpeta (p.ej. <c>...-nts-...</c>).</summary>
    private static string? ParseThreadSafetyFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var m = ThreadSafetyInName().Match(name);
        if (!m.Success) return null;
        return m.Groups[1].Value.Equals("nts", StringComparison.OrdinalIgnoreCase) ? "nts" : "ts";
    }

    /// <summary>
    /// Arquitectura a partir de cualquier texto (banner del binario o nombre de carpeta).
    /// El orden importa: "x86_64" es x64, no x86, así que se comprueba 64 bits primero.
    /// </summary>
    private static string? ParseArchitecture(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = text.ToLowerInvariant();

        if (t.Contains("x86_64") || t.Contains("amd64") || t.Contains("win64") || t.Contains("x64"))
            return "x64";
        if (t.Contains("arm64") || t.Contains("aarch64"))
            return "arm64";
        if (t.Contains("win32") || t.Contains("x86") || t.Contains("i386") || t.Contains("i686"))
            return "x86";

        return null;
    }

    /// <summary>Primera línea no vacía del texto (donde vive siempre la versión).</summary>
    private static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length > 0) return line;
        }

        return text.Trim();
    }
}
