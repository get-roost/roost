using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Roost.Core.Models;
using Roost.Core.Node;

namespace Roost.Core.Install;

/// <summary>
/// Un zip de Node para Windows x64 (<c>node-v&lt;versión&gt;-win-x64.zip</c> de nodejs.org) con el
/// SHA-256 que tiene que dar.
/// </summary>
/// <param name="Size">Lo que ocupa, en bytes (para el progreso de la descarga); null si no se sabe.</param>
public sealed record NodeBuild(string Version, Uri Url, string Sha256, long? Size = null)
{
    /// <summary>
    /// true si va fijado en Roost (<see cref="NodeCatalog.Lts"/>: el SHA-256 está en el binario,
    /// comprobado al fijarlo con la firma GPG del <c>SHASUMS256.txt</c>). false si sale del
    /// <c>SHASUMS256.txt</c> de esa versión, leído de nodejs.org al instalar.
    /// </summary>
    public bool Pinned { get; init; }

    /// <summary>La versión de npm que trae (la de <c>index.json</c>), si se sabe.</summary>
    public string? Npm { get; init; }

    public string FileName => NodeCatalog.ZipName(Version);

    public string Label => $"Node {Version}";

    public ComponentVersion ParsedVersion => ComponentVersion.TryParse(Version, out var v) ? v : default;

    public int Major => ParsedVersion.Major;
}

/// <summary>Cómo está una rama de Node según su calendario (<c>nodejs/Release</c>, schedule.json).</summary>
public enum NodeBranchStatus
{
    /// <summary>Recién salida y aún no LTS (o una impar, que nunca lo es).</summary>
    Current,

    ActiveLts,

    MaintenanceLts,

    /// <summary>Sin parches: hay que pasar a otra.</summary>
    Eol,
}

/// <summary>Una rama de Node y sus fechas (las de schedule.json de nodejs/Release).</summary>
/// <param name="Lts">Cuándo pasa a LTS; null en las impares, que no lo son nunca.</param>
public sealed record NodeBranch(int Major, string? Codename, DateOnly Start, DateOnly? Lts, DateOnly Maintenance, DateOnly End)
{
    /// <summary>true si en <paramref name="today"/> ya es una LTS (activa, de mantenimiento o EOL).</summary>
    public bool IsLtsOn(DateOnly today) => Lts is { } lts && today >= lts;

    public NodeBranchStatus StatusOn(DateOnly today) =>
        today >= End ? NodeBranchStatus.Eol
        : today >= Maintenance && Lts is not null ? NodeBranchStatus.MaintenanceLts
        : IsLtsOn(today) ? NodeBranchStatus.ActiveLts
        : NodeBranchStatus.Current;
}

/// <summary>Una versión de <c>index.json</c> de nodejs.org: si es LTS (su nombre) y si hay zip para Windows x64.</summary>
public sealed record NodeRelease(string Version, string? Lts, bool HasWindowsZip, bool Security, string? Npm)
{
    public ComponentVersion ParsedVersion => ComponentVersion.TryParse(Version, out var v) ? v : default;
}

