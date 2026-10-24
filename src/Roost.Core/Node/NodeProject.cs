using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Roost.Core.Node;

/// <summary>Con qué gestor de paquetes va un proyecto de Node (por su <c>packageManager</c> o su lockfile).</summary>
public enum NodeManager
{
    Npm,

    /// <summary><c>pnpm-lock.yaml</c>: con el pnpm de corepack.</summary>
    Pnpm,

    /// <summary><c>yarn.lock</c>: con el yarn de corepack.</summary>
    Yarn,

    /// <summary><c>bun.lock</c>: Roost no trae bun.</summary>
    Bun,
}

/// <summary>Un script del <c>package.json</c>: su nombre, lo que ejecuta y si se queda en marcha (un servidor de desarrollo).</summary>
public sealed record NodeScript(string Name, string Command)
{
    public bool LongRunning => NodeProject.IsLongRunning(Name, Command);
}

/// <summary>
/// Lo que interesa del <c>package.json</c> de una carpeta y de lo que tiene al lado: los scripts (en su
/// orden), <c>engines.node</c>, el gestor de paquetes (el campo <c>packageManager</c> o, si no, el
/// lockfile) y si ya hay <c>node_modules/</c>. Puro sobre disco y nunca lanza: un <c>package.json</c>
/// roto es un proyecto sin scripts (<see cref="Unreadable"/>). Lo lee también el shim (JSON generado:
/// NativeAOT).
/// </summary>
public sealed partial record NodeProject(string Folder, bool HasPackageJson)
{
    public const string FileName = "package.json";

