using System.ComponentModel;
using Roost.Core.Audit;
using Roost.Cli.Rendering;
using Roost.Core.Scanning;
using Roost.Core.Platform;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost audit</c>: escanea y evalua cada componente contra los avisos: el feed firmado de
/// roost-catalog (se actualiza como mucho una vez al dia) o, sin el, el dataset empaquetado. La
/// evaluacion es siempre local. Devuelve exit code != 0 si hay algo vulnerable o EOL con certeza,
/// para servir en CI.
/// </summary>
public sealed class AuditCommand : AsyncCommand<AuditCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar del semaforo.")]
        public bool Json { get; init; }

        [CommandOption("--dataset <RUTA>")]
        [Description("Carga el dataset de avisos desde un fichero externo, sin firma (por defecto, el feed firmado o el empaquetado).")]
        public string? DatasetPath { get; init; }

        [CommandOption("--offline")]
        [Description("No pregunta por un feed nuevo: usa el ultimo bueno de la cache o el empaquetado.")]
        public bool Offline { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        AuditDataset dataset;
        DatasetOrigin origin;
        try
        {
            if (settings.DatasetPath is { Length: > 0 } path)
                (dataset, origin) = (AuditDataset.LoadFile(path), new DatasetOrigin(DatasetKind.File));
            else
                (dataset, origin) = await CliFeed.LoadAsync(paths, update: !settings.Offline, cancellation);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo cargar el dataset de auditoria:[/] {ex.Message}");
            return 2;
        }

        dataset = CliExtensions.Current.WithAdvisories(dataset, paths);
        var result = CliExtensions.Current.Scanner(paths).ScanAll();
        var evaluator = new AuditEvaluator(dataset);
        var findings = evaluator.EvaluateAll(result.Components).ToList();

        if (settings.Json)
            AuditRenderer.RenderJson(dataset, findings, origin);
        else
            AuditRenderer.RenderTable(dataset, findings, origin);

        // Exit != 0 si algo es vulnerable o EOL con certeza (lo ambar/incierto no bloquea).
        return findings.Any(f => f.IsBlocking) ? 1 : 0;
    }
}
