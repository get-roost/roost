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
/// <c>roost shims install</c>: pone los shims en bin/shims y los hace llegar a la terminal. En Windows,
/// esa carpeta delante en el PATH del usuario; en Linux, enlaces en ~/.local/bin (y, con --profile, el
/// bloque que la pone la primera en el PATH en el perfil de la shell). Desde ahi, <c>php</c> en una
/// terminal nueva usa el PHP que toca en cada carpeta.
/// </summary>
public sealed class ShimsInstallCommand : Command<ShimsInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--profile")]
        [Description("Linux: ademas de los enlaces en ~/.local/bin, escribe en el perfil de tu shell (~/.profile o ~/.bash_profile, ~/.zprofile, fish) el bloque que pone esa carpeta la primera en el PATH. En Debian y Ubuntu el ~/.profile de serie ya lo hace si la carpeta existe.")]
        public bool Profile { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
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

            if (OperatingSystem.IsWindows())
            {
                if (UserPath.AddFirst(shims.Directory))
                    AnsiConsole.MarkupLineInterpolated($"[green]PATH del usuario:[/] {shims.Directory} va el primero. [grey]Abre una terminal nueva para que lo vea.[/]");
                else
                    AnsiConsole.MarkupLine("[grey]El PATH del usuario ya lo tenia.[/]");
            }
            else
            {
                LinkOnUnix(shims, settings.Profile);
            }

            foreach (var tool in shims.Wanted.Where(t => !ShimInstaller.NodeTools.Contains(t))) ShimSupport.WarnAboutShadowing(shims.PathDirectory, tool);
            if (shims.Has("node")) ShimSupport.WarnAboutNodeShadowing(shims.PathDirectory);
            AnsiConsole.MarkupLine("[grey]Para fijar un PHP en un proyecto: roost php use 8.4 --here (escribe un .php-version).[/]");
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

    /// <summary>Linux y macOS: los enlaces de ~/.local/bin, el bloque del perfil si se pide, y si la terminal los va a ver.</summary>
    private static void LinkOnUnix(ShimInstaller shims, bool profile)
    {
        var links = shims.Links;
        var (linked, skipped) = links.Link(shims.Wanted, shims.Directory);
        if (linked.Count > 0)
            AnsiConsole.MarkupLineInterpolated($"[green]Enlaces en {links.LinkDirectory}:[/] {string.Join(", ", linked)} [grey](cada uno apunta a su shim de {shims.Directory})[/]");
        foreach (var tool in skipped)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] en {links.LinkDirectory} ya hay un {tool} que no es de Roost: se deja. Quitalo o renombralo y repite roost shims install para usar el de Roost.");

        if (profile)
        {
            var touched = links.InstallProfile();
            if (touched.Count > 0)
                AnsiConsole.MarkupLineInterpolated($"[green]Perfil de la shell:[/] bloque # roost:begin/end en {string.Join(", ", touched)}. [grey]Vale en la proxima sesion (o ahora: source {touched[0]}).[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[grey]El perfil de la shell ya tenia el bloque ({string.Join(", ", links.ProfilesWithBlock())}).[/]");
            return;
        }

        if (links.IsInPath())
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]{links.LinkDirectory} ya esta en el PATH de esta terminal: {string.Join(", ", linked)} valen en cualquier terminal nueva.[/]");
            return;
        }
        AnsiConsole.MarkupLineInterpolated($"[yellow]{links.LinkDirectory} no esta en el PATH de esta terminal.[/] En Debian y Ubuntu, ~/.profile lo pone el primero al iniciar sesion si la carpeta existe (ahora existe): cierra la sesion y vuelve a entrar, o en esta terminal: export PATH=\"$HOME/.local/bin:$PATH\"");
        AnsiConsole.MarkupLine("[grey]Para que lo ponga el perfil de tu shell (zsh, fish, otra distro): roost shims install --profile[/]");
    }
}

/// <summary><c>roost shims remove</c>: quita los shims y lo que los hacia llegar a la terminal (el PATH del usuario, o los enlaces y el bloque del perfil).</summary>
public sealed class ShimsRemoveCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var shims = new ShimInstaller(paths);
            if (OperatingSystem.IsWindows())
            {
                if (UserPath.Remove(shims.Directory))
                    AnsiConsole.MarkupLineInterpolated($"[green]Quitado del PATH del usuario:[/] {shims.Directory}");
            }
            else
            {
                var unlinked = shims.Links.Unlink(shims.Directory, ShimInstaller.Tools);
                if (unlinked.Count > 0)
                    AnsiConsole.MarkupLineInterpolated($"[green]Enlaces quitados de {shims.Links.LinkDirectory}:[/] {string.Join(", ", unlinked)} [grey](lo que no era de Roost se queda)[/]");
                var profiles = shims.Links.RemoveProfile();
                if (profiles.Count > 0)
                    AnsiConsole.MarkupLineInterpolated($"[green]Bloque quitado de:[/] {string.Join(", ", profiles)}");
            }
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