    /// <summary>El <c>package.json</c> no es JSON (o no se puede leer).</summary>
    public bool Unreadable { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<NodeScript> Scripts { get; init; } = [];

    /// <summary><c>engines.node</c> tal cual («&gt;=20», «^22.11»), o null.</summary>
    public string? EnginesNode { get; init; }

    /// <summary>El campo <c>packageManager</c> («pnpm@9.15.0»), o null.</summary>
    public string? PackageManagerField { get; init; }

    public NodeManager Manager { get; init; } = NodeManager.Npm;

    /// <summary>El lockfile que hay (<c>package-lock.json</c>, <c>pnpm-lock.yaml</c>…), o null.</summary>
    public string? LockFile { get; init; }

    public bool HasNodeModules { get; init; }

    /// <summary>Usa Vite (lo tiene en sus dependencias o hay un <c>vite.config.*</c>).</summary>
    public bool UsesVite { get; init; }

    /// <summary>package.json sin node_modules/: hace falta instalar las dependencias.</summary>
    public bool NeedsInstall => HasPackageJson && !HasNodeModules;

    public bool HasScript(string name) => Scripts.Any(s => s.Name == name);

    /// <summary>npm lo hace con <c>npm ci</c> (que exige el lockfile y lo respeta): hay <c>package-lock.json</c> o <c>npm-shrinkwrap.json</c>.</summary>
    public bool HasNpmLock => LockFile is "package-lock.json" or "npm-shrinkwrap.json";

    // Por este orden: si hay varios, gana el primero (como corepack, que solo mira packageManager).
    private static readonly (string File, NodeManager Manager)[] LockFiles =
    [
        ("pnpm-lock.yaml", NodeManager.Pnpm),
        ("yarn.lock", NodeManager.Yarn),
        ("bun.lock", NodeManager.Bun),
        ("bun.lockb", NodeManager.Bun),
        ("package-lock.json", NodeManager.Npm),
        ("npm-shrinkwrap.json", NodeManager.Npm),
    ];

    public static NodeProject Read(string folder)
    {
        try
        {
            var file = Path.Combine(folder, FileName);
            if (!File.Exists(file)) return new NodeProject(folder, false);

            var lockFile = LockFiles.FirstOrDefault(l => File.Exists(Path.Combine(folder, l.File)));
            var project = new NodeProject(folder, true)
            {
                LockFile = lockFile.File,
                Manager = lockFile.File is null ? NodeManager.Npm : lockFile.Manager,
                HasNodeModules = Directory.Exists(Path.Combine(folder, "node_modules")),
                UsesVite = Directory.EnumerateFiles(folder, "vite.config.*").Any(),
            };

            PackageJsonModel? model;
            try { model = JsonSerializer.Deserialize(File.ReadAllText(file), PackageJsonJson.Default.PackageJsonModel); }
            catch (JsonException) { return project with { Unreadable = true }; }
            if (model is null) return project with { Unreadable = true };

            var scripts = new List<NodeScript>();
            if (model.Scripts.ValueKind == JsonValueKind.Object)
            {
                foreach (var script in model.Scripts.EnumerateObject())
                    if (script.Value.ValueKind == JsonValueKind.String && script.Name.Length > 0)
                        scripts.Add(new NodeScript(script.Name, script.Value.GetString()!));
            }

            var field = Text(model.PackageManager);
            return project with
            {
                Name = Text(model.Name),
                Scripts = scripts,
                EnginesNode = model.Engines.ValueKind == JsonValueKind.Object && model.Engines.TryGetProperty("node", out var node) ? Text(node) : null,
                PackageManagerField = field,
                Manager = ManagerOf(field) ?? project.Manager,
                UsesVite = project.UsesVite || Has(model.DevDependencies, "vite") || Has(model.Dependencies, "vite"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new NodeProject(folder, false);
        }
    }

    /// <summary>PURO: el gestor del campo <c>packageManager</c> («pnpm@9.15.0+sha512…» → pnpm), o null.</summary>
    public static NodeManager? ManagerOf(string? field) =>
        (field?.Split('@')[0].Trim().ToLowerInvariant()) switch
        {
            "npm" => NodeManager.Npm,
            "pnpm" => NodeManager.Pnpm,
            "yarn" => NodeManager.Yarn,
            "bun" => NodeManager.Bun,
            _ => null,
        };

    [GeneratedRegex(@"(^|[\s&;|""'])(vite|nuxt|next|astro|remix|svelte-kit|ng|vue-cli-service|react-scripts|webpack-dev-server|nodemon|live-server|http-server|serve|browser-sync|concurrently|storybook)(\s+(dev|serve|start|preview))?(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ServerPattern();

    [GeneratedRegex(@"(^|\s)(--watch|-w|--serve|--hot|watch)(\s|=|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WatchPattern();

    [GeneratedRegex(@"\b(build|lint|test|format|check|typecheck|generate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OneShotPattern();

    /// <summary>
    /// PURO: si el script se queda en marcha hasta que se para (un servidor de desarrollo, un
    /// «watch»): <c>dev</c>, <c>serve</c>, <c>start</c>, <c>watch</c>, <c>preview</c>, <c>hot</c>, o uno
    /// que lanza <c>vite</c> sin <c>build</c>, <c>nodemon</c>, <c>--watch</c>… La app los lanza aparte,
    /// con «Parar», en vez de esperar a que acaben.
    /// </summary>
    public static bool IsLongRunning(string name, string command)
    {
        var n = name.ToLowerInvariant();
        if (n is "dev" or "serve" or "start" or "watch" or "preview" or "hot" or "storybook"
            || n.StartsWith("dev:", StringComparison.Ordinal) || n.StartsWith("watch:", StringComparison.Ordinal) || n.StartsWith("serve:", StringComparison.Ordinal))
            return true;
        if (n is "build" or "test" or "lint" || n.StartsWith("build:", StringComparison.Ordinal)) return false;
        if (WatchPattern().IsMatch(command)) return true;
        // «vite build», «nuxt generate»: acaban. «vite», «vite preview», «next dev»: no.
        return ServerPattern().IsMatch(command) && !OneShotPattern().IsMatch(command);
    }

    private static string? Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static bool Has(JsonElement dependencies, string package) =>
        dependencies.ValueKind == JsonValueKind.Object && dependencies.TryGetProperty(package, out _);
}

/// <summary>Lo que se lee del <c>package.json</c>; todo como <see cref="JsonElement"/>, porque hay de todo (y un campo raro no tumba el resto).</summary>
internal sealed class PackageJsonModel
{
    [JsonPropertyName("name")] public JsonElement Name { get; set; }

    [JsonPropertyName("scripts")] public JsonElement Scripts { get; set; }

    [JsonPropertyName("engines")] public JsonElement Engines { get; set; }

    [JsonPropertyName("packageManager")] public JsonElement PackageManager { get; set; }

    [JsonPropertyName("dependencies")] public JsonElement Dependencies { get; set; }

    [JsonPropertyName("devDependencies")] public JsonElement DevDependencies { get; set; }
}

/// <summary>El JSON del <c>package.json</c>, generado (el shim es NativeAOT). Admite comentarios y comas al final, que npm no admite: así uno así no rompe el panel.</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(PackageJsonModel))]
internal sealed partial class PackageJsonJson : JsonSerializerContext;
