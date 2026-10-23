using System.Globalization;
using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Catalog;

/// <summary>Un ciclo de endoflife.date. Sus fechas pueden ser una fecha o un sí/no.</summary>
public sealed record EolCycle(string Cycle, string? Latest, DateOnly? EolDate, bool? EolFlag, DateOnly? SupportDate, bool? SupportFlag)
{
    /// <summary>La rama tal como la usa el dataset: «8.3», «10.11»; null si el ciclo no tiene esa forma.</summary>
    public string? Branch => SortKey(Cycle) is { } k ? $"{k.Major}.{k.Minor}" : null;

    /// <summary>«10.11» → (10, 11), para ordenar ramas por número; null si no es «X.Y».</summary>
    public static (int Major, int Minor)? SortKey(string branch)
    {
        var parts = branch.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                                 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : null;
    }

    /// <summary>La rama del dataset a la fecha <paramref name="today"/>.</summary>
    public BranchInfo ToBranch(DateOnly today)
    {
        var eol = EolFlag == true || EolDate is { } e && e <= today;
        var securityOnly = !eol && (SupportFlag == false || SupportDate is { } s && s <= today);
        return new BranchInfo
        {
            Latest = Latest,
            Eol = EolDate,
            SecurityUntil = securityOnly ? EolDate : null,
            Status = eol ? "eol" : securityOnly ? "security" : "active",
        };
    }
}

