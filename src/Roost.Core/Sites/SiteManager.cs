using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>El resultado de <see cref="SiteManager.Add"/>: el sitio y si es nuevo y si se creó su carpeta.</summary>
public sealed record SiteChange(Site Site, bool Created, bool FolderCreated);

/// <summary>
/// Dar de alta y de baja sitios, como <c>roost site add / remove</c> (los usan la CLI y la app).
/// Nunca borra la carpeta de un proyecto; el <c>.conf</c> de un sitio quitado se aparta, no se borra.
/// Con un <paramref name="git"/>, también clona y actualiza sitios desde un repositorio
/// (<c>SiteManager.Git.cs</c>).
/// </summary>
public sealed partial class SiteManager(RoostPaths paths, Git.GitClient? git = null)
{
    /// <summary>Lo que se escribe en la carpeta de un sitio nuevo si no existía.</summary>
    public const string IndexTemplate = """
        <?php // Creado por Roost al registrar el sitio. Sustituyelo por tu proyecto. ?>
        <!doctype html>
        <meta charset="utf-8">
        <h1><?= htmlspecialchars($_SERVER['HTTP_HOST'] ?? '') ?></h1>
        <p>PHP <?= PHP_VERSION ?> &middot; <code><?= htmlspecialchars(__DIR__) ?></code></p>

        """;

    /// <summary>
    /// Las subcarpetas que suelen ser la raíz web de un proyecto, en el orden en que se proponen
    /// (la app las ofrece como accesos rápidos si existen; <c>Add</c> solo detecta <c>public</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> TypicalRoots = ["public", "web", "htdocs", "public_html", "dist", "wordpress"];

    public SiteRegistry Registry { get; } = new(paths);

    /// <summary>El sitio con ese nombre (vale <c>tienda</c> o <c>tienda.test</c>), o null.</summary>
    public Site? Find(string name) => Registry.Find(NormalizeName(name));

    /// <summary>Donde se deja la lista de nombres para roost-helper.</summary>
    public string HostsRequestFile => Path.Combine(paths.Run, "hosts-request.txt");

    /// <summary>
    /// Registra el sitio o, si ya existe, lo actualiza. <paramref name="folder"/> null: la que
    /// tenía, o <c>www/&lt;nombre&gt;</c> (que se crea con un index.php si no existe); en relativo,
    /// cuelga de la www. <paramref name="root"/> null: la que tenía si la carpeta no cambia, o
    /// <c>public</c> si existe; si se da, pasa por <see cref="NormalizeRoot"/>. <paramref name="php"/>
    /// null: el que tenía; <c>"global"</c>: ninguno propio; si no, tiene que casar con un PHP de Roost.
    /// </summary>
    public SiteChange Add(string name, string? folder = null, string? root = null, string? php = null)
    {
        name = name.Trim().ToLowerInvariant();
        if (!Site.IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre de sitio: usa minusculas, numeros y guiones (por ejemplo tienda o mi-blog).");

        var existing = Registry.Find(name);
        // Una carpeta en relativo es relativa a la www, no a donde se ejecute el comando.
        var dir = folder is { } f ? Path.GetFullPath(f, paths.Www) : existing?.Folder ?? Path.Combine(paths.Www, name);
        var sameFolder = existing is not null && SamePath(existing.Folder, dir);

        var sitePhp = php is { } wanted
            ? (wanted.Equals("global", StringComparison.OrdinalIgnoreCase) ? null : wanted)
            : existing?.Php;
        if (sitePhp is not null && PhpInstaller.Match(new PhpInstaller(paths).ListInstalled(), sitePhp).Count == 0)
            throw new RoostException($"No hay ningun PHP de Roost que case con '{sitePhp}'. Instalalo: roost php install {sitePhp}");

        // Una raíz pedida se valida antes de crear nada: si no vale, no queda ni carpeta ni registro.
        var webRoot = root is not null ? NormalizeRoot(dir, root) : null;

        var folderCreated = false;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.php"), IndexTemplate);
            folderCreated = true;
        }

        // Sin raíz dicha: la de antes solo si sigue siendo la misma carpeta; si la carpeta cambia,
        // se vuelve a mirar (la public/ de un proyecto no vale para otro).
        if (root is null)
            webRoot = sameFolder ? existing!.Root : (Directory.Exists(Path.Combine(dir, "public")) ? "public" : null);

        // El repositorio del que vino el sitio va con su carpeta: si la carpeta cambia, ya no es ese checkout.
        var site = new Site { Name = name, Folder = dir, Root = webRoot, Php = sitePhp, Git = sameFolder ? existing!.Git : null };
        Registry.Upsert(site);
        new LocalCa(paths).LoadOrCreate();
        return new SiteChange(site, existing is null, folderCreated);
    }

