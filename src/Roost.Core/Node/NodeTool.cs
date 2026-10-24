using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Node;

/// <summary>
/// Las órdenes de npm (y de pnpm y yarn, con corepack) que lanza Roost en la carpeta de un proyecto:
/// <c>node.exe npm-cli.js …</c> con el Node que toca en esa carpeta (<see cref="TerminalNode"/>) y su
/// entorno (<see cref="TerminalNode.Environment"/>), sin ningún <c>npm</c> ni <c>node</c> del sistema.
/// Lo usan la app y los pasos de instalación; se ejecutan sobre <see cref="ICommandRunner"/>.
/// </summary>
public static class NodeTool
{
    /// <summary>El aviso de siempre: npm ejecuta los scripts del propio proyecto (y los de sus dependencias).</summary>
    public const string ScriptsWarning =
        "npm ejecuta los scripts del package.json del proyecto y los de instalacion de sus dependencias (postinstall): hazlo solo con proyectos en los que confies.";

    /// <summary>El nombre del gestor como se escribe en la terminal.</summary>
    public static string Name(NodeManager manager) => manager switch
    {
        NodeManager.Pnpm => "pnpm",
        NodeManager.Yarn => "yarn",
        NodeManager.Bun => "bun",
        _ => "npm",
    };

    /// <summary>
    /// Por qué no se puede usar <paramref name="manager"/> con <paramref name="node"/>, o null si se puede:
    /// bun no lo trae Roost, y pnpm y yarn van con corepack, que no está en Node 25 o más nuevo.
    /// </summary>
    public static string? WhyNot(NodeManager manager, InstalledNode node) => manager switch
    {
        NodeManager.Bun => "El proyecto usa bun (bun.lock) y Roost no trae bun: instalalo tu, o usa npm.",
        NodeManager.Pnpm or NodeManager.Yarn when !node.HasCorepack =>
            $"{Name(manager)} va con corepack, y {node.Label} no lo trae (Node lo quito en la 25): usa una LTS de Roost (roost node install 24) o instala {Name(manager)} tu.",
        _ => null,
    };

    /// <summary>
    /// Los argumentos para instalar las dependencias. Con <paramref name="clean"/> (los pasos tras clonar),
    /// lo que fija el lockfile y sin tocarlo: <c>npm ci</c> si hay <c>package-lock.json</c>,
    /// <c>pnpm install --frozen-lockfile</c>, <c>yarn install --frozen-lockfile</c>. Sin él (el botón del
    /// panel), <c>install</c> a secas, que puede actualizar el lockfile.
    /// </summary>
    public static IReadOnlyList<string> InstallArguments(NodeProject project, bool clean) => project.Manager switch
    {
        NodeManager.Npm => clean && project.HasNpmLock ? ["ci"] : ["install"],
        NodeManager.Pnpm or NodeManager.Yarn when clean && project.LockFile is not null => ["install", "--frozen-lockfile"],
        _ => ["install"],
    };

    /// <summary><c>run &lt;script&gt;</c>, igual en npm, pnpm y yarn.</summary>
    public static IReadOnlyList<string> RunArguments(string script) => ["run", script];

    /// <summary>Lo que se ve de la orden: «npm ci», «pnpm run dev».</summary>
    public static string Display(NodeManager manager, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Name(manager) }.Concat(arguments));

    /// <summary>
    /// La orden: <c>node.exe npm-cli.js &lt;argumentos&gt;</c> (o <c>node.exe corepack.js pnpm …</c>) en
    /// <paramref name="workingDirectory"/>, con el entorno de Roost; sin colores ni preguntas salvo con
    /// <paramref name="interactive"/>. Lanza <see cref="RoostException"/> si ese gestor no se puede usar.
    /// </summary>
    public static CommandSpec Command(
        RoostPaths paths, InstalledNode node, NodeManager manager, IReadOnlyList<string> arguments, string workingDirectory, bool interactive = false)
    {
        if (WhyNot(manager, node) is { } why) throw new RoostException(why);
        var environment = new Dictionary<string, string?>();
        foreach (var (name, value) in TerminalNode.Environment(paths, node, interactive)) environment[name] = value;
        IReadOnlyList<string> script = manager switch
        {
            NodeManager.Pnpm or NodeManager.Yarn => [node.CorepackJs, Name(manager), .. arguments],
            _ => [node.NpmCli, .. arguments],
        };
        return new CommandSpec(node.Executable, script, workingDirectory, environment);
    }

    /// <summary><c>node.exe --version</c>, para comprobar que un Node recién instalado arranca.</summary>
    public static CommandSpec Version(InstalledNode node) =>
        new(node.Executable, ["--version"], node.Directory, Timeout: TimeSpan.FromSeconds(30));
}
