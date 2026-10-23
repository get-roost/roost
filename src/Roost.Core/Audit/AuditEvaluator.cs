using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Audit;

/// <summary>
/// Evaluador PURO de auditoria: toma un <see cref="DetectedComponent"/> y el
/// dataset, y devuelve un <see cref="AuditFinding"/>. No escanea, no toca disco,
/// no mira la red. Factorizado aparte a proposito para poder testearlo con
/// componentes construidos a mano, sin depender del escaneo real.
/// </summary>
public sealed partial class AuditEvaluator
{
    private readonly AuditDataset _dataset;
    private readonly DateOnly _today;

    /// <param name="today">Fecha de referencia para el EOL; inyectable para tests deterministas.</param>
    public AuditEvaluator(AuditDataset dataset, DateOnly? today = null)
    {
        _dataset = dataset;
        _today = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
    }

    public IEnumerable<AuditFinding> EvaluateAll(IEnumerable<DetectedComponent> components)
        => components.Select(Evaluate);

    public AuditFinding Evaluate(DetectedComponent component)
    {
        var uncertain = LooksLikeDistroBackport(component, out var distroReason);
        var notes = new List<string>();

        var productKey = AuditDataset.ProductKey(component.Kind);
        var branchKey = $"{component.Version.Major}.{component.Version.Minor}";

        BranchInfo? branch = null;
        if (productKey is not null
            && _dataset.Products.TryGetValue(productKey, out var product))
        {
            product.Branches.TryGetValue(branchKey, out branch);
        }

        // Avisos cuyo rango afectado cubre esta version (comparacion por numero).
        var matched = productKey is null
            ? new List<Advisory>()
            : _dataset.Advisories
                .Where(a => string.Equals(a.Product, productKey, StringComparison.OrdinalIgnoreCase))
                .Where(a => a.Affected.Any(r => VersionRange.Satisfies(component.Version, r)))
                .ToList();

        var mitigations = matched.SelectMany(a => a.Mitigations).ToList();

        AuditVerdict verdict;
        AuditColor color;
        string headline;

        var eolDate = branch?.Eol;
        var isEol = branch is not null
            && (string.Equals(branch.Status, "eol", StringComparison.OrdinalIgnoreCase)
                || (eolDate is { } e && e <= _today));

        if (branch is null)
        {
            // Rama que el dataset no conoce (p.ej. una futura, o un producto sin feed).
            verdict = AuditVerdict.Unknown;
            color = AuditColor.Amber;
            headline = productKey is null
                ? "Sin dataset de avisos para este componente."
                : $"Rama {branchKey} no reconocida en el dataset; confirmala en la fuente del proveedor.";
        }
        else if (isEol)
        {
            // EOL manda sobre vulnerable: la accion es migrar de rama, no parchear.
            verdict = AuditVerdict.Eol;
            color = AuditColor.Red;
            headline = eolDate is { } d
                ? $"Rama {branchKey} fin de vida ({d:yyyy-MM-dd}): sin parches oficiales de seguridad."
                : $"Rama {branchKey} fin de vida: sin parches oficiales de seguridad.";
            if (matched.Count > 0)
                notes.Add($"Ademas le afecta {Ids(matched)}, que en esta rama ya no se corrige.");
        }
        else if (matched.Count > 0)
        {
            verdict = AuditVerdict.Vulnerable;
            color = AuditColor.Red;
            var worst = Gravity(matched);
            var kev = matched.Any(a => a.Kev) ? ", en KEV (explotacion activa)" : "";
            var fix = FixTarget(matched, branchKey) ?? branch.Latest;
            headline = fix is null
                ? $"Afectada por {Ids(matched)} ({worst}{kev})."
                : $"Afectada por {Ids(matched)} ({worst}{kev}); actualiza a {fix}.";
        }
        else
        {
            var hasAdvisoryFeed = _dataset.Advisories
                .Any(a => string.Equals(a.Product, productKey, StringComparison.OrdinalIgnoreCase));
            verdict = hasAdvisoryFeed ? AuditVerdict.Patched : AuditVerdict.Supported;
            color = AuditColor.Green;
            headline = hasAdvisoryFeed
                ? "Al dia frente a los avisos conocidos."
                : $"Rama {branchKey} con soporte; el dataset no trae CVE de este producto, asi que no se puede afirmar que este parcheada.";
            if (branch.Latest is { } latest
                && ComponentVersion.TryParse(latest, out var latestVer)
                && component.Version < latestVer)
            {
                notes.Add($"Hay una version mas nueva en la rama: {latest} (no hay aviso abierto, pero conviene subir).");
            }
            if (string.Equals(branch.Status, "security", StringComparison.OrdinalIgnoreCase))
                notes.Add("Rama solo con soporte de SEGURIDAD; planifica migrar a una rama con soporte activo.");
        }

        // Las bases de datos solo se miran por fecha de fin de vida: el feed aún no trae sus CVE.
        if (component.Kind is ComponentKind.MySql or ComponentKind.MariaDb)
            notes.Add("Solo se comprueba el fin de vida de la rama: el feed de Roost aun no trae CVE de MySQL ni de MariaDB.");

        // "Ambar honesto": si es un backport de distro, la comparacion por numero
        // no es fiable. No lo damos por rojo: lo marcamos incierto y lo pintamos ambar.
        if (uncertain)
        {
            color = AuditColor.Amber;
            notes.Insert(0, distroReason);
            notes.Add("La comparacion por numero NO es fiable con backports: un 8.3.9+esm puede estar ya parcheado. "
                      + "Comprueba con `apt changelog <paquete>` / `pro fix`, o con el changelog del paquete.");
            headline = "Incierto (paquete de distro con posible backport). " + headline;
        }

        return new AuditFinding
        {
            Component = component,
            Verdict = verdict,
            Color = color,
            Uncertain = uncertain,
            Headline = headline,
            BranchLatest = branch?.Latest,
            Eol = eolDate,
            MatchedAdvisories = matched,
            Mitigations = mitigations,
            Notes = notes,
        };
    }

