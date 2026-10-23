using System.Net;
using System.Text.Json;
using Roost.Core.Platform;

namespace Roost.Core.Audit;

/// <summary>De dónde salen los avisos de una evaluación.</summary>
public enum DatasetKind
{
    /// <summary>El feed firmado de roost-catalog, verificado, de la caché.</summary>
    Feed,

    /// <summary>El dataset que va dentro del binario.</summary>
    Embedded,

    /// <summary>Un fichero que se pasó a mano (<c>roost audit --dataset</c>), sin firma.</summary>
    File,
}

/// <param name="Note">Por qué no es el feed, o el aviso de que el feed es viejo (texto para el usuario, sin tildes).</param>
public sealed record DatasetOrigin(DatasetKind Kind, string? Note = null)
{
    /// <summary>«feed firmado del 2026-10-23 07:17 UTC (hace 0 dias)», «dataset empaquetado del 2026-10-22…».</summary>
    public string Describe(AuditDataset dataset, DateTimeOffset now)
    {
        var days = Math.Max(0, (now - dataset.Generated).Days);
        var when = $"{dataset.Generated.UtcDateTime:yyyy-MM-dd HH:mm} UTC (hace {days} dia{(days == 1 ? "" : "s")})";
        var what = Kind switch
        {
            DatasetKind.Feed => $"feed firmado del {when}",
            DatasetKind.Embedded => $"dataset empaquetado del {when}",
            _ => $"fichero sin firma del {when}",
        };
        return Note is null ? what : $"{what}; {Note}";
    }
}

public enum FeedUpdateStatus
{
    /// <summary>Se ha bajado uno nuevo, con la firma bien.</summary>
    Updated,

    /// <summary>El servidor dice que no ha cambiado (ETag).</summary>
    NotModified,

    /// <summary>No se ha preguntado: el de la caché se comprobó hace menos de <see cref="AdvisoryFeed.MaxAge"/>.</summary>
    Fresh,

    /// <summary>No se ha podido; se sigue con lo que había.</summary>
    Failed,
}

public sealed record FeedUpdate(FeedUpdateStatus Status, string? Detail = null)
{
    public bool Ok => Status != FeedUpdateStatus.Failed;
}

/// <summary>
/// El cliente del feed de avisos (ROOST.md §7.3 y §7.4). Baja <c>advisories.json</c> y su firma,
/// con ETag y como mucho una vez al día, y solo los guarda si la firma es de una clave de
/// <see cref="FeedSignature.TrustedKeys"/> y el feed no es más viejo que el que ya había (que nadie
/// pueda colar uno antiguo). La evaluación sigue siendo en local: sin red se usa el último feed
/// bueno de la caché o, si no hay o es más viejo, el dataset empaquetado. Nunca lanza.
/// </summary>
public sealed class AdvisoryFeed
{
    /// <summary>La rama <c>catalog</c> de este repo, que regenera cada día el workflow del catálogo.</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/get-roost/roost/catalog/advisories.json";

