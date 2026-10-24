using Roost.Core.Platform;
using Roost.Daemon;
using Spectre.Console;

namespace Roost.Cli.Commands;

/// <summary>
/// Tras cambiar configuracion o sitios: si el stack esta en marcha, se le aplica el cambio. El
/// daemon solo toca lo que cambia (recarga nginx, arranca un PHP, reinicia la base de datos si es
/// suya la novedad) y dice lo que ha hecho, que se ensena debajo.
/// </summary>
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
            PrintApplied(response, partial: true);
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Aplicado:[/] {describe(response.Status)}");
        PrintApplied(response, partial: false);
        return 0;
    }

    /// <summary>Lo que ha hecho el daemon con el stack en marcha («recargado nginx; arrancado php-7.4»).</summary>
    public static void PrintApplied(CommandResponse response, bool partial)
    {
        if (response.Applied.Count > 0)
            AnsiConsole.MarkupLineInterpolated($"  [grey]{(partial ? "Lo que si se ha hecho: " : "")}{string.Join("; ", response.Applied)}[/]");
        else if (!partial)
            AnsiConsole.MarkupLine("  [grey]sin tocar nada de lo que esta en marcha[/]");
    }
}
