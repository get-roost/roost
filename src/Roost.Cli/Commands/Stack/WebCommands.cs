using System.ComponentModel;
using Roost.Cli.Net;
using Roost.Cli.Commands;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
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
            var build = ApacheCatalog.Windows;
            if (installer.Find(build) is not null) return;
            await InstallAsync($"Apache {build.Version}", build.Url.Host, (progress, ct) => installer.InstallAsync(build, progress, ct), cancellation);
            AnsiConsole.MarkupLineInterpolated($"[grey]Apache de Apache Lounge necesita el {build.Redistributable}: si no arranca, instalalo con winget install Microsoft.VCRedist.2015+.x64[/]");
        }
        else
        {
            var installer = new NginxInstaller(paths, RoostHttp.Client);
            var build = NginxCatalog.Windows;
            if (installer.Find(build) is not null) return;
            await InstallAsync($"nginx {build.Version}", build.Url.Host, (progress, ct) => installer.InstallAsync(build, progress, ct), cancellation);
        }
    }

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
/// falta y, si el stack esta en marcha, lo rearranca con el nuevo. Los dos usan los mismos PHP.
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
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost web solo funciona en Windows por ahora[/] (ROOST.md: fase 1, Windows primero).");
            return 1;
        }
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
                + (server == RoostConfig.WebApache ? " [grey](lee los .htaccess; PHP por los mismos pools de php-cgi)[/]" : ""));
            return await StackApply.IfRunningAsync(paths, _ => $"sirve {WebServerSetup.Label(server)}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
