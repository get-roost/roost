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

            foreach (var tool in shims.Wanted.Where(t => !ShimInstaller.NodeTools.Contains(t))) ShimSupport.WarnAboutShadowing(shims.Directory, tool);
            if (shims.Has("node")) ShimSupport.WarnAboutNodeShadowing(shims.Directory);
            AnsiConsole.MarkupLine("[grey]Para fijar un PHP en un proyecto: roost php use 7.4 --here (escribe un .php-version).[/]");
            if (!shims.Has("composer"))
                AnsiConsole.MarkupLine("[grey]El shim de composer se pone al instalar el Composer de Roost (roost composer install, o la app la primera vez): hasta entonces, composer sigue siendo el tuyo.[/]");
            if (!shims.Has("node"))
                AnsiConsole.MarkupLine("[grey]Los de node, npm y npx se ponen al instalar un Node de Roost (roost node install 24): hasta entonces, node sigue siendo el tuyo.[/]");
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

/// <summary><c>roost shims status</c>: si estan puestos, si estan en el PATH y si otro php, composer o node les gana.</summary>
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
        if (shims.IsInstalled && !shims.Has("node"))
            AnsiConsole.MarkupLine(shims.Wanted.Contains("node")
                ? "[yellow]Faltan los de node, npm y npx[/] (son de antes de tener Node en Roost): [grey]roost node install los anade.[/]"
                : "[grey]Sin shims de node, npm y npx: se ponen al instalar un Node de Roost (roost node install 24); hasta entonces, node es el tuyo.[/]");
        if (OperatingSystem.IsWindows())
        {
            var inPath = UserPath.Contains(UserPath.Read(), shims.Directory);
            AnsiConsole.MarkupLine($"PATH del usuario: {(inPath ? "[green]la incluye[/]" : "[yellow]no la incluye[/]")}");
            foreach (var tool in tools.Where(t => !ShimInstaller.NodeTools.Contains(t))) ShimSupport.WarnAboutShadowing(shims.Directory, tool);
            if (tools.Contains("node")) ShimSupport.WarnAboutNodeShadowing(shims.Directory);
        }
        if (shims.Has("composer") && Roost.Core.Composer.ComposerTool.Locate(paths) is null)
            AnsiConsole.MarkupLine("[grey]El shim de composer necesita el Composer de Roost: roost composer install[/]");
        if (shims.Has("node") && new NodeInstaller(paths).ListInstalled().Count == 0)
            AnsiConsole.MarkupLine("[yellow]Los shims de node no tienen ningun Node de Roost detras:[/] [grey]roost node install 24, o roost shims install para quitarlos.[/]");
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

    /// <summary>
    /// Lo mismo para node, npm y npx, diciendo de quien es el que gana (nvm-windows, Volta, fnm, el
    /// instalador de Node...) y que hacer con cada uno. Mira el PATH del registro (sistema y usuario) y
    /// el de esta terminal, donde sale lo que pone el perfil de la shell (fnm env). Una vez por carpeta.
    /// </summary>
    public static void WarnAboutNodeShadowing(string shimDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in ShimInstaller.NodeTools)
        {
            var registry = ShimShadowing.Find(tool, shimDirectory);
            var session = ShimShadowing.FindInSession(tool, shimDirectory, Environment.GetEnvironmentVariable("PATH"), File.Exists);
            var shadow = registry ?? session;
            if (shadow is null || !seen.Add(Path.GetDirectoryName(shadow.Path) ?? shadow.Path)) continue;
            var owner = ShimShadowing.NodeOwner(shadow.Path);
            var who = owner is null ? shadow.Path : $"{shadow.Path} ({owner})";

            if (registry is null)
            {
                // Solo en esta terminal: lo pone el perfil de la shell, no el PATH guardado.
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] en esta terminal, {tool} es {who}, que va antes que los shims.");
                AnsiConsole.MarkupLine(owner == "fnm"
                    ? "[grey]Lo pone fnm al abrir la terminal (fnm env en tu perfil de PowerShell): quita esa linea, o abre la terminal sin ella, para usar el de Roost.[/]"
                    : "[grey]No esta en el PATH guardado de Windows: lo ha puesto algo al abrir esta terminal (tu perfil, un nvm use). En una terminal nueva puede no estar.[/]");
                continue;
            }

            if (!registry.ShimsInPath)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]Los shims no estan en el PATH: ahora, en la terminal, {tool} es {who}. roost shims install los pone los primeros en el de tu usuario.[/]");
                if (!registry.InSystemPath) continue;
            }
            else if (!registry.InSystemPath)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {who} esta en el PATH de tu usuario antes que los shims: ese {tool} ganara al de Roost.");
                AnsiConsole.MarkupLine("[grey]roost shims install vuelve a poner los shims los primeros.[/]");
                continue;
            }

            AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {who} esta en el PATH del sistema, que Windows mira antes que el de tu usuario: ese {tool} ganara al de Roost.");
            var folder = Markup.Escape(Path.GetDirectoryName(shadow.Path) ?? shadow.Path);
            AnsiConsole.MarkupLine(owner switch
            {
                "nvm-windows" => $"[grey]Con nvm-windows: nvm off (como administrador) lo aparta sin desinstalarlo, o quita {folder} del PATH del sistema.[/]",
                "el instalador de Node (nodejs.org)" => $"[grey]Desinstalalo (Configuracion > Aplicaciones > Node.js) o quita {folder} del PATH del sistema (hace falta ser administrador).[/]",
                _ => $"[grey]Quita {folder} del PATH del sistema (hace falta ser administrador) o pon {Markup.Escape(shimDirectory)} delante en el del sistema.[/]",
            });
            AnsiConsole.MarkupLineInterpolated($"[grey]Mientras tanto, el de Roost se puede llamar por su ruta: {Path.Combine(shimDirectory, tool + ".exe")}[/]");
        }
    }
}
