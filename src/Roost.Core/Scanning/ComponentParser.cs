using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Scanning;

/// <summary>
/// Convierte la salida de <c>php -v</c> / <c>mysqld --version</c> / <c>mariadbd --version</c> /
/// <c>nginx -v</c> / <c>httpd -v</c> en un <see cref="DetectedComponent"/>. Es PURO (no ejecuta nada, no toca disco), así que
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

    // «/usr/sbin/mysqld  Ver 8.0.39 …»: lo de antes de «Ver» es el programa que habla.
    [GeneratedRegex(@"^(?<program>.+?)\s+Ver\s", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramBeforeVer();

    // El cliente de MariaDB 11: «mariadb from 11.4.2-MariaDB, client 15.2 for Linux».
    [GeneratedRegex(@",\s*client\s+\d", RegexOptions.IgnoreCase)]
    private static partial Regex ClientMarker();

    // «nginx version: nginx/1.30.6» (por stderr). Un fork («openresty/1.25.3.1», «Tengine/3.1.0») no casa.
    [GeneratedRegex(@"nginx version:\s*nginx/(?<version>\d+\.\d+\.\d+)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex NginxBanner();

    // «Server version: Apache/2.4.69 (Win64)», «Server version: Apache/2.4.58 (Ubuntu)».
    [GeneratedRegex(@"Server version:\s*Apache/(?<version>\d+\.\d+\.\d+)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ApacheBanner();

    private static readonly HashSet<string> ClientPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "mysql", "mariadb", "mysqladmin", "mariadb-admin", "mysqldump", "mariadb-dump",
    };

    /// <summary>
    /// true si la versión la ha dado un cliente o una herramienta (<c>mysql</c>, <c>mariadb</c>…)
    /// y no un servidor: el cliente no es una instalación, y su versión («Ver 15.1 Distrib
    /// 10.6.18») ni siquiera es la del servidor.
    /// </summary>
    public static bool IsClient(string versionLine)
    {
        if (ClientMarker().IsMatch(versionLine)) return true;
        var m = ProgramBeforeVer().Match(versionLine);
        if (!m.Success) return false;
        var program = m.Groups["program"].Value.Trim().Replace('\\', '/');
        var name = program[(program.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return ClientPrograms.Contains(name);
    }

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
        if (IsClient(firstLine)) return null;
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

    /// <summary>
    /// Parsea la salida de <c>nginx -v</c> (que nginx escribe en stderr): <c>nginx version:
    /// nginx/1.30.6</c>. Null si no es nginx (también los forks como OpenResty o Tengine, que
    /// numeran a su manera y llevan sus propios parches: compararlos con los avisos de nginx mentiría).
    /// </summary>
    public static DetectedComponent? ParseNginx(
        string? output,
        string executablePath,
        string source,
        bool isOnPath,
        string? folderHint = null)
        => ParseWebServer(output, NginxBanner(), ComponentKind.Nginx, executablePath, source, isOnPath, folderHint);

    /// <summary>
    /// Parsea la salida de <c>httpd -v</c> / <c>apache2 -v</c>: <c>Server version: Apache/2.4.69
    /// (Win64)</c>. La arquitectura sale del paréntesis (Win64, Win32) o de la carpeta.
    /// </summary>
    public static DetectedComponent? ParseApache(
        string? output,
        string executablePath,
        string source,
        bool isOnPath,
        string? folderHint = null)
        => ParseWebServer(output, ApacheBanner(), ComponentKind.Apache, executablePath, source, isOnPath, folderHint);

    // La versión cruda es la línea del banner: la auditoría busca en ella los sufijos de distro.
    private static DetectedComponent? ParseWebServer(string? output, Regex banner, ComponentKind kind,
        string executablePath, string source, bool isOnPath, string? folderHint)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = banner.Match(output);
        if (!m.Success || !ComponentVersion.TryParse(m.Groups["version"].Value, out var version)) return null;

        return new DetectedComponent
        {
            Kind = kind,
            Version = version with { Raw = m.Value.Trim() },
            ExecutablePath = executablePath,
            Source = source,
            Sapi = null,
            IsOnPath = isOnPath,
            Architecture = ParseArchitecture(m.Value) ?? ParseArchitecture(folderHint),
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