    /// <summary>Otra URL para el feed (un espejo, o pruebas). La firma se comprueba igual.</summary>
    public const string UrlVariable = "ROOST_CATALOG_URL";

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>A partir de aquí, el feed se usa pero se avisa de que es viejo.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>Lo más grande que se acepta (el de hoy pesa unos 50 KB).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>La versión mayor del esquema que entiende este cliente; uno más nuevo se ignora.</summary>
    public const int SupportedSchemaMajor = 1;

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string>? _keys;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="trustedKeys">Para los tests; por defecto, las de <see cref="FeedSignature.TrustedKeys"/>.</param>
    public AdvisoryFeed(RoostPaths paths, HttpClient http, string? url = null, IReadOnlyList<string>? trustedKeys = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _keys = trustedKeys;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Url = url ?? (Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } custom ? custom : DefaultUrl);
        CacheDir = Path.Combine(paths.Cache, "catalog");
    }

    public string Url { get; }

    public string SignatureUrl => Url + ".sig";

    public string CacheDir { get; }

    public string FeedFile => Path.Combine(CacheDir, "advisories.json");

    public string SignatureFile => FeedFile + ".sig";

    private string StateFile => Path.Combine(CacheDir, "state.json");

    /// <summary>Cuándo se preguntó por última vez al servidor con éxito (null si nunca).</summary>
    public DateTimeOffset? LastChecked => ReadState().CheckedAt;

    /// <summary>El feed de la caché, si está, su firma vale y su esquema se entiende; si no, null y el motivo.</summary>
    public AuditDataset? LoadCached(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(FeedFile) || !File.Exists(SignatureFile))
            {
                problem = "aun no se ha bajado el feed";
                return null;
            }
            return Accept(File.ReadAllBytes(FeedFile), File.ReadAllBytes(SignatureFile), out problem);
        }
        catch (IOException ex)
        {
            problem = $"no se pudo leer el feed de la cache: {ex.Message}";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = $"no se pudo leer el feed de la cache: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Lo que hay que evaluar: el feed de la caché si es bueno y no es más viejo que el empaquetado
    /// (un Roost recién actualizado puede traer datos más nuevos que una caché abandonada); si no, el
    /// empaquetado, diciendo por qué.
    /// </summary>
    public (AuditDataset Dataset, DatasetOrigin Origin) LoadBest(AuditDataset? embedded = null)
    {
        embedded ??= AuditDataset.LoadEmbedded();
        var cached = LoadCached(out var problem);
        if (cached is not null && cached.Generated >= embedded.Generated)
        {
            var stale = _now() - cached.Generated > StaleAfter ? "es viejo: no se ha podido actualizar" : null;
            return (cached, new DatasetOrigin(DatasetKind.Feed, stale));
        }
        return (embedded, new DatasetOrigin(DatasetKind.Embedded, cached is not null ? null : problem));
    }

    /// <summary>
    /// Pregunta al servidor si hay un feed nuevo (salvo que el de la caché se comprobara hace menos de
    /// <see cref="MaxAge"/> y no se fuerce) y lo guarda si es bueno.
    /// </summary>
    public async Task<FeedUpdate> UpdateAsync(bool force = false, CancellationToken cancellation = default)
    {
        try
        {
            var cached = LoadCached(out _);
            var state = ReadState();
            if (!force && cached is not null && state.CheckedAt is { } at && _now() - at < MaxAge)
                return new FeedUpdate(FeedUpdateStatus.Fresh);

            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            if (cached is not null && state.ETag is { Length: > 0 } etag && state.Url == Url)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                WriteState(state with { CheckedAt = _now() });
                return new FeedUpdate(FeedUpdateStatus.NotModified);
            }
            if (!response.IsSuccessStatusCode)
                return new FeedUpdate(FeedUpdateStatus.Failed, $"el servidor del feed respondio {(int)response.StatusCode}");
            var feed = await ReadLimitedAsync(response, MaxBytes, cancellation);
            var newETag = response.Headers.ETag?.ToString();

            using var sigResponse = await _http.GetAsync(SignatureUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!sigResponse.IsSuccessStatusCode)
                return new FeedUpdate(FeedUpdateStatus.Failed, $"la firma del feed no se pudo bajar ({(int)sigResponse.StatusCode})");
            var signature = await ReadLimitedAsync(sigResponse, 1024, cancellation);

            if (Accept(feed, signature, out var problem) is not { } dataset)
                return new FeedUpdate(FeedUpdateStatus.Failed, problem);
            if (cached is not null && dataset.Generated < cached.Generated)
                return new FeedUpdate(FeedUpdateStatus.Failed, "el feed del servidor es mas viejo que el que ya habia: se ignora");

            Directory.CreateDirectory(CacheDir);
            WriteAtomic(SignatureFile, signature);
            WriteAtomic(FeedFile, feed);
            WriteState(new FeedState(Url, newETag, _now()));
            return new FeedUpdate(FeedUpdateStatus.Updated);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var reason = ex is TaskCanceledException ? "no respondio a tiempo" : ex.Message;
            return new FeedUpdate(FeedUpdateStatus.Failed, $"no se pudo bajar el feed: {reason}");
        }
    }

    /// <summary>El dataset de <paramref name="feed"/> si la firma vale y el esquema se entiende.</summary>
    private AuditDataset? Accept(byte[] feed, byte[] signature, out string? problem)
    {
        problem = null;
        if (!FeedSignature.Verify(feed, signature, _keys))
        {
            problem = "la firma del feed no es valida: se ignora";
            return null;
        }
        AuditDataset dataset;
        try
        {
            dataset = AuditDataset.Load(new MemoryStream(feed));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            problem = $"el feed no se puede leer: {ex.Message}";
            return null;
        }
        var major = dataset.SchemaVersion.Split('.')[0];
        if (!int.TryParse(major, out var m) || m > SupportedSchemaMajor)
        {
            problem = $"el feed usa el esquema {dataset.SchemaVersion}, mas nuevo que este Roost: actualiza Roost";
            return null;
        }
        return dataset;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("el feed es demasiado grande");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("el feed es demasiado grande");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void WriteAtomic(string file, byte[] bytes)
    {
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, file, overwrite: true);
    }

    private sealed record FeedState(string? Url = null, string? ETag = null, DateTimeOffset? CheckedAt = null);

    private FeedState ReadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<FeedState>(File.ReadAllText(StateFile)) ?? new FeedState()
                : new FeedState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new FeedState();
        }
    }

    private void WriteState(FeedState state)
    {
        Directory.CreateDirectory(CacheDir);
        WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state));
    }
}