/// <summary>
/// Lectores PUROS de las fuentes del catálogo (ROOST.md §7.3): reciben el JSON tal cual lo dan y
/// no van a la red. Lo que no entienden lo dicen, no lo inventan.
/// </summary>
public static class Sources
{
    /// <summary><c>endoflife.date/api/&lt;producto&gt;.json</c>: la lista de ciclos.</summary>
    public static IReadOnlyList<EolCycle> ParseEndOfLife(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("endoflife.date no devolvio una lista de ciclos");
        var cycles = new List<EolCycle>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var cycle = c.TryGetProperty("cycle", out var cy) ? cy.ToString() : null;
            if (string.IsNullOrEmpty(cycle)) continue;
            var latest = c.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var (eolDate, eolFlag) = DateOrFlag(c, "eol");
            var (supportDate, supportFlag) = DateOrFlag(c, "support");
            cycles.Add(new EolCycle(cycle, latest, eolDate, eolFlag, supportDate, supportFlag));
        }
        return cycles;
    }

    private static (DateOnly?, bool?) DateOrFlag(JsonElement cycle, string name)
    {
        if (!cycle.TryGetProperty(name, out var value)) return (null, null);
        return value.ValueKind switch
        {
            JsonValueKind.True => (null, true),
            JsonValueKind.False => (null, false),
            JsonValueKind.String when DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => (d, null),
            _ => (null, null),
        };
    }

    /// <summary><c>php.net/releases/index.php?json&amp;version=8.3</c>: la última de la rama, o null.</summary>
    public static string? ParsePhpNetLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("version", out var v)
               && v.ValueKind == JsonValueKind.String
               && ComponentVersion.TryParse(v.GetString()!, out _)
            ? v.GetString()
            : null;
    }

    /// <summary>El catálogo KEV de CISA: los CVE con explotación activa y la versión del catálogo.</summary>
    public static (HashSet<string> Cves, string Version) ParseKev(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("vulnerabilities", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("el catalogo KEV no trae 'vulnerabilities'");
        var cves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in list.EnumerateArray())
            if (v.TryGetProperty("cveID", out var id) && id.GetString() is { Length: > 0 } cve)
                cves.Add(cve);
        var version = root.TryGetProperty("catalogVersion", out var cv) ? cv.GetString() ?? "" : "";
        return (cves, version);
    }

    /// <summary>
    /// Una página de <c>/repos/php/php-src/security-advisories</c>. Cada aviso trae una entrada por
    /// rama con su cota superior (<c>&lt; 8.3.36</c>, corregido en 8.3.36): se acota a su rama
    /// (<c>&gt;=8.3.0 &lt;8.3.36</c>), porque si no un 8.2.35 ya corregido caería en <c>&lt; 8.3.36</c>.
    /// Lo que no se puede leer sin adivinar se descarta y se cuenta en <paramref name="skipped"/>.
    /// </summary>
    public static List<SourceAdvisory> ParsePhpSrcAdvisories(string json, List<string> skipped)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub no devolvio una lista de avisos");
        var result = new List<SourceAdvisory>();
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            var ghsa = Str(a, "ghsa_id") ?? "?";
            if (Str(a, "state") is { } state && state != "published") continue;
            if (a.TryGetProperty("withdrawn_at", out var w) && w.ValueKind == JsonValueKind.String) continue;

            var affected = new List<string>();
            var fixedIn = new Dictionary<string, string>();
            var unreadable = new List<string>();
            if (a.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vulns.EnumerateArray())
                {
                    var range = Str(v, "vulnerable_version_range") ?? "";
                    var patched = Str(v, "patched_versions") ?? "";
                    var ranges = NormalizeRange(range, patched, fixedIn);
                    if (ranges.Count == 0) unreadable.Add($"'{range}' / '{patched}'");
                    foreach (var r in ranges)
                        if (!affected.Contains(r)) affected.Add(r);
                }
            }
            if (affected.Count == 0)
            {
                skipped.Add($"{ghsa}: sin rangos que se puedan leer ({string.Join(", ", unreadable)})");
                continue;
            }
            if (unreadable.Count > 0)
                skipped.Add($"{ghsa}: se ignora parte de sus rangos ({string.Join(", ", unreadable)})");

            var cve = Str(a, "cve_id");
            var published = DateTimeOffset.TryParse(Str(a, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : default;
            result.Add(new SourceAdvisory(published, new Advisory
            {
                Id = cve ?? ghsa,
                Aliases = cve is null ? [] : [ghsa],
                Product = "php",
                Title = Str(a, "summary") ?? ghsa,
                Affected = affected,
                FixedIn = fixedIn,
                Cvss = Score(a),
                Severity = Str(a, "severity"),
                Refs = Str(a, "html_url") is { } url ? [url] : [],
            }));
        }
        return result;
    }

    /// <summary>
    /// Los rangos del dataset para una entrada de GHSA (puro). Restricciones separadas por comas
    /// (<c>&gt;=8.1.0, &lt;=8.1.15</c>); una cota superior sin inferior se acota a su rama; una versión
    /// suelta es esa versión (la regla de GitHub) salvo que haya versiones corregidas, que mandan;
    /// sin cota superior ni versión corregida no se publica (dejaría en rojo hasta la última).
    /// </summary>
    public static List<string> NormalizeRange(string range, string patched, Dictionary<string, string> fixedIn)
    {
        var fixes = new List<ComponentVersion>();
        foreach (var t in patched.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ComponentVersion.TryParse(t, out var v)) fixes.Add(v);
        foreach (var fix in fixes)
        {
            var key = $"{fix.Major}.{fix.Minor}";
            if (!fixedIn.TryGetValue(key, out var known) || ComponentVersion.TryParse(known, out var k) && fix < k)
                fixedIn[key] = fix.ToString();
        }

        var constraints = new List<(string Op, ComponentVersion Version)>();
        foreach (var token in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = token.StartsWith(">=") ? ">=" : token.StartsWith("<=") ? "<=" : token.StartsWith('>') ? ">" : token.StartsWith('<') ? "<" : token.StartsWith('=') ? "=" : "";
            var text = token[op.Length..].Trim();
            if (!ComponentVersion.TryParse(text, out var version) || text.Any(c => !(char.IsDigit(c) || c == '.')))
                return FromFixes(fixes);
            constraints.Add((op.Length == 0 ? "=" : op, version));
        }

        var upperIndex = constraints.FindIndex(c => c.Op is "<" or "<=");
        var hasLower = constraints.Any(c => c.Op is ">=" or ">");
        var exact = constraints.Any(c => c.Op == "=");
        if (constraints.Count == 0 || (exact && fixes.Count > 0) || (upperIndex < 0 && !exact))
            return FromFixes(fixes);

        var parts = new List<string>();
        if (upperIndex >= 0)
        {
            var (op, u) = constraints[upperIndex];
            if (!hasLower && u.Patch > 0) parts.Add($">={u.Major}.{u.Minor}.0");
            if (op == "<") fixedIn.TryAdd($"{u.Major}.{u.Minor}", u.ToString());
        }
        parts.AddRange(constraints.Select(c => $"{c.Op}{c.Version}"));
        return [string.Join(' ', parts)];
    }

    private static List<string> FromFixes(IEnumerable<ComponentVersion> fixes)
        => fixes.Select(f => f.Patch > 0 ? $">={f.Major}.{f.Minor}.0 <{f}" : $"<{f}").Distinct().ToList();

    /// <summary>La puntuación CVSS: la v3.x si la hay (la escala con la que se razona), si no la v4 o la antigua.</summary>
    private static double Score(JsonElement advisory)
    {
        if (advisory.TryGetProperty("cvss_severities", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "cvss_v3", "cvss_v4" })
                if (s.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Object
                    && c.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.GetDouble() > 0)
                    return score.GetDouble();
        }
        return advisory.TryGetProperty("cvss", out var old) && old.ValueKind == JsonValueKind.Object
               && old.TryGetProperty("score", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetDouble()
            : 0;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Un aviso leído de una fuente automática, con su fecha de publicación (para ordenar).</summary>
public sealed record SourceAdvisory(DateTimeOffset Published, Advisory Advisory);