    private static readonly string[] SeverityOrder = ["critical", "high", "medium", "low"];

    /// <summary>
    /// Los ids para el titular: todos si son tres o menos; si no, los tres peores (KEV, luego
    /// puntuación o gravedad) «y N mas». Con el feed entero, un PHP viejo casa con decenas.
    /// </summary>
    private static string Ids(IReadOnlyList<Advisory> advisories)
    {
        const int shown = 3;
        if (advisories.Count <= shown) return string.Join(", ", advisories.Select(a => a.Id));
        var worst = advisories
            .OrderByDescending(a => a.Kev)
            .ThenByDescending(a => a.Cvss > 0 ? a.Cvss : 10 - 2.5 * (Array.IndexOf(SeverityOrder, a.Severity?.ToLowerInvariant()) is var i and >= 0 ? i : 4))
            .Take(shown)
            .Select(a => a.Id);
        return $"{string.Join(", ", worst)} y {advisories.Count - shown} mas";
    }

    /// <summary>«CVSS 9.2» con la peor puntuación; si ninguno la trae, la peor gravedad («gravedad critica»).</summary>
    private static string Gravity(IReadOnlyList<Advisory> advisories)
    {
        var worst = advisories.Max(a => a.Cvss);
        // Con punto siempre (como la app), no según el idioma del sistema: el titular va también al JSON.
        if (worst > 0) return $"CVSS {worst.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
        string[] labels = ["critica", "alta", "media", "baja"];
        var rank = advisories.Select(a => Array.IndexOf(SeverityOrder, a.Severity?.ToLowerInvariant())).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        return rank < 0 ? "sin puntuacion" : $"gravedad {labels[rank]}";
    }

    private static string? FixTarget(IEnumerable<Advisory> advisories, string branchKey)
    {
        foreach (var a in advisories)
            if (a.FixedIn.TryGetValue(branchKey, out var fix))
                return fix;
        return null;
    }

    private static readonly HashSet<string> SystemSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "apt", "dpkg", "dnf", "yum", "rpm", "distro", "pacman", "zypper", "apk", "portage",
    };

    [GeneratedRegex(
        @"(\+deb|~deb|\+esm|\+ubuntu|-ubuntu|ubuntu\d|\+dfsg|\+rpi|~bpo|\.el\d|el\d_|\.fc\d|\+b\d|\+focal|\+jammy|\+noble|\+bookworm|\+bullseye|\+buster)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistroMarkerRegex();

    /// <summary>
    /// Detecta si la instalacion es un paquete de distro con backport, en cuyo caso
    /// el numero de version miente (p.ej. 7.4.3-4ubuntu2.29+esm5 esta parcheado).
    /// </summary>
    private static bool LooksLikeDistroBackport(DetectedComponent component, out string reason)
    {
        if (SystemSources.Contains(component.Source))
        {
            reason = $"Instalado por el gestor de paquetes del sistema (origen: {component.Source}).";
            return true;
        }

        var raw = component.Version.Raw ?? string.Empty;
        var m = DistroMarkerRegex().Match(raw);
        if (m.Success)
        {
            reason = $"La version trae sufijo de distribucion (\"{m.Value}\"), tipico de un backport.";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
