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

namespace Roost.Cli.Commands.Services;

/// <summary>Lo comun de Redis y Mailpit: instalarlos la primera vez (con su progreso) y lo que cuenta el daemon de ellos.</summary>
internal static class ServiceSetup
{
    /// <summary>Instala con el progreso en pantalla y dice que el SHA-256 se ha verificado.</summary>
    public static async Task<T> InstallAsync<T>(string what, Uri url, Func<IProgress<long>, CancellationToken, Task<T>> install, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLineInterpolated($"Instalando {what} desde {url.Host}...");
        T result = default!;
        await AnsiConsole.Status().StartAsync($"Instalando {what}...", async ctx =>
        {
            var progress = new Progress<long>(bytes => ctx.Status($"Instalando {what}... {bytes / 1048576.0:0.0} MB"));
            result = await install(progress, cancellation);
        });
        AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {what} [grey](SHA-256 verificado)[/]");
        return result;
    }

    /// <summary>El Redis que usa el stack; si no hay ninguno, instala el de esta version de Roost.</summary>
    public static async Task<InstalledRedis> EnsureRedisAsync(RoostPaths paths, CancellationToken cancellation)
    {
        if (new RedisInstaller(paths).Locate() is { } found) return found;
        var build = RedisCatalog.Current;
        var installer = new RedisInstaller(paths, RoostHttp.Client);
        return await InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
    }

    /// <summary>El Mailpit que usa el stack; si no hay ninguno, instala el de esta version de Roost.</summary>
    public static async Task<InstalledMailpit> EnsureMailpitAsync(RoostPaths paths, CancellationToken cancellation)
    {
        if (new MailpitInstaller(paths).Locate() is { } found) return found;
        var build = MailpitCatalog.Current;
        var installer = new MailpitInstaller(paths, RoostHttp.Client);
        return await InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
    }

    /// <summary>El estado de un servicio del stack («Running», «Stopped»…), o null si el stack no esta en marcha o no lo lleva.</summary>
    public static async Task<ServiceDto?> ServiceAsync(RoostPaths paths, string name, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        return await client.TryStatusAsync(cancellation) is { Running: true } status ? status.Services.FirstOrDefault(s => s.Name == name) : null;
    }

    public static string StateText(ServiceDto? service, bool enabled) => service?.State switch
    {
        "Running" => "[green]en marcha[/]",
        "Restarting" => "[yellow]reiniciando[/]",
        "Failed" => "[red]fallando[/]",
        "Stopped" => "parado a mano [grey](roost start)[/]",
        _ => enabled ? "parado [grey](arranca con roost up)[/]" : "[grey]no va en el stack[/]",
    };
}

