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

        // nginx tiene dos ramas vivas a la vez, y las dos reciben los parches.
        if (component.Kind == ComponentKind.Nginx && branch is not null && !isEol)
            notes.Add(component.Version.Minor % 2 == 0
                ? $"Rama {branchKey} de nginx: la estable (las pares)."
                : $"Rama {branchKey} de nginx: la mainline (las impares), la de las novedades; tambien recibe los parches.");

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

    /// <summary>
    /// PURO (K4): el hallazgo con el estado de sus mitigaciones frente al php.ini efectivo. Un
    /// VULNERABLE cierto cuyos avisos están todos mitigados (cada uno con al menos una mitigación, y
    /// todas cumplidas) baja a ámbar y deja de bloquear (ROOST.md §7.2: «CVE mitigado por
    /// configuración»), salvo que:
    /// <list type="bullet">
    /// <item>alguno esté en KEV (explotación activa: sigue en rojo, solo lo arregla actualizar);</item>
    /// <item>no sea un PHP de Roost (<paramref name="trusted"/> false): <c>php -i</c> es lo que lee la
    /// terminal, y el servidor web de Laragon, XAMPP o la distro puede leer otro php.ini.</item>
    /// </list>
    /// EOL no baja nunca: sin parches, la mitigación no basta. Siempre parte del hallazgo sin mitigar
    /// (<see cref="AuditFinding.BeforeMitigations"/>), así que se puede volver a llamar.
    /// </summary>
    public static AuditFinding WithMitigations(AuditFinding finding, IReadOnlyList<MitigationState> states, IReadOnlyList<string> iniFiles, bool trusted)
    {
        var original = finding.BeforeMitigations ?? finding;
        bool Done(Mitigation m) => states.Any(s => s.Mitigation.SameAs(m) && s.Status == MitigationStatus.Mitigated);
        var advisories = original.MatchedAdvisories;
        var covered = advisories.Where(a => a.Mitigations.Count > 0 && a.Mitigations.All(Done)).ToList();
        var notes = original.Notes.ToList();
        var color = original.Color;
        var headline = original.Headline;
        var mitigated = false;

        if (covered.Count > 0 && covered.Count == advisories.Count && original.Verdict == AuditVerdict.Vulnerable && !original.Uncertain)
        {
            if (covered.Where(a => a.Kev).Select(a => a.Id).ToList() is { Count: > 0 } kev)
                notes.Add($"Las mitigaciones estan aplicadas, pero sigue en rojo: {string.Join(", ", kev)} esta en KEV (explotacion activa) y solo lo arregla actualizar.");
            else if (!trusted)
                notes.Add("Mitigada en el php.ini que lee la terminal, pero su servidor web puede leer otro: sigue en rojo hasta actualizar.");
            else
            {
                color = AuditColor.Amber;
                mitigated = true;
                headline = $"Mitigada por configuracion (no sustituye al parche). {headline}";
            }
        }
        else if (covered.Count > 0)
        {
            notes.Add($"Mitigados en el php.ini efectivo: {string.Join(", ", covered.Select(a => a.Id))}; los demas avisos siguen abiertos.");
        }

        return original with
        {
            Color = color,
            Headline = headline,
            Notes = notes,
            Mitigated = mitigated,
            MitigationStates = states,
            IniFiles = iniFiles,
            BeforeMitigations = original,
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

    /// <summary>A qué subir para quitarse todos los avisos: la mayor de las corregidas en esta rama.</summary>
    private static string? FixTarget(IEnumerable<Advisory> advisories, string branchKey)
    {
        ComponentVersion? best = null;
        foreach (var a in advisories)
            if (a.FixedIn.TryGetValue(branchKey, out var fix) && ComponentVersion.TryParse(fix, out var v) && (best is null || v > best.Value))
                best = v;
        return best?.ToString();
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
