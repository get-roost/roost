using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Node;

/// <summary>De dónde sale el Node de una carpeta.</summary>
public enum NodeChoiceSource
{
    /// <summary>La variable <see cref="TerminalNode.VersionVariable"/>.</summary>
    Variable,

    /// <summary>Un <c>.nvmrc</c> o un <c>.node-version</c> en la carpeta o en una de arriba.</summary>
    VersionFile,

    /// <summary><c>engines.node</c> del <c>package.json</c>, que el global no cumple.</summary>
    Engines,

    /// <summary>El global: <c>[node] version</c> de roost.toml o, sin él, el más nuevo instalado.</summary>
    Global,
}

/// <summary>El Node elegido para una carpeta y por qué (lo cuentan <c>roost node which</c>, la app y los pasos).</summary>
/// <param name="Reason">Por qué, sin tildes (la CLI); la app pinta el suyo con <see cref="Source"/>, <see cref="File"/> y <see cref="Spec"/>.</param>
public sealed record NodeChoice(InstalledNode Node, NodeChoiceSource Source, string Reason)
{
    /// <summary>El fichero que lo pide (<c>.nvmrc</c>, <c>.node-version</c> o <c>package.json</c>), o null.</summary>
    public string? File { get; init; }

    /// <summary>Lo que pide ese fichero, la variable o roost.toml («22», «lts/*», «&gt;=20»); null con el más nuevo instalado.</summary>
    public string? Spec { get; init; }

    /// <summary>Con el global: <c>engines.node</c> del proyecto, si lo tiene, y si el global lo cumple.</summary>
    public string? Engines { get; init; }

    public bool EnginesMet { get; init; } = true;
}

/// <summary>
/// Qué Node usa una carpeta (los shims <c>node</c>, <c>npm</c> y <c>npx</c>, la app y los pasos), en
/// este orden, como nvm y fnm:
/// <list type="number">
/// <item>la variable <see cref="VersionVariable"/> (para una orden o un script);</item>
/// <item>el <c>.nvmrc</c> o el <c>.node-version</c> más cercano, en la carpeta o en una de arriba (con
/// los dos en la misma, el <c>.nvmrc</c>), que se versiona con el proyecto;</item>
/// <item><c>engines.node</c> del <c>package.json</c> más cercano: si el global lo cumple, el global; si
/// no, el Node de Roost más nuevo que lo cumpla;</item>
/// <item>el global: <c>[node] version</c> de roost.toml (<see cref="NodeConfig"/>) o el más nuevo instalado.</item>
/// </list>
/// Lo que se pide explícitamente y no está instalado es un error que dice qué instalar, no otro Node.
/// Puro sobre disco (lee los ficheros de versión, el package.json y roost.toml).
/// </summary>
public static class TerminalNode
{
    public const string VersionVariable = "ROOST_NODE_VERSION";

    /// <summary>Los ficheros de versión, por orden: el de nvm y el de fnm, nodenv y Volta.</summary>
    public static readonly IReadOnlyList<string> VersionFiles = [".nvmrc", ".node-version"];

    /// <summary>Lo que Node añade a sus CA (la de Roost: así <c>fetch</c> a un <c>https://….test</c> funciona desde Node).</summary>
    public const string ExtraCaVariable = "NODE_EXTRA_CA_CERTS";