/// <summary><c>roost redis install</c>: Redis verificado en bin/redis y en el stack ([redis] enabled), aplicado si esta en marcha.</summary>
public sealed class RedisInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var build = RedisCatalog.Current;
            var installer = new RedisInstaller(paths, RoostHttp.Client);
            var redis = installer.Find(build);
            if (redis is null)
                redis = await ServiceSetup.InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
            else
                AnsiConsole.MarkupLineInterpolated($"[green]{build.Label} ya esta instalado:[/] {redis.Directory}");

            RoostConfig.SetRedis(paths.ConfigFile, enabled: true);
            var config = RoostConfig.Load(paths.ConfigFile);
            AnsiConsole.MarkupLineInterpolated($"[green]Redis va en el stack[/] en 127.0.0.1:{config.RedisPort} [grey](datos en {paths.RedisData}; tus ajustes, en {RedisConf.UserFragmentPath(paths)})[/]");
            RedisHints.Env(config.RedisPort);
            return await StackApply.IfRunningAsync(paths, _ => $"Redis en 127.0.0.1:{config.RedisPort}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost redis remove</c>: lo saca del stack y quita sus binarios. Los datos de data/redis se quedan.</summary>
public sealed class RedisRemoveCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var installer = new RedisInstaller(paths);
            if (RoostConfig.Load(paths.ConfigFile).RedisEnabled)
            {
                RoostConfig.SetRedis(paths.ConfigFile, enabled: false);
                AnsiConsole.MarkupLine("Redis ya no va en el stack.");
                // Primero se para (si el stack esta en marcha): un redis-server vivo no deja quitar su carpeta.
                var applied = await StackApply.IfRunningAsync(paths, _ => "sin Redis.", cancellation);
                if (applied != 0) return applied;
            }

            var installed = installer.ListInstalled();
            if (installed.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No habia ningun Redis de Roost instalado.[/]");
                return 0;
            }
            foreach (var redis in installed)
            {
                installer.Remove(redis);
                AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {redis.Label}.");
            }
            if (Directory.Exists(paths.RedisData))
                AnsiConsole.MarkupLineInterpolated($"[grey]Sus datos siguen en {paths.RedisData}: Roost nunca borra datos. Borralos a mano si ya no los quieres.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost redis enable</c> / <c>disable</c>: Redis entra o sale del stack sin tocar sus binarios ni sus datos.</summary>
public sealed class RedisEnableCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var enable = context.Name == "enable";
        try
        {
            if (enable) await ServiceSetup.EnsureRedisAsync(paths, cancellation);
            RoostConfig.SetRedis(paths.ConfigFile, enabled: enable);
            var port = RoostConfig.Load(paths.ConfigFile).RedisPort;
            AnsiConsole.MarkupLine(enable ? $"[green]Redis va en el stack[/] en 127.0.0.1:{port}." : "Redis ya no va en el stack [grey](sus binarios y sus datos se quedan)[/].");
            return await StackApply.IfRunningAsync(paths, _ => enable ? $"Redis en 127.0.0.1:{port}." : "sin Redis.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost redis port &lt;puerto&gt;</c>: cambia [redis] port y lo aplica si el stack esta en marcha.</summary>
public sealed class RedisPortCommand : AsyncCommand<RedisPortCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PUERTO>")]
        [Description("El puerto de Redis, solo en 127.0.0.1 (6379 por defecto).")]
        public int Port { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            RoostConfig.SetRedis(paths.ConfigFile, port: settings.Port);
            AnsiConsole.MarkupLineInterpolated($"[green]Puerto de Redis:[/] {settings.Port} [grey](acuerdate del REDIS_PORT de tus proyectos)[/]");
            if (!RoostConfig.Load(paths.ConfigFile).RedisEnabled)
            {
                AnsiConsole.MarkupLine("[grey]Redis no va en el stack: se usara cuando lo actives (roost redis enable).[/]");
                return 0;
            }
            return await StackApply.IfRunningAsync(paths, _ => $"Redis en 127.0.0.1:{settings.Port}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost redis status</c>: que Redis hay, si va en el stack, si contesta al PING y donde estan su configuracion y sus datos.</summary>
public sealed class RedisStatusCommand : AsyncCommand<RedisStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        var build = RedisCatalog.Current;
        var installer = new RedisInstaller(paths);
        var redis = installer.Locate();
        var service = await ServiceSetup.ServiceAsync(paths, "redis", cancellation);
        var reply = await RedisProbe.PingAsync(config.RedisPort, cancellation);
        // Otro Redis como servicio de Windows (Memurai, el de un MSI): Roost no lo usa, pero si se queda el puerto, hay que saberlo.
        var others = WindowsServices.Find(ProcessRunner.Default).Where(s => s.Kind == "Redis").ToList();

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = redis is not null,
                version = redis?.Version,
                directory = redis?.Directory,
                roost = new { version = build.Version, installed = installer.Find(build) is not null, url = build.Url.ToString(), sha256 = build.Sha256 },
                enabled = config.RedisEnabled,
                port = config.RedisPort,
                state = service?.State,
                ping = reply,
                conf = RedisConf.ConfPath(paths),
                userConf = RedisConf.UserFragmentPath(paths),
                data = paths.RedisData,
                log = Path.Combine(paths.Logs, "redis.log"),
                windowsServices = others.Select(s => new { name = s.Name, displayName = s.DisplayName, executable = s.Executable, start = s.Start.ToString(), running = s.Running }),
            }, JsonOutput.Options));
            return 0;
        }

        if (redis is null)
        {
            AnsiConsole.MarkupLine("[yellow]Redis no esta instalado.[/]" + " Instalalo (y ponlo en el stack) con: roost redis install");
            AnsiConsole.MarkupLineInterpolated($"[grey]Sera {build.Label} (build MSYS2 de redis-windows, SHA-256 {build.Sha256[..12]}...), en {paths.RedisBin}.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]Redis:[/] {redis.Label} [grey]({redis.Directory})[/]");
            if (installer.Find(build) is null)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Esta version de Roost trae {build.Label}:[/] roost redis install [grey](la de ahora se queda en su carpeta)[/]");
            AnsiConsole.MarkupLine($"En el stack: {(config.RedisEnabled ? "[green]si[/]" : "[grey]no (roost redis enable)[/]")} · 127.0.0.1:{config.RedisPort} · {ServiceSetup.StateText(service, config.RedisEnabled)}");
        }

        AnsiConsole.MarkupLine($"PING: {(RedisProbe.IsPong(reply) ? "[green]PONG[/]" : RedisProbe.NeedsPassword(reply) ? "[yellow]contesta, pero pide contrasena (requirepass)[/]" : reply is null ? "[grey]no contesta nadie en ese puerto[/]" : Markup.Escape(reply))}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Configuracion: {RedisConf.ConfPath(paths)} (generada) · tus ajustes: {RedisConf.UserFragmentPath(paths)}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Datos: {paths.RedisData} · log: {Path.Combine(paths.Logs, "redis.log")}[/]");
        if (redis is not null)
            AnsiConsole.MarkupLineInterpolated($"[grey]Consola: \"{redis.Cli}\" -p {config.RedisPort}[/]");
        foreach (var other in others)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] hay otro Redis como servicio de Windows, {WindowsServices.Label(other)}, que {WindowsServices.StateText(other)}. Roost no lo usa ni lo toca; si se queda el {config.RedisPort}, cambia el puerto (roost redis port <puerto>) o ponlo en Manual en services.msc.");
        }
        if (redis is not null) RedisHints.Env(config.RedisPort);
        return 0;
    }
}

/// <summary>Lo que se le cuenta al usuario de Redis y sus proyectos.</summary>
internal static class RedisHints
{
    /// <summary>
    /// Las variables de Laravel. phpredis no viene en los PHP de windows.php.net (es PECL, sin
    /// checksums): con predis por Composer no hace falta ninguna extension.
    /// </summary>
    public static void Env(int port)
    {
        AnsiConsole.MarkupLine("[grey]Para tu .env (Laravel), con predis: composer require predis/predis[/]");
        foreach (var line in RedisConf.EnvLines(port).Split('\n'))
            AnsiConsole.MarkupLineInterpolated($"  [grey]{line}[/]");
    }
}
