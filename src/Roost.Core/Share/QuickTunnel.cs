using System.Text.RegularExpressions;
using Roost.Core.Platform;

namespace Roost.Core.Share;

/// <summary>
/// Cómo lanza Roost un túnel rápido de Cloudflare (<c>cloudflared tunnel --url</c>, sin cuenta, en
/// <c>https://&lt;algo&gt;.trycloudflare.com</c>) y cómo lo aísla de lo que tenga el usuario:
/// <list type="bullet">
/// <item><c>--config</c> con un fichero de Roost vacío: sin él, cloudflared lee el
/// <c>~/.cloudflared/config.yml</c> del usuario, y sus reglas de <c>ingress</c> mandarían sobre
/// <c>--url</c> (el túnel acabaría en otro servicio de su equipo);</item>
/// <item>sin las variables <c>TUNNEL_*</c>, <c>NO_TLS_VERIFY</c>, <c>NO_AUTOUPDATE</c> y <c>CFDPATH</c>
/// heredadas (un <c>TUNNEL_URL</c> o un <c>TUNNEL_TOKEN</c> del usuario cambiarían qué se publica);</item>
/// <item><c>--no-autoupdate</c>: el ejecutable es el fijado por Roost con su SHA-256 y no se cambia solo;</item>
/// <item>las métricas solo en loopback, en un puerto que elige el sistema, y <c>--label roost</c> en
/// vez del nombre del equipo.</item>
/// </list>
/// </summary>
public static class QuickTunnel
{
    /// <summary>El dominio de los túneles rápidos.</summary>
    public const string Domain = "trycloudflare.com";

    /// <summary>Lo que se espera, como mucho, a que cloudflared diga la URL.</summary>
    public static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Lo que se espera después, como mucho, a que registre la primera conexión (antes, la URL da error).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>El fichero de configuración vacío de Roost: <c>state/tunnel/cloudflared.yml</c>.</summary>
    public static string ConfigFile(RoostPaths paths) => Path.Combine(paths.State, "tunnel", "cloudflared.yml");

    /// <summary>El log de un túnel: <c>logs/tunel-&lt;sitio&gt;.log</c>, con todo lo que escribe cloudflared.</summary>
    public static string LogFile(RoostPaths paths, string site) => Path.Combine(paths.Logs, $"tunel-{site}.log");

    /// <summary>Escribe (si falta o cambió) el fichero de configuración vacío y devuelve su ruta.</summary>
    public static string EnsureConfigFile(RoostPaths paths)
    {
        var file = ConfigFile(paths);
        const string content = "# Generado por Roost: vacio a proposito, para que los tuneles de roost share no lean el config.yml de ~/.cloudflared.\n{}\n";
        if (!File.Exists(file) || File.ReadAllText(file) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        return file;
    }

    /// <summary>PURO: los argumentos de <c>cloudflared</c> para publicar <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public static IReadOnlyList<string> Arguments(int port, string configFile) =>
    [
        "tunnel",
        "--config", configFile,
        "--no-autoupdate",
        "--metrics", "127.0.0.1:0",
        "--label", "roost",
        "--url", $"http://127.0.0.1:{port}",
    ];

    /// <summary>
    /// PURO: lo que se quita del entorno heredado (valor null) para que nada de fuera cambie el túnel:
    /// cada variable <c>TUNNEL_*</c> que haya y las sueltas que lee cloudflared.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment(IEnumerable<string> inherited)
    {
        var removed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in inherited)
        {
            if (name.StartsWith("TUNNEL_", StringComparison.OrdinalIgnoreCase)) removed[name] = null;
        }
        foreach (var name in new[] { "NO_TLS_VERIFY", "NO_AUTOUPDATE", "CFDPATH" }) removed[name] = null;
        return removed;
    }

    /// <summary>Lo mismo con el entorno de este proceso.</summary>
    public static IReadOnlyDictionary<string, string?> Environment() =>
        Environment(System.Environment.GetEnvironmentVariables().Keys.Cast<string>());
}

/// <summary>
/// Lee, línea a línea, lo que escribe <c>cloudflared tunnel --url</c> (todo va a stderr): la URL
/// pública, que sale en un recuadro después de «Your quick Tunnel has been created!», el momento en
/// que la primera conexión queda registrada («Registered tunnel connection»: antes, la URL aún da
/// error) y el último error, para contarlo si no llega a salir la URL.
/// </summary>
public sealed partial class QuickTunnelOutput
{
    private bool _created;

    /// <summary>La URL pública (<c>https://xxx.trycloudflare.com</c>), en cuanto sale.</summary>
    public string? Url { get; private set; }

    /// <summary>Si ya hay al menos una conexión con Cloudflare (la URL ya contesta).</summary>
    public bool Connected { get; private set; }

    /// <summary>El último error que ha escrito (sin la fecha ni el nivel), o null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Lee una línea. Devuelve true si con ella ha salido la URL o se ha registrado la conexión.</summary>
    public bool Feed(string line)
    {
        var text = StripPrefix(line);
        if (text.Contains("quick Tunnel has been created", StringComparison.OrdinalIgnoreCase))
        {
            _created = true;
            return false;
        }
        if (Url is null && UrlPattern().Match(text) is { Success: true } match && (_created || IsQuickTunnelHost(match.Value)))
        {
            Url = match.Value.TrimEnd('/');
            return true;
        }
        if (!Connected && text.Contains("Registered tunnel connection", StringComparison.Ordinal))
        {
            Connected = true;
            return true;
        }
        if (IsError(line, text)) LastError = text.Trim();
        return false;
    }

    /// <summary>PURO: en palabras, por qué no ha salido la URL (sin tildes: lo enseñan la CLI y el log).</summary>
    public static string Explain(string? lastError, int? exitCode)
    {
        if (lastError is null)
            return exitCode is { } code ? $"cloudflared ha terminado (codigo {code}) sin dar la URL." : "cloudflared no ha dado la URL a tiempo.";
        if (lastError.Contains("429", StringComparison.Ordinal) || lastError.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            return "Cloudflare no da mas tuneles rapidos ahora mismo (demasiados seguidos). Prueba dentro de un rato. Detalle: " + lastError;
        if (lastError.Contains("failed to request quick Tunnel", StringComparison.OrdinalIgnoreCase)
            && (lastError.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) || lastError.Contains("no such host", StringComparison.OrdinalIgnoreCase)
                || lastError.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
            return "No se ha podido hablar con trycloudflare.com: mira la conexion a internet (o el proxy o el cortafuegos). Detalle: " + lastError;
        return "cloudflared no ha podido abrir el tunel: " + lastError;
    }

    private static bool IsQuickTunnelHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.EndsWith("." + QuickTunnel.Domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lo que va detrás de «2026-10-24T05:32:17Z INF », o la línea tal cual si no lo lleva.</summary>
    private static string StripPrefix(string line)
    {
        var match = LogPrefix().Match(line);
        return match.Success ? line[match.Length..] : line;
    }

    private static bool IsError(string line, string text) =>
        LogPrefix().Match(line) is { Success: true } prefix
            ? prefix.Groups["level"].Value is "ERR" or "FTL"
            : text.StartsWith("failed", StringComparison.OrdinalIgnoreCase) || text.Contains("error", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https://[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?(?::\d+)?/?")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^\S+Z (?<level>[A-Z]{3}) ")]
    private static partial Regex LogPrefix();
}
