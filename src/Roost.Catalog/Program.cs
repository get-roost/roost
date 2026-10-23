using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Roost.Catalog;
using Roost.Core.Audit;

// roost-catalog: genera, firma y comprueba el feed de avisos de Roost (ROOST.md §7.3).
//
//   roost-catalog keygen --out <clave.pem>
//   roost-catalog pubkey --key <clave.pem>
//   roost-catalog generate --catalog <dir> --out <dir> [--previous <advisories.json>] [--summary <fichero>]
//                          [--key <clave.pem> | env ROOST_CATALOG_KEY] [--unsigned]
//   roost-catalog verify <dir>
//
// Codigos de salida: 0 bien, 1 errores en los datos (no se publica), 2 uso o E/S.

try
{
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(args),
        "pubkey" => PubKey(args),
        "generate" => await GenerateAsync(args),
        "verify" => Verify(args),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine($"roost-catalog: {ex.Message}");
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine("uso: roost-catalog keygen --out <clave.pem> | generate --catalog <dir> --out <dir> [...] | verify <dir>");
    return 2;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Una clave nueva. La privada no sale de aqui: va al secreto ROOST_CATALOG_KEY del repo; la publica,
// a FeedSignature.TrustedKeys.
static int KeyGen(string[] args)
{
    var file = Option(args, "--out") ?? throw new InvalidDataException("falta --out <clave.pem>");
    if (File.Exists(file)) throw new IOException($"{file} ya existe: no se sobrescribe una clave");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(file, key.ExportPkcs8PrivateKeyPem() + "\n");
    Console.WriteLine($"Clave privada: {Path.GetFullPath(file)} (al secreto ROOST_CATALOG_KEY; no la subas al repo)");
    Console.WriteLine($"Clave publica (a FeedSignature.TrustedKeys): {FeedSignature.PublicKeyOf(key)}");
    return 0;
}

// La clave publica de una privada (para TrustedKeys, o al rotar).
static int PubKey(string[] args)
{
    using var key = LoadKey(Option(args, "--key") ?? throw new InvalidDataException("falta --key <clave.pem>"), unsigned: false)!;
    Console.WriteLine(FeedSignature.PublicKeyOf(key));
    return 0;
}

static async Task<int> GenerateAsync(string[] args)
{
    var catalogDir = Option(args, "--catalog") ?? "catalog";
    var outDir = Option(args, "--out") ?? throw new InvalidDataException("falta --out <dir>");
    var previousFile = Option(args, "--previous");
    var summaryFile = Option(args, "--summary");
    var unsigned = args.Contains("--unsigned");

    // La clave, antes de ir a la red: sin ella no se publica nada.
    using var key = LoadKey(Option(args, "--key"), unsigned);
    if (key is not null && !FeedSignature.TrustedKeys.Contains(FeedSignature.PublicKeyOf(key)) && !args.Contains("--allow-untrusted-key"))
        throw new InvalidDataException("la clave no esta en FeedSignature.TrustedKeys: ningun Roost aceptaria este feed");

    var meta = ManualData.LoadMeta(catalogDir);
    var manual = ManualData.LoadAdvisories(catalogDir);
    AuditDataset? previous = null;
    if (previousFile is not null && File.Exists(previousFile))
        previous = AuditDataset.LoadFile(previousFile);

    using var http = CreateHttp();
    var cycles = new Dictionary<string, IReadOnlyList<EolCycle>>();
    foreach (var (product, info) in meta.Products)
        cycles[product] = Sources.ParseEndOfLife(await GetAsync(http, $"https://endoflife.date/api/{info.EndOfLife}.json"));

    var now = DateTimeOffset.UtcNow;
    var phpLatest = new Dictionary<string, string>();
    if (cycles.TryGetValue("php", out var phpCycles))
    {
        foreach (var cycle in phpCycles.Where(c => c.Branch is not null && c.ToBranch(DateOnly.FromDateTime(now.UtcDateTime)).Status != "eol"))
            if (Sources.ParsePhpNetLatest(await GetAsync(http, $"https://www.php.net/releases/index.php?json&version={cycle.Branch}")) is { } v)
                phpLatest[cycle.Branch!] = v;
    }

    var skipped = new List<string>();
    var phpSrc = new List<SourceAdvisory>();
    string? page = "https://api.github.com/repos/php/php-src/security-advisories?per_page=100&state=published";
    while (page is not null)
    {
        using var response = await SendAsync(http, page, github: true);
        phpSrc.AddRange(Sources.ParsePhpSrcAdvisories(await response.Content.ReadAsStringAsync(), skipped));
        page = NextPage(response);
    }

    var (kev, kevVersion) = Sources.ParseKev(await GetAsync(http, "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json"));

    var result = CatalogBuilder.Build(new CatalogInputs
    {
        Meta = meta,
        Cycles = cycles,
        PhpLatest = phpLatest,
        PhpSrc = phpSrc,
        Skipped = skipped,
        Kev = kev,
        KevVersion = kevVersion,
        Manual = manual,
        Previous = previous,
        Now = now,
    });

    var summary = CatalogBuilder.Summary(result);
    Console.WriteLine(summary);
    if (summaryFile is not null) File.AppendAllText(summaryFile, summary);
    // Las anotaciones de GitHub Actions: se ven en la pagina del run sin abrir el log.
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    {
        foreach (var e in result.Errors) Console.WriteLine($"::error::{e}");
        foreach (var h in result.Held) Console.WriteLine($"::warning::Retenido: {h}");
    }
    if (result.Errors.Count > 0) return 1;

    Directory.CreateDirectory(outDir);
    var json = CatalogBuilder.Serialize(result.Dataset);
    // Comprobacion de ida y vuelta: lo que se publica lo tiene que poder leer Roost.
    AuditDataset.Load(new MemoryStream(json));
    File.WriteAllBytes(Path.Combine(outDir, "advisories.json"), json);
    if (key is not null)
    {
        File.WriteAllBytes(Path.Combine(outDir, "advisories.json.sig"), FeedSignature.Sign(json, key));
        File.WriteAllText(Path.Combine(outDir, "roost-catalog.pub.pem"), FeedSignature.ToPem(FeedSignature.PublicKeyOf(key)));
    }
    File.WriteAllText(Path.Combine(outDir, "README.md"), FeedReadme);
    // Que git no toque ni un byte (fin de linea incluido): la firma es de los bytes exactos.
    File.WriteAllText(Path.Combine(outDir, ".gitattributes"), "* -text\n");
    Console.WriteLine($"Escrito en {Path.GetFullPath(outDir)}{(key is null ? " (SIN FIRMAR)" : "")}");
    return 0;
}

static int Verify(string[] args)
{
    var dir = args.Length > 1 ? args[1] : throw new InvalidDataException("falta el directorio del feed");
    var json = File.ReadAllBytes(Path.Combine(dir, "advisories.json"));
    var sig = File.ReadAllBytes(Path.Combine(dir, "advisories.json.sig"));
    if (!FeedSignature.Verify(json, sig))
    {
        Console.Error.WriteLine("La firma NO es de una clave de confianza de Roost.");
        return 1;
    }
    var dataset = AuditDataset.Load(new MemoryStream(json));
    Console.WriteLine($"Firma correcta. Feed del {dataset.Generated:yyyy-MM-dd HH:mm} UTC, esquema {dataset.SchemaVersion}, {dataset.Advisories.Count} avisos.");
    return 0;
}

static ECDsa? LoadKey(string? file, bool unsigned)
{
    var pem = file is not null ? File.ReadAllText(file) : Environment.GetEnvironmentVariable("ROOST_CATALOG_KEY");
    if (string.IsNullOrWhiteSpace(pem))
        return unsigned ? null : throw new InvalidDataException("no hay clave (--key o ROOST_CATALOG_KEY); para probar sin firmar, --unsigned");
    var key = ECDsa.Create();
    key.ImportFromPem(pem);
    if (key.KeySize != 256) throw new InvalidDataException("la clave no es ECDSA P-256");
    return key;
}

static HttpClient CreateHttp()
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("roost-catalog (+https://github.com/get-roost/roost)");
    return http;
}

