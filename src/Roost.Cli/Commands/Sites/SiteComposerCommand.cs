using System.ComponentModel;
using Roost.Cli.Commands.Composer;
using Roost.Core;
using Roost.Core.Composer;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>
/// <c>roost site composer &lt;sitio&gt; install | update | require &lt;paquete&gt;</c>: Composer en la carpeta
/// del sitio con su PHP y el php.ini de Roost, sin preguntas, con la salida segun sale. Lo mismo
/// que el bloque Composer del panel del sitio en la app. Para todo lo demas, el shim: composer en
/// la terminal.
/// </summary>
public sealed class SiteComposerCommand : AsyncCommand<SiteComposerCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SITIO>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "<ACCION>")]
        [Description("install (lo del composer.lock), update (lo mas nuevo que permite el composer.json) o require (anade un paquete).")]
        public string Action { get; init; } = "";

        [CommandArgument(2, "[PAQUETE]")]
        [Description("Con require: vendor/paquete, y si quieres su version (monolog/monolog:^3).")]
        public string? Package { get; init; }

        [CommandOption("--dev")]
        [Description("Con require: solo para desarrollo (require-dev).")]
        public bool Dev { get; init; }

        public override ValidationResult Validate()
        {
            if (!TryParse(Action, out var action))
                return ValidationResult.Error($"'{Action}' no es una accion: usa install, update o require.");
            if (action == ComposerAction.Require && string.IsNullOrWhiteSpace(Package))
                return ValidationResult.Error("require necesita el paquete: roost site composer tienda require monolog/monolog");
            if (action != ComposerAction.Require && (Package is not null || Dev))
                return ValidationResult.Error("El paquete y --dev solo van con require.");
            return ValidationResult.Success();
        }
    }

    internal static bool TryParse(string text, out ComposerAction action)
    {
        action = text.Trim().ToLowerInvariant() switch
        {
            "install" => ComposerAction.Install,
            "update" => ComposerAction.Update,
            "require" => ComposerAction.Require,
            _ => (ComposerAction)(-1),
        };
        return Enum.IsDefined(action);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site composer")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            TryParse(settings.Action, out var action);
            var site = new SiteManager(paths).Find(settings.Name);
            if (site is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }
            if (!Directory.Exists(site.Folder))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]La carpeta de {site.Host} ya no existe ({site.Folder}).[/]");
                return 1;
            }
            if (action != ComposerAction.Require && !File.Exists(Path.Combine(site.Folder, "composer.json")))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{site.Folder} no tiene composer.json.[/] [grey](roost site composer {site.Name} require <paquete> crea uno.)[/]");
                return 1;
            }

            var arguments = ComposerTool.Arguments(action, settings.Package, settings.Dev);
            var php = ComposerTool.PhpFor(paths, site.Folder);
            ComposerTool.CheckPhp(php.Php);
            var composer = await ComposerSetup.EnsureAsync(paths, cancellation);

            AnsiConsole.MarkupLineInterpolated($"[bold]{ComposerTool.Display(arguments)}[/] en {site.Host} [grey]({site.Folder}) con PHP {php.Php.Version?.ToString() ?? php.Php.Name}, porque {php.Reason}[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]{ComposerTool.ScriptsWarning}[/]");
            var spec = ComposerTool.Command(paths, php.Php, composer, arguments, site.Folder);
            var result = await CommandRunner.Default.RunAsync(spec, new ConsoleLines(), cancellation);
            if (result.Succeeded)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Hecho:[/] {ComposerTool.Display(arguments)} en {site.Host}.");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[red]{ComposerTool.Display(arguments)} termino con el codigo {result.ExitCode}.[/] [grey]Arriba esta lo que dijo Composer.[/]");
            return result.ExitCode is > 0 and < 256 ? result.ExitCode : 1;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