    /// <summary>
    /// Cambia la raíz web de un sitio: <paramref name="root"/> es una subcarpeta del proyecto
    /// (relativa, o absoluta dentro de él); null, vacío o <c>.</c> vuelven a la carpeta del
    /// proyecto. Se guarda relativa. Devuelve el sitio ya cambiado.
    /// </summary>
    public Site SetRoot(string name, string? root)
    {
        name = NormalizeName(name);
        var site = Registry.Find(name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        var changed = site with { Root = NormalizeRoot(site.Folder, root) };
        Registry.Upsert(changed);
        return changed;
    }

    /// <summary>
    /// La raíz web que se guarda para <paramref name="root"/>: null si es la propia carpeta del
    /// proyecto (null, vacío, <c>.</c>), y si no la ruta relativa a <paramref name="folder"/>, con
    /// barras. Lanza <see cref="RoostException"/> si no existe o si queda fuera del proyecto
    /// (también si es un enlace que apunta fuera).
    /// </summary>
    public static string? NormalizeRoot(string folder, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var wanted = root.Trim();
        var projectDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wanted, projectDir));
        var relative = Path.GetRelativePath(projectDir, full);
        if (relative == ".") return null;
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new RoostException($"La raiz web tiene que estar dentro de la carpeta del proyecto ({projectDir}); '{wanted}' queda fuera.");
        if (!Directory.Exists(full))
            throw new RoostException($"No existe la carpeta '{relative}' dentro de {projectDir}. Creala o elige otra raiz web.");
        if (ResolvesOutside(full, projectDir))
            throw new RoostException($"'{relative}' es un enlace que apunta fuera de la carpeta del proyecto ({projectDir}).");
        return relative.Replace('\\', '/');
    }

    /// <summary>Quita el sitio: aparta sus .conf (nginx y Apache) y borra su certificado. Devuelve el sitio quitado, o null si no había.</summary>
    public Site? Remove(string name)
    {
        name = NormalizeName(name);
        var site = Registry.Find(name);
        if (site is null || !Registry.Remove(name)) return null;

        // Los .conf pueden tener cambios del usuario fuera de las marcas: se apartan y dejan de
        // cargarse. Los dos servidores, no solo el activo: si no, al volver al otro cargaría un
        // vhost que apunta a un certificado ya borrado.
        foreach (var dir in new[] { paths.NginxSites, paths.ApacheSites })
        {
            var conf = Path.Combine(dir, name + ".conf");
            if (File.Exists(conf)) File.Move(conf, conf + ".removed", overwrite: true);
        }
        new LocalCa(paths).RemoveSiteCert(name);
        return site;
    }

    /// <summary>El vhost de un sitio en el servidor web activo (<c>[web] server</c>): <c>etc/nginx/sites/&lt;nombre&gt;.conf</c> o el de Apache. Lo escribe cada up.</summary>
    public string ConfFile(Site site)
    {
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        return ConfFile(site, config);
    }

    /// <summary>El vhost de un sitio en el servidor web que diga <paramref name="config"/>.</summary>
    public string ConfFile(Site site, RoostConfig config) =>
        Path.Combine(config.UsesApache ? paths.ApacheSites : paths.NginxSites, site.Name + ".conf");

    /// <summary>
    /// Deja en el hosts los .test de todos los sitios. Con el hosts del sistema pide UAC (vía
    /// <paramref name="helperExe"/>) solo si el bloque cambia. Con el DNS local no hace nada: los
    /// nombres ya resuelven. true si ha cambiado algo.
    /// </summary>
    public bool SyncHosts(string helperExe)
    {
        if (RoostConfig.Load(paths.ConfigFile).UsesLocalDns) return false;
        return new HostsUpdater().Sync(Registry.Load().Select(s => s.Host), helperExe, HostsRequestFile);
    }

    /// <summary>Acepta <c>tienda</c> y <c>tienda.test</c>, en cualquier caja.</summary>
    private static string NormalizeName(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".test", StringComparison.Ordinal) ? name[..^".test".Length] : name;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// true si <paramref name="dir"/> es un enlace (symlink, unión) cuyo destino final queda fuera
    /// de <paramref name="projectDir"/> (o de su destino, si el proyecto también es un enlace).
    /// Lo que no se puede resolver se da por bueno: esto es una salvaguarda, no un cortafuegos.
    /// </summary>
    private static bool ResolvesOutside(string dir, string projectDir)
    {
        try
        {
            var target = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return false;
            var project = new DirectoryInfo(projectDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? projectDir;
            var relative = Path.GetRelativePath(project, target.FullName);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
