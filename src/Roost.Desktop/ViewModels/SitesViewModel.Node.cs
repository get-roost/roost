using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Un botón «npm run …» del bloque Node: el script del package.json y si se queda en marcha.</summary>
public sealed record NodeScriptButton(NodeScript Script, string Label)
{
    public string Tip => Script.LongRunning
        ? $"{Script.Command}\nSe queda en marcha (un servidor de desarrollo): sigue aunque cierres la ventana, y se para con «Parar»."
        : Script.Command;
}

/// <summary>
/// El bloque Node del panel del sitio, si la carpeta tiene package.json (oleada 5, N1): el Node que
/// usa y por qué (<see cref="TerminalNode"/>: <c>.nvmrc</c>, <c>.node-version</c>, <c>engines.node</c> o
/// el global), el gestor del lockfile, «npm install» y un botón por script del package.json, en el
/// diálogo de salida en vivo. Los que se quedan en marcha (<c>npm run dev</c> con Vite) van aparte
/// (<see cref="NodeDevSession"/>): siguen aunque se cierre su ventana, y el panel ofrece «Ver salida» y
/// «Parar». Sin Node de Roost, ofrece instalar la LTS (o la que pida el .nvmrc) con el diálogo Instalando.
/// Todo con el Node de Roost por su ruta: nunca un node o un npm del sistema.
/// </summary>
public sealed partial class SitesViewModel
{
    /// <summary>Lo que se lanza aparte, por sitio: sobrevive a cambiar de sitio o de pantalla.</summary>
    private readonly Dictionary<string, NodeDevSession> _devSessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El package.json del sitio elegido (scripts, gestor, node_modules).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNode), nameof(NodeChip), nameof(NodeChipTone), nameof(HasNodeChip), nameof(NodeNeedsInstall),
        nameof(NodeManagerText), nameof(NodeInstallLabel), nameof(NodeCanInstallDeps), nameof(NodeUsesVite), nameof(NodeViteHint))]
    public partial NodeProject? Package { get; private set; }

    /// <summary>El Node que toca en la carpeta del sitio; null si no hay (y <see cref="NodeProblemText"/> dice por qué).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeReady), nameof(NodeCanInstallDeps), nameof(NodeManagerText))]
    public partial NodeChoice? SiteNode { get; private set; }

    [ObservableProperty] public partial string NodeText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNodeProblem))]
    public partial string NodeProblemText { get; private set; } = "";

    /// <summary>El Node que se ofrece instalar desde el panel («24», o el que pide el .nvmrc); null si no se ofrece ninguno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallNode), nameof(NodeInstallNodeLabel))]
    public partial string? NodeInstallSpec { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<NodeScriptButton> NodeScripts { get; private set; } = [];

    /// <summary>npm (o Node, al instalarse) está trabajando en este sitio: los botones esperan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeIdle))]
    public partial bool NodeBusy { get; private set; }

    /// <summary>Lo que el sitio elegido tiene lanzado aparte (<c>npm run dev</c>), en marcha o ya terminado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNodeDev))]
    public partial NodeDevSession? NodeDev { get; private set; }

    public bool HasNode => Package?.HasPackageJson == true;

    public bool NodeReady => SiteNode is not null;

    public bool HasNodeProblem => NodeProblemText.Length > 0;

    public bool CanInstallNode => NodeInstallSpec is not null;

    public string NodeInstallNodeLabel => NodeInstallSpec is { } spec
        ? spec == NodeCatalog.Default.Major.ToString(CultureInfo.InvariantCulture) ? $"Instalar Node {spec} (LTS)" : $"Instalar Node {spec}"
        : "";

    public bool NodeIdle => !NodeBusy;

    public bool HasNodeDev => NodeDev is not null;

    public bool HasNodeScripts => NodeScripts.Count > 0;

    public bool HasNodeChip => HasNode && Package!.Unreadable is false;

    public string NodeChip => Package is not { HasPackageJson: true } p ? "" : p.HasNodeModules ? "node_modules instalado" : "falta node_modules/";

    public Tone NodeChipTone => NodeNeedsInstall ? Tone.Warn : Tone.Ok;

    public bool NodeNeedsInstall => Package is { NeedsInstall: true, Unreadable: false };

    public bool NodeUsesVite => Package?.UsesVite == true;

    /// <summary>«npm install», «pnpm install»: lo que hace el botón de instalar.</summary>
    public string NodeInstallLabel => Package is { } p ? NodeTool.Display(p.Manager, NodeTool.InstallArguments(p, clean: false)) : "npm install";

    /// <summary>Hay Node y su gestor se puede usar con él (bun no; pnpm y yarn, con corepack).</summary>
    public bool NodeCanInstallDeps => SiteNode is { } node && Package is { HasPackageJson: true, Unreadable: false } p && NodeTool.WhyNot(p.Manager, node.Node) is null;

    /// <summary>El gestor y su lockfile, y si se puede usar con este Node.</summary>
    public string NodeManagerText => Package is not { HasPackageJson: true } p ? ""
        : p.Unreadable ? "El package.json no es JSON válido: arréglalo para ver sus scripts."
        : NodeTexts.Manager(p, SiteNode?.Node);

    public string NodeViteHint => Selected is { } row
        ? $"Vite en HTTPS (https://{row.Host}:5173) va con el certificado del sitio: «Copiar server de Vite» da lo que va en vite.config.js."
        : "";

    partial void OnNodeScriptsChanged(IReadOnlyList<NodeScriptButton> value) => OnPropertyChanged(nameof(HasNodeScripts));

    private void PickNode(Site site)
    {
        Package = Directory.Exists(site.Folder) ? NodeProject.Read(site.Folder) : null;
        NodeDev = _devSessions.GetValueOrDefault(site.Name);
        if (Package is not { HasPackageJson: true } project)
        {
            SiteNode = null;
            NodeText = NodeProblemText = "";
            NodeInstallSpec = null;
            NodeScripts = [];
            return;
        }

        var paths = Main.App.Paths;
        var name = NodeTool.Name(project.Manager);
        NodeScripts = project.Scripts.Select(s => new NodeScriptButton(s, $"{name} run {s.Name}")).ToList();
        SiteNode = TerminalNode.TryResolve(paths, site.Folder, out var problem);
        if (SiteNode is { } choice)
        {
            NodeText = $"{choice.Node.Label} · {NodeTexts.Reason(choice)}";
            NodeProblemText = "";
            NodeInstallSpec = null;
            return;
        }

        NodeText = "";
        (NodeProblemText, NodeInstallSpec) = NodeTexts.Problem(paths, site.Folder, problem);
    }

    [RelayCommand]
    private Task NodeInstallDepsAsync() =>
        Package is { } p ? RunNpmAsync(NodeTool.InstallArguments(p, clean: false)) : Task.CompletedTask;

    [RelayCommand]
    private Task NodeRunScriptAsync(NodeScriptButton? button) =>
        button is null ? Task.CompletedTask
        : button.Script.LongRunning ? StartDevAsync(button.Script)
        : RunNpmAsync(NodeTool.RunArguments(button.Script.Name));

    /// <summary>
    /// npm (o el pnpm o el yarn del lockfile) en la carpeta del sitio, en el diálogo de salida en vivo,
    /// con el Node que toca. Al terminar, el panel vuelve a mirar node_modules/.
    /// </summary>
    private async Task RunNpmAsync(IReadOnlyList<string> arguments)
    {
        if (Selected is not { } row || NodeBusy) return;
        if (Prepare(row, arguments) is not { } prepared) return;
        NodeBusy = true;
        try
        {
            var (spec, choice, manager) = prepared;
            var display = NodeTool.Display(manager, arguments);
            var dialog = new CommandOutputDialogViewModel(
                $"{display} · {row.Host}",
                $"{choice.Node.Label} · {row.Site.Folder}",
                spec, Runner, text => Main.CopyAsync(text, "Salida de npm copiada"))
            {
                CommandText = display,
                Note = "npm ejecuta los scripts del package.json del proyecto y los de instalación de sus dependencias (postinstall): úsalo con proyectos en los que confíes.",
            };
            var run = dialog.RunAsync();
            await Main.ShowDialogAsync(dialog);
            await run;   // mientras corre, el diálogo no se puede cerrar: aquí ya ha terminado

            if (Selected?.Site.Name == row.Site.Name) PickNode(row.Site);
            if (dialog.Succeeded) Main.Flash($"{display} terminado en {row.Host}");
        }
        finally
        {
            NodeBusy = false;
        }
    }

    /// <summary>
    /// Un script que se queda en marcha (<c>npm run dev</c>): se lanza aparte y se enseña su ventana, que
    /// se puede cerrar sin pararlo. Si ese sitio ya tiene uno en marcha, se enseña ese.
    /// </summary>
    private async Task StartDevAsync(NodeScript script)
    {
        if (Selected is not { } row) return;
        if (_devSessions.TryGetValue(row.Site.Name, out var current) && current.Running)
        {
            if (current.Display.EndsWith(" " + script.Name, StringComparison.Ordinal))
            {
                await ShowDevAsync(current);
                return;
            }
            if (!await Main.ConfirmAsync($"{current.Display} sigue en marcha en {row.Host}",
                    $"Para lanzar {script.Name}, Roost para antes {current.Display} (uno por sitio).", "Parar y lanzar"))
                return;
            current.Stop();
            await current.Completion;
        }

        var arguments = NodeTool.RunArguments(script.Name);
        if (Prepare(row, arguments) is not { } prepared) return;
        var (spec, choice, manager) = prepared;
        var session = new NodeDevSession(row.Site.Name, row.Host, NodeTool.Display(manager, arguments),
            $"{choice.Node.Label} · {row.Site.Folder}", spec, Runner);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NodeDevSession.Running) && !session.Running)
                Main.Flash($"{session.StatusText} en {session.Host}");
        };
        _devSessions[row.Site.Name] = session;
        session.Start();
        if (Selected?.Site.Name == row.Site.Name) NodeDev = session;
        await ShowDevAsync(session);
    }

    private Task ShowDevAsync(NodeDevSession session) =>
        Main.ShowDialogAsync(new NodeDevServerDialogViewModel(session, text => Main.CopyAsync(text, "Salida copiada"), Shell.OpenUrl));

    [RelayCommand]
    private Task NodeShowDevAsync() => NodeDev is { } session ? ShowDevAsync(session) : Task.CompletedTask;

    [RelayCommand]
    private void NodeStopDev() => NodeDev?.Stop();

    [RelayCommand]
    private void NodeOpenDev()
    {
        if (NodeDev?.Url is { } url) Shell.OpenUrl(url);
    }

    /// <summary>Lo que va en <c>server</c> del vite.config.js para servir en HTTPS con el certificado del sitio.</summary>
    [RelayCommand]
    private Task NodeCopyViteAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        var ca = new LocalCa(Main.App.Paths);
        string Js(string path) => path.Replace('\\', '/');
        var origin = Urls.Split(Environment.NewLine)[0];
        var snippet = $$"""
            server: {
                host: '{{row.Host}}',
                https: {
                    key: fs.readFileSync('{{Js(ca.SiteKeyFile(row.Site.Name))}}'),
                    cert: fs.readFileSync('{{Js(ca.SiteCertFile(row.Site.Name))}}'),
                },
                hmr: { host: '{{row.Host}}' },
                cors: { origin: '{{origin}}' },
            },
            """;
        return Main.CopyAsync(snippet, "server de Vite copiado (con import fs from 'node:fs' arriba)");
    }

    /// <summary>
    /// «Instalar Node 24 (LTS)» (o el que pide el .nvmrc): la descarga verificada con el diálogo
    /// Instalando, como Composer; con los shims puestos, añade node, npm y npx.
    /// </summary>
    [RelayCommand]
    private async Task NodeInstallNodeAsync()
    {
        if (Selected is not { } row || NodeInstallSpec is not { } text || NodeBusy) return;
        NodeBusy = true;
        try
        {
            NodeBuild build;
            try { build = await NodeCatalog.ResolveAsync(Main.App.Http, NodeSpec.Parse(text)); }
            catch (RoostException ex)
            {
                await Main.InfoAsync($"No se puede instalar Node {text}", ex.Message, Tone.Bad);
                return;
            }
            if (await EnsureNodeAsync(build) is null) return;
            if (Selected?.Site.Name == row.Site.Name) PickNode(row.Site);
            Main.Flash($"{build.Label} instalado");
        }
        finally
        {
            NodeBusy = false;
        }
    }

    /// <summary>Instala <paramref name="build"/> con el diálogo Instalando; null si no se instaló.</summary>
    private async Task<InstalledNode?> EnsureNodeAsync(NodeBuild build)
    {
        var paths = Main.App.Paths;
        var installer = new NodeInstaller(paths, Main.App.Http);
        if (installer.Find(build) is { } found) return found;

        InstalledNode? installed = null;
        var steps = new List<InstallStep>
        {
            new(build.Pinned ? "LTS fijada en Roost" : "Versión de nodejs.org", build.Pinned
                ? $"· {build.Version}, con su SHA-256 en Roost"
                : $"· {build.Version}, con el SHA-256 de su SHASUMS256.txt"),
            new("Descargando el zip de Windows x64", $"· de {build.Url.Host}"),
            new("Verificar el SHA-256", "· antes de instalar nada"),
            new($"Instalar en {installer.InstallDirectory(build)}", null),
        };
        var dialog = new InstallDialogViewModel($"Instalando {build.Label}", $"node, npm y corepack · desde {build.Url.Host}", build.Size, steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                var bytes = new Progress<long>(b =>
                {
                    d.Downloaded(b);
                    if (d.TotalBytes is { } total && b >= total) d.Step(2, "Verificando");
                });
                installed = await installer.InstallAsync(build, bytes, cancel);
                d.Step(3, "Instalando");
            });
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        if (!await dialog.Finished)
        {
            if (shown is null && dialog.Failed) await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
            return null;
        }

        // Con los shims puestos, node, npm y npx en la terminal también.
        try { new ShimInstaller(paths).AddMissing(AppServices.AppDirectory); }
        catch (RoostException) { /* la terminal sigue sin node de Roost; la app no lo necesita */ }
        return installed;
    }

    /// <summary>La orden lista (con el Node y el gestor de la carpeta), o null si no se puede; entonces lo dice.</summary>
    private (CommandSpec Spec, NodeChoice Choice, NodeManager Manager)? Prepare(SiteRow row, IReadOnlyList<string> arguments)
    {
        try
        {
            if (!Directory.Exists(row.Site.Folder))
                throw new RoostException($"La carpeta del proyecto ya no existe ({row.Site.Folder}).");
            var project = NodeProject.Read(row.Site.Folder);
            var choice = TerminalNode.Resolve(Main.App.Paths, row.Site.Folder, Environment.GetEnvironmentVariable(TerminalNode.VersionVariable));
            return (NodeTool.Command(Main.App.Paths, choice.Node, project.Manager, arguments, row.Site.Folder), choice, project.Manager);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            _ = Main.InfoAsync("No se puede ejecutar npm", ex.Message, Tone.Bad);
            return null;
        }
    }
}

