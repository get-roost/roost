using System.IO.Compression;
using System.Text.Json;
using Roost.Core.Models;
using Roost.Core.Node;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Un Node instalado por Roost, en <c>bin/node/&lt;versión&gt;</c>: el paquete de nodejs.org tal cual. En
/// Windows, el zip: <c>node.exe</c> en la raíz y el npm que trae en <c>node_modules/npm</c>. En Linux, el
/// tar.gz: <c>bin/node</c> (y <c>bin/npm</c>, <c>bin/npx</c>, enlaces a los scripts) y <c>lib/node_modules/npm</c>.
/// </summary>
public sealed record InstalledNode(string Version, string Directory)
{
    public static string ExecutableName => OsNames.Exe("node");

    /// <summary>Dónde está el ejecutable dentro de la carpeta: <c>node.exe</c> en Windows, <c>bin/node</c> en Linux.</summary>
    public static string ExecutableRelative => OperatingSystem.IsWindows() ? ExecutableName : Path.Combine("bin", "node");

    public ComponentVersion ParsedVersion => ComponentVersion.TryParse(Version, out var v) ? v : default;

    public int Major => ParsedVersion.Major;

    public string Label => $"Node {Version}";

    /// <summary>
    /// En Linux, true si el Node está «plano» como el zip de Windows (<c>node</c> y <c>node_modules/</c> en la
    /// raíz de la carpeta) en vez de con <c>bin/</c> y <c>lib/</c> como el tar.gz: una copia a mano. Se
    /// acepta igual; lee el disco.
    /// </summary>
    public bool Flat => !OperatingSystem.IsWindows() && !File.Exists(Path.Combine(Directory, "bin", "node")) && File.Exists(Path.Combine(Directory, "node"));

    public string Executable => OperatingSystem.IsWindows() || Flat ? Path.Combine(Directory, ExecutableName) : Path.Combine(Directory, "bin", "node");

    /// <summary>La carpeta con <c>node</c> (y <c>npm</c>, <c>npx</c>): la que va la primera en el PATH de lo que lanza Roost. En Windows es la propia carpeta; en Linux, <c>bin/</c>.</summary>
    public string BinDirectory => OperatingSystem.IsWindows() || Flat ? Directory : Path.Combine(Directory, "bin");

    /// <summary>Los módulos globales que trae (npm y corepack): <c>node_modules</c> en Windows, <c>lib/node_modules</c> en Linux.</summary>
    public string Modules => OperatingSystem.IsWindows() || Flat ? Path.Combine(Directory, "node_modules") : Path.Combine(Directory, "lib", "node_modules");

    /// <summary>El npm que trae (lo que ejecuta su <c>npm.cmd</c>, o su <c>bin/npm</c>).</summary>
    public string NpmCli => Path.Combine(Modules, "npm", "bin", "npm-cli.js");

    public string NpxCli => Path.Combine(Modules, "npm", "bin", "npx-cli.js");

    /// <summary>Corepack, para pnpm y yarn. Lo traen la 22 y la 24; desde la 25, Node ya no lo incluye.</summary>
    public string CorepackJs => Path.Combine(Modules, "corepack", "dist", "corepack.js");

    public bool HasCorepack => File.Exists(CorepackJs);

    /// <summary>La versión del npm que trae (de su <c>package.json</c>), o null si no se puede leer. Lee el disco.</summary>
    public string? NpmVersion
    {
        get
        {
            try
            {
                var file = Path.Combine(Modules, "npm", "package.json");
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
/// Instala y quita Node con las reglas de siempre (ver <c>InstallFiles</c>): el paquete (zip en Windows,
/// tar.gz en Linux, que conserva los permisos y los enlaces de <c>bin/</c>) se descarga a la caché y se
/// verifica su SHA-256 antes de nada, se extrae a una carpeta <c>.tmp-…</c> que se renombra al final (o
/// queda entero o no queda nada) y cada versión va en su carpeta. Quitar primero renombra: con un
/// <c>node</c> en marcha (un <c>npm run dev</c>), falla sin tocar nada.
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
        var node = new InstalledNode(version, Path.Combine(_paths.NodeBin, version));
        return File.Exists(node.Executable) ? node : null;
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

            var archive = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            // Antes de extraer: que sea un Node completo, con su npm.
            if (!ArchiveHasNpm(archive))
                throw new RoostException($"{(InstallFiles.IsTarGz(archive) ? "El tar.gz" : "El zip")} {build.FileName} no trae npm (node_modules/npm/bin/npm-cli.js); no se instala.");

            // El paquete lo mete todo en node-v<versión>-win-x64/ (o -linux-x64/).
            InstallFiles.ExtractAtomic(archive, target, InstalledNode.ExecutableRelative, stripSingleRoot: true, build.FileName);
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

    /// <summary>true si el zip o el tar.gz trae <c>node_modules/npm/bin/npm-cli.js</c> (en el tar.gz, bajo <c>lib/</c>).</summary>
    public static bool ArchiveHasNpm(string archive)
    {
        const string marker = "/node_modules/npm/bin/npm-cli.js";
        if (!InstallFiles.IsTarGz(archive))
        {
            using var zip = ZipFile.OpenRead(archive);
            return zip.Entries.Any(e => e.FullName.EndsWith(marker, StringComparison.OrdinalIgnoreCase));
        }
        using var file = File.OpenRead(archive);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gzip);
        while (tar.GetNextEntry(copyData: false) is { } entry)
            if (entry.Name.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
