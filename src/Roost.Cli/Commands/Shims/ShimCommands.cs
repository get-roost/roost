using System.ComponentModel;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Shims;

/// <summary>
/// <c>roost shims install</c>: pone el shim de php en bin/shims y esa carpeta delante en el PATH
/// del usuario. Desde ahi, <c>php</c> en una terminal nueva usa el PHP que toca en cada carpeta.
/// </summary>
public sealed class ShimsInstallCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost shims solo funciona en Windows por ahora[/] (ROOST.md: fase 1, Windows primero).");
            return 1;
        }

        var paths = RoostPaths.Default();
        try
        {
            var shims = new ShimInstaller(paths);
            shims.Install(AppContext.BaseDirectory);
            AnsiConsole.MarkupLineInterpolated($"[green]Shims puestos[/] en {shims.Directory}");

            // El php de la terminal carga el mismo php.ini que el servidor: se genera si falta.
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            planner.EnsureUserIni();
            foreach (var php in new PhpInstaller(paths).ListInstalled())
                planner.WritePhpIni(php);

            if (UserPath.AddFirst(shims.Directory))
                AnsiConsole.MarkupLineInterpolated($"[green]PATH del usuario:[/] {shims.Directory} va el primero. [grey]Abre una terminal nueva para que lo vea.[/]");
            else
                AnsiConsole.MarkupLine("[grey]El PATH del usuario ya lo tenia.[/]");

            ShimSupport.WarnAboutShadowing(shims.Directory);
            AnsiConsole.MarkupLine("[grey]Para fijar un PHP en un proyecto: roost php use 7.4 --here (escribe un .php-version).[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost shims remove</c>: quita la carpeta del PATH del usuario y los shims.</summary>
public sealed class ShimsRemoveCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var shims = new ShimInstaller(paths);
            if (OperatingSystem.IsWindows() && UserPath.Remove(shims.Directory))
                AnsiConsole.MarkupLineInterpolated($"[green]Quitado del PATH del usuario:[/] {shims.Directory}");
            shims.Remove();
            AnsiConsole.MarkupLine("[green]Shims quitados.[/] [grey]Las terminales abiertas siguen con el PATH de antes.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost shims status</c>: si estan puestos, si estan en el PATH y que php toca aqui.</summary>
public sealed class ShimsStatusCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var shims = new ShimInstaller(paths);
        AnsiConsole.MarkupLineInterpolated($"Shims: {(shims.IsInstalled ? "[green]puestos[/]" : "[yellow]no puestos[/]")} [grey]({shims.Directory})[/]");
        if (OperatingSystem.IsWindows())
        {
            var inPath = UserPath.Contains(UserPath.Read(), shims.Directory);
            AnsiConsole.MarkupLineInterpolated($"PATH del usuario: {(inPath ? "[green]la incluye[/]" : "[yellow]no la incluye[/]")}");
            if (shims.IsInstalled) ShimSupport.WarnAboutShadowing(shims.Directory);
        }
        if (!shims.IsInstalled) AnsiConsole.MarkupLine("[grey]Para ponerlos: roost shims install[/]");
        return 0;
    }
}

/// <summary><c>roost php which</c>: que PHP usaria la terminal en una carpeta, y por que.</summary>
public sealed class PhpWhichCommand : Command<PhpWhichCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dir <CARPETA>")]
        [Description("Carpeta en la que preguntar. Por defecto, la actual.")]
        public string? Directory { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var choice = TerminalPhp.Resolve(paths, settings.Directory ?? Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable(TerminalPhp.VersionVariable));
            AnsiConsole.MarkupLineInterpolated($"[bold]{choice.Php.Name}[/]  [grey]{choice.Php.Executable}[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Porque {choice.Reason}.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

internal static class ShimSupport
{
    /// <summary>
    /// Windows pone el PATH del sistema antes que el del usuario: un php.exe que esté en el del
    /// sistema (Laragon, XAMPP...) gana al shim. Roost no puede cambiar eso sin ser administrador:
    /// lo dice.
    /// </summary>
    public static void WarnAboutShadowing(string shimDirectory)
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var before = machine.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "php.exe")));
        if (before is null) return;
        AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {Path.Combine(before, "php.exe")} esta en el PATH del sistema, que Windows mira antes que el del usuario.");
        AnsiConsole.MarkupLineInterpolated($"[yellow]Ese php ganara al de Roost.[/] Quitalo del PATH del sistema (hace falta ser administrador) o pon {shimDirectory} delante en el del sistema.");
    }
}
