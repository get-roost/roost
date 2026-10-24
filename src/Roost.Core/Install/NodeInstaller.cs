using System.IO.Compression;
using System.Text.Json;
using Roost.Core.Models;
using Roost.Core.Node;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Un Node instalado por Roost, en <c>bin/node/&lt;versión&gt;</c>: el zip de nodejs.org tal cual, con
/// su <c>node.exe</c> y el npm que trae en <c>node_modules/npm</c>.
/// </summary>
public sealed record InstalledNode(string Version, string Directory)
{
    public static string ExecutableName => OsNames.Exe("node");

    public ComponentVersion ParsedVersion => ComponentVersion.TryParse(Version, out var v) ? v : default;

    public int Major => ParsedVersion.Major;

    public string Label => $"Node {Version}";

    public string Executable => Path.Combine(Directory, ExecutableName);

    /// <summary>El npm que trae (lo que ejecuta su <c>npm.cmd</c>).</summary>
    public string NpmCli => Path.Combine(Directory, "node_modules", "npm", "bin", "npm-cli.js");

    public string NpxCli => Path.Combine(Directory, "node_modules", "npm", "bin", "npx-cli.js");

    /// <summary>Corepack, para pnpm y yarn. Lo traen la 22 y la 24; desde la 25, Node ya no lo incluye.</summary>
    public string CorepackJs => Path.Combine(Directory, "node_modules", "corepack", "dist", "corepack.js");

    public bool HasCorepack => File.Exists(CorepackJs);

    /// <summary>La versión del npm que trae (de su <c>package.json</c>), o null si no se puede leer. Lee el disco.</summary>
    public string? NpmVersion
    {
        get
        {
            try
            {
                var file = Path.Combine(Directory, "node_modules", "npm", "package.json");
                if (!File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v)
                       && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Instala y quita Node con las reglas de siempre (ver <c>InstallFiles</c>): el zip se descarga a la
/// caché y se verifica su SHA-256 antes de nada, se extrae a una carpeta <c>.tmp-…</c> que se renombra
/// al final (o queda entero o no queda nada) y cada versión va en su carpeta. Quitar primero renombra:
/// con un <c>node.exe</c> en marcha (un <c>npm run dev</c>), falla sin tocar nada.
/// </summary>
public sealed class NodeInstaller
{
    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar; listar y quitar no van a la red.</param>
    public NodeInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public string InstallDirectory(NodeBuild build) => Path.Combine(_paths.NodeBin, build.Version);

    public InstalledNode? Find(NodeBuild build) => Find(build.Version);

    public InstalledNode? Find(string version)
    {
        var dir = Path.Combine(_paths.NodeBin, version);
        return File.Exists(Path.Combine(dir, InstalledNode.ExecutableName)) ? new InstalledNode(version, dir) : null;
    }

    /// <summary>Los Node instalados, el más nuevo primero. Las carpetas de trabajo (con punto) y lo que no es una versión no cuentan.</summary>
    public IReadOnlyList<InstalledNode> ListInstalled()
    {
        if (!System.IO.Directory.Exists(_paths.NodeBin)) return [];
        return System.IO.Directory.EnumerateDirectories(_paths.NodeBin)
            .Select(dir => new InstalledNode(Path.GetFileName(dir), dir))
            .Where(n => !n.Version.StartsWith('.') && ComponentVersion.TryParse(n.Version, out _) && File.Exists(n.Executable))
            .OrderByDescending(n => n.ParsedVersion)
            .ToList();
    }

    /// <summary>
    /// Instalaciones que casan con lo que se pide: la versión exacta (<c>22.23.4</c>), la rama
    /// (<c>22</c>, <c>22.23</c>) o cualquier cosa que entienda <see cref="NodeSpec"/>. La más nueva primero.
    /// </summary>
    public static IReadOnlyList<InstalledNode> Match(IEnumerable<InstalledNode> installed, NodeSpec spec, DateOnly? today = null)
    {
        var day = today ?? NodeCatalog.Today;
        return installed.Where(n => spec.Matches(n.ParsedVersion, day)).OrderByDescending(n => n.ParsedVersion).ToList();
    }

    public async Task<InstalledNode> InstallAsync(NodeBuild build, IProgress<long>? downloaded = null, CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("NodeInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Find(build) is not null)
            throw new RoostException($"Node {build.Version} ya esta instalado en {target}.");

        try
        {
            InstallFiles.CleanLeftovers(_paths.NodeBin);
            // Una carpeta de la versión sin node.exe no la dejó Roost a medias (renombra al final): se aparta.
            if (System.IO.Directory.Exists(target))
                InstallFiles.RemoveAtomic(target, _paths.NodeBin, $"la carpeta incompleta de Node {build.Version}");

            var zip = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            // Antes de extraer: que sea un Node completo, con su npm.
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (!archive.Entries.Any(e => e.FullName.EndsWith("/node_modules/npm/bin/npm-cli.js", StringComparison.OrdinalIgnoreCase)))
                    throw new RoostException($"El zip {build.FileName} no trae npm (node_modules/npm/bin/npm-cli.js); no se instala.");
            }

            // El zip lo mete todo en node-v<versión>-win-x64/.
            InstallFiles.ExtractAtomic(zip, target, InstalledNode.ExecutableName, stripSingleRoot: true, build.FileName);
            return new InstalledNode(build.Version, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo preparar Node {build.Version}: {ex.Message}", ex);
        }
        catch (RoostException ex)
        {
            throw new RoostException($"No se pudo preparar Node {build.Version}: {ex.Message} Prueba de nuevo (roost node install {build.Version}).", ex);
        }
    }

    /// <summary>Quita un Node de Roost. Si algo lo está usando (un <c>npm run dev</c>, una terminal), falla sin tocar nada.</summary>
    public void Remove(InstalledNode node)
    {
        try
        {
            InstallFiles.RemoveAtomic(node.Directory, _paths.NodeBin, node.Label);
        }
        catch (RoostException ex)
        {
            throw new RoostException(
                $"No se pudo quitar {node.Label}: algo lo esta usando (un npm run dev, una terminal con node). Cierralo y repite. ({ex.InnerException?.Message ?? ex.Message})", ex);
        }
    }
}
