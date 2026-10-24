using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public static partial class Sources
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

    // ---- nginx.org/en/security_advisories.html ------------------------------------------------

    /// <summary>
    /// La página de avisos de nginx (HTML): un <c>&lt;li&gt;</c> por aviso con su título, la gravedad,
    /// el CVE, «Not vulnerable: 1.31.7+, 1.30.6+» (la primera corregida de cada rama) y «Vulnerable:
    /// 0.1.29-1.31.6». El rango de una sola pieza no vale tal cual: 1.30.6 cae en 0.1.29-1.31.6 y ya
    /// está corregida. Así que a lo vulnerable se le quita, de cada rama corregida, desde su versión
    /// corregida hasta el final de la rama (<see cref="NginxRanges"/>). Lo que no se lee («all», un
    /// aviso sin CVE) se descarta y se cuenta en <paramref name="skipped"/>.
    /// </summary>
    public static List<SourceAdvisory> ParseNginxAdvisories(string html, List<string> skipped)
    {
        var result = new List<SourceAdvisory>();
        foreach (Match item in NginxItem().Matches(html))
        {
            var body = item.Groups["body"].Value;
            var segments = LineBreak().Split(body).Select(s => Text(s)).Where(s => s.Length > 0).ToList();
            if (segments.Count == 0) continue;
            var title = segments[0];
            var cves = Cve().Matches(body).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            var label = cves.FirstOrDefault() ?? title;
            var vulnerable = Field(segments, "Vulnerable:");
            var notVulnerable = Field(segments, "Not vulnerable:");
            if (cves.Count == 0)
            {
                skipped.Add($"nginx.org: '{title}': sin CVE");
                continue;
            }
            if (vulnerable is null || notVulnerable is null)
            {
                skipped.Add($"nginx.org: {label}: sin 'Vulnerable' o 'Not vulnerable'");
                continue;
            }

            var fixedIn = new Dictionary<string, string>();
            var affected = NginxRanges(vulnerable, notVulnerable, fixedIn, out var windowsOnly);
            if (affected is null)
            {
                skipped.Add($"nginx.org: {label}: rangos que no se leen ('{vulnerable}' / '{notVulnerable}')");
                continue;
            }

            var refs = new List<string>();
            foreach (Match link in Href().Matches(body))
            {
                var url = WebUtility.HtmlDecode(link.Groups["url"].Value);
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !refs.Contains(url)) refs.Add(url);
            }
            result.Add(new SourceAdvisory(default, new Advisory
            {
                Id = cves[0],
                Aliases = [.. cves.Skip(1)],
                Product = "nginx",
                Title = windowsOnly ? $"{title} (solo nginx para Windows)" : title,
                Affected = affected,
                FixedIn = fixedIn,
                Severity = NginxSeverity(Field(segments, "Severity:")),
                Refs = refs,
            }));
        }
        return result;
    }

    /// <summary>
    /// PURO: los rangos del dataset de un aviso de nginx. <paramref name="vulnerable"/> son tramos
    /// «A-B» (o una versión suelta) separados por comas, y <paramref name="notVulnerable"/> la primera
    /// corregida de cada rama («1.31.7+, 1.30.6+», o «none»). De cada tramo se quita, en cada rama
    /// corregida, desde su versión corregida hasta el final de la rama: 0.1.29-1.31.6 con 1.31.7+ y
    /// 1.30.6+ da <c>&gt;=0.1.29 &lt;1.30.6</c> y <c>&gt;=1.31.0 &lt;=1.31.6</c>. Null si algo no se lee.
    /// </summary>
    public static List<string>? NginxRanges(string vulnerable, string notVulnerable, Dictionary<string, string> fixedIn, out bool windowsOnly)
    {
        windowsOnly = false;
        var fixes = new List<ComponentVersion>();
        foreach (var token in notVulnerable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            if (!token.EndsWith('+') || ExactVersion(token[..^1]) is not { } fix) return null;
            fixes.Add(fix);
            var key = $"{fix.Major}.{fix.Minor}";
            if (!fixedIn.TryGetValue(key, out var known) || ComponentVersion.TryParse(known, out var k) && fix < k)
                fixedIn[key] = fix.ToString();
        }

        var spans = new List<Span>();
        foreach (var raw in vulnerable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            if (token.StartsWith("nginx/Windows ", StringComparison.OrdinalIgnoreCase))
            {
                windowsOnly = true;
                token = token["nginx/Windows ".Length..].Trim();
            }
            var dash = token.IndexOf('-');
            var lo = ExactVersion(dash < 0 ? token : token[..dash]);
            var hi = ExactVersion(dash < 0 ? token : token[(dash + 1)..]);
            if (lo is null || hi is null || hi < lo) return null;
            spans.Add(new Span(lo.Value, hi.Value, HiInclusive: true));
        }
        if (spans.Count == 0) return null;

        foreach (var fix in fixes)
        {
            var endOfBranch = new ComponentVersion(fix.Major, fix.Minor + 1, 0, "");
            spans = [.. spans.SelectMany(s => s.Without(fix, endOfBranch))];
        }
        // Si no queda nada vulnerable, el aviso se contradice: mejor no publicarlo.
        return spans.Count == 0 ? null : spans.Select(s => s.ToRange()).Distinct().ToList();
    }

    /// <summary>Un tramo de versiones, del <see cref="Lo"/> incluido al <see cref="Hi"/> (incluido o no).</summary>
    private readonly record struct Span(ComponentVersion Lo, ComponentVersion Hi, bool HiInclusive)
    {
        private bool Reaches(ComponentVersion v) => HiInclusive ? v <= Hi : v < Hi;

        /// <summary>Este tramo sin <c>[from, to)</c>: nada, uno o dos tramos.</summary>
        public IEnumerable<Span> Without(ComponentVersion from, ComponentVersion to)
        {
            if (!Reaches(from) || to <= Lo)
            {
                yield return this;
                yield break;
            }
            if (Lo < from) yield return new Span(Lo, from, HiInclusive: false);
            if (Reaches(to)) yield return new Span(to, Hi, HiInclusive);
        }

        public string ToRange() =>
            HiInclusive && Lo.CompareTo(Hi) == 0 ? $"={Lo}"
            : HiInclusive ? $">={Lo} <={Hi}"
            : $">={Lo} <{Hi}";
    }

    /// <summary>La gravedad de nginx en la escala del dataset: «major» es alta y «minor», baja.</summary>
    private static string? NginxSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => "critical",
        "major" or "high" => "high",
        "medium" => "medium",
        "minor" or "low" => "low",
        _ => null,
    };

    // ---- httpd.apache.org/security/vulnerabilities_24.html --------------------------------------

    /// <summary>
    /// La página de vulnerabilidades de Apache httpd 2.4 (HTML): una sección «Fixed in Apache HTTP
    /// Server 2.4.69» por versión, con un <c>&lt;dt&gt;</c> por CVE (gravedad y título) y su tabla, que trae
    /// «Affects». La cota de arriba es siempre la versión de la sección (<c>&lt;2.4.69</c>), que es lo
    /// que se sabe seguro; la de abajo sale de «Affects» (<see cref="ApacheLowerBound"/>). Lo que no se
    /// lee se descarta y se cuenta en <paramref name="skipped"/>.
    /// </summary>
    public static List<SourceAdvisory> ParseApacheAdvisories(string html, List<string> skipped)
    {
        var result = new List<SourceAdvisory>();
        var sections = ApacheSection().Matches(html).ToList();
        for (var s = 0; s < sections.Count; s++)
        {
            var start = sections[s].Index + sections[s].Length;
            var end = s + 1 < sections.Count ? sections[s + 1].Index : html.Length;
            var chunk = html[start..end];
            if (ExactVersion(sections[s].Groups["version"].Value) is not { } fixedVersion) continue;
            var released = ApacheReleased().Match(chunk) is { Success: true } r
                           && DateTimeOffset.TryParse(r.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : default;

            foreach (var entry in chunk.Split("<dt>").Skip(1))
            {
                var id = ApacheCveId().Match(entry) is { Success: true } idMatch ? idMatch.Groups["id"].Value.ToUpperInvariant() : null;
                if (id is null) continue;
                var affects = ApacheAffects().Match(entry) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups["value"].Value).Trim() : null;
                var lower = affects is null ? null : ApacheLowerBound(affects);
                if (lower is null || lower.Value >= fixedVersion)
                {
                    skipped.Add($"httpd.apache.org: {id}: 'Affects' que no se lee ('{affects}') en la {fixedVersion}");
                    continue;
                }

                var title = ApacheName().Match(entry) is { Success: true } n ? Text(n.Groups["title"].Value) : id;
                if (title.StartsWith("Apache HTTP Server: ", StringComparison.OrdinalIgnoreCase)) title = title["Apache HTTP Server: ".Length..];
                result.Add(new SourceAdvisory(released, new Advisory
                {
                    Id = id,
                    Product = "apache",
                    Title = title,
                    Affected = [$">={lower.Value} <{fixedVersion}"],
                    FixedIn = new() { [$"{fixedVersion.Major}.{fixedVersion.Minor}"] = fixedVersion.ToString() },
                    Severity = ApacheSeverity(ApacheSeverityLabel().Match(entry) is { Success: true } sev ? sev.Groups["severity"].Value : null),
                    Refs = [$"https://httpd.apache.org/security/vulnerabilities_24.html#{id}", $"https://www.cve.org/CVERecord?id={id}"],
                }));
            }
        }
        return result;
    }

    /// <summary>
    /// PURO: la primera versión afectada según «Affects», que la página escribe de muchas maneras:
    /// «2.4.17 through 2.4.68», « through 2.4.68», «2.4 through 2.4.63», «&lt;=2.4.53», «&lt;=2.4.48, !&lt;2.4.17»,
    /// «&gt;=2.4.7, &lt;=2.4.51», «2.4.30 before 2.4.66» o la lista de versiones («2.4.50, 2.4.49»). Sin cota
    /// de abajo es la 2.4.0 (la página es la de la 2.4). Null si no se entiende.
    /// </summary>
    public static ComponentVersion? ApacheLowerBound(string affects)
    {
        var text = affects.Trim();
        var start = new ComponentVersion(2, 4, 0, "2.4.0");
        if (ApacheNotBelow().Match(text) is { Success: true } notBelow) return ExactVersion(notBelow.Groups["v"].Value);
        if (ApacheThrough().Match(text) is { Success: true } through)
        {
            var lo = through.Groups["lo"].Value;
            return lo.Length == 0 || lo == "2.4" ? start : ExactVersion(lo);
        }
        if (text.StartsWith('<')) return start;
        var versions = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ExactVersion).ToList();
        if (versions.Count == 0 || versions.Any(v => v is null)) return null;
        var of24 = versions.Select(v => v!.Value).Where(v => v.Major == 2 && v.Minor == 4).ToList();
        return of24.Count == 0 ? null : of24.Min();
    }

    /// <summary>La gravedad de Apache («important», «moderate») en la escala del dataset.</summary>
    private static string? ApacheSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => "critical",
        "important" => "high",
        "moderate" => "medium",
        "low" => "low",
        _ => null,
    };

    /// <summary>Una versión X.Y.Z y nada más (sin «+», sin texto), o null.</summary>
    private static ComponentVersion? ExactVersion(string text)
    {
        var t = text.Trim();
        return t.Length > 0 && t.All(c => char.IsDigit(c) || c == '.') && t.Count(c => c == '.') == 2 && ComponentVersion.TryParse(t, out var v) ? v : null;
    }

    /// <summary>El valor de la línea que empieza por <paramref name="label"/> («Severity: medium» → «medium»).</summary>
    private static string? Field(IEnumerable<string> segments, string label) =>
        segments.FirstOrDefault(s => s.StartsWith(label, StringComparison.Ordinal)) is { } line ? line[label.Length..].Trim() : null;

    /// <summary>El texto de un trozo de HTML: sin etiquetas, con las entidades resueltas y los espacios juntos.</summary>
    private static string Text(string html) => Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(html, "")), " ").Trim();

    [GeneratedRegex(@"<li>\s*<p>(?<body>.*?)</p>\s*</li>", RegexOptions.Singleline)]
    private static partial Regex NginxItem();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"CVE-\d{4}-\d{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    [GeneratedRegex("""href="(?<url>[^"]+)""")]
    private static partial Regex Href();

    [GeneratedRegex(@"<h1 id=""[^""]*"">Fixed in Apache HTTP Server (?<version>[\d.]+)</h1>")]
    private static partial Regex ApacheSection();

    [GeneratedRegex(@"Released <time datetime=""(?<date>\d{4}-\d{2}-\d{2})"">")]
    private static partial Regex ApacheReleased();

    [GeneratedRegex(@"<h3 id=""(?<id>CVE-\d{4}-\d{4,})""", RegexOptions.IgnoreCase)]
    private static partial Regex ApacheCveId();

    [GeneratedRegex(@"\((?<severity>[a-z]+)\):\s*<name", RegexOptions.IgnoreCase)]
    private static partial Regex ApacheSeverityLabel();

    [GeneratedRegex(@"<name[^>]*>(?<title>.*?)</name>", RegexOptions.Singleline)]
    private static partial Regex ApacheName();

    [GeneratedRegex(@"<td class=""cve-header"">Affects</td>\s*<td class=""cve-value"">(?<value>[^<]*)</td>")]
    private static partial Regex ApacheAffects();

    [GeneratedRegex(@"(?:!<|>=)\s*(?<v>\d+\.\d+\.\d+)")]
    private static partial Regex ApacheNotBelow();

    [GeneratedRegex(@"^(?<lo>[\d.]*)\s*(?:through|before)\s+\d+\.\d+\.\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ApacheThrough();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Un aviso leído de una fuente automática, con su fecha de publicación (para ordenar).</summary>
public sealed record SourceAdvisory(DateTimeOffset Published, Advisory Advisory);
