using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Services;
using Roost.Core.Stack;
using Roost.WebPanel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.WebPanel;

/// <summary>
/// <c>roost panel up [puerto]</c> (alias <c>roost cp</c>): el panel web local (ROOST.md, decision 42), la misma
/// interfaz que la app en el navegador, para donde la app no sirve (Linux sin escritorio, servidores, WSL,
/// un equipo remoto por un tunel ssh). Lo sirve un proceso aparte (<c>roost panel serve</c>, oculto) en
/// 127.0.0.1, con un token nuevo en cada arranque que va en la URL que dan <c>up</c> y <c>url</c>.
/// </summary>
public sealed class PanelUpCommand : AsyncCommand<PanelUpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[puerto]")]
        [Description("El puerto en 127.0.0.1 (5556 por defecto). Nunca escucha fuera del equipo: desde otro, ssh -L 5556:127.0.0.1:5556.")]
        public int? Port { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON (pid, puerto y URL con el token).")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var port = settings.Port ?? PanelState.DefaultPort;
        if (port is < 1 or > 65535)
        {
            AnsiConsole.MarkupLine("[red]El puerto tiene que estar entre 1 y 65535.[/]");
            return 1;
        }

        if (PanelState.ReadAlive(paths) is { } running)
        {
            if (settings.Json) { Console.WriteLine(JsonSerializer.Serialize(new { running = true, running.Pid, running.Port, running.Url }, JsonOutput.Options)); return 0; }
            AnsiConsole.MarkupLineInterpolated($"El panel ya esta en marcha (pid {running.Pid}{(running.Port == port ? "" : $", en el puerto {running.Port}")}).");
            AnsiConsole.MarkupLineInterpolated($"  [bold]{running.Url}[/]");
            AnsiConsole.MarkupLine("  [grey]Para cambiar de puerto: roost panel down y despues roost panel up <puerto>.[/]");
            return 0;
        }

        if (StackPlanner.Probe(port) != PortState.Free)
        {
            var owner = PortOwners.Find(port);
            AnsiConsole.MarkupLineInterpolated($"[red]El puerto {port} esta ocupado{(owner is null ? "" : $": {PortOwners.Describe(owner)}")}.[/] Prueba otro: roost panel up {port + 1}");
            return 1;
        }

        PanelState.Delete(paths);
        Directory.CreateDirectory(paths.Logs);
        using var process = Process.Start(PanelLauncher.ForCurrentCli(port));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        PanelInfo? info = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellation);
            info = PanelState.Read(paths);
            if (info is not null && info.Port == port && PanelState.IsAlive(info)) break;
            info = null;
            if (process is { HasExited: true }) break;
        }
        if (info is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]El panel no arranca.[/] Mira {PanelHost.LogFile(paths)}");
            return 1;
        }

        if (settings.Json) { Console.WriteLine(JsonSerializer.Serialize(new { running = true, info.Pid, info.Port, info.Url }, JsonOutput.Options)); return 0; }
        AnsiConsole.MarkupLineInterpolated($"[green]Panel en marcha[/] [grey](pid {info.Pid}, solo en este equipo)[/]");
        AnsiConsole.MarkupLineInterpolated($"  [bold]{info.Url}[/]");
        AnsiConsole.MarkupLine("  [grey]La direccion lleva un codigo nuevo en cada arranque: abrela en el navegador de este equipo. Desde otro, un tunel ssh:[/]");
        AnsiConsole.MarkupLineInterpolated($"  [grey]ssh -L {info.Port}:127.0.0.1:{info.Port} <usuario>@<este-equipo>  y despues la misma direccion.[/]");
        AnsiConsole.MarkupLine("  [grey]Para pararlo: roost panel down[/]");
        return 0;
    }
}

/// <summary><c>roost panel down</c>: para el panel. El stack no se toca.</summary>
public sealed class PanelDownCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var info = PanelState.ReadAlive(paths);
        if (info is null)
        {
            PanelState.Delete(paths);
            AnsiConsole.MarkupLine("El panel ya esta parado.");
            return 0;
        }

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (OperatingSystem.IsWindows())
            {
                process.Kill(entireProcessTree: true);
            }
            else
            {
                // SIGTERM: el host de ASP.NET para limpio y borra run/panel.json; si no se va, SIGKILL.
                Posix.Kill(info.Pid, Posix.SignalNumber(StopSignal.Term));
                if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            }
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]El proceso {info.Pid} ya no estaba ({ex.Message}).[/]");
        }
        PanelState.Delete(paths);
        AnsiConsole.MarkupLine("[green]Panel parado.[/] [grey]El stack sigue como estaba (roost down lo para).[/]");
        return 0;
    }
}

/// <summary><c>roost panel status</c>: si esta en marcha, su pid y su puerto (la URL con el token la da <c>roost panel url</c>).</summary>
public sealed class PanelStatusCommand : Command<PanelStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var info = PanelState.ReadAlive(paths);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(info is null ? new { running = false, pid = (int?)null, port = (int?)null, url = (string?)null } : new { running = true, pid = (int?)info.Pid, port = (int?)info.Port, url = (string?)info.PlainUrl }, JsonOutput.Options));
            return 0;
        }
        if (info is null)
        {
            AnsiConsole.MarkupLine("El panel esta parado. Arrancalo con: roost panel up");
            return 0;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Panel en marcha[/] (pid {info.Pid}) en {info.PlainUrl} desde las {info.Started.ToLocalTime():HH:mm}");
        AnsiConsole.MarkupLine("  [grey]La direccion con el codigo de acceso: roost panel url[/]");
        return 0;
    }
}

/// <summary><c>roost panel url</c>: la URL con el token de esta sesion (solo la ve quien puede leer run/panel.json: tu usuario).</summary>
public sealed class PanelUrlCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var info = PanelState.ReadAlive(RoostPaths.Default());
        if (info is null)
        {
            AnsiConsole.MarkupLine("El panel esta parado. Arrancalo con: roost panel up");
            return 1;
        }
        Console.WriteLine(info.Url);
        return 0;
    }
}

/// <summary><c>roost panel serve --port N</c> (oculto): el proceso del panel. Lo lanza <c>up</c>, no el usuario.</summary>
public sealed class PanelServeCommand : AsyncCommand<PanelServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PUERTO>")]
        public int Port { get; init; } = PanelState.DefaultPort;
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        PanelHost.RunAsync(settings.Port, cancellation);
}
