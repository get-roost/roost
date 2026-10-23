using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Roost.Core.Platform;

namespace Roost.Core.Updates;

/// <summary>Lo que se sabe tras comprobar si hay una Roost nueva.</summary>
public enum UpdateStatus
{
    /// <summary>La instalada es la última publicada (o una más nueva: una build de desarrollo).</summary>
    UpToDate,

    /// <summary>Hay una publicada más nueva que la instalada.</summary>
    Available,

    /// <summary>Aún no hay ninguna versión publicada (la API da 404).</summary>
    NoReleases,

    /// <summary>No se pudo comprobar: sin red, GitHub limita las consultas, una respuesta rara… (<see cref="UpdateCheck.Error"/>).</summary>
    Failed,
}

/// <summary>
/// El resultado de una comprobación. <see cref="Latest"/> puede venir también con
/// <see cref="UpdateStatus.Failed"/>: es la última que se conocía (de la caché), para no dejar
/// de avisar de una versión nueva solo porque hoy no hay red.
/// </summary>
public sealed record UpdateCheck
{
    public required UpdateStatus Status { get; init; }

    /// <summary>La versión que se está ejecutando.</summary>
    public required string Installed { get; init; }

    public ReleaseInfo? Latest { get; init; }

    /// <summary>Cuándo se le preguntó a GitHub lo que cuenta <see cref="Latest"/> (null si nunca).</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>La respuesta sale de la caché, sin ir a la red.</summary>
    public bool FromCache { get; init; }

    /// <summary>Por qué no se pudo comprobar (sin tildes: lo enseña también la CLI).</summary>
    public string? Error { get; init; }

    /// <summary>Hay una versión publicada más nueva que la instalada (aunque hoy haya fallado la comprobación).</summary>
    public bool UpdateAvailable => Latest is { } latest && SemanticVersion.TryParse(Installed, out var installed) && latest.Version > installed;

    /// <summary>La instalada es más nueva que la última publicada: una build de desarrollo.</summary>
    public bool InstalledIsNewer => Latest is { } latest && SemanticVersion.TryParse(Installed, out var installed) && installed > latest.Version;
}

/// <summary>
/// Comprueba si hay una Roost nueva en las releases de GitHub. No descarga ni sustituye nada
/// (eso llegará con el instalador): solo avisa. <b>Nunca lanza</b>: todo lo que pueda fallar
/// acaba en un <see cref="UpdateCheck"/> con su motivo.
/// <list type="bullet">
/// <item>La respuesta se guarda en <c>&lt;caché&gt;/update-check.json</c> y vale 24 h, para no
/// preguntar en cada arranque; <c>force</c> pregunta igualmente.</item>
/// <item><c>ROOST_NO_UPDATE_CHECK=1</c> desactiva las comprobaciones automáticas (la de la app al
/// abrirse, el aviso de <c>roost status</c>); lo que pide el usuario (<c>roost update</c>, el botón
/// de Ajustes) sigue funcionando. Mirarlo es cosa de quien llama: <see cref="DisabledByEnvironment"/>.</item>
/// <item><c>ROOST_UPDATE_URL</c> cambia la URL de la API, para probar con un servidor propio.</item>
/// </list>
/// </summary>
public sealed class UpdateChecker
{
    public const string DisableVariable = "ROOST_NO_UPDATE_CHECK";
    public const string UrlVariable = "ROOST_UPDATE_URL";

    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const int CacheFormat = 1;

    private readonly RoostPaths _paths;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    /// <param name="installed">La versión instalada; por defecto, <see cref="RoostVersion.Current"/>.</param>
    /// <param name="endpoint">La URL de la API; por defecto, la de <c>ROOST_UPDATE_URL</c> o la de GitHub.</param>
    /// <param name="time">El reloj (para los tests de la caché).</param>
    public UpdateChecker(RoostPaths paths, HttpClient http, string? installed = null, Uri? endpoint = null,
        TimeProvider? time = null, TimeSpan? timeout = null)
    {
        _paths = paths;
        _http = http;
        Installed = installed ?? RoostVersion.Current;
        Endpoint = endpoint ?? EndpointFromEnvironment();
        _time = time ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string Installed { get; }

    public Uri Endpoint { get; }

    public string CacheFile => Path.Combine(_paths.Cache, "update-check.json");

    /// <summary><c>ROOST_NO_UPDATE_CHECK</c> vale 1, true, yes u on: nada de comprobaciones automáticas.</summary>
    public static bool DisabledByEnvironment => DisabledIn(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Las comprobaciones que nadie ha pedido (las del daemon y la línea de aviso de la CLI) no se
    /// hacen con <c>ROOST_NO_UPDATE_CHECK</c> ni en CI (<c>CI</c> o <c>GITHUB_ACTIONS</c> definidas):
    /// en un runner nadie va a actualizar Roost a mano. <c>roost update</c> sigue funcionando.
    /// </summary>
    public static bool AutomaticChecksOff => AutomaticChecksOffIn(Environment.GetEnvironmentVariable);

    /// <summary>PURO: <see cref="DisabledByEnvironment"/> con las variables de <paramref name="env"/>.</summary>
    public static bool DisabledIn(Func<string, string?> env) =>
        env(DisableVariable)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>PURO: <see cref="AutomaticChecksOff"/> con las variables de <paramref name="env"/>.</summary>
    public static bool AutomaticChecksOffIn(Func<string, string?> env) =>
        DisabledIn(env) || !string.IsNullOrWhiteSpace(env("CI")) || !string.IsNullOrWhiteSpace(env("GITHUB_ACTIONS"));

    /// <summary>La URL de <c>ROOST_UPDATE_URL</c> si es una URL http(s) válida; si no, la de GitHub.</summary>
    public static Uri EndpointFromEnvironment() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable(UrlVariable)?.Trim(), UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : GitHubReleases.LatestUrl;

    /// <summary>
    /// Comprueba. Sin <paramref name="force"/>, si la caché es de menos de 24 h (y de la misma URL),
    /// contesta con ella sin ir a la red. Nunca lanza.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(bool force = false, CancellationToken cancellation = default)
    {
        var cached = Cached();
        if (!force && cached is { } c && IsFresh(c)) return c;

        try
        {
            var (ok, latest, error) = await FetchAsync(cancellation);
            if (!ok)
                return new UpdateCheck
                {
                    Status = UpdateStatus.Failed,
                    Installed = Installed,
                    Error = error,
                    Latest = cached?.Latest,
                    CheckedAt = cached?.CheckedAt,
                    FromCache = cached is not null,
                };

            var now = _time.GetUtcNow();
            SaveCache(now, latest);
            return Evaluate(latest, now, fromCache: false);
        }
        catch (Exception ex)
        {
            // Lo que no se esperaba (un bug, la caché sin permisos…): tampoco puede romper a quien llama.
            return new UpdateCheck { Status = UpdateStatus.Failed, Installed = Installed, Error = ex.Message, Latest = cached?.Latest, CheckedAt = cached?.CheckedAt, FromCache = cached is not null };
        }
    }

    /// <summary>
    /// Lo que haya en la caché, sin ir a la red y aunque tenga más de 24 h (para <c>roost status</c>);
    /// null si no hay, es de otra URL o no se entiende.
    /// </summary>
    public UpdateCheck? Cached()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != CacheFormat) return null;
            if (!root.TryGetProperty("source", out var source) || source.GetString() != Endpoint.ToString()) return null;
            if (!root.TryGetProperty("checkedAt", out var at) || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var checkedAt)) return null;
            if (!root.TryGetProperty("release", out var release)) return null;