/// <summary>Los textos de Node en la app (con tildes): los de Core son los de la CLI.</summary>
internal static class NodeTexts
{
    /// <summary>Por qué ese Node, en corto: «el que pide .nvmrc (22)», «el global».</summary>
    public static string Reason(NodeChoice choice) => choice.Source switch
    {
        NodeChoiceSource.Variable => $"el de ROOST_NODE_VERSION ({choice.Spec})",
        NodeChoiceSource.VersionFile => $"el que pide {Path.GetFileName(choice.File)} ({choice.Spec})",
        NodeChoiceSource.Engines => $"el más nuevo que cumple engines.node ({choice.Spec}); el global no",
        _ => choice.Spec is null ? "el global (el más nuevo instalado)" : $"el global ({choice.Spec})",
    } + (choice.EnginesMet ? "" : $" · ojo: engines.node pide {choice.Engines} y ningún Node de Roost lo cumple");

    /// <summary>«npm · package-lock.json», «pnpm (pnpm-lock.yaml) con corepack», o por qué no se puede.</summary>
    public static string Manager(NodeProject project, InstalledNode? node)
    {
        var name = NodeTool.Name(project.Manager);
        var lockText = project.LockFile is null ? "sin lockfile" : project.LockFile;
        return project.Manager switch
        {
            NodeManager.Bun => $"bun ({lockText}): Roost no trae bun. Instálalo tú (bun install); los scripts, con npm run.",
            NodeManager.Pnpm or NodeManager.Yarn when node is { HasCorepack: false } =>
                $"{name} ({lockText}) va con corepack, y {node.Label} no lo trae: usa una LTS de Roost (la 22 o la 24).",
            NodeManager.Pnpm or NodeManager.Yarn => $"{name} ({lockText}), con el corepack de Node (lo baja la primera vez)",
            _ => $"npm · {lockText}",
        };
    }