/// <summary><c>roost shims status</c>: si estan puestos, si llegan a la terminal y si otro php, composer o node les gana.</summary>
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
        }
        else
        {
            var links = shims.Links;
            var linked = links.Linked(shims.Directory, ShimInstaller.Tools);
            AnsiConsole.MarkupLine($"Enlaces en {Markup.Escape(links.LinkDirectory)}: {(linked.Count > 0 ? "[green]" + string.Join(", ", linked) + "[/]" : shims.IsInstalled ? "[yellow]ninguno[/] [grey](roost shims install los pone)[/]" : "[grey]ninguno[/]")}");
            foreach (var tool in ShimInstaller.Tools.Where(t => links.Foreign(t, shims.Directory) is not null))
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {links.LinkPath(tool)} no es de Roost: ese {tool} es el que vale en la terminal.");
            AnsiConsole.MarkupLine($"PATH de esta terminal: {(links.IsInPath() ? "[green]la incluye[/]" : "[yellow]no la incluye[/] [grey](cierra la sesion y vuelve a entrar, o roost shims install --profile)[/]")}");
            var profiles = links.ProfilesWithBlock();
            if (profiles.Count > 0) AnsiConsole.MarkupLineInterpolated($"[grey]Bloque # roost:begin/end en: {string.Join(", ", profiles)}[/]");
        }
        foreach (var tool in tools.Where(t => !ShimInstaller.NodeTools.Contains(t))) ShimSupport.WarnAboutShadowing(shims.PathDirectory, tool);
        if (tools.Contains("node")) ShimSupport.WarnAboutNodeShadowing(shims.PathDirectory);
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
    /// eso sin ser administrador: lo dice, con qué hacer. En Linux, lo que vaya antes que ~/.local/bin
    /// en el PATH de esta terminal (el php de la distro, el node de nvm).
    /// </summary>
    public static void WarnAboutShadowing(string shimDirectory, string tool = "php")
    {
        if (ShimShadowing.Find(tool, shimDirectory) is not { } shadow) return;
        if (!OperatingSystem.IsWindows())
        {
            WarnUnix(shadow, shimDirectory);
            return;
        }
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

    /// <summary>Linux y macOS: quien gana en el PATH de esta terminal, de quien es y que hacer.</summary>
    private static void WarnUnix(ShimShadow shadow, string linkDirectory)
    {
        var owner = ShimShadowing.UnixOwner(shadow.Path);
        var who = owner is null ? shadow.Path : $"{shadow.Path} ({owner})";
        if (!shadow.ShimsInPath)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]{linkDirectory} no esta en el PATH de esta terminal: ahora {shadow.Tool} es {who}. En una sesion nueva (o con roost shims install --profile) ganara el de Roost.[/]");
            return;
        }
        AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {who} va antes que {linkDirectory} en el PATH de esta terminal: ese {shadow.Tool} ganara al de Roost.");
        AnsiConsole.MarkupLine(owner switch
        {
            "nvm" or "fnm" or "Volta" or "asdf" or "mise" or "nodenv" or "n" or "phpenv" =>
                $"[grey]Lo pone {Markup.Escape(owner)} al abrir cada terminal (en tu ~/.bashrc o ~/.zshrc), despues del perfil de inicio de sesion, asi que seguira delante: quita esa linea, o usa {Markup.Escape(owner)} y no el de Roost en esa terminal.[/]",
            "un programa de Windows, por el interop de WSL" =>
                "[grey]Es un programa de Windows que WSL anade al PATH: quitalo con appendWindowsPath=false en [[interop]] de /etc/wsl.conf, o pon ~/.local/bin delante (roost shims install --profile).[/]",
            _ => $"[grey]Pon {Markup.Escape(linkDirectory)} delante en tu PATH (roost shims install --profile lo hace en el perfil de inicio de sesion) o quita esa carpeta del PATH.[/]",
        });
        AnsiConsole.MarkupLineInterpolated($"[grey]Mientras tanto, el de Roost se puede llamar por su ruta: {Path.Combine(linkDirectory, shadow.Tool)}[/]");
    }

    /// <summary>
    /// Lo mismo para node, npm y npx, diciendo de quien es el que gana (nvm-windows, Volta, fnm, el
    /// instalador de Node...) y que hacer con cada uno. Mira el PATH del registro (sistema y usuario) y
    /// el de esta terminal, donde sale lo que pone el perfil de la shell (fnm env). Una vez por carpeta.
    /// En Linux, el PATH de esta terminal (nvm, fnm, Volta, la distro, snap).
    /// </summary>
    public static void WarnAboutNodeShadowing(string shimDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var seenUnix = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in ShimInstaller.NodeTools)
            {
                if (ShimShadowing.Find(tool, shimDirectory) is not { } unix || !seenUnix.Add(Path.GetDirectoryName(unix.Path) ?? unix.Path)) continue;
                WarnUnix(unix, shimDirectory);
            }
            return;
        }
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
