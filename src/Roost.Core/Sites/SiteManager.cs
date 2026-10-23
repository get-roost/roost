using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>El resultado de <see cref="SiteManager.Add"/>: el sitio y si es nuevo y si se creó su carpeta.</summary>
public sealed record SiteChange(Site Site, bool Created, bool FolderCreated);

/// <summary>
/// Dar de alta y de baja sitios, como <c>roost site add / remove</c> (los usan la CLI y la app).
/// Nunca borra la carpeta de un proyecto; el <c>.conf</c> de un sitio quitado se aparta, no se borra.
/// </summary>
public sealed class SiteManager(RoostPaths paths)
{
    /// <summary>Lo que se escribe en la carpeta de un sitio nuevo si no existía.</summary>
    public const string IndexTemplate = """
        <?php // Creado por Roost al registrar el sitio. Sustituyelo por tu proyecto. ?>
        <!doctype html>
        <meta charset="utf-8">
        <h1><?= htmlspecialchars($_SERVER['HTTP_HOST'] ?? '') ?></h1>
        <p>PHP <?= PHP_VERSION ?> &middot; <code><?= htmlspecialchars(__DIR__) ?></code></p>

        """;

    public SiteRegistry Registry { get; } = new(paths);

    /// <summary>Donde se deja la lista de nombres para roost-helper.</summary>
    public string HostsRequestFile => Path.Combine(paths.Run, "hosts-request.txt");

    /// <summary>
    /// Registra el sitio o, si ya existe, lo actualiza. <paramref name="folder"/> null: la que
    /// tenía, o <c>www/&lt;nombre&gt;</c> (que se crea con un index.php si no existe).
    /// <paramref name="root"/> null: la que tenía, o <c>public</c> si existe. <paramref name="php"/>
    /// null: el que tenía; <c>"global"</c>: ninguno propio; si no, tiene que casar con un PHP de Roost.
    /// </summary>
    public SiteChange Add(string name, string? folder = null, string? root = null, string? php = null)
    {
        name = name.Trim().ToLowerInvariant();
        if (!Site.IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre de sitio: usa minusculas, numeros y guiones (por ejemplo tienda o mi-blog).");

        var existing = Registry.Find(name);
        var dir = folder is { } f ? Path.GetFullPath(f) : existing?.Folder ?? Path.Combine(paths.Www, name);
        var folderCreated = false;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.php"), IndexTemplate);
            folderCreated = true;
        }

        var webRoot = root ?? existing?.Root ?? (Directory.Exists(Path.Combine(dir, "public")) ? "public" : null);
        var sitePhp = php is { } wanted
            ? (wanted.Equals("global", StringComparison.OrdinalIgnoreCase) ? null : wanted)
            : existing?.Php;
        if (sitePhp is not null && PhpInstaller.Match(new PhpInstaller(paths).ListInstalled(), sitePhp).Count == 0)
            throw new RoostException($"No hay ningun PHP de Roost que case con '{sitePhp}'. Instalalo: roost php install {sitePhp}");

        var site = new Site { Name = name, Folder = dir, Root = webRoot, Php = sitePhp };
        Registry.Upsert(site);
        new LocalCa(paths).LoadOrCreate();
        return new SiteChange(site, existing is null, folderCreated);
    }

    /// <summary>Quita el sitio: aparta su .conf y borra su certificado. Devuelve el sitio quitado, o null si no había.</summary>
    public Site? Remove(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (name.EndsWith(".test", StringComparison.Ordinal)) name = name[..^".test".Length];
        var site = Registry.Find(name);
        if (site is null || !Registry.Remove(name)) return null;

        // El .conf puede tener cambios del usuario fuera de las marcas: se aparta y deja de cargarse.
        var conf = Path.Combine(paths.NginxSites, name + ".conf");
        if (File.Exists(conf)) File.Move(conf, conf + ".removed", overwrite: true);
        new LocalCa(paths).RemoveSiteCert(name);
        return site;
    }

    /// <summary>El vhost de un sitio, <c>etc/nginx/sites/&lt;nombre&gt;.conf</c> (lo escribe cada up).</summary>
    public string ConfFile(Site site) => Path.Combine(paths.NginxSites, site.Name + ".conf");

    /// <summary>
    /// Deja en el hosts los .test de todos los sitios. Con el hosts del sistema pide UAC (vía
    /// <paramref name="helperExe"/>) solo si el bloque cambia. Con el DNS local no hace nada: los
    /// nombres ya resuelven. true si ha cambiado algo.
    /// </summary>
    public bool SyncHosts(string helperExe)
    {
        if (Config.RoostConfig.Load(paths.ConfigFile).UsesLocalDns) return false;
        return new HostsUpdater().Sync(Registry.Load().Select(s => s.Host), helperExe, HostsRequestFile);
    }
}
