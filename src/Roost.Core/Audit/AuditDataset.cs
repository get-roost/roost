using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Models;

namespace Roost.Core.Audit;

/// <summary>
/// Dataset de avisos de seguridad y ciclos de vida, EMPAQUETADO en el binario
/// (embedded resource) y evaluado en local sin red. Es la semilla del panel de
/// auditoria de ROOST.md (seccion 7): mismo esquema que el futuro feed firmado
/// 'roost-catalog', pero cargado desde un JSON estatico mientras no haya red.
/// </summary>
public sealed class AuditDataset
{
    /// <summary>Version del ESQUEMA del dataset (semver). Permite evolucionarlo sin romper lectores.</summary>
    public string SchemaVersion { get; init; } = "0";

    /// <summary>Marca temporal de cuando se genero el dataset (feed "de hace N dias").</summary>
    public DateTimeOffset Generated { get; init; }

    /// <summary>Procedencia de los datos, en texto libre.</summary>
    public string Source { get; init; } = "";

    public RemediationText Remediation { get; init; } = new();

    /// <summary>Productos por clave: "php", "mysql", "mariadb", "nginx", "apache".</summary>
    public Dictionary<string, ProductInfo> Products { get; init; } = new();

    public List<Advisory> Advisories { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Este dataset con lo de <paramref name="other"/> encima (el de una extensión: el feed
    /// ampliado). Los avisos se suman; uno con el mismo id lo sustituye. Las ramas de un producto
    /// que ya existe se mezclan, y las de <paramref name="other"/> mandan. Ninguno de los dos cambia.
    /// </summary>
    public AuditDataset Merge(AuditDataset other)
    {
        var products = Products.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, product) in other.Products)
        {
            if (!products.TryGetValue(key, out var mine))
            {
                products[key] = product;
                continue;
            }
            var branches = new Dictionary<string, BranchInfo>(mine.Branches);
            foreach (var (branch, info) in product.Branches) branches[branch] = info;
            products[key] = new ProductInfo { DisplayName = mine.DisplayName.Length > 0 ? mine.DisplayName : product.DisplayName, Branches = branches };
        }

        var replaced = other.Advisories.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AuditDataset
        {
            SchemaVersion = SchemaVersion,
            Generated = other.Generated > Generated ? other.Generated : Generated,
            Source = other.Source.Length == 0 ? Source : $"{Source} + {other.Source}",
            Remediation = Remediation,
            Products = products,
            Advisories = [.. Advisories.Where(a => !replaced.Contains(a.Id)), .. other.Advisories],
        };
    }

    /// <summary>Carga el dataset empaquetado como recurso embebido de este ensamblado.</summary>
    public static AuditDataset LoadEmbedded()
    {
        var asm = typeof(AuditDataset).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("advisories.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "No se encontro el dataset 'advisories.json' embebido en Roost.Core.");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No se pudo abrir el recurso embebido '{name}'.");
        return Load(stream);
    }

    /// <summary>Carga el dataset desde un fichero externo (para actualizarlo sin recompilar).</summary>
    public static AuditDataset LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static AuditDataset Load(Stream stream)
        => JsonSerializer.Deserialize<AuditDataset>(stream, JsonOptions)
           ?? throw new InvalidDataException("Dataset de auditoria vacio o ilegible.");

    /// <summary>Clave de producto del dataset para un <see cref="ComponentKind"/>; null si no se audita.</summary>
    public static string? ProductKey(ComponentKind kind) => kind switch
    {
        ComponentKind.Php => "php",
        ComponentKind.MySql => "mysql",
        ComponentKind.MariaDb => "mariadb",
        ComponentKind.Nginx => "nginx",
        ComponentKind.Apache => "apache",
        _ => null,
    };
}

public sealed class RemediationText
{
    public string MitigationHeader { get; init; } = "MITIGACIONES:";
    public string RealFix { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed class ProductInfo
{
    public string DisplayName { get; init; } = "";
    public Dictionary<string, BranchInfo> Branches { get; init; } = new();
}

public sealed class BranchInfo
{
    /// <summary>Ultima version publicada de la rama.</summary>
    public string? Latest { get; init; }

    /// <summary>Fecha de fin de vida (fin de soporte de seguridad) de la rama.</summary>
    public DateOnly? Eol { get; init; }

    /// <summary>Fecha hasta la que la rama solo recibe arreglos de seguridad.</summary>
    public DateOnly? SecurityUntil { get; init; }

    /// <summary>"active" | "security" | "eol".</summary>
    public string Status { get; init; } = "unknown";
}

public sealed class Advisory
{
    public string Id { get; init; } = "";
    public List<string> Aliases { get; init; } = new();
    public string Product { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>Rangos afectados, p.ej. "&gt;=8.3.0 &lt;8.3.36". Se cumple el aviso si la version cae en ALGUNO.</summary>
    public List<string> Affected { get; init; } = new();

    /// <summary>Primera version corregida por rama, p.ej. {"8.3":"8.3.36"}.</summary>
    public Dictionary<string, string> FixedIn { get; init; } = new();

    public double Cvss { get; init; }

    /// <summary>"critical" | "high" | "medium" | "low", como la da GitHub; sirve cuando no hay CVSS.</summary>
    public string? Severity { get; init; }

    /// <summary>true si esta en el catalogo KEV de CISA (explotacion activa).</summary>
    public bool Kev { get; init; }

    public List<Mitigation> Mitigations { get; init; } = new();
    public List<string> Refs { get; init; } = new();
}

public sealed class Mitigation
{
    public string Ini { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Note { get; init; }

    public override string ToString()
        => string.IsNullOrEmpty(Note) ? $"{Ini} = {Value}" : $"{Ini} = {Value}  ({Note})";
}
