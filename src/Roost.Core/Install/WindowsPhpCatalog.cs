using System.Text.Json;
using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Install;

/// <summary>
/// Catálogo de PHP para Windows leído directamente del releases.json oficial. Solo trae
/// la ÚLTIMA versión de cada rama (las anteriores viven en /archives/, fuera de este
/// JSON), así que Roost solo instala esas. Es la fuente provisional hasta que exista el
/// feed firmado roost-catalog (ROOST.md §5.4); los comandos no cambiarán al sustituirla.
/// </summary>
public static partial class WindowsPhpCatalog
{
    /// <summary>URL pública del catálogo; redirige a downloads.php.net/~windows/releases/.</summary>
    public static readonly Uri ReleasesUrl = new("https://windows.php.net/downloads/releases/releases.json");

    // Claves de variante dentro de cada rama: "nts-vs16-x64", "ts-vc15-x86"…
    [GeneratedRegex(@"^(ts|nts)-(v[cs]\d+)-(x64|x86)$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantPattern();

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex BranchPattern();

    /// <summary>
    /// Descarga y parsea el catálogo. Los zips se resuelven contra la URL final, tras las
    /// redirecciones, para que un cambio de servidor de php.net no rompa las descargas.
    /// </summary>
    public static async Task<IReadOnlyList<PhpBuild>> FetchAsync(HttpClient http, CancellationToken cancellation = default)
    {
        using var response = await http.GetAsync(ReleasesUrl, cancellation);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellation);
        return Parse(json, response.RequestMessage?.RequestUri ?? ReleasesUrl);
    }

    /// <summary>
    /// Parsea el releases.json. PURO: sin red ni disco. Ignora lo que no es un zip de PHP
    /// (código fuente, test pack, debug/devel pack) y las entradas incompletas.
    /// </summary>
    public static IReadOnlyList<PhpBuild> Parse(string json, Uri baseUri)
    {
        var builds = new List<PhpBuild>();
        using var doc = JsonDocument.Parse(json);

        foreach (var branch in doc.RootElement.EnumerateObject())
        {
            if (branch.Value.ValueKind != JsonValueKind.Object) continue;
            if (!ComponentVersion.TryParse(GetString(branch.Value, "version"), out var version)) continue;

            foreach (var entry in branch.Value.EnumerateObject())
            {
                var m = VariantPattern().Match(entry.Name);
                if (!m.Success || entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("zip", out var zip) || zip.ValueKind != JsonValueKind.Object) continue;

                var path = GetString(zip, "path");
                var sha = GetString(zip, "sha256");
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha)) continue;

                builds.Add(new PhpBuild
                {
                    Version = version,
                    Branch = branch.Name,
                    ThreadSafe = m.Groups[1].Value.Equals("ts", StringComparison.OrdinalIgnoreCase),
                    Compiler = m.Groups[2].Value.ToLowerInvariant(),
                    Architecture = m.Groups[3].Value.ToLowerInvariant(),
                    FileName = path,
                    Url = new Uri(baseUri, path),
                    Sha256 = sha.Trim().ToLowerInvariant(),
                    SizeText = GetString(zip, "size"),
                });
            }
        }

        return builds;
    }

    /// <summary>
    /// Elige el build que pide el usuario. <paramref name="spec"/> es una rama ("8.3",
    /// que da la última de esa rama) o una versión exacta ("8.3.36"), que tiene que ser
    /// la última de su rama porque el catálogo no trae otras.
    /// </summary>
    public static PhpBuild Resolve(
        IEnumerable<PhpBuild> builds,
        string spec,
        bool threadSafe = false,
        string architecture = "x64")
    {
        var all = builds.ToList();
        spec = spec.Trim();

        string branch;
        ComponentVersion? exact = null;
        if (BranchPattern().IsMatch(spec))
        {
            branch = spec;
        }
        else if (ComponentVersion.TryParse(spec, out var parsed))
        {
            exact = parsed;
            branch = $"{parsed.Major}.{parsed.Minor}";
        }
        else
        {
            throw new RoostException(
                $"'{spec}' no es una version de PHP: usa una rama (8.3) o una version (8.3.36).");
        }

        var inBranch = all.Where(b => b.Branch == branch).ToList();
        if (inBranch.Count == 0)
        {
            var branches = string.Join(", ", all.Select(b => b.Branch).Distinct());
            throw new RoostException(
                $"La rama {branch} no esta en el catalogo de windows.php.net. Ramas disponibles: {branches}.");
        }

        var latest = inBranch[0].Version;
        if (exact is { } wanted && wanted.CompareTo(latest) != 0)
        {
            throw new RoostException(
                $"windows.php.net solo publica la ultima version de cada rama: para la {branch} es {latest}. "
                + $"Instala {latest} (o pide la rama: {branch}).");
        }

        return inBranch.FirstOrDefault(b =>
                   b.ThreadSafe == threadSafe
                   && b.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
               ?? throw new RoostException(
                   $"No hay build {(threadSafe ? "TS" : "NTS")} {architecture} de PHP {latest} en el catalogo.");
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