/// <summary>
/// Node para Windows, de nodejs.org (ROOST.md: N1, Node gratis). Las LTS que ofrece Roost van fijadas
/// a mano con su SHA-256, como <c>GitCatalog</c> con MinGit: así lo que se instala no depende de lo
/// que diga nodejs.org ese día. Cualquier otra versión (la que pida un <c>.nvmrc</c> exacto, o una
/// rama que Roost no fija) se puede instalar igual, verificada con el <c>SHASUMS256.txt</c> de esa
/// misma versión (que se baja de nodejs.org, como el <c>releases.json</c> de windows.php.net).
/// </summary>
/// <remarks>
/// <para>
/// <b>La firma GPG.</b> Node firma cada <c>SHASUMS256.txt</c> (en claro, <c>.asc</c>, y aparte,
/// <c>.sig</c>) con la clave de quien publica la versión. Roost no la comprueba al instalar: .NET no
/// trae OpenPGP (haría falta BouncyCastle en Core, que es un paquete de NuGet) y varias claves de los
/// publicadores son Ed25519, que .NET tampoco tiene; además habría que mantener el llavero. Se comprueba
/// al fijar: el SHA-256 que va en el binario sale de un <c>SHASUMS256.txt</c> con la firma buena, así
/// que para las versiones fijadas la garantía es la misma. Las no fijadas se fían de nodejs.org por
/// HTTPS, como PHP de windows.php.net.
/// </para>
/// <para>
/// <b>Para cambiar de versión</b> (Node saca parches de seguridad casi cada mes; hay que subirlas con
/// Roost): en <c>https://nodejs.org/dist/index.json</c>, la última de cada rama LTS con
/// <c>win-x64-zip</c> (y su <c>npm</c>); el calendario, en
/// <c>https://raw.githubusercontent.com/nodejs/Release/main/schedule.json</c>. En una carpeta vacía:
/// <c>SHASUMS256.txt</c>, <c>SHASUMS256.txt.sig</c> y el zip de
/// <c>https://nodejs.org/dist/v&lt;versión&gt;/</c>; el llavero de
/// <c>https://github.com/nodejs/release-keys/raw/main/gpg-only-active-keys/pubring.kbx</c>;
/// <c>gpgv --keyring ./pubring.kbx SHASUMS256.txt.sig SHASUMS256.txt</c> tiene que dar «Good
/// signature» de una clave que esté también en la lista «Release keys» del README de nodejs/node;
/// <c>sha256sum -c --ignore-missing SHASUMS256.txt</c> con el zip bajado; y fijar aquí la versión, el
/// SHA-256, el tamaño y el npm. Cuando una rama pasa a LTS (la 26, el 28-oct-2026), se añade y la más
/// nueva va la primera; la que llega a EOL se quita (se puede seguir instalando sin fijar).
/// </para>
/// </remarks>
public static partial class NodeCatalog
{
    public const string DistUrl = "https://nodejs.org/dist/";

    // node-v24.22.1-win-x64.zip (37.748.064 bytes), LTS «Krypton», verificado el 24-oct-2026: el
    // SHASUMS256.txt de v24.22.1 tiene la firma buena (gpgv, .sig y .asc) de la clave EdDSA
    // 5BE8A3F6C8A5C01D106C0AD820B1A390B168D356 (Antoine du Hamel), que está en el llavero de
    // nodejs/release-keys y en la lista del README de nodejs/node; y el SHA-256 del zip bajado
    // coincide con el de ese SHASUMS256.txt. Trae npm 11.19.1.
    public static readonly NodeBuild V24 = new(
        "24.22.1",
        new Uri($"{DistUrl}v24.22.1/node-v24.22.1-win-x64.zip"),
        "d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5",
        Size: 37_748_064)
    { Pinned = true, Npm = "11.19.1" };

    // node-v22.23.4-win-x64.zip (35.604.229 bytes), LTS «Jod», verificado el 24-oct-2026 igual que la
    // 24 (misma clave de firma, la del SHASUMS256.txt de v22.23.4). Trae npm 10.9.9.
    public static readonly NodeBuild V22 = new(
        "22.23.4",
        new Uri($"{DistUrl}v22.23.4/node-v22.23.4-win-x64.zip"),
        "cbc74b5e0982c9dbb41525fd8fb5dcd32fbee237863f7829072f4639a1ce5f20",
        Size: 35_604_229)
    { Pinned = true, Npm = "10.9.9" };

    /// <summary>
    /// Las LTS que ofrece Roost, la más nueva primero (es la de <c>roost node install lts</c> y la de la
    /// app). El 24-oct-2026: la 24 (en mantenimiento desde el 20-oct) y la 22 (mantenimiento hasta el
    /// 30-abr-2027). La 26 es LTS el 28-oct-2026.
    /// </summary>
    public static readonly IReadOnlyList<NodeBuild> Lts = [V24, V22];

    /// <summary>La que se instala si no se dice otra.</summary>
    public static NodeBuild Default => Lts[0];

