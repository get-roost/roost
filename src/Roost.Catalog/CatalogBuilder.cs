using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Catalog;

/// <summary>Todo lo que se ha leído de las fuentes, ya parseado, más lo escrito a mano y el feed anterior.</summary>
public sealed record CatalogInputs
{
    public required CatalogMeta Meta { get; init; }

    /// <summary>Los ciclos de endoflife.date, por clave de producto del dataset.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<EolCycle>> Cycles { get; init; }

    /// <summary>La última de cada rama de PHP con soporte según php.net (manda sobre endoflife.date si es más nueva).</summary>
    public IReadOnlyDictionary<string, string> PhpLatest { get; init; } = new Dictionary<string, string>();

    public required IReadOnlyList<SourceAdvisory> PhpSrc { get; init; }

    /// <summary>Lo que el lector de GHSA no ha podido leer.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public required IReadOnlySet<string> Kev { get; init; }
    public string KevVersion { get; init; } = "";
    public IReadOnlyList<ManualAdvisory> Manual { get; init; } = [];

    /// <summary>El feed publicado ahora mismo, para no publicar uno que haya perdido datos de golpe.</summary>
    public AuditDataset? Previous { get; init; }

    public required DateTimeOffset Now { get; init; }
}

public sealed class CatalogResult
{
    public required AuditDataset Dataset { get; init; }

    /// <summary>Si hay alguno, no se publica.</summary>
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>Avisos automáticos que se quedan fuera hasta que alguien los mire (ver <see cref="CatalogBuilder"/>).</summary>
    public List<string> Held { get; } = [];

    /// <summary>Ids que no estaban en el feed anterior.</summary>
    public List<string> New { get; } = [];
}

/// <summary>
/// Monta el feed (puro). Las reglas que protegen de un falso rojo, que quema más confianza que un
/// ámbar honesto (ROOST.md §7.5):
/// <list type="bullet">
/// <item>un aviso automático que dejaría en rojo la última versión de una rama con soporte se
/// retiene: o no hay parche todavía o se ha leído mal, y lo tiene que mirar una persona (que lo
/// confirma con un fichero en <c>catalog/advisories/</c>);</item>
/// <item>no se publica un feed que haya perdido productos, ramas o más del 20 % de los avisos del
/// anterior (una API que responde a medias), ni uno con un rango escrito a mano que no se lee.</item>
/// </list>
/// </summary>
public static class CatalogBuilder
{
    public const string SchemaVersion = "1.0.0";

