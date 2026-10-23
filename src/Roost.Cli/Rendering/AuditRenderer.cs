using Roost.Core.Audit;
using Spectre.Console;

namespace Roost.Cli.Rendering;

/// <summary>Imprime el resultado de <c>roost audit</c>: semaforo legible o JSON.</summary>
public static class AuditRenderer
{
    private static string Dot(AuditColor color) => color switch
    {
        AuditColor.Green => "[green]●[/]",
        AuditColor.Amber => "[yellow]●[/]",
        AuditColor.Red => "[red]●[/]",
        _ => "●",
    };

    private static string VerdictMarkup(AuditFinding f)
    {
        var label = f.Uncertain ? $"{f.VerdictLabel}?" : f.VerdictLabel;
        return f.Color switch
        {
            AuditColor.Green => $"[green]{label}[/]",
            AuditColor.Amber => $"[yellow]{label}[/]",
            AuditColor.Red => $"[red bold]{label}[/]",
            _ => label,
        };
    }

    public static void RenderTable(AuditDataset dataset, IReadOnlyList<AuditFinding> findings, DatasetOrigin origin,
        Core.Scanning.ComponentFilter? only = null, int skipped = 0)
    {
        var console = AnsiConsole.Console;
        var color = origin.Kind == DatasetKind.Feed && origin.Note is null ? "grey" : "yellow";
        console.MarkupLine(
            $"[bold]Auditoria[/]  ·  [{color}]{Markup.Escape(origin.Describe(dataset, DateTimeOffset.UtcNow))}[/]  [grey](evaluado en local)[/]");
        if (only is not null)
            console.MarkupLine($"[grey]Solo {Markup.Escape(string.Join(", ", only.Keys))} (--only): {skipped} componente(s) mas quedan fuera.[/]");
        console.WriteLine();

        if (findings.Count == 0)
        {
            console.MarkupLine(only is null
                ? "[yellow]Nada que auditar:[/] no se detecto ningun componente. El catalogo de scanners aun no esta conectado o no hay stacks instalados."
                : "[yellow]Nada que auditar:[/] no se detecto ningun componente de los que pide --only.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("");
        table.AddColumn("Componente");
        table.AddColumn("Version");
        table.AddColumn("Veredicto");
        table.AddColumn("Origen");
        table.AddColumn("Detalle");

        foreach (var f in Ordered(findings))
        {
            var c = f.Component;
            table.AddRow(
                Dot(f.Color),
                Markup.Escape(c.Kind.ToString()) + (c.IsOnPath ? " [green]*[/]" : ""),
                Markup.Escape(c.Version.Raw),
                VerdictMarkup(f),
                Markup.Escape(c.Source),
                Markup.Escape(f.Headline));
        }

        console.Write(table);
        console.WriteLine();

        // Detalle de mitigaciones para lo rojo y lo incierto.
        foreach (var f in Ordered(findings).Where(x => x.Color != AuditColor.Green))
            RenderDetail(console, dataset, f);

        RenderSummary(console, findings);
    }

    private static void RenderDetail(IAnsiConsole console, AuditDataset dataset, AuditFinding f)
    {
        var rows = new List<string>();
        var c = f.Component;
        rows.Add($"[bold]{Markup.Escape(c.Kind.ToString())} {Markup.Escape(c.Version.Raw)}[/]  [grey]{Markup.Escape(c.ExecutablePath)}[/]");
        rows.Add(Markup.Escape(f.Headline));

        foreach (var a in f.MatchedAdvisories)
        {
            var aliases = a.Aliases.Count > 0 ? $" ({string.Join(", ", a.Aliases)})" : "";
            var kev = a.Kev ? " [red]KEV[/]" : "";
            rows.Add($"  [red]{Markup.Escape(a.Id)}[/]{Markup.Escape(aliases)}  CVSS {a.Cvss:0.0}{kev}  —  {Markup.Escape(a.Title)}");
        }

        if (f.Mitigations.Count > 0)
        {
            rows.Add($"[bold]{Markup.Escape(dataset.Remediation.MitigationHeader)}[/]");
            foreach (var m in f.Mitigations)
                rows.Add($"  · {Markup.Escape(m.ToString())}");
            rows.Add($"[bold]ARREGLO REAL:[/] {Markup.Escape(dataset.Remediation.RealFix)}");
        }

        foreach (var note in f.Notes)
            rows.Add($"[grey]– {Markup.Escape(note)}[/]");

        var border = f.Color == AuditColor.Red ? Color.Red : Color.Yellow;
        var panel = new Panel(string.Join("\n", rows))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(border),
            Header = new PanelHeader($" {(f.Uncertain ? "INCIERTO" : f.VerdictLabel)} "),
        };
        console.Write(panel);
    }

    private static void RenderSummary(IAnsiConsole console, IReadOnlyList<AuditFinding> findings)
    {
        int red = findings.Count(f => f.Color == AuditColor.Red);
        int amber = findings.Count(f => f.Color == AuditColor.Amber);
        int green = findings.Count(f => f.Color == AuditColor.Green);
        int blocking = findings.Count(f => f.IsBlocking);

        console.MarkupLineInterpolated(
            $"[bold]Resumen:[/]  [red]{red} rojo[/]  ·  [yellow]{amber} ambar[/]  ·  [green]{green} verde[/]");
        if (blocking > 0)
            console.MarkupLineInterpolated($"[red bold]{blocking} instalacion(es) vulnerable(s) o EOL con certeza → exit != 0.[/]");
        else if (amber > 0)
            console.MarkupLine("[yellow]Sin rojos ciertos, pero hay ambar que conviene verificar a mano.[/]");
        else
            console.MarkupLine("[green]Todo verde frente a los avisos conocidos.[/]");
    }

    public static void RenderJson(AuditDataset dataset, IReadOnlyList<AuditFinding> findings, DatasetOrigin origin,
        Core.Scanning.ComponentFilter? only = null, int skipped = 0) =>
        Console.WriteLine(AuditReport.ToJson(dataset, findings, JsonOutput.Options, origin, only, skipped));

    private static IEnumerable<AuditFinding> Ordered(IEnumerable<AuditFinding> findings) => AuditReport.Ordered(findings);
}
