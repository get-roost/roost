using System.ComponentModel;
using Roost.Cli.Net;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php remove</c>: quita un PHP instalado por Roost. Solo lo suyo: los PHP de
/// Laragon, XAMPP o la distro no se tocan nunca.
/// </summary>
public sealed class PhpRemoveCommand : Command<PhpRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Instalacion a quitar: nombre (8.3.36-nts-x64), version (8.3.36) o rama (8.3).")]
        public string Version { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var installer = new PhpInstaller(paths, RoostHttp.Client);
        var matches = installer.Find(settings.Version);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun PHP de Roost que case con '{settings.Version}'.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Roost solo quita lo que instalo el, en {paths.PhpBin}; lo de Laragon, XAMPP... no lo toca.[/]");
            return 1;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]'{settings.Version}' casa con varias instalaciones:[/] {string.Join(", ", matches.Select(m => m.Name))}. Indica el nombre exacto.");
            return 1;
        }

        try
        {
            // Lo mismo que impide la app: ni el global, ni uno que un sitio necesite.
            var sites = new SiteRegistry(paths).Load();
            var config = RoostConfig.Load(paths.ConfigFile);
            if (new StackPlanner(paths, ProcessRunner.Default).WhyNotRemove(matches[0], config, installer.ListInstalled(), sites) is { } why)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]No se quita PHP {matches[0].Name}.[/] {why}");
                return 1;
            }

            installer.Remove(matches[0]);
            AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] PHP {matches[0].Name}.");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
