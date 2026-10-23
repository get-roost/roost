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
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var result = CliExtensions.Current.Scanner(RoostPaths.Default()).ScanAll();

        if (settings.Json)
            ScanRenderer.RenderJson(result);
        else
            ScanRenderer.RenderTable(result);

        return 0;
    }
}
