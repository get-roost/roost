using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Commands.Shims;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Node;

/// <summary>Lo comun de los comandos de Node: instalar con su progreso, comprobar que arranca y los shims.</summary>
internal static class NodeSetup
{
    /// <summary>Instala <paramref name="build"/> con su progreso en pantalla y dice de donde sale lo que se verifica.</summary>
    public static async Task<InstalledNode> InstallAsync(RoostPaths paths, NodeBuild build, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLineInterpolated($"Instalando {build.Label} de {build.Url.Host}...");
        AnsiConsole.MarkupLine(build.Pinned
            ? $"[grey]Es una de las LTS que fija Roost: su SHA-256 ({Markup.Escape(build.Sha256[..12])}...) va en el binario, comprobado con el SHASUMS256.txt firmado de nodejs.org.[/]"
            : $"[grey]No es una de las que fija Roost: se verifica con el SHASUMS256.txt de Node {Markup.Escape(build.Version)} en nodejs.org (SHA-256 {Markup.Escape(build.Sha256[..12])}...).[/]");
        WarnAboutBranch(build.Major);

        InstalledNode? installed = null;
        await AnsiConsole.Status().StartAsync($"Instalando {build.Label}...", async ctx =>
        {
            var progress = new Progress<long>(bytes => ctx.Status(build.Size is { } size
                ? $"Instalando {build.Label}... {bytes / 1048576.0:0.0} de {size / 1048576.0:0.0} MB"
                : $"Instalando {build.Label}... {bytes / 1048576.0:0.0} MB descargados"));
            installed = await new NodeInstaller(paths, RoostHttp.Client).InstallAsync(build, progress, cancellation);
        });
        AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {build.Label} en {installed!.Directory} [grey](SHA-256 verificado)[/]");
        return installed;
    }

    /// <summary>EOL o Current: lo dice antes de instalar o al listar.</summary>
    public static void WarnAboutBranch(int major)
    {
        var status = NodeCatalog.Status(major, NodeCatalog.Today);
        if (status == NodeBranchStatus.Eol)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] Node {major} esta {NodeCatalog.StatusText(major, NodeCatalog.Today)}. Mejor una LTS: roost node install {NodeCatalog.Default.Major}");
        else if (status == NodeBranchStatus.Current)
            AnsiConsole.MarkupLineInterpolated($"[grey]Node {major}: {NodeCatalog.StatusText(major, NodeCatalog.Today)}.[/]");
    }

    /// <summary>Lo que dice <c>node --version</c>, o null si no arranca.</summary>
    public static async Task<string?> VersionAsync(InstalledNode node, CancellationToken cancellation)
    {
        try
        {
            var result = await CommandRunner.Default.RunAsync(NodeTool.Version(node), null, cancellation);
            return result.Succeeded ? result.Stdout.Trim() : null;
        }
        catch (RoostException)
        {
            return null;
        }
    }

    /// <summary>Con los shims puestos, anade los de node, npm y npx si faltan, y avisa si otro node les gana.</summary>
    public static void EnsureShims(RoostPaths paths)
    {
        var shims = new ShimInstaller(paths);
        if (!shims.IsInstalled)
        {
            AnsiConsole.MarkupLine("[grey]Para usar node, npm y npx en la terminal con el Node de cada carpeta (.nvmrc, .node-version): roost shims install (los pone delante en el PATH de tu usuario).[/]");
            return;
        }
        try
        {
            var added = shims.AddMissing(AppContext.BaseDirectory).Where(ShimInstaller.NodeTools.Contains).ToList();
            if (added.Count > 0)
                AnsiConsole.MarkupLineInterpolated($"[green]Shims de {string.Join(", ", added)} puestos[/] en {shims.Directory}: [grey]en una terminal nueva, usan el Node de cada carpeta.[/]");
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{ex.Message}[/]");
        }
        ShimSupport.WarnAboutNodeShadowing(shims.Directory);
    }

    /// <summary>Lo que se pide, leido; si no se entiende, lo dice y devuelve null.</summary>
    public static NodeSpec? Parse(string text)
    {
        if (NodeSpec.TryParse(text, out var spec, out var error)) return spec;
        AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
        return null;
    }
}