    public static NodeChoice Resolve(RoostPaths paths, string directory, string? pinned, DateOnly? today = null)
    {
        var day = today ?? NodeCatalog.Today;
        var installed = new NodeInstaller(paths).ListInstalled();
        if (installed.Count == 0)
            throw new RoostException($"No hay ningun Node instalado por Roost. Instala uno: roost node install {NodeCatalog.Default.Major}");

        if (!string.IsNullOrWhiteSpace(pinned))
        {
            var spec = pinned.Trim();
            return new NodeChoice(Pick(installed, spec, $"{VersionVariable}={spec}", day), NodeChoiceSource.Variable, $"{VersionVariable}={spec}") { Spec = spec };
        }

        directory = Path.GetFullPath(directory);
        if (FindVersionFile(directory) is { } file)
        {
            var spec = ReadVersionFile(file)
                ?? throw new RoostException($"{file} esta vacio: pon una version (22, 24.22.1, lts/*) o borralo.");
            var reason = $"{file} pide {spec}";
            return new NodeChoice(Pick(installed, spec, reason, day), NodeChoiceSource.VersionFile, reason) { File = file, Spec = spec };
        }

        var global = Global(paths, installed, day);
        if (FindPackageJson(directory) is { } package && NodeProject.Read(Path.GetDirectoryName(package)!).EnginesNode is { } engines
            && NodeSpec.TryParse(engines, out var wanted, out _) && wanted.Kind == NodeSpecKind.Range)
        {
            if (wanted.Matches(global.Node.ParsedVersion, day))
                return global with { Engines = engines, File = package, Reason = $"{global.Reason}, que cumple engines.node ({engines}) de {package}" };
            if (Best(installed, wanted, day) is { } best)
                return new NodeChoice(best, NodeChoiceSource.Engines, $"{package} pide node {engines} en engines, y el global ({global.Node.Version}) no lo cumple")
                {
                    File = package,
                    Spec = engines,
                    Engines = engines,
                };
            return global with
            {
                Engines = engines,
                EnginesMet = false,
                File = package,
                Reason = $"{global.Reason}; ojo: {package} pide node {engines} en engines y ningun Node de Roost lo cumple (roost node install {Suggest(wanted)})",
            };
        }
        return global;
    }

    /// <summary>El Node global (<c>[node] version</c> o el más nuevo instalado), sin mirar ninguna carpeta. Lanza como <see cref="Resolve"/>.</summary>
    public static NodeChoice ResolveGlobal(RoostPaths paths, DateOnly? today = null)
    {
        var installed = new NodeInstaller(paths).ListInstalled();
        if (installed.Count == 0)
            throw new RoostException($"No hay ningun Node instalado por Roost. Instala uno: roost node install {NodeCatalog.Default.Major}");
        return Global(paths, installed, today ?? NodeCatalog.Today);
    }

