using Roost.Core.Platform;
using Roost.Daemon;
using Spectre.Console;

namespace Roost.Cli.Commands;

/// <summary>Tras cambiar configuracion o sitios: si el stack esta en marcha, se rearranca para aplicarlo.</summary>
internal static class StackApply
{
    public static async Task<int> IfRunningAsync(RoostPaths paths, Func<StatusResponse, string> describe, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        if (await client.TryStatusAsync(cancellation) is not { Running: true })
        {
            AnsiConsole.MarkupLine("[grey]Se aplica en el proximo roost up.[/]");
            return 0;
        }

        var response = await client.UpAsync(cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo aplicar:[/] {response.Error}");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Aplicado:[/] {describe(response.Status)}");
        return 0;
    }
}