    public static CatalogResult Build(CatalogInputs input)
    {
        var today = DateOnly.FromDateTime(input.Now.UtcDateTime);
        var errors = new List<string>();
        var warnings = new List<string>();
        var held = new List<string>();

        // Productos y ramas.
        var products = new Dictionary<string, ProductInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, meta) in input.Meta.Products)
        {
            if (!input.Cycles.TryGetValue(key, out var cycles) || cycles.Count == 0)
            {
                errors.Add($"endoflife.date no ha dado ciclos de {key}");
                continue;
            }
            var branches = new Dictionary<string, BranchInfo>();
            foreach (var cycle in cycles
                         .Where(c => c.Branch is not null)
                         .OrderBy(c => EolCycle.SortKey(c.Branch!)))
            {
                var branch = cycle.ToBranch(today);
                if (key == "php" && input.PhpLatest.TryGetValue(cycle.Branch!, out var phpNet)
                    && (branch.Latest is null || Newer(phpNet, branch.Latest)))
                {
                    branch = new BranchInfo { Latest = phpNet, Eol = branch.Eol, SecurityUntil = branch.SecurityUntil, Status = branch.Status };
                }
                branches[cycle.Branch!] = branch;
            }
            products[key] = new ProductInfo { DisplayName = meta.DisplayName, Branches = branches };
        }

        // Avisos automáticos (lo más nuevo primero), uno por id.
        var advisories = new List<Advisory>();
        foreach (var source in input.PhpSrc.OrderByDescending(s => s.Published))
        {
            var a = source.Advisory;
            if (advisories.FindIndex(x => Same(x, a.Id)) is var i and >= 0)
            {
                advisories[i] = Overlay(advisories[i], new ManualAdvisory { Id = a.Id, Affected = [.. advisories[i].Affected.Union(a.Affected)] });
                continue;
            }
            advisories.Add(a);
        }
        if (input.PhpSrc.Count == 0)
            errors.Add("GitHub no ha dado ningun aviso de php/php-src");
        foreach (var skipped in input.Skipped)
            warnings.Add($"GHSA sin leer del todo: {skipped}");

        // Lo escrito a mano.
        var reviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manual in input.Manual)
        {
            if (string.IsNullOrWhiteSpace(manual.Id))
            {
                errors.Add($"{manual.File}: falta 'id'");
                continue;
            }
            if (!reviewed.Add(manual.Id))
            {
                errors.Add($"{manual.File}: el aviso {manual.Id} ya lo trae otro fichero");
                continue;
            }
            foreach (var bad in (manual.Affected ?? []).Where(r => !RangeIsValid(r)))
                errors.Add($"{manual.File}: el rango '{bad}' no se puede leer");

            // Solo por su id (que puede ser el CVE o el GHSA del aviso), nunca por sus alias: un
            // nombre público como «PHP2Shell» lo comparten varios avisos.
            var index = advisories.FindIndex(x => Same(x, manual.Id));
            if (manual.Ignore)
            {
                if (index >= 0) advisories.RemoveAt(index);
                else warnings.Add($"{manual.File}: pide ignorar {manual.Id}, pero ninguna fuente lo trae ya (se puede borrar el fichero)");
                continue;
            }
            if (index >= 0)
            {
                advisories[index] = Overlay(advisories[index], manual);
                continue;
            }
            if (manual.Product is null || manual.Affected is not { Count: > 0 })
            {
                errors.Add($"{manual.File}: {manual.Id} no lo trae ninguna fuente, asi que necesita 'product' y 'affected'");
                continue;
            }
            if (!products.ContainsKey(manual.Product))
            {
                errors.Add($"{manual.File}: el producto '{manual.Product}' no esta en catalog.json");
                continue;
            }
            advisories.Add(Overlay(new Advisory { Id = manual.Id, Product = manual.Product, Title = manual.Id }, manual));
        }

        // Retener lo automático que dejaría en rojo la última de una rama con soporte.
        advisories.RemoveAll(a =>
        {
            if (reviewed.Contains(a.Id) || a.Aliases.Any(reviewed.Contains)) return false;
            if (!products.TryGetValue(a.Product, out var product)) return false;
            var hit = product.Branches.FirstOrDefault(b => b.Value.Status != "eol"
                && ComponentVersion.TryParse(b.Value.Latest, out var latest)
                && a.Affected.Any(r => VersionRange.Satisfies(latest, r)));
            if (hit.Value is null) return false;
            held.Add($"{a.Id}: afectaria a {hit.Value.Latest}, la ultima de {a.Product} {hit.Key} ({string.Join(" | ", a.Affected)})");
            return true;
        });

        // KEV.
        advisories = advisories
            .Select(a => input.Kev.Contains(a.Id) || a.Aliases.Any(input.Kev.Contains) ? With(a, kev: true) : With(a, kev: false))
            .ToList();
        if (input.Kev.Count < 100)
            errors.Add($"el catalogo KEV de CISA trae solo {input.Kev.Count} entradas: parece roto");

        var remediation = input.Meta.Remediation;
        if (remediation.RealFix.Length == 0 && products.TryGetValue("php", out var php))
        {
            var latest = php.Branches.Values.Where(b => b.Status != "eol" && b.Latest is not null).Select(b => b.Latest!);
            remediation = new RemediationText
            {
                MitigationHeader = remediation.MitigationHeader,
                Note = remediation.Note,
                RealFix = $"Actualiza a la ultima de tu rama: {string.Join(" / ", latest)} (o posterior).",
            };
        }

        var dataset = new AuditDataset
        {
            SchemaVersion = SchemaVersion,
            Generated = new DateTimeOffset(input.Now.UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero),
            Source = $"roost-catalog: endoflife.date, php.net, avisos de GitHub de php/php-src y CISA KEV {input.KevVersion}"
                     + (reviewed.Count > 0 ? $"; {reviewed.Count} revisado(s) a mano" : ""),
            Remediation = remediation,
            Products = products,
            Advisories = advisories,
        };

        var result = new CatalogResult { Dataset = dataset };
        result.Errors.AddRange(errors);
        result.Warnings.AddRange(warnings);
        result.Held.AddRange(held);
        if (input.Previous is { } previous)
            CompareWithPrevious(previous, dataset, result);
        return result;
    }

    private static void CompareWithPrevious(AuditDataset previous, AuditDataset current, CatalogResult result)
    {
        if (current.Generated <= previous.Generated)
            result.Errors.Add($"el feed nuevo ({current.Generated:O}) no es posterior al publicado ({previous.Generated:O})");
        foreach (var (key, product) in previous.Products)
        {
            if (!current.Products.TryGetValue(key, out var now))
                result.Errors.Add($"el feed nuevo ha perdido el producto {key}");
            else if (now.Branches.Count < product.Branches.Count)
                result.Errors.Add($"el feed nuevo tiene menos ramas de {key} ({now.Branches.Count}) que el publicado ({product.Branches.Count})");
        }
        if (current.Advisories.Count < previous.Advisories.Count * 0.8)
            result.Errors.Add($"el feed nuevo trae {current.Advisories.Count} avisos y el publicado {previous.Advisories.Count}: demasiados de golpe");
        var known = previous.Advisories.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.New.AddRange(current.Advisories.Where(a => !known.Contains(a.Id)).Select(a => a.Id));
    }

    /// <summary>El JSON del feed: estable (mismo orden, saltos de línea <c>\n</c>) para que el diff diario se lea.</summary>
    public static byte[] Serialize(AuditDataset dataset)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dataset, options) + "\n");
    }

    /// <summary>El resumen en Markdown para el job (GITHUB_STEP_SUMMARY).</summary>
    public static string Summary(CatalogResult result)
    {
        var d = result.Dataset;
        var md = new StringBuilder();
        md.AppendLine($"## Catalogo de avisos — {d.Generated:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine(result.Errors.Count == 0 ? "Listo para publicar." : "**No se publica**: hay errores.");
        md.AppendLine();
        foreach (var (key, p) in d.Products)
            md.AppendLine($"- {p.DisplayName}: {p.Branches.Count} ramas ({p.Branches.Count(b => b.Value.Status != "eol")} con soporte)");
        md.AppendLine($"- Avisos: {d.Advisories.Count} ({d.Advisories.Count(a => a.Kev)} en KEV)");
        Section(md, "Errores", result.Errors);
        Section(md, "Nuevos", result.New);
        Section(md, "Retenidos hasta que alguien los revise", result.Held);
        Section(md, "Avisos", result.Warnings);
        return md.ToString();
    }

    private static void Section(StringBuilder md, string title, List<string> lines)
    {
        if (lines.Count == 0) return;
        md.AppendLine();
        md.AppendLine($"### {title}");
        foreach (var line in lines) md.AppendLine($"- {line}");
    }

    /// <summary>true si todas las restricciones del rango llevan una versión que se lee.</summary>
    public static bool RangeIsValid(string range)
    {
        var tokens = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(t => ComponentVersion.TryParse(t.TrimStart('<', '>', '='), out _));
    }

    private static bool Same(Advisory a, string id)
        => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) || a.Aliases.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));

    private static bool Newer(string a, string b)
        => ComponentVersion.TryParse(a, out var va) && ComponentVersion.TryParse(b, out var vb) && va > vb;

    private static Advisory Overlay(Advisory a, ManualAdvisory m) => new()
    {
        Id = a.Id,
        Aliases = [.. (m.Aliases ?? []).Concat(a.Aliases).Where(x => !string.Equals(x, a.Id, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)],
        Product = a.Product,
        Title = m.Title ?? a.Title,
        Affected = m.Affected ?? a.Affected,
        FixedIn = m.FixedIn ?? a.FixedIn,
        Cvss = m.Cvss ?? a.Cvss,
        Severity = m.Severity ?? a.Severity,
        Kev = a.Kev,
        Mitigations = m.Mitigations ?? a.Mitigations,
        Refs = [.. a.Refs.Concat(m.Refs ?? []).Distinct()],
    };

    private static Advisory With(Advisory a, bool kev) => new()
    {
        Id = a.Id,
        Aliases = a.Aliases,
        Product = a.Product,
        Title = a.Title,
        Affected = a.Affected,
        FixedIn = a.FixedIn,
        Cvss = a.Cvss,
        Severity = a.Severity,
        Kev = kev,
        Mitigations = a.Mitigations,
        Refs = a.Refs,
    };
}