    /// <summary>
    /// Por qué no hay Node en la carpeta, con tildes, y qué versión ofrecer instalar desde el panel
    /// (null: ninguna; por ejemplo, un .nvmrc con «system»).
    /// </summary>
    public static (string Text, string? Install) Problem(Roost.Core.Platform.RoostPaths paths, string folder, string? problem)
    {
        var missing = TerminalNode.MissingSpec(paths, folder);
        if (new NodeInstaller(paths).ListInstalled().Count == 0)
            return ("Node no está instalado en Roost. Roost trae las LTS de nodejs.org, verificadas con su SHA-256: instala una y npm queda listo para este proyecto.", missing);
        string? file = null;
        try { file = TerminalNode.FindVersionFile(Path.GetFullPath(folder)); } catch (Exception ex) when (ex is IOException or ArgumentException) { }
        string? spec = null;
        try { spec = file is null ? null : TerminalNode.ReadVersionFile(file); } catch (IOException) { }
        if (spec is not null && NodeSpec.TryParse(spec, out var parsed, out _) && parsed.Kind == NodeSpecKind.System)
            return ($"{Path.GetFileName(file)} pide «system» (el Node de fuera de Roost), que Roost no usa. Pon una versión (22, lts/*) o quita el fichero.", null);
        if (file is not null && missing is not null)
            return ($"{Path.GetFileName(file)} pide Node {spec}, que no está instalado en Roost.", missing);
        return ($"No se puede elegir el Node de esta carpeta: {problem}", missing);
    }

