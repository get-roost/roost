using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost autostart on|off|status</c> (E4): una tarea programada del usuario que, al iniciar
/// sesion, lanza <c>roost up</c> sin abrir la app (<see cref="LogonTask"/>). No pide permisos de
/// administrador. La app lo hace con la casilla de Ajustes.
/// </summary>
internal static class Autostart
{
    /// <summary>El roost.exe que hay que lanzar: este mismo (el de la carpeta de Roost, no un dotnet).</summary>
    public static string? RoostExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "roost.exe");
        return File.Exists(beside) ? beside : null;
    }

    public static LogonTask Task => new(CommandRunner.Default);
}

public sealed class AutostartOnCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("autostart on")) return 1;
        if (LogonTask.WhyNot() is { } why)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{why}[/]");
            return 1;
        }
        if (Autostart.RoostExe() is not { } exe)
        {
            AnsiConsole.MarkupLine("[yellow]No se encuentra roost.exe junto a este roost: la tarea necesita su ruta. Ejecutalo desde la carpeta de Roost.[/]");
            return 1;
        }
        try
        {
            await Autostart.Task.EnableAsync(exe, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green]Hecho:[/] al iniciar sesion, Roost arrancara el stack (roost up) sin abrir la app.");
        AnsiConsole.MarkupLineInterpolated($"[grey]Tarea {LogonTask.TaskName}, como {LogonTask.CurrentUser}, sin permisos de administrador. Se quita con roost autostart off.[/]");
        return 0;
    }
}

public sealed class AutostartOffCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("autostart off")) return 1;
        try
        {
            await Autostart.Task.DisableAsync(cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green]Hecho:[/] Roost ya no arranca el stack al iniciar sesion. [grey](Si lo arrancaba la app, eso sigue en Ajustes.)[/]");
        return 0;
    }
}

public sealed class AutostartStatusCommand : AsyncCommand<AutostartStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("autostart status")) return 1;
        var status = await Autostart.Task.StatusAsync(cancellation);
        var stale = status.IsStale(File.Exists);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                task = LogonTask.TaskName,
                exists = status.Exists,
                enabled = status.Enabled,
                command = status.Command,
                arguments = status.Arguments,
                stale,
                error = status.Error,
            }, JsonOutput.Options));
            return status.Error is null ? 0 : 1;
        }

        if (status.Error is { } error)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo preguntar al Programador de tareas:[/] {error}");
            return 1;
        }
        if (!status.Exists)
        {
            AnsiConsole.MarkupLine("Arrancar al iniciar sesion sin la app: [grey]no esta puesto[/] [grey](roost autostart on)[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"Arrancar al iniciar sesion sin la app: {(status.Enabled ? "[green]puesto[/]" : "[yellow]puesto, pero desactivado en el Programador de tareas[/]")}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Tarea {LogonTask.TaskName}: {status.Command} {status.Arguments}[/]");
        if (stale)
            AnsiConsole.MarkupLine("[yellow]Lanza un roost.exe que ya no esta: vuelve a ponerla con roost autostart on.[/]");
        return 0;
    }
}