/// <summary><c>roost node list</c>: los Node de Roost y las LTS que ofrece, con su estado.</summary>
public sealed class NodeListCommand : Command<NodeListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var today = NodeCatalog.Today;
        var installed = new NodeInstaller(paths).ListInstalled();
        NodeChoice? global = null;
        try { global = installed.Count == 0 ? null : TerminalNode.ResolveGlobal(paths); }
        catch (RoostException) { /* sin global que valga: se dice en status */ }
        var configured = SafeConfigured(paths);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = installed.Select(n => new
                {
                    version = n.Version,
                    directory = n.Directory,
                    npm = n.NpmVersion,
                    corepack = n.HasCorepack,
                    global = global is { Source: NodeChoiceSource.Global } && global.Node.Version == n.Version,
                    status = NodeCatalog.Status(n.Major, today)?.ToString(),
                    eol = NodeCatalog.Status(n.Major, today) == NodeBranchStatus.Eol,
                }),
                globalSpec = configured,
                offered = NodeCatalog.Lts.Select(b => new
                {
                    version = b.Version,
                    npm = b.Npm,
                    url = b.Url.ToString(),
                    sha256 = b.Sha256,
                    status = NodeCatalog.Status(b.Major, today)?.ToString(),
                    installed = installed.Any(n => n.Version == b.Version),
                }),
            }, JsonOutput.Options));
            return 0;
        }

        if (installed.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay ningun Node de Roost instalado.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Node").AddColumn("npm").AddColumn("Estado").AddColumn("Carpeta");
            foreach (var node in installed)
            {
                var isGlobal = global is not null && global.Node.Version == node.Version;
                var status = NodeCatalog.Status(node.Major, today);
                var newer = NodeCatalog.Lts.FirstOrDefault(b => b.Major == node.Major && b.ParsedVersion > node.ParsedVersion);
                var state = NodeCatalog.StatusText(node.Major, today) + (newer is null ? "" : $"; hay {newer.Version} (roost node install {newer.Version})");
                table.AddRow(
                    new Markup($"[bold]{Markup.Escape(node.Version)}[/]{(isGlobal ? " [green](global)[/]" : "")}"),
                    new Text(node.NpmVersion ?? "?"),
                    new Markup(status == NodeBranchStatus.Eol || newer is not null ? $"[yellow]{Markup.Escape(state)}[/]" : Markup.Escape(state)),
                    new Text(node.Directory));
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(configured is null
                ? "[grey]Global: el mas nuevo instalado (roost node use <version> fija otro).[/]"
                : $"[grey]Global: {Markup.Escape(configured)} ([[node]] version en {Markup.Escape(paths.ConfigFile)}).[/]");
        }

        AnsiConsole.MarkupLine("[bold]Las LTS que fija Roost[/] [grey](SHA-256 en el binario, comprobado con el SHASUMS256.txt firmado):[/]");
        foreach (var build in NodeCatalog.Lts)
        {
            var mark = installed.Any(n => n.Version == build.Version) ? "[green]instalada[/]" : $"[grey]roost node install {build.Major}[/]";
            AnsiConsole.MarkupLine($"  Node {Markup.Escape(build.Version)} (npm {Markup.Escape(build.Npm ?? "?")}) · {Markup.Escape(NodeCatalog.StatusText(build.Major, today))} · {mark}");
        }
        AnsiConsole.MarkupLine("[grey]Otra version (la de un .nvmrc, por ejemplo): roost node install 22.11.0 o roost node install 20; se verifica con el SHASUMS256.txt de nodejs.org.[/]");
        return 0;
    }

    internal static string? SafeConfigured(RoostPaths paths)
    {
        try { return NodeConfig.ReadVersion(paths.ConfigFile); }
        catch (RoostException) { return null; }
    }
}

/// <summary><c>roost node install</c>: descarga un Node de nodejs.org, verifica su SHA-256 y lo instala en bin/node.</summary>
public sealed class NodeInstallCommand : AsyncCommand<NodeInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[VERSION]")]
        [Description("Rama (24, 22), version (22.11.0), lts, lts/jod o un rango (^20.11). Sin nada, la LTS mas nueva que fija Roost.")]
        public string? Version { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("node install")) return 1;
        var paths = RoostPaths.Default();
        if (NodeSetup.Parse(settings.Version ?? "lts") is not { } spec) return 1;
        try
        {
            var build = await NodeCatalog.ResolveAsync(RoostHttp.Client, spec, cancellation);
            var installer = new NodeInstaller(paths, RoostHttp.Client);
            var node = installer.Find(build);
            if (node is not null)
                AnsiConsole.MarkupLineInterpolated($"[green]{build.Label} ya esta instalado:[/] {node.Directory}");
            else
                node = await NodeSetup.InstallAsync(paths, build, cancellation);

            var code = 0;
            var version = await NodeSetup.VersionAsync(node, cancellation);
            if (version is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {node.Executable} no responde a --version.");
                code = 1;
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]node {version} · npm {node.NpmVersion ?? "?"}{(node.HasCorepack ? " · corepack (pnpm y yarn)" : "")}[/]");
            }

            var all = installer.ListInstalled();
            if (all.Count == 1 && NodeListCommand.SafeConfigured(paths) is null)
                AnsiConsole.MarkupLine("[grey]Es tu Node global (el unico). Para fijar uno en un proyecto: roost node use 22 --here (escribe un .nvmrc).[/]");
            NodeSetup.EnsureShims(paths);
            return code;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost node remove</c>: quita un Node de Roost (nunca el de nvm, Volta o el instalador).</summary>
