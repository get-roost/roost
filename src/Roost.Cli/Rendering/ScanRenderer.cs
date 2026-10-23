using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;
using Spectre.Console;

namespace Roost.Cli.Rendering;

/// <summary>Imprime el resultado de <c>roost scan</c>: tabla legible o JSON.</summary>
public static class ScanRenderer
{
    private static readonly ComponentKind[] KindOrder =
    [
        ComponentKind.Php, ComponentKind.MySql, ComponentKind.MariaDb,
        ComponentKind.Nginx, ComponentKind.Apache,
    ];

    private static string KindTitle(ComponentKind kind) => kind switch
    {
        ComponentKind.Php => "PHP",
        ComponentKind.MySql => "MySQL",
        ComponentKind.MariaDb => "MariaDB",
        ComponentKind.Nginx => "nginx",
        ComponentKind.Apache => "Apache",
        _ => kind.ToString(),
    };

    public static void RenderTable(ScanResult result)
    {
        var console = AnsiConsole.Console;
        console.MarkupLineInterpolated($"[bold]Entorno:[/] {result.OsDescription}  ([grey]{result.RuntimeId}[/])");
        console.MarkupLineInterpolated($"[bold]Componentes encontrados:[/] {result.Components.Count}   [grey](* = activo en el PATH)[/]");
        console.WriteLine();

        if (result.Components.Count == 0)
        {
            console.MarkupLine("[yellow]No se encontro ningun componente.[/] "
                + "El catalogo de scanners aun no esta conectado (lo rellena el otro agente) "
                + "o no hay stacks instalados.");
            return;
        }

        foreach (var kind in KindOrder)
        {
            var items = result.OfKind(kind)
                .OrderByDescending(c => c.Version)
                .ThenBy(c => c.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (items.Count == 0) continue;

            console.Write(new Rule($"[bold]{KindTitle(kind)}[/]  [grey]({items.Count})[/]")
            {
                Justification = Justify.Left,
            });

            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Version");
            table.AddColumn("Source");
            table.AddColumn("SAPI");
            table.AddColumn("PATH");
            table.AddColumn("Arq.");
            table.AddColumn("Ruta");

            foreach (var c in items)
            {
                var onPath = c.IsOnPath ? "[green]*[/]" : "";
                table.AddRow(
                    Markup.Escape(c.Version.ToString()),
                    Markup.Escape(c.Source),
                    Markup.Escape(c.Sapi ?? "-"),
                    onPath,
                    Markup.Escape(c.Architecture ?? "-"),
                    Markup.Escape(c.ExecutablePath));
            }

            console.Write(table);
            console.WriteLine();
        }
    }

    public static void RenderJson(ScanResult result)
    {
        var payload = new
        {
            os = result.OsDescription,
            runtimeId = result.RuntimeId,
            count = result.Components.Count,
            components = result.Components.Select(ToDto),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOutput.Options));
    }

    internal static object ToDto(DetectedComponent c) => AuditReport.ComponentDto(c);
}
