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
            var globalDb = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());

            await WebServerSetup.EnsureAsync(paths, config.WebServer, cancellation);
            // Redis y Mailpit, si van en el stack y aun no hay ninguno (roost.toml editado a mano).
            if (config.RedisEnabled) await Services.ServiceSetup.EnsureRedisAsync(paths, cancellation);
            if (config.MailEnabled) await Services.ServiceSetup.EnsureMailpitAsync(paths, cancellation);
            WarnAboutWindowsServices(config, paths, globalDb is not null);

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
            {
                // Si root tiene contraseña, la guarda Roost (roost db password): no se enseña aquí.
                var root = "usuario root, sin contrasena";
                try
                {
                    if (new StackPlanner(paths, ProcessRunner.Default).ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled()) is { } db
                        && new DbPasswords(paths).For(db) is not null)
                        root = "usuario root, con la contrasena que guarda Roost";
                }
                catch (RoostException) { /* se queda el texto de siempre */ }
                AnsiConsole.MarkupLineInterpolated($"  {status.Db} en 127.0.0.1:{status.DbPort} [grey]({root})[/]");
            }
            if (status.Services.FirstOrDefault(s => s.Name == "redis") is { Port: { } redisPort })
                AnsiConsole.MarkupLineInterpolated($"  Redis en 127.0.0.1:{redisPort}");
            if (status.Services.FirstOrDefault(s => s.Name == "mailpit") is { Port: { } smtpPort })
                AnsiConsole.MarkupLineInterpolated($"  Mailpit: SMTP en 127.0.0.1:{smtpPort} · bandeja en [bold]{config.MailWebUrl}[/] [grey](el mail() de PHP va ahi)[/]");
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

    /// <summary>
    /// E7: los servicios de Windows (un MySQL80 o una MariaDB de su MSI, Apache, IIS, otro Redis) que
    /// arrancan con Windows o estan en marcha y suelen usar un puerto de este stack. Solo avisa:
    /// Roost nunca cambia, para ni arranca servicios. Si al final se queda el puerto, el daemon lo
    /// dice en el error con el nombre del servicio.
    /// </summary>
    private static void WarnAboutWindowsServices(RoostConfig config, RoostPaths paths, bool withDb)
    {
        var sites = 0;
        try { sites = new Roost.Core.Sites.SiteRegistry(paths).Load().Count; } catch (Exception ex) when (ex is IOException or RoostException) { }
        var conflicts = WindowsServices.Conflicts(WindowsServices.Find(ProcessRunner.Default), config.PlannedPorts(sites > 0, withDb));
        foreach (var conflict in conflicts)
        {
            var service = conflict.Service;
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Aviso:[/] el servicio de Windows {WindowsServices.Label(service)} {WindowsServices.StateText(service)} y suele usar el puerto {string.Join(" y el ", conflict.Ports)}, que tambien es de este stack.");
            AnsiConsole.MarkupLineInterpolated(
                $"  [grey]Si se lo queda, Roost no podra arrancar. Ponlo en Manual en services.msc (o, en una consola de administrador: {WindowsServices.ManualCommand(service)}) y paralo ({WindowsServices.StopCommand(service)}), o cambia el puerto en {paths.ConfigFile}. Roost no toca servicios de Windows.[/]");
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

/// <summary>Un servicio o grupo del stack, como lo muestra <c>roost status</c>.</summary>
public sealed class ServiceSettings : CommandSettings
{
    [CommandArgument(0, "<servicio>")]
    [Description("nginx, apache, php-<rama> (todo su pool), <motor>-<rama>, redis, mailpit o dns, como los muestra roost status.")]
    public string Service { get; init; } = "";
}

/// <summary>
/// <c>roost restart [servicio]</c>: reinicia nginx, el pool de un PHP o la base de datos sin
/// tocar el resto; sin servicio, todo el stack en orden, con el mismo plan.
/// </summary>
public sealed class RestartCommand : AsyncCommand<RestartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[servicio]")]
        [Description("nginx, apache, php-<rama> (todo su pool), <motor>-<rama>, redis, mailpit o dns, como los muestra roost status. Sin servicio, se reinicia todo.")]
        public string? Service { get; init; }
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

        var all = string.IsNullOrWhiteSpace(settings.Service);
        CommandResponse response = null!;
        if (all)
            await AnsiConsole.Status().StartAsync("Reiniciando todo...", async _ => response = await client.RestartAllAsync(cancellation));
        else
            response = await client.RestartAsync(settings.Service!, cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{response.Error}[/]");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Reiniciado:[/] {(all ? "todo el stack" : settings.Service)}");
        return 0;
    }
}

/// <summary>
/// <c>roost stop &lt;servicio&gt;</c>: para un servicio o grupo sin tocar el resto (la base de
/// datos, limpia). Queda parado hasta <c>roost start</c>, <c>roost restart</c> o el siguiente <c>up</c>.
/// </summary>
public sealed class StopServiceCommand : AsyncCommand<ServiceSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ServiceSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(context.Name)) return 1;
        using var client = new DaemonClient(RoostPaths.Default());
        if (await client.TryStatusAsync(cancellation) is not { Running: true })
        {
            AnsiConsole.MarkupLine("Roost esta parado: no hay nada que parar. Para parar todo el stack: roost down");
            return 1;
        }

        var response = await client.StopServiceAsync(settings.Service, cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{response.Error}[/]");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Parado:[/] {settings.Service} [grey](vuelve con: roost start {settings.Service})[/]");
        return 0;
    }
}

/// <summary><c>roost start &lt;servicio&gt;</c>: vuelve a arrancar un servicio o grupo parado con <c>roost stop</c>.</summary>
public sealed class StartServiceCommand : AsyncCommand<ServiceSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ServiceSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(context.Name)) return 1;
        using var client = new DaemonClient(RoostPaths.Default());
        if (await client.TryStatusAsync(cancellation) is not { Running: true })
        {
            AnsiConsole.MarkupLine("Roost esta parado. Para arrancar todo el stack: roost up");
            return 1;
        }

        var response = await client.StartServiceAsync(settings.Service, cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{response.Error}[/]");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Arrancado:[/] {settings.Service}");
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
