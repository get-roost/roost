using System.Text;
using System.Text.Json;
using Roost.Core.Models;

namespace Roost.Core.Audit;

/// <summary>
/// El informe de una auditoría, en JSON (el de <c>roost audit --json</c>) o en Markdown (para
/// pegarlo en una incidencia). Lo usan la CLI y el «Exportar» de la app.
/// </summary>
public static class AuditReport
{
    /// <summary>Peor primero: rojo, ámbar, verde; y dentro, por tipo y versión.</summary>
    public static IEnumerable<AuditFinding> Ordered(IEnumerable<AuditFinding> findings) =>
        findings
            .OrderBy(f => f.Color switch { AuditColor.Red => 0, AuditColor.Amber => 1, _ => 2 })
            .ThenBy(f => f.Component.Kind)
            .ThenByDescending(f => f.Component.Version);

    /// <summary>Un componente detectado tal como sale en el JSON de <c>scan</c> y <c>audit</c>.</summary>
    public static object ComponentDto(DetectedComponent c) => new
    {
        kind = c.Kind.ToString(),
        version = c.Version.ToString(),
        versionRaw = c.Version.Raw,
        source = c.Source,
        sapi = c.Sapi,
        isOnPath = c.IsOnPath,
        architecture = c.Architecture,
        executablePath = c.ExecutablePath,
        extra = c.Extra,
    };

    /// <param name="origin">De dónde salen los avisos (feed firmado, empaquetado o un fichero); null si no se sabe.</param>
    /// <param name="only">El filtro de <c>--only</c>, si lo hay: entonces <c>findings</c> ya viene filtrado.</param>
    /// <param name="skipped">Cuántos componentes encontrados ha dejado fuera el filtro.</param>
    public static string ToJson(AuditDataset dataset, IReadOnlyList<AuditFinding> findings, JsonSerializerOptions options,
        DatasetOrigin? origin = null, Scanning.ComponentFilter? only = null, int skipped = 0)
    {
        var payload = new
        {
            only = only?.Keys,
            skipped,
            dataset = new
            {
                schemaVersion = dataset.SchemaVersion,
                generated = dataset.Generated,
                source = dataset.Source,
                origin = origin?.Kind.ToString().ToLowerInvariant(),
                signed = origin is null ? (bool?)null : origin.Kind == DatasetKind.Feed,
                note = origin?.Note,
            },
            blocking = findings.Any(f => f.IsBlocking),
            summary = new
            {
                red = findings.Count(f => f.Color == AuditColor.Red),
                amber = findings.Count(f => f.Color == AuditColor.Amber),
                green = findings.Count(f => f.Color == AuditColor.Green),
            },
            findings = Ordered(findings).Select(f => new
            {
                component = ComponentDto(f.Component),
                verdict = f.VerdictLabel,
                color = f.Color.ToString().ToLowerInvariant(),
                uncertain = f.Uncertain,
                blocking = f.IsBlocking,
                headline = f.Headline,
                branchLatest = f.BranchLatest,
                eol = f.Eol,
                advisories = f.MatchedAdvisories.Select(a => new
                {
                    id = a.Id,
                    aliases = a.Aliases,
                    cvss = a.Cvss,
                    kev = a.Kev,
                    title = a.Title,
                }),
                mitigations = f.Mitigations.Select(m => new { ini = m.Ini, value = m.Value, note = m.Note }),
                notes = f.Notes,
            }),
        };
        return JsonSerializer.Serialize(payload, options);
    }

    /// <summary>Resumen y una tabla con todo; debajo, el detalle de lo que no está en verde.</summary>
    public static string ToMarkdown(AuditDataset dataset, IReadOnlyList<AuditFinding> findings, DateTimeOffset when, string machine, DatasetOrigin? origin = null)
    {
        var red = findings.Count(f => f.Color == AuditColor.Red);
        var amber = findings.Count(f => f.Color == AuditColor.Amber);
        var green = findings.Count(f => f.Color == AuditColor.Green);
        var md = new StringBuilder();
        md.AppendLine($"# Auditoria de Roost: {machine}");
        md.AppendLine();
        md.AppendLine(origin is null
            ? $"{when:yyyy-MM-dd HH:mm} · dataset del {dataset.Generated:yyyy-MM-dd} · evaluado en local"
            : $"{when:yyyy-MM-dd HH:mm} · {origin.Describe(dataset, when)} · evaluado en local");
        md.AppendLine();
        md.AppendLine($"**{red} en rojo** · {amber} en ambar · {green} en verde");
        md.AppendLine();
        md.AppendLine("| | Componente | Origen | Veredicto | Detalle | Ruta |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var f in Ordered(findings))
        {
            var dot = f.Color switch { AuditColor.Red => "🔴", AuditColor.Amber => "🟠", _ => "🟢" };
            var verdict = f.Uncertain ? $"{f.VerdictLabel}?" : f.VerdictLabel;
            md.AppendLine($"| {dot} | {f.Component.Kind} {f.Component.Version.Raw} | {Cell(f.Component.Source)} | {verdict} | {Cell(f.Headline)} | `{Cell(f.Component.ExecutablePath)}` |");
        }

        foreach (var f in Ordered(findings).Where(f => f.Color != AuditColor.Green))
        {
            md.AppendLine();
            md.AppendLine($"## {f.Component.Kind} {f.Component.Version.Raw} ({f.Component.Source}): {f.VerdictLabel}");
            md.AppendLine();
            md.AppendLine(f.Headline);
            foreach (var a in f.MatchedAdvisories)
                md.AppendLine($"- **{a.Id}**{(a.Aliases.Count > 0 ? $" ({string.Join(", ", a.Aliases)})" : "")} · CVSS {a.Cvss:0.0}{(a.Kev ? " · KEV" : "")}: {a.Title}");
            if (f.Mitigations.Count > 0)
            {
                md.AppendLine();
                md.AppendLine($"{dataset.Remediation.MitigationHeader}");
                foreach (var m in f.Mitigations) md.AppendLine($"- `{m}`");
                md.AppendLine();
                md.AppendLine($"Arreglo real: {dataset.Remediation.RealFix}");
            }
            foreach (var note in f.Notes) md.AppendLine($"- {note}");
        }
        return md.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