    /// <summary>
    /// El calendario de las ramas recientes (schedule.json de nodejs/Release, 24-oct-2026). Las impares
    /// no pasan nunca a LTS y duran unos ocho meses.
    /// </summary>
    public static readonly IReadOnlyList<NodeBranch> Branches =
    [
        new(16, "Gallium", D("2021-04-20"), D("2021-10-26"), D("2022-10-18"), D("2023-09-11")),
        new(18, "Hydrogen", D("2022-04-19"), D("2022-10-25"), D("2023-10-18"), D("2025-04-30")),
        new(19, null, D("2022-10-18"), null, D("2023-04-01"), D("2023-06-01")),
        new(20, "Iron", D("2023-04-18"), D("2023-10-24"), D("2024-10-22"), D("2026-04-30")),
        new(21, null, D("2023-10-17"), null, D("2024-04-01"), D("2024-06-01")),
        new(22, "Jod", D("2024-04-24"), D("2024-10-29"), D("2025-10-21"), D("2027-04-30")),
        new(23, null, D("2024-10-16"), null, D("2025-04-01"), D("2025-06-01")),
        new(24, "Krypton", D("2025-05-06"), D("2025-10-28"), D("2026-10-20"), D("2028-04-30")),
        new(25, null, D("2025-10-15"), null, D("2026-04-01"), D("2026-06-01")),
        new(26, null, D("2026-05-05"), D("2026-10-28"), D("2027-10-20"), D("2029-04-30")),
    ];