    /// <summary>El texto del paso npm en el diálogo de los pasos de instalación.</summary>
    public static string DeployHint(DeployStep step, DeployTarget target)
    {
        if (target.Node is not { } node)
            return target.NodeMissing is { } v
                ? $"Hace falta Node {v} en Roost: instálalo desde el bloque Node del panel del sitio (o con roost node install {v}) y repite este paso."
                : $"No se puede usar Node aquí: {target.NodeProblem}";
        var name = NodeTool.Name(step.NodeManager);
        if (!step.Available)
            return step.NodeManager == NodeManager.Bun
                ? "El proyecto usa bun (bun.lock) y Roost no trae bun: instálalo tú, o usa npm."
                : $"{name} va con corepack, y {node.Node.Label} no lo trae: usa una LTS de Roost (la 22 o la 24).";
        var with = step.NodeManager is NodeManager.Pnpm or NodeManager.Yarn
            ? $"Con {node.Node.Label}, {Reason(node)}, y el {name} de corepack"
            : $"Con {node.Node.Label}, {Reason(node)}";
        return step.HasNodeModules ? $"{with}. node_modules/ ya está: márcalo solo si quieres repetirlo."
            : target.Fresh ? $"{with}: las dependencias de la plantilla y, con su build, los assets de Vite."
            : !step.HasLock ? $"{with}. Sin lockfile: npm install resuelve las versiones y crea package-lock.json."
            : $"{with}: lo que fija el lockfile{(step.HasBuildScript ? ", y luego el build" : "")}.";
    }
}
