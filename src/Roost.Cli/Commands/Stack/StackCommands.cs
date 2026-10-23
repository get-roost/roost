using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Stack;

/// <summary>
/// <c>roost up</c>: arranca (o vuelve a arrancar con la configuracion actual) el stack local:
/// el pool de php-cgi del PHP global y el servidor web ([web] server: nginx o Apache) en
/// http://localhost/. La primera vez instala el servidor web.
/// Lo hace el daemon, que se lanza solo si no esta en marcha.
/// </summary>
public sealed class UpCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("up")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            // Comprobaciones rapidas antes de lanzar nada: configuracion legible, un PHP que servir
            // y, si roost.toml pide una base de datos, que este instalada.
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var php = planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled());
            planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());

            await WebServerSetup.EnsureAsync(paths, config.WebServer, cancellation);

            using var client = new DaemonClient(paths);
            await client.EnsureRunningAsync(DaemonLauncher.ForCurrentCli(), cancellation);

            CommandResponse response = null!;
            await AnsiConsole.Status().StartAsync($"Arrancando PHP {php.Name} y {WebServerSetup.Label(config.WebServer)}...", async _ =>
                response = await client.UpAsync(cancellation));

            if (!response.Ok)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No se pudo arrancar:[/] {response.Error}");
                return 1;
            }

            var status = response.Status;
            var pool = status.Services.Count(s => s.Name.StartsWith("php-", StringComparison.Ordinal));
            AnsiConsole.MarkupLineInterpolated($"[green]En marcha:[/] [bold]{status.Url}[/]");
            AnsiConsole.MarkupLineInterpolated($"  PHP {status.Php} ({pool} php-cgi) · {status.WebLabel}");
            if (status.Db is null)
                AnsiConsole.MarkupLine("  [grey]Sin base de datos (instala una con: roost db install mariadb@11.4)[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"  {status.Db} en 127.0.0.1:{status.DbPort} [grey](usuario root, sin contrasena)[/]");
            foreach (var site in status.Sites)
                AnsiConsole.MarkupLineInterpolated($"  [bold]{site.Url}[/] [grey](PHP {site.Php})[/]");
            AnsiConsole.MarkupLineInterpolated($"  [grey]www: {paths.Www}   logs: {paths.Logs}[/]");
            UpdateNotice.FromCache(paths);
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

}

/// <summary><c>roost down</c>: para el stack y el daemon.</summary>
public sealed class DownCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(context.Name)) return 1;
        using var client = new DaemonClient(RoostPaths.Default());
        if (await client.TryStatusAsync(cancellation) is null)
        {
            AnsiConsole.MarkupLine("Roost ya esta parado.");
            return 0;
        }

        await client.DownAsync(cancellation);
        AnsiConsole.MarkupLine("[green]Parado.[/]");
        return 0;
    }
}

/// <summary><c>roost restart &lt;servicio&gt;</c>: reinicia nginx, el pool de un PHP o la base de datos.</summary>
public sealed class RestartCommand : AsyncCommand<RestartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<servicio>")]
        [Description("nginx, php-<rama> (todo su pool) o <motor>-<rama>, como los muestra roost status.")]
        public string Service { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(context.Name)) return 1;
        using var client = new DaemonClient(RoostPaths.Default());
        if (await client.TryStatusAsync(cancellation) is not { Running: true })
        {
            AnsiConsole.MarkupLine("Roost esta parado. Arrancalo con: roost up");
            return 1;
        }

        var response = await client.RestartAsync(settings.Service, cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{response.Error}[/]");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Reiniciado:[/] {settings.Service}");
        return 0;
    }
}

/// <summary><c>roost status</c>: que esta en marcha, en que puertos y cuantas veces se ha reiniciado.</summary>
public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de la tabla.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(context.Name)) return 1;
        using var client = new DaemonClient(RoostPaths.Default());
        var status = await client.TryStatusAsync(cancellation);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                status ?? new StatusResponse(0, false, null, null, null, null, null, [], []), JsonOutput.Options));
            return 0;
        }

        if (status is null || !status.Running)
        {
            AnsiConsole.MarkupLine(status is null
                ? "Roost esta parado. Arrancalo con: roost up"
                : "El daemon esta en marcha pero sin stack. Arrancalo con: roost up");
            UpdateNotice.FromCache(RoostPaths.Default());
            return 0;
        }

        var db = status.Db is null ? "sin base de datos" : $"{status.Db} :{status.DbPort}";
        AnsiConsole.MarkupLineInterpolated($"[bold]{status.Url}[/]  ·  PHP {status.Php}  ·  {status.WebLabel}  ·  {db}  [grey](daemon pid {status.DaemonPid})[/]");
        foreach (var site in status.Sites)
            AnsiConsole.MarkupLineInterpolated($"  {site.Url} [grey](PHP {site.Php})[/]");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Servicio");
        table.AddColumn("Estado");
        table.AddColumn("PID");
        table.AddColumn("Puerto");
        table.AddColumn("Reinicios");
        table.AddColumn("Ultimo error");
        foreach (var s in status.Services)
        {
            var state = s.State switch
            {
                "Running" => "[green]en marcha[/]",
                "Restarting" => "[yellow]reiniciando[/]",
                "Failed" => "[red]fallando[/]",
                _ => "parado",
            };
            table.AddRow(
                Markup.Escape(s.Name),
                state,
                s.Pid?.ToString() ?? "-",
                s.Port?.ToString() ?? "-",
                s.Restarts.ToString(),
                Markup.Escape(s.LastError ?? ""));
        }
        AnsiConsole.Write(table);
        UpdateNotice.FromCache(RoostPaths.Default());
        return 0;
    }
}

/// <summary><c>roost daemon</c> (oculto): ejecuta roostd en este proceso. Lo lanza la CLI, no el usuario.</summary>
public sealed class DaemonCommand : AsyncCommand<EmptyCommandSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation) =>
        DaemonHost.RunAsync(cancellation, CliExtensions.Current);
}
