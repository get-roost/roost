using System.ComponentModel;
using Roost.Cli.Net;
using Roost.Cli.Commands;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Stack;

/// <summary>Lo comun del servidor web: instalar el que pide roost.toml si falta (la primera vez).</summary>
internal static class WebServerSetup
{
    /// <summary>El nombre para la salida: «nginx» o «Apache».</summary>
    public static string Label(string server) => server == RoostConfig.WebApache ? "Apache" : "nginx";

    public static async Task EnsureAsync(RoostPaths paths, string server, CancellationToken cancellation)
    {
        if (server == RoostConfig.WebApache)
        {
            var installer = new ApacheInstaller(paths, RoostHttp.Client);
            var build = ApacheCatalog.Current;
            if (installer.Find(build) is { } found)
            {
                if (found.IsDistro) WarnAboutDistroApache(paths, found);
                return;
            }
            if (build.IsDistro)
            {
                // Linux: es el de la distro y Roost no instala paquetes del sistema; InstallAsync lo dice (y como ponerlo).
                await installer.InstallAsync(build, null, cancellation);
                return;
            }
            // Apache de Apache Lounge no arranca sin el VC++ 14.50 o posterior: avisar antes, con el enlace.
            WarnVcRedist(forApache: true);
            await InstallAsync($"Apache {build.Version}", Source(paths, build.FileName, build.Url), (progress, ct) => installer.InstallAsync(build, progress, ct), cancellation);
        }
        else
        {
            var installer = new NginxInstaller(paths, RoostHttp.Client);
            var build = NginxCatalog.Current;
            if (installer.Find(build) is not null) return;
            // nginx no lo necesita, pero el PHP que va a servir si (en Windows el VC++; en Linux, la glibc).
            WarnVcRedist(forApache: false);
            WarnLinuxDeps(LinuxNeed.Php);
            await InstallAsync($"nginx {build.Version}", Source(paths, build.FileName, build.Url), (progress, ct) => installer.InstallAsync(build, progress, ct), cancellation);
        }
    }

    private static void WarnVcRedist(bool forApache)
    {
        if (VcRedist.Warning(VcRedist.Detect(), forApache) is { } warning)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {warning}");
    }

    /// <summary>
    /// Linux: lo que hay que saber del Apache de la distro antes de elegirlo: cual es, que el servicio del
    /// sistema (apache2.service, que apt deja habilitado) es otro Apache en el 80 y el 443, y que a este no se
    /// le dan los puertos bajos con setcap (como usuario, solo de 1024 en adelante).
    /// </summary>
    private static void WarnAboutDistroApache(RoostPaths paths, InstalledApache apache)
    {
        var config = RoostConfig.Load(paths.ConfigFile);
        AnsiConsole.MarkupLineInterpolated($"[grey]Apache {apache.Version} de la distro ({apache.Executable}), con tus permisos y la configuracion de Roost.[/]");
        if (LinuxApache.ServiceWarning(SystemServices.Current.Find(ProcessRunner.Default), config.HttpPort, config.HttpsPort) is { } service)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {service}");
        var low = new[] { config.HttpPort, config.HttpsPort }.Where(p => p < 1024).Distinct().ToList();
        if (low.Count > 0)
        {
            var text = "con Apache, como usuario, solo valen puertos de 1024 en adelante (a un binario del sistema no se le da el permiso con setcap), "
                + $"y roost.toml pide el {string.Join(" y el ", low)}: roost up se negara. Pon [web] port = 8080 y https_port = 8443 en {paths.ConfigFile}, o usa nginx para los puertos bajos.";
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {text}");
        }
    }

    /// <summary>Linux: lo que falta en la distro para <paramref name="need"/> (fuera de Linux no se comprueba nada).</summary>
    private static void WarnLinuxDeps(LinuxNeed need)
    {
        if (LinuxDeps.Warning(LinuxDeps.Detect(), need) is { } warning)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {warning}");
    }

    /// <summary>De donde sale: la cache de descargas si ya esta ahi (se comprueba su SHA-256 igual) o el servidor de la URL.</summary>
    private static string Source(RoostPaths paths, string fileName, Uri url) =>
        File.Exists(Path.Combine(paths.Downloads, fileName)) ? "la cache de descargas" : url.Host;

    private static async Task InstallAsync(string what, string host, Func<IProgress<long>, CancellationToken, Task> install, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLineInterpolated($"Instalando {what} (la primera vez) desde {host}...");
        await AnsiConsole.Status().StartAsync($"Instalando {what}...", async ctx =>
        {
            var progress = new Progress<long>(bytes => ctx.Status($"Instalando {what}... {bytes / 1048576.0:0.0} MB"));
            await install(progress, cancellation);
        });
        AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {what} [grey](SHA-256 verificado)[/]");
    }
}

/// <summary>
/// <c>roost web use nginx|apache</c>: elige el servidor web que arranca roost up. Lo instala si
/// falta y, si el stack esta en marcha, cambia solo el servidor web (PHP y la base de datos siguen).
/// Los dos usan los mismos PHP.
/// </summary>
public sealed class WebUseCommand : AsyncCommand<WebUseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SERVIDOR>")]
        [Description("nginx o apache.")]
        public string Server { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var server = settings.Server.Trim().ToLowerInvariant();
        if (server is not (RoostConfig.WebNginx or RoostConfig.WebApache))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]El servidor web tiene que ser nginx o apache[/] (no {settings.Server}).");
            return 1;
        }
        var paths = RoostPaths.Default();
        try
        {
            await WebServerSetup.EnsureAsync(paths, server, cancellation);
            RoostConfig.SetWebServer(paths.ConfigFile, server);
            AnsiConsole.MarkupLine($"[green]Servidor web:[/] {WebServerSetup.Label(server)}"
                + (server == RoostConfig.WebApache ? $" [grey](lee los .htaccess; PHP por los mismos pools de {(OperatingSystem.IsWindows() ? "php-cgi" : "php-fpm")})[/]" : ""));
            return await StackApply.IfRunningAsync(paths, _ => $"sirve {WebServerSetup.Label(server)}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
