using System.ComponentModel;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php use</c>: fija el PHP global en roost.toml. Si el stack esta en marcha, lo
/// vuelve a arrancar con ese PHP; si no, se aplica en el proximo <c>roost up</c>.
/// </summary>
public sealed class PhpUseCommand : AsyncCommand<PhpUseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64) de un PHP instalado por Roost.")]
        public string Version { get; init; } = "";

        [CommandOption("--here")]
        [Description("En vez del global, fija este PHP para la carpeta actual: escribe un .php-version (lo lee el php de la terminal; ver roost shims).")]
        public bool Here { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        if (settings.Here) return PinHere(paths, settings.Version);
        try
        {
            // Que case con algo instalado ANTES de escribirlo en la configuracion.
            var installed = new PhpInstaller(paths).ListInstalled();
            if (PhpInstaller.Match(installed, settings.Version).Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun PHP de Roost que case con '{settings.Version}'.[/] Instalalo: roost php install {settings.Version}");
                return 1;
            }
            var wanted = RoostConfig.Load(paths.ConfigFile) with { PhpVersion = settings.Version };
            var php = new StackPlanner(paths, ProcessRunner.Default).ResolvePhp(wanted, installed);

            RoostConfig.SetPhpVersion(paths.ConfigFile, settings.Version);
            AnsiConsole.MarkupLineInterpolated($"[green]PHP global:[/] {settings.Version} [grey](instalacion {php.Name}, guardado en {paths.ConfigFile})[/]");

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
                StackApply.PrintApplied(response, partial: true);
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Aplicado:[/] {response.Status.Url} sirve ahora PHP {response.Status.Php}.");
            StackApply.PrintApplied(response, partial: false);
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    // .php-version en la carpeta actual: lo versionas con el proyecto y lo lee el shim de php.
    private static int PinHere(RoostPaths paths, string version)
    {
        var installed = new PhpInstaller(paths).ListInstalled();
        if (PhpInstaller.Match(installed, version).Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun PHP de Roost que case con '{version}'.[/] Instalalo: roost php install {version}");
            return 1;
        }

        var file = Path.Combine(Environment.CurrentDirectory, TerminalPhp.VersionFile);
        File.WriteAllText(file, version.Trim() + Environment.NewLine);
        AnsiConsole.MarkupLineInterpolated($"[green]PHP {version} para esta carpeta[/] [grey]({file})[/]");
        if (!new ShimInstaller(paths).IsInstalled)
            AnsiConsole.MarkupLine("[grey]Para que php lo use en la terminal: roost shims install[/]");
        return 0;
    }
}