public sealed class NodeRemoveCommand : Command<NodeRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("La version (22.23.4) o la rama (22) de un Node de Roost.")]
        public string Version { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        if (NodeSetup.Parse(settings.Version) is not { } spec) return 1;
        var installer = new NodeInstaller(paths);
        var matches = NodeInstaller.Match(installer.ListInstalled(), spec);
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun Node de Roost que case con '{settings.Version}'.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Roost solo quita lo que instalo el, en {paths.NodeBin}; el de nvm, Volta o el instalador de Node no lo toca.[/]");
            return 1;
        }
        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]'{settings.Version}' casa con varios:[/] {string.Join(", ", matches.Select(m => m.Version))}. Indica la version exacta.");
            return 1;
        }

        try
        {
            installer.Remove(matches[0]);
            AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {matches[0].Label}.");

            var removed = new ShimInstaller(paths).RemoveUnwanted();
            if (removed.Count > 0)
                AnsiConsole.MarkupLineInterpolated($"[grey]Era el ultimo Node de Roost: fuera los shims de {string.Join(", ", removed)} (node vuelve a ser el tuyo).[/]");
            else if (installer.ListInstalled().Count > 0)
            {
                try { TerminalNode.ResolveGlobal(paths); }
                catch (RoostException ex) { AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {ex.Message}"); }
            }
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary>
/// <c>roost node use</c>: fija el Node global en roost.toml ([node] version) o, con --here, el de la
/// carpeta actual (.nvmrc). No hay que rearrancar nada: los shims lo leen en cada llamada.
/// </summary>
public sealed class NodeUseCommand : Command<NodeUseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Rama (24), version (22.23.4) o lts/* de un Node de Roost instalado.")]
        public string Version { get; init; } = "";

        [CommandOption("--here")]
        [Description("En vez del global, fija este Node para la carpeta actual: escribe un .nvmrc (o el .node-version si ya lo hay). Lo leen los shims, nvm y fnm.")]
        public bool Here { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var text = settings.Version.Trim();
        if (NodeSetup.Parse(text) is not { } spec) return 1;
        if (spec.Kind == NodeSpecKind.System)
        {
            AnsiConsole.MarkupLine("[red]'system' es el Node de fuera de Roost:[/] no se puede fijar aqui.");
            return 1;
        }
        var matches = NodeInstaller.Match(new NodeInstaller(paths).ListInstalled(), spec);
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun Node de Roost que case con '{text}'.[/] Instalalo: roost node install {text}");
            return 1;
        }

        try
        {
            if (settings.Here)
            {
                var folder = Environment.CurrentDirectory;
                var nodeVersion = Path.Combine(folder, ".node-version");
                var nvmrc = Path.Combine(folder, ".nvmrc");
                var file = File.Exists(nodeVersion) && !File.Exists(nvmrc) ? nodeVersion : nvmrc;
                File.WriteAllText(file, text + Environment.NewLine);
                AnsiConsole.MarkupLineInterpolated($"[green]Node {text} para esta carpeta[/] [grey]({file}: ahora es {matches[0].Version})[/]");
                if (!new ShimInstaller(paths).Has("node"))
                    AnsiConsole.MarkupLine("[grey]Para que node y npm lo usen en la terminal: roost shims install[/]");
                return 0;
            }

            NodeConfig.SetVersion(paths.ConfigFile, text);
            AnsiConsole.MarkupLineInterpolated($"[green]Node global:[/] {text} [grey](ahora {matches[0].Version}, guardado en {paths.ConfigFile})[/]");
            AnsiConsole.MarkupLine("[grey]Vale ya en las terminales con los shims, salvo en las carpetas con su .nvmrc o .node-version.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost node which</c>: que Node usaria la terminal en una carpeta, y por que.</summary>
public sealed class NodeWhichCommand : Command<NodeWhichCommand.Settings>
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
            var choice = TerminalNode.Resolve(paths, settings.Directory ?? Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable(TerminalNode.VersionVariable));
            AnsiConsole.MarkupLineInterpolated($"[bold]{choice.Node.Label}[/]  [grey]{choice.Node.Executable}[/]");
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

/// <summary>
/// <c>roost node status</c>: que Node usa esta carpeta y por que, los instalados, el global, los shims
/// node, npm y npx y si otro node les gana (nvm-windows, Volta, fnm, el instalador de Node).
/// </summary>
public sealed class NodeStatusCommand : AsyncCommand<NodeStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dir <CARPETA>")]
        [Description("Carpeta en la que preguntar. Por defecto, la actual.")]
        public string? Directory { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var folder = settings.Directory ?? Environment.CurrentDirectory;
        var installed = new NodeInstaller(paths).ListInstalled();
        NodeChoice? here = null;
        string? hereError = null;
        try { here = TerminalNode.Resolve(paths, folder, Environment.GetEnvironmentVariable(TerminalNode.VersionVariable)); }
        catch (RoostException ex) { hereError = ex.Message; }
        var version = here is null ? null : await NodeSetup.VersionAsync(here.Node, cancellation);

        var shims = new ShimInstaller(paths);
        var tools = ShimInstaller.NodeTools.Where(shims.Has).ToList();
        var shadows = ShimInstaller.NodeTools
            .Select(tool => (Tool: tool, Registry: ShimShadowing.Find(tool, shims.Directory), Session: OperatingSystem.IsWindows()
                ? ShimShadowing.FindInSession(tool, shims.Directory, Environment.GetEnvironmentVariable("PATH"), File.Exists)
                : null))
            .ToList();
        var project = NodeProject.Read(folder);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = installed.Select(n => new { version = n.Version, directory = n.Directory, npm = n.NpmVersion, corepack = n.HasCorepack }),
                globalSpec = NodeListCommand.SafeConfigured(paths),
                here = here is null ? null : new
                {
                    version = here.Node.Version,
                    executable = here.Node.Executable,
                    source = here.Source.ToString(),
                    file = here.File,
                    spec = here.Spec,
                    reason = here.Reason,
                    enginesMet = here.EnginesMet,
                    nodeVersion = version,
                },
                hereError,
                project = project.HasPackageJson ? new { manager = NodeTool.Name(project.Manager), lockFile = project.LockFile, nodeModules = project.HasNodeModules, scripts = project.Scripts.Select(s => s.Name) } : null,
                shims = new
                {
                    directory = shims.Directory,
                    installed = tools,
                    shadowedBy = shadows.Where(s => s.Registry is not null || s.Session is not null).Select(s => new
                    {
                        tool = s.Tool,
                        path = (s.Session ?? s.Registry)!.Path,
                        owner = ShimShadowing.NodeOwner((s.Session ?? s.Registry)!.Path),
                        inSystemPath = s.Registry?.InSystemPath ?? false,
                        sessionOnly = s.Registry is null,
                    }),
                },
            }, JsonOutput.Options));
            return 0;
        }

        if (installed.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]No hay ningun Node de Roost.[/] Instala la LTS: roost node install {NodeCatalog.Default.Major}");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]Instalados:[/] {string.Join(", ", installed.Select(n => n.Version))} [grey]({paths.NodeBin})[/]");
            var configured = NodeListCommand.SafeConfigured(paths);
            AnsiConsole.MarkupLineInterpolated($"[bold]Global:[/] {configured ?? "el mas nuevo instalado"}");
        }

        if (here is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]Aqui:[/] {here.Node.Label} [grey](porque {here.Reason})[/]");
            AnsiConsole.MarkupLine($"[bold]node --version:[/] {(version is null ? "[red]no contesta[/]" : Markup.Escape(version))} [grey]· npm {Markup.Escape(here.Node.NpmVersion ?? "?")}{(here.Node.HasCorepack ? " · corepack" : "")}[/]");
        }
        else if (installed.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aqui:[/] {hereError}");
        }

        if (project.HasPackageJson)
        {
            var manager = NodeTool.Name(project.Manager);
            AnsiConsole.MarkupLineInterpolated($"[bold]package.json:[/] {manager}{(project.LockFile is null ? " (sin lockfile)" : $" ({project.LockFile})")} · {(project.HasNodeModules ? "node_modules instalado" : "falta node_modules (npm install)")}");
            if (here is not null && NodeTool.WhyNot(project.Manager, here.Node) is { } why) AnsiConsole.MarkupLineInterpolated($"[yellow]{why}[/]");
        }

        AnsiConsole.MarkupLine($"Shims de Node: {(tools.Count == 3 ? "[green]puestos[/]" : tools.Count == 0 ? "[yellow]no puestos[/]" : "[yellow]a medias[/]")} [grey]({Markup.Escape(shims.Directory)}{(tools.Count > 0 ? ": " + string.Join(", ", tools) : "")})[/]");
        if (tools.Count > 0) ShimSupport.WarnAboutNodeShadowing(shims.Directory);
        else if (shims.IsInstalled && installed.Count > 0) AnsiConsole.MarkupLine("[grey]Los shims son de antes de tener Node: roost shims install (o roost node install) los anade.[/]");
        else if (!shims.IsInstalled) AnsiConsole.MarkupLine("[grey]Para usarlos en la terminal: roost shims install[/]");
        return here is null && installed.Count > 0 ? 1 : 0;
    }
}