    // Los nombres de las LTS (para lts/<nombre> en un .nvmrc), también las viejas.
    private static readonly IReadOnlyDictionary<string, int> Codenames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = 4, ["boron"] = 6, ["carbon"] = 8, ["dubnium"] = 10, ["erbium"] = 12, ["fermium"] = 14,
        ["gallium"] = 16, ["hydrogen"] = 18, ["iron"] = 20, ["jod"] = 22, ["krypton"] = 24,
    };

    private static DateOnly D(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public static NodeBranch? Branch(int major) => Branches.FirstOrDefault(b => b.Major == major);

    /// <summary>La versión mayor de una LTS por su nombre (<c>jod</c> → 22), o null.</summary>
    public static int? MajorOfCodename(string name) => Codenames.TryGetValue(name.Trim(), out var major) ? major : null;

    /// <summary>
    /// true si la rama es LTS en <paramref name="today"/>. Una que no está en el calendario (más nueva
    /// que esta Roost) cuenta como LTS si es par: todas las pares lo acaban siendo.
    /// </summary>
    public static bool IsLts(int major, DateOnly today) =>
        Branch(major) is { } branch ? branch.IsLtsOn(today) : major % 2 == 0 && major > Branches.Max(b => b.Major);

    /// <summary>Cómo está la rama; null si no está en el calendario.</summary>
    public static NodeBranchStatus? Status(int major, DateOnly today) => Branch(major)?.StatusOn(today);

    /// <summary>
    /// Lo que se cuenta de una rama (sin tildes, para la CLI): «LTS (Krypton), en mantenimiento hasta el
    /// 2028-04-30», «EOL desde el 2026-04-30: sin parches de seguridad», «Current, no es LTS».
    /// </summary>
    public static string StatusText(int major, DateOnly today)
    {
        if (Branch(major) is not { } branch) return major % 2 == 0 ? "rama que esta Roost no conoce aun" : "rama impar: nunca es LTS";
        var name = branch.Codename is { } c ? $" ({c})" : "";
        return branch.StatusOn(today) switch
        {
            NodeBranchStatus.Eol => $"EOL desde el {branch.End:yyyy-MM-dd}: sin parches de seguridad",
            NodeBranchStatus.MaintenanceLts => $"LTS{name}, en mantenimiento hasta el {branch.End:yyyy-MM-dd}",
            NodeBranchStatus.ActiveLts => $"LTS{name} activa; mantenimiento desde el {branch.Maintenance:yyyy-MM-dd}, hasta el {branch.End:yyyy-MM-dd}",
            _ => branch.Lts is { } lts ? $"Current: pasa a LTS el {lts:yyyy-MM-dd}" : $"Current (impar, nunca es LTS): hasta el {branch.End:yyyy-MM-dd}",
        };
    }

    public static string ZipName(string version) => $"node-v{version}-win-x64.zip";

    public static Uri ZipUrl(string version) => new($"{DistUrl}v{version}/{ZipName(version)}");

    public static Uri ShasumsUrl(string version) => new($"{DistUrl}v{version}/SHASUMS256.txt");

    public static Uri IndexUrl => new($"{DistUrl}index.json");

    /// <summary>
    /// La versión fijada en Roost que vale para <paramref name="spec"/> (la más nueva de las que valen),
    /// sin ir a la red; null si ninguna (entonces, <see cref="ResolveAsync"/> pregunta a nodejs.org).
    /// </summary>
    public static NodeBuild? ResolvePinned(NodeSpec spec, DateOnly? today = null)
    {
        // «node», «latest»: la más nueva de nodejs.org, que no tiene por qué ser una LTS de Roost.
        if (spec.Kind == NodeSpecKind.Newest) return null;
        var day = today ?? Today;
        return Lts.Where(b => spec.Matches(b.ParsedVersion, day)).OrderByDescending(b => b.ParsedVersion).FirstOrDefault();
    }

    /// <summary>
    /// El zip de Node para <paramref name="spec"/>: el fijado si alguno vale; si no, una versión exacta
    /// se busca en su <c>SHASUMS256.txt</c> y lo demás (<c>20</c>, <c>^20.11</c>, <c>lts/iron</c>,
    /// <c>latest</c>) en <c>index.json</c>, y luego su <c>SHASUMS256.txt</c>. Lanza
    /// <see cref="RoostException"/> si no hay ninguna, o si nodejs.org no contesta.
    /// </summary>
    public static async Task<NodeBuild> ResolveAsync(HttpClient http, NodeSpec spec, CancellationToken cancellation = default, DateOnly? today = null)
    {
        if (spec.Kind == NodeSpecKind.System)
            throw new RoostException("'system' es el Node que ya tenga la maquina: Roost no lo instala ni lo usa.");
        var day = today ?? Today;
        if (ResolvePinned(spec, day) is { } pinned) return pinned;

        string version;
        string? npm = null;
        if (spec.Exact is { } exact)
        {
            version = exact.ToString();
        }
        else
        {
            var releases = ParseIndex(await GetTextAsync(http, IndexUrl, cancellation));
            var match = releases
                .Where(r => r.HasWindowsZip && ComponentVersion.TryParse(r.Version, out _))
                .Where(r => spec.Kind == NodeSpecKind.LtsLatest ? r.Lts is not null : spec.Matches(r.ParsedVersion, day))
                .OrderByDescending(r => r.ParsedVersion)
                .FirstOrDefault()
                ?? throw new RoostException($"nodejs.org no tiene ninguna version de Node para Windows x64 que case con '{spec.Text}'.");
            version = match.Version;
            npm = match.Npm;
        }

        var sums = await GetTextAsync(http, ShasumsUrl(version), cancellation, notFound: $"No existe Node {version} en nodejs.org (mira las versiones en {DistUrl}).");
        var sha = ParseShasums(sums, ZipName(version))
            ?? throw new RoostException($"El SHASUMS256.txt de Node {version} no trae {ZipName(version)}: esa version no tiene zip para Windows x64.");
        return new NodeBuild(version, ZipUrl(version), sha) { Pinned = false, Npm = npm };
    }

    private static async Task<string> GetTextAsync(HttpClient http, Uri url, CancellationToken cancellation, string? notFound = null)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && notFound is not null) throw new RoostException(notFound);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellation);
        }
        catch (HttpRequestException ex)
        {
            throw new RoostException($"No se pudo consultar {url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new RoostException($"{url} no ha contestado a tiempo.", ex);
        }
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$")]
    private static partial Regex ShasumLine();

    /// <summary>PURO: el SHA-256 (en minúsculas) de <paramref name="fileName"/> en un <c>SHASUMS256.txt</c>, o null.</summary>
    public static string? ParseShasums(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var m = ShasumLine().Match(line.Trim());
            if (m.Success && m.Groups[2].Value == fileName) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>PURO: las versiones de <c>index.json</c> de nodejs.org (lo que no se entiende, se salta).</summary>
    public static IReadOnlyList<NodeRelease> ParseIndex(string json)
    {
        var releases = new List<NodeRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) continue;
                var version = v.GetString()!.TrimStart('v', 'V');
                if (!ComponentVersion.TryParse(version, out _)) continue;
                var lts = item.TryGetProperty("lts", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                var zip = item.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                          && f.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "win-x64-zip");
                var security = item.TryGetProperty("security", out var s) && s.ValueKind == JsonValueKind.True;
                var npm = item.TryGetProperty("npm", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                releases.Add(new NodeRelease(version, lts, zip, security, npm));
            }
        }
        catch (JsonException)
        {
            throw new RoostException($"{IndexUrl} no es el JSON que se esperaba.");
        }
        return releases;
    }
}