    /// <summary>Lo mismo sin lanzar: el Node, o null y por qué no (para la app y los pasos de instalación).</summary>
    public static NodeChoice? TryResolve(RoostPaths paths, string directory, out string? problem, DateOnly? today = null)
    {
        try
        {
            problem = null;
            return Resolve(paths, directory, System.Environment.GetEnvironmentVariable(VersionVariable), today);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Lo que pide la carpeta y no está instalado, para ofrecer instalarlo: la versión del
    /// <c>.nvmrc</c> (o de la variable) si es eso lo que falla, o la LTS de Roost si no hay ningún Node.
    /// null si no se sabe qué ofrecer (un <c>system</c>, una versión que no se entiende).
    /// </summary>
    public static string? MissingSpec(RoostPaths paths, string directory)
    {
        if (System.Environment.GetEnvironmentVariable(VersionVariable) is { Length: > 0 } variable) return Installable(variable);
        try
        {
            if (FindVersionFile(Path.GetFullPath(directory)) is { } file && ReadVersionFile(file) is { } spec) return Installable(spec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new NodeInstaller(paths).ListInstalled().Count == 0 ? NodeCatalog.Default.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string? Installable(string spec) =>
        NodeSpec.TryParse(spec, out var parsed, out _) && parsed.Kind != NodeSpecKind.System ? spec.Trim() : null;

    /// <summary>
    /// Lo que necesita un Node lanzado por Roost: su carpeta de binarios la primera en el PATH (así los
    /// scripts de npm que llaman a <c>node</c>, o a <c>npm</c>, usan este mismo, y no otro del sistema ni
    /// el shim otra vez; en Linux es su <c>bin/</c>) y, si el usuario no lo ha puesto ya,
    /// <see cref="ExtraCaVariable"/> con la CA de Roost.
    /// Con <paramref name="interactive"/> false (la app, los pasos), además sin colores ni preguntas: la
    /// salida se pinta tal cual y nadie puede contestar. Nunca toca la caché ni el <c>.npmrc</c> del usuario.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(RoostPaths paths, InstalledNode node, bool interactive = true, Func<string, string?>? env = null)
    {
        env ??= System.Environment.GetEnvironmentVariable;
        var path = env("PATH") ?? env("Path") ?? "";
        var result = new Dictionary<string, string>
        {
            ["PATH"] = path.Length == 0 ? node.BinDirectory : node.BinDirectory + Path.PathSeparator + path,
        };
        var ca = new LocalCa(paths).CaCertFile;
        if (string.IsNullOrEmpty(env(ExtraCaVariable)) && File.Exists(ca)) result[ExtraCaVariable] = ca;
        if (!interactive)
        {
            // picocolors (Vite) pinta colores en Windows aunque la salida no sea una terminal.
            result["NO_COLOR"] = "1";
            result["npm_config_update_notifier"] = "false";
            result["npm_config_fund"] = "false";
            // corepack: sin preguntar antes de bajar pnpm o yarn, y sin escribir packageManager en el package.json.
            result["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
            result["COREPACK_ENABLE_AUTO_PIN"] = "0";
        }
        return result;
    }

    /// <summary>El fichero de versión más cercano subiendo desde <paramref name="directory"/> (en cada carpeta, primero <c>.nvmrc</c>), o null.</summary>
    public static string? FindVersionFile(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            foreach (var name in VersionFiles)
            {
                var file = Path.Combine(dir.FullName, name);
                if (System.IO.File.Exists(file)) return file;
            }
        }
        return null;
    }

    /// <summary>El <c>package.json</c> más cercano subiendo desde <paramref name="directory"/>, o null.</summary>
    public static string? FindPackageJson(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, NodeProject.FileName);
            if (System.IO.File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>
    /// Lo que pide un <c>.nvmrc</c> o un <c>.node-version</c>: la primera línea con algo, sin los
    /// comentarios (<c>#</c>, como nvm) y sin el prefijo de asdf («nodejs 22»).
    /// </summary>
    public static string? ReadVersionFile(string file)
    {
        foreach (var raw in System.IO.File.ReadLines(file))
        {
            var line = raw.Trim().TrimStart('﻿');
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;
            foreach (var prefix in new[] { "nodejs ", "node " })
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) line = line[prefix.Length..].Trim();
            return line;
        }
        return null;
    }

    /// <summary>PURO: lo que va tras el script de npm (o npx) en la línea del shim: el script entre comillas y la línea tal cual.</summary>
    public static string ShimArguments(string script, string rest) =>
        rest.Length == 0 ? $"\"{script}\"" : $"\"{script}\" {rest}";

    private static NodeChoice Global(RoostPaths paths, IReadOnlyList<InstalledNode> installed, DateOnly day)
    {
        var configured = NodeConfig.ReadVersion(paths.ConfigFile);
        if (configured is null)
            return new NodeChoice(installed[0], NodeChoiceSource.Global, "el Node global (el mas nuevo instalado)");
        var reason = $"el Node global ({configured}, en {paths.ConfigFile})";
        if (!NodeSpec.TryParse(configured, out var spec, out var error))
            throw new RoostException($"{paths.ConfigFile}: [node] version: {error}");
        return Best(installed, spec, day) is { } node
            ? new NodeChoice(node, NodeChoiceSource.Global, reason) { Spec = configured }
            : throw new RoostException($"El Node global es {configured} ({paths.ConfigFile}), pero no esta instalado. Instalalo (roost node install {Suggest(spec)}) o elige otro (roost node use <version>).");
    }

    private static InstalledNode Pick(IReadOnlyList<InstalledNode> installed, string text, string reason, DateOnly day)
    {
        if (!NodeSpec.TryParse(text, out var spec, out var error))
            throw new RoostException($"{reason}, pero no se entiende: {error}");
        if (spec.Kind == NodeSpecKind.System)
            throw new RoostException($"{reason}: el Node del sistema (system, de nvm). Roost no lo usa: pon una version (22, lts/*) o quita ese fichero.");
        return Best(installed, spec, day)
            ?? throw new RoostException($"{reason}, pero no hay ningun Node de Roost que case. Instalalo: roost node install {Suggest(spec)}");
    }

    private static InstalledNode? Best(IReadOnlyList<InstalledNode> installed, NodeSpec spec, DateOnly day) =>
        NodeInstaller.Match(installed, spec, day).FirstOrDefault();

    /// <summary>Lo que se sugiere instalar para <paramref name="spec"/>: lo mismo, salvo un rango con espacios (entre comillas).</summary>
    private static string Suggest(NodeSpec spec) => spec.Text.Contains(' ') ? $"\"{spec.Text}\"" : spec.Text;
}
