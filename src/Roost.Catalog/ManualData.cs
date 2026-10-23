using System.Text.Json;
using Roost.Core.Audit;

namespace Roost.Catalog;

/// <summary>
/// <c>catalog/catalog.json</c>: lo fijo del catálogo, escrito a mano. Qué productos lleva, con qué
/// nombre de endoflife.date, y los textos de remediación.
/// </summary>
public sealed class CatalogMeta
{
    public Dictionary<string, ProductMeta> Products { get; init; } = new();
    public RemediationText Remediation { get; init; } = new();
}

public sealed class ProductMeta
{
    public string DisplayName { get; init; } = "";

    /// <summary>El producto en endoflife.date (<c>/api/&lt;este&gt;.json</c>).</summary>
    public string EndOfLife { get; init; } = "";
}

/// <summary>
/// Un fichero de <c>catalog/advisories/</c>: lo que no se automatiza bien (nombre público, título en
/// castellano, mitigaciones por ini, rangos corregidos). Si su id (o un alias) es el de un aviso
/// automático, sus campos sustituyen a los de ese aviso; si no, es un aviso nuevo y entonces
/// <c>product</c> y <c>affected</c> son obligatorios. <c>kev</c> no se escribe a mano: sale de CISA.
/// </summary>
public sealed class ManualAdvisory
{
    public string Id { get; init; } = "";
    public List<string>? Aliases { get; init; }
    public string? Product { get; init; }
    public string? Title { get; init; }
    public List<string>? Affected { get; init; }
    public Dictionary<string, string>? FixedIn { get; init; }
    public double? Cvss { get; init; }
    public string? Severity { get; init; }
    public List<Mitigation>? Mitigations { get; init; }
    public List<string>? Refs { get; init; }

    /// <summary>true para quitar del feed un aviso automático que está mal (y en <see cref="Note"/>, por qué).</summary>
    public bool Ignore { get; init; }

    /// <summary>Para quien lo lea: por qué está este fichero. No va al feed.</summary>
    public string? Note { get; init; }

    /// <summary>El nombre del fichero, para los mensajes.</summary>
    public string File { get; set; } = "";
}

public static class ManualData
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CatalogMeta LoadMeta(string catalogDir)
    {
        var file = Path.Combine(catalogDir, "catalog.json");
        return JsonSerializer.Deserialize<CatalogMeta>(System.IO.File.ReadAllText(file), Options)
               ?? throw new InvalidDataException($"{file} esta vacio");
    }

    public static List<ManualAdvisory> LoadAdvisories(string catalogDir)
    {
        var dir = Path.Combine(catalogDir, "advisories");
        if (!Directory.Exists(dir)) return [];
        var list = new List<ManualAdvisory>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            ManualAdvisory advisory;
            try
            {
                advisory = JsonSerializer.Deserialize<ManualAdvisory>(System.IO.File.ReadAllText(file), Options)
                           ?? throw new InvalidDataException("vacio");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{Path.GetFileName(file)}: {ex.Message}", ex);
            }
            advisory.File = Path.GetFileName(file);
            list.Add(advisory);
        }
        return list;
    }
}
