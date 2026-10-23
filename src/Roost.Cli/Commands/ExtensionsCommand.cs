using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core.Platform;
using Roost.Extensibility;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// Las extensiones de bin/extensions (ROOST.md §12.4), cargadas una vez al arrancar la CLI. Los
/// comandos que inventarian, auditan o arrancan el stack las usan desde aqui.
/// </summary>
internal static class CliExtensions
{
    public static ExtensionSet Current { get; private set; } = ExtensionSet.Empty;

    public static ExtensionSet Load() => Current = ExtensionSet.Load(RoostPaths.Default());
}

/// <summary><c>roost extensions</c>: que extensiones hay, cuales cargan y por que no las que no.</summary>
public sealed class ExtensionsCommand : Command<ExtensionsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de la tabla.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var extensions = CliExtensions.Current;
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                folder = RoostPaths.Default().Extensions,
                api = ExtensionApi.Current,
                loaded = extensions.Loaded.Select(e => new { id = e.Id, name = e.Name, version = e.Version, provides = Provides(e) }),
                errors = extensions.Errors.Select(e => new { source = e.Source, message = e.Message }),
            }, JsonOutput.Options));
            return extensions.Errors.Count == 0 ? 0 : 1;
        }

        if (extensions.Loaded.Count == 0 && extensions.Errors.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"Ninguna extension. [grey](Se buscan en {RoostPaths.Default().Extensions})[/]");
            return 0;
        }

        foreach (var e in extensions.Loaded)
            AnsiConsole.MarkupLineInterpolated($"[green]●[/] [bold]{e.Name}[/] {e.Version} [grey]({e.Id}: {string.Join(", ", Provides(e))})[/]");
        foreach (var error in extensions.Errors)
            AnsiConsole.MarkupLineInterpolated($"[red]●[/] [bold]{error.Source}[/]: {error.Message}");
        return extensions.Errors.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<string> Provides(IRoostExtension e)
    {
        if (e is IStackExtension) yield return "servicios";
        if (e is IAuditExtension) yield return "auditoria";
        if (e is ICliExtension) yield return "comandos";
        if (e.GetType().GetInterfaces().Any(i => i.Name == "IDesktopExtension")) yield return "pantallas";
    }
}