            ReleaseInfo? latest = null;
            if (release.ValueKind == JsonValueKind.Object)
            {
                if (!GitHubReleases.TryRead(release, out latest, out _) || latest is null) return null;
            }
            else if (release.ValueKind != JsonValueKind.Null) return null;

            return Evaluate(latest, checkedAt, fromCache: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Si una comprobación de la caché sirve aún (menos de 24 h; una fecha futura no vale).</summary>
    public bool IsFresh(UpdateCheck check)
    {
        if (check.CheckedAt is not { } at) return false;
        var age = _time.GetUtcNow() - at;
        return age >= TimeSpan.FromMinutes(-5) && age < CacheFor;
    }

    private UpdateCheck Evaluate(ReleaseInfo? latest, DateTimeOffset checkedAt, bool fromCache)
    {
        if (latest is null)
            return new UpdateCheck { Status = UpdateStatus.NoReleases, Installed = Installed, CheckedAt = checkedAt, FromCache = fromCache };

        if (!SemanticVersion.TryParse(Installed, out var installed))
            return new UpdateCheck
            {
                Status = UpdateStatus.Failed,
                Installed = Installed,
                Latest = latest,
                CheckedAt = checkedAt,
                FromCache = fromCache,
                Error = $"no se reconoce la version instalada ({Installed})",
            };

        return new UpdateCheck
        {
            Status = latest.Version > installed ? UpdateStatus.Available : UpdateStatus.UpToDate,
            Installed = Installed,
            Latest = latest,
            CheckedAt = checkedAt,
            FromCache = fromCache,
        };
    }

    /// <summary>Pregunta a la API. Ok con <c>Latest</c> null: aún no hay ninguna release.</summary>
    private async Task<(bool Ok, ReleaseInfo? Latest, string? Error)> FetchAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        // Los pide la API de GitHub: sin User-Agent contesta 403.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("roost", UserAgentVersion(Installed)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return (false, null, $"{Endpoint.Host} no ha contestado en {_timeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            return (false, null, "comprobacion cancelada");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"sin conexion con {Endpoint.Host} ({ex.Message})");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (true, null, null);

            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && Header(response, "X-RateLimit-Remaining") == "0"))
                return (false, null, "GitHub limita las consultas sin cuenta a 60 por hora; vuelve a probar en un rato");

            if (!response.IsSuccessStatusCode)
                return (false, null, $"{Endpoint.Host} ha contestado {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                return (false, null, $"la respuesta de {Endpoint.Host} llego cortada");
            }

            return GitHubReleases.TryParse(json, out var release, out var error) ? (true, release, null) : (false, null, error);
        }
    }

    private void SaveCache(DateTimeOffset checkedAt, ReleaseInfo? latest)
    {
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("format", CacheFormat);
                w.WriteString("source", Endpoint.ToString());
                w.WriteString("checkedAt", checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                w.WritePropertyName("release");
                if (latest is null) w.WriteNullValue();
                else GitHubReleases.Write(w, latest);
                w.WriteEndObject();
            }
            // Atómico: nunca queda una caché a medias que luego no se entienda.
            var tmp = CacheFile + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tmp, buffer.ToArray());
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin caché se vuelve a preguntar la próxima vez; no es motivo para fallar.
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    // Un product token no admite cualquier carácter: 0.2.0-beta.1 vale; lo raro se cambia por guiones.
    private static string UserAgentVersion(string version)
    {
        var sb = new StringBuilder(version.Length);
        foreach (var c in version) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        return sb.Length == 0 ? "0" : sb.ToString();
    }
}