static async Task<string> GetAsync(HttpClient http, string url)
{
    using var response = await SendAsync(http, url, github: false);
    return await response.Content.ReadAsStringAsync();
}

// Tres intentos con espera: las fuentes son publicas y a veces fallan un momento.
static async Task<HttpResponseMessage> SendAsync(HttpClient http, string url, bool github)
{
    for (var attempt = 1; ; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (github)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        try
        {
            var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return response;
            var status = (int)response.StatusCode;
            response.Dispose();
            if (attempt == 3 || status is >= 400 and < 500 and not 429)
                throw new HttpRequestException($"{url} respondio {status}");
        }
        catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
        {
        }
        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
    }
}

static string? NextPage(HttpResponseMessage response)
{
    if (!response.Headers.TryGetValues("Link", out var links)) return null;
    var match = Regex.Match(string.Join(",", links), "<([^>]+)>;\\s*rel=\"next\"");
    return match.Success ? match.Groups[1].Value : null;
}

partial class Program
{
    private const string FeedReadme = """
        # roost-catalog

        El feed de avisos de seguridad y ciclos de vida que usa Roost para `roost audit`
        y la pantalla Auditoría (ROOST.md §7.3). Lo regenera cada día el workflow
        `catalog.yml` de la rama `main` a partir de endoflife.date, php.net, los avisos
        de GitHub de `php/php-src` y el catálogo KEV de CISA, más los avisos revisados a
        mano de `catalog/advisories/`.

        - `advisories.json`: el feed.
        - `advisories.json.sig`: su firma, ECDSA P-256 sobre SHA-256 (DER).
        - `roost-catalog.pub.pem`: la clave pública (Roost la lleva dentro).

        Comprobarlo sin Roost:

            openssl dgst -sha256 -verify roost-catalog.pub.pem -signature advisories.json.sig advisories.json

        No edites esta rama a mano: se sobrescribe cada día.

        """;
}
