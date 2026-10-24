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
        if (!WindowsOnly.Check("shims install")) return 1;

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

            foreach (var tool in shims.Wanted) ShimSupport.WarnAboutShadowing(shims.Directory, tool);
            AnsiConsole.MarkupLine("[grey]Para fijar un PHP en un proyecto: roost php use 7.4 --here (escribe un .php-version).[/]");
            if (!shims.Has("composer"))
                AnsiConsole.MarkupLine("[grey]El shim de composer se pone al instalar el Composer de Roost (roost composer install, o la app la primera vez): hasta entonces, composer sigue siendo el tuyo.[/]");
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
        var tools = ShimInstaller.Tools.Where(shims.Has).ToList();
        // Los colores van en el texto fijo: lo que se interpola, MarkupLineInterpolated lo escapa.
        AnsiConsole.MarkupLine($"Shims: {(shims.IsInstalled ? "[green]puestos[/]" : "[yellow]no puestos[/]")} [grey]({Markup.Escape(shims.Directory)}{(tools.Count > 0 ? ": " + string.Join(", ", tools) : "")})[/]");
        if (shims.IsInstalled && !shims.Has("composer"))
            AnsiConsole.MarkupLine(shims.Wanted.Contains("composer")
                ? "[yellow]Falta el de composer[/] (son de una version anterior de Roost): [grey]roost composer install lo anade.[/]"
                : "[grey]Sin shim de composer: se pone al instalar el Composer de Roost (roost composer install); hasta entonces, composer es el tuyo.[/]");
        if (OperatingSystem.IsWindows())
        {
            var inPath = UserPath.Contains(UserPath.Read(), shims.Directory);
            AnsiConsole.MarkupLine($"PATH del usuario: {(inPath ? "[green]la incluye[/]" : "[yellow]no la incluye[/]")}");
            foreach (var tool in tools) ShimSupport.WarnAboutShadowing(shims.Directory, tool);
        }
        if (shims.Has("composer") && Roost.Core.Composer.ComposerTool.Locate(paths) is null)
            AnsiConsole.MarkupLine("[grey]El shim de composer necesita el Composer de Roost: roost composer install[/]");
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
    /// Windows pone el PATH del sistema antes que el del usuario: un php.exe (Laragon, XAMPP...) o un
    /// composer.bat (ComposerSetup) que esté en el del sistema gana al shim. Roost no puede cambiar
    /// eso sin ser administrador: lo dice, con qué hacer.
    /// </summary>
    public static void WarnAboutShadowing(string shimDirectory, string tool = "php")
    {
        if (ShimShadowing.Find(tool, shimDirectory) is not { } shadow) return;
        if (!shadow.ShimsInPath)
        {
            // Primero, que los shims no estan; y si lo que gana viene del PATH del sistema, seguira
            // ganando cuando lo esten: eso se dice tambien.
            AnsiConsole.MarkupLineInterpolated($"[grey]Los shims no estan en el PATH: ahora, en la terminal, {tool} es {shadow.Path}. roost shims install los pone los primeros en el de tu usuario.[/]");
            if (!shadow.InSystemPath) return;
        }
        else if (!shadow.InSystemPath)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {shadow.Path} esta en el PATH de tu usuario antes que los shims: ese {tool} ganara al de Roost.");
            AnsiConsole.MarkupLine("[grey]roost shims install vuelve a poner los shims los primeros.[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {shadow.Path} esta en el PATH del sistema, que Windows mira antes que el del usuario.");
        if (tool == "composer" && shadow.Path.Contains("ComposerSetup", StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine("[yellow]Es el de Composer-Setup y ganara al de Roost.[/] Desinstalalo (Configuracion > Aplicaciones > Composer) o quita su carpeta del PATH del sistema (hace falta ser administrador); tu cache y tu auth.json se quedan.");
        else
            AnsiConsole.MarkupLineInterpolated($"[yellow]Ese {tool} ganara al de Roost.[/] Quita su carpeta del PATH del sistema (hace falta ser administrador) o pon {shimDirectory} delante en el del sistema.");
        AnsiConsole.MarkupLineInterpolated($"[grey]Mientras tanto, el de Roost se puede llamar por su ruta: {Path.Combine(shimDirectory, tool + ".exe")}[/]");
    }
}
