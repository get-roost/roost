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
/// <c>roost autostart on|off|status</c> (E4): arrancar el stack al iniciar sesion sin abrir la app. En
/// Windows, una tarea programada del usuario (<see cref="LogonTask"/>), sin permisos de administrador;
/// en Linux, una unidad de <c>systemd --user</c> o, sin ella, un .desktop de autostart
/// (<see cref="UserUnit"/>). La app lo hace con la casilla de Ajustes.
/// </summary>
internal static class Autostart
{
    /// <summary>El roost que hay que lanzar: este mismo (el de la carpeta de Roost, no un dotnet).</summary>
    public static string? RoostExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "roost.exe" : "roost");
        return File.Exists(beside) ? beside : null;
    }

    public static LogonTask Task => new(CommandRunner.Default);

    public static UserUnit Unit => new(CommandRunner.Default);

    /// <summary>Que este sistema tenga autoarranque (Windows y Linux); si no, lo dice y devuelve false.</summary>
    public static bool Supported()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) return true;
        AnsiConsole.MarkupLine("[yellow]roost autostart solo esta hecho para Windows (tarea programada) y Linux (systemd --user o un .desktop).[/]");
        return false;
    }
}

public sealed class AutostartOnCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!Autostart.Supported()) return 1;
        if (Autostart.RoostExe() is not { } exe)
        {
            AnsiConsole.MarkupLine($"[yellow]No se encuentra {(OperatingSystem.IsWindows() ? "roost.exe" : "roost")} junto a este roost: el arranque necesita su ruta. Ejecutalo desde la carpeta de Roost.[/]");
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            if (LogonTask.WhyNot() is { } why)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]{why}[/]");
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

        UserUnitStatus put;
        try
        {
            put = await Autostart.Unit.EnableAsync(exe, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green]Hecho:[/] al iniciar sesion, Roost arrancara el stack (roost up) sin abrir la app.");
        if (put.Kind == AutostartKind.SystemdUser)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Unidad de usuario {UserUnit.UnitName} ({put.File}), activada en default.target; al cerrar la sesion, systemd la para con roost down. Se quita con roost autostart off.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Sin systemd de usuario:[/] {put.Error}.");
            AnsiConsole.MarkupLineInterpolated($"[grey]Va por un .desktop de autostart ({put.File}), que lanza el escritorio (GNOME, KDE, XFCE...) al entrar. Sin escritorio (WSL, un servidor), nada lo lanza: arranca con roost up. Se quita con roost autostart off.[/]");
            if (Wsl.IsWsl) AnsiConsole.MarkupLine("[grey]En WSL, la sesion de usuario de systemd a veces no arranca («Failed to start the systemd user session»): comprueba [[boot]] systemd=true en /etc/wsl.conf y wsl --shutdown desde Windows.[/]");
        }
        if (put.RoostHome is { } home) AnsiConsole.MarkupLineInterpolated($"[grey]Con {RoostPaths.HomeVariable}={home}: arrancara esa Roost.[/]");
        return 0;
    }
}

public sealed class AutostartOffCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!Autostart.Supported()) return 1;
        try
        {
            if (OperatingSystem.IsWindows()) await Autostart.Task.DisableAsync(cancellation);
            else await Autostart.Unit.DisableAsync(cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green]Hecho:[/] Roost ya no arranca el stack al iniciar sesion. [grey](Si lo arrancaba la app, eso sigue en Ajustes. El stack en marcha no se toca.)[/]");
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
        if (!Autostart.Supported()) return 1;
        if (OperatingSystem.IsWindows()) return await WindowsAsync(settings, cancellation);

        var unit = Autostart.Unit;
        var status = unit.Status();
        var stale = status.IsStale(File.Exists);
        var systemd = await unit.WhySystemdUnavailableAsync(cancellation);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = status.Kind switch { AutostartKind.SystemdUser => "systemd-user", AutostartKind.Desktop => "desktop", _ => null },
                file = status.File,
                exists = status.Exists,
                enabled = status.Enabled,
                command = status.Command,
                roostHome = status.RoostHome,
                stale,
                systemdUser = systemd is null,
                systemdUserProblem = systemd,
                error = status.Error,
            }, JsonOutput.Options));
            return status.Error is null ? 0 : 1;
        }

        if (status.Error is { } error)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo leer el arranque al iniciar sesion:[/] {error}");
            return 1;
        }
        if (!status.Exists)
        {
            AnsiConsole.MarkupLine("Arrancar al iniciar sesion sin la app: [grey]no esta puesto[/] [grey](roost autostart on)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"Arrancar al iniciar sesion sin la app: {(status.Enabled ? "[green]puesto[/]" : "[yellow]puesto, pero desactivado[/]")}");
            var home = status.RoostHome is null ? "" : $", con {RoostPaths.HomeVariable}={status.RoostHome}";
            if (status.Kind == AutostartKind.SystemdUser)
                AnsiConsole.MarkupLineInterpolated($"[grey]Unidad de usuario {UserUnit.UnitName} ({status.File}): {status.Command} up{home}[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[grey].desktop de autostart ({status.File}): {status.Command} up{home}; lo lanza el escritorio al entrar[/]");
            if (stale) AnsiConsole.MarkupLine("[yellow]Lanza un roost que ya no esta: vuelve a ponerlo con roost autostart on.[/]");
        }
        if (systemd is null)
            AnsiConsole.MarkupLine("[grey]systemd --user: contesta (roost autostart on usaria una unidad de usuario).[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[grey]systemd --user: {systemd}; roost autostart on usaria un .desktop de autostart.[/]");
        return 0;
    }

    private static async Task<int> WindowsAsync(Settings settings, CancellationToken cancellation)
    {
        var status = await Autostart.Task.StatusAsync(cancellation);
        var stale = status.IsStale(File.Exists);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "task",
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
