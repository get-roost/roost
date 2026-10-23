using System.ComponentModel;
using Roost.Cli.Rendering;
using Roost.Core.Scanning;
using Roost.Core.Platform;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost scan</c>: escanea el entorno con el motor de Roost.Core e imprime
/// el inventario de componentes, agrupado por tipo.
/// </summary>
public sealed class ScanCommand : Command<ScanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de la tabla.")]
        public bool Json { get; init; }

        [CommandOption("--only <TIPOS>")]
        [Description("Solo esos tipos (php, mysql, mariadb; varios separados por comas).")]
        public string? Only { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!ComponentFilter.TryParse(settings.Only, out var only, out var error))
        {
            Spectre.Console.AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
            return AuditCommand.BadUsage;
        }

        var result = CliExtensions.Current.Scanner(RoostPaths.Default()).ScanAll();
        if (only is not null) result = result with { Components = only.Apply(result.Components) };

        if (settings.Json)
            ScanRenderer.RenderJson(result);
        else
            ScanRenderer.RenderTable(result);

        return 0;
    }
}
