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
/// <c>roost up</c>: arranca el stack local (el pool de php-cgi del PHP global y el servidor web,
/// [web] server: nginx o Apache, en http://localhost/) o, si ya esta en marcha, le aplica la
/// configuracion actual tocando solo lo que cambia. La primera vez instala el servidor web.
/// Lo hace el daemon, que se lanza solo si no esta en marcha.
/// </summary>
public sealed class UpCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
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
            await GrantLowPortsAsync(config, paths, cancellation);
            // Redis y Mailpit, si van en el stack y aun no hay ninguno (roost.toml editado a mano).
            if (config.RedisEnabled) await Services.ServiceSetup.EnsureRedisAsync(paths, cancellation);
            if (config.MailEnabled) await Services.ServiceSetup.EnsureMailpitAsync(paths, cancellation);
            WarnAboutSystemServices(config, paths, globalDb is not null);
            // Sitios avanzados: lo que cambie en un .roost.toml o en una carpeta aparcada, antes de aplicar.
            Sites.SiteSources.PrintSync(paths);

            using var client = new DaemonClient(paths);
            var wasRunning = (await client.EnsureRunningAsync(DaemonLauncher.ForCurrentCli(), cancellation)).Running;

            CommandResponse response = null!;
            var doing = wasRunning ? "Aplicando la configuracion" : $"Arrancando PHP {php.Name} y {WebServerSetup.Label(config.WebServer)}";
            await AnsiConsole.Status().StartAsync(doing + "...", async _ =>
                response = await client.UpAsync(cancellation));

            if (!response.Ok)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{(wasRunning ? "No se pudo aplicar" : "No se pudo arrancar")}:[/] {response.Error}");
                StackApply.PrintApplied(response, partial: true);
                return 1;
            }

            var status = response.Status;
            var pool = status.Services.Count(s => s.Name.StartsWith("php-", StringComparison.Ordinal));
            AnsiConsole.MarkupLineInterpolated($"[green]En marcha:[/] [bold]{status.Url}[/]");
            // Ya estaba en marcha: el daemon solo ha tocado lo que cambiaba, y lo dice.
            if (wasRunning) StackApply.PrintApplied(response, partial: false);
            // En Linux el pool de cada rama es un solo php-fpm (con sus procesos dentro); en Windows, n php-cgi.
            var phpProcesses = !OperatingSystem.IsWindows() && pool == 1 ? "php-fpm" : $"{pool} php-cgi";
            AnsiConsole.MarkupLineInterpolated($"  PHP {status.Php} ({phpProcesses}) · {status.WebLabel}");
            if (status.Db is null)
                AnsiConsole.MarkupLine("  [grey]Sin base de datos" + PlatformGate.WindowsHint(" (instala una con: roost db install mariadb@11.4)") + "[/]");
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
            SuggestSharedWww(paths);
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
    /// E7: los servicios del sistema (en Windows, un MySQL80 o una MariaDB de su MSI, Apache, IIS, otro
    /// Redis; en Linux, el apache2 o el mysql de la distro, por systemd) que arrancan con el sistema o estan
    /// en marcha y suelen usar un puerto de este stack. Solo avisa: Roost nunca cambia, para ni arranca
    /// servicios. Si al final se queda el puerto, el daemon lo dice en el error con el nombre del servicio.
    /// </summary>
    private static void WarnAboutSystemServices(RoostConfig config, RoostPaths paths, bool withDb)
    {
        var sites = 0;
        try { sites = new Roost.Core.Sites.SiteRegistry(paths).Load().Count; } catch (Exception ex) when (ex is IOException or RoostException) { }
        var system = SystemServices.Current;
        var conflicts = WindowsServices.Conflicts(system.Find(ProcessRunner.Default), config.PlannedPorts(sites > 0, withDb));
        foreach (var conflict in conflicts)
        {
            var service = conflict.Service;
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Aviso:[/] {system.Describe(service)} y suele usar el puerto {string.Join(" y el ", conflict.Ports)}, que tambien es de este stack.");
            AnsiConsole.MarkupLineInterpolated(
                $"  [grey]Si se lo queda, Roost no podra arrancar. {system.HowToFree(service)}, o cambia el puerto en {paths.ConfigFile}. {system.Disclaimer}[/]");
        }
    }

    /// <summary>
    /// Linux: con [web] port o https_port por debajo del 1024, el binario de nginx necesita la capability
    /// cap_net_bind_service (un usuario normal no puede abrir esos puertos). Si aun no la tiene (la primera
    /// vez, o tras instalar otra version de nginx, que la pierde), se pide al helper con su aviso de permiso.
    /// Apache, el de la distro, no se toca: con el, puertos a partir del 1024.
    /// </summary>
    private static async Task GrantLowPortsAsync(RoostConfig config, RoostPaths paths, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsLinux() || config.UsesApache) return;
        var low = new[] { config.HttpPort, config.HttpsPort }.Where(port => port < 1024).Distinct().ToList();
        if (low.Count == 0) return;
        if (new NginxInstaller(paths).Find(NginxCatalog.Current) is not { } nginx) return;   // sin nginx, lo dira el daemon
        if (await NginxCapability.IsGrantedAsync(CommandRunner.Default, nginx.Executable, cancellation)) return;

        AnsiConsole.MarkupLine(Sites.SiteSupport.ElevationNotice($"que nginx pueda escuchar en el {string.Join(" y el ", low)} (setcap)"));
        NginxCapability.Request(Sites.SiteSupport.HelperExe, nginx.Executable);
        AnsiConsole.MarkupLineInterpolated($"[green]nginx ya puede escuchar en el {string.Join(" y el ", low)}[/] [grey]({nginx.Executable})[/]");
    }

    /// <summary>
    /// Linux: si los proyectos van en la www de respaldo bajo la raiz porque no existe /var/www/roost (o no
    /// es del usuario), se dice como tenerla ahi, como en un servidor. Con ROOST_HOME no se sugiere nada.
    /// </summary>
    private static void SuggestSharedWww(RoostPaths paths)
    {
        if (!OperatingSystem.IsLinux() || !paths.WwwIsFallback) return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RoostPaths.HomeVariable))) return;
        if (Directory.Exists(RoostPaths.LinuxSharedWww))
            AnsiConsole.MarkupLineInterpolated($"  [grey]{RoostPaths.LinuxSharedWww} existe pero no es tuya: los proyectos van en {paths.Www}. Para usarla, pon [[paths]] www en {paths.ConfigFile} o pide a su dueno que te deje escribir.[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"  [grey]Para tener los proyectos en {RoostPaths.LinuxSharedWww}, como en un servidor (en vez de en {paths.Www}): roost www init[/]");
    }
}

/// <summary><c>roost down</c>: para el stack y el daemon.</summary>
public sealed class DownCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        using var client = new DaemonClient(RoostPaths.Default());
        if (await client.TryStatusAsync(cancellation) is null)
        {
            AnsiConsole.MarkupLine("Roost ya esta parado.");
            return 0;
        }

        // Compartir (oleada 6): lo compartido se para con el stack; lo de la red local deja su regla del cortafuegos.
        var shared = await Share.ShareSupport.CurrentAsync(client, cancellation);
        await client.DownAsync(cancellation);
        AnsiConsole.MarkupLine("[green]Parado.[/]");
        Share.ShareSupport.AfterDown(shared);
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
        // El pool de un PHP va uno a uno: siempre queda algun php-cgi atendiendo.
        var oneByOne = response.Applied.Any(a => a.EndsWith("(uno a uno)", StringComparison.Ordinal));
        AnsiConsole.MarkupLine($"[green]Reiniciado:[/] {Markup.Escape(all ? "todo el stack" : settings.Service!)}"
            + (oneByOne ? " [grey](uno a uno, sin dejar de atender)[/]" : ""));
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
