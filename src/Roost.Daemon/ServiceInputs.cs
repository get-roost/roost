using System.Security.Cryptography;
using System.Text;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Daemon;

/// <summary>
/// Lo que lee cada servicio del stack al arrancar (y el servidor web al recargar), resumido en una
/// huella. Si con la misma definición cambia la huella, hay que reiniciarlo, o recargarlo, para que
/// lo lea: es la otra mitad de la comparación de la recarga selectiva (<see cref="ReloadPlan"/>).
/// <list type="bullet">
/// <item>el servidor web: su nginx.conf o httpd.conf, los <c>sites/*.conf</c> (también lo editado fuera de las marcas) y el certificado y la clave de cada sitio;</item>
/// <item>un php-cgi: el php.ini de su instalación y los <c>.ini</c> de conf.d, el común y el de su rama;</item>
/// <item>la base de datos: su my.ini sin el <c>[client]</c> (lo lee el cliente, no el servidor: cambiar la contraseña de root no la reinicia) y el <c>usuario.cnf</c>;</item>
/// <item>Redis: su redis.conf y el <c>99-usuario.conf</c>.</item>
/// </list>
/// Mailpit y los servicios de las extensiones no leen nada de Roost: cuenta solo su definición.
/// Se guarda la huella cuando el servicio arranca o se recarga, así un cambio hecho entre dos
/// <c>up</c> (un <c>cert renew</c>, el conf.d del usuario) también se aplica.
/// </summary>
internal static class ServiceInputs
{
    public static Dictionary<string, string> Of(RoostPaths paths, StackPlan plan) =>
        plan.Services.ToDictionary(s => s.Name, s => Fingerprint(paths, plan, s), StringComparer.Ordinal);

    /// <summary>La huella de lo que lee <paramref name="service"/>; la misma para todo lo que no lee nada.</summary>
    public static string Fingerprint(RoostPaths paths, StackPlan plan, ServiceDefinition service)
    {
        var dbIni = plan.Db is { Branch: not null } db ? DbIni.PathFor(paths, db) : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files(paths, plan, service))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.ToLowerInvariant() + "\n"));
            byte[] content;
            try
            {
                content = string.Equals(file, dbIni, StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8.GetBytes(ServerSections(File.ReadAllText(file)))
                    : File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                content = "\u0001no existe"u8.ToArray();
            }
            hash.AppendData(content);
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Los ficheros que lee <paramref name="service"/>, en un orden fijo (existan o no).</summary>
    public static IEnumerable<string> Files(RoostPaths paths, StackPlan plan, ServiceDefinition service)
    {
        var planner = new StackPlanner(paths, ProcessRunner.Default);   // solo sus rutas
        if (service.Name == plan.WebServer)
        {
            var apache = plan.WebServer == RoostConfig.WebApache;
            yield return apache ? planner.ApacheConfPath : planner.NginxConfPath;
            foreach (var conf in Sorted(apache ? paths.ApacheSites : paths.NginxSites, "*.conf", ".conf"))
                yield return conf;
            var ca = new LocalCa(paths);
            foreach (var site in plan.Sites.OrderBy(s => s.Site.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return ca.SiteCertFile(site.Site.Name);
                yield return ca.SiteKeyFile(site.Site.Name);
            }
            // Compartir (oleada 6): las escuchas de lo compartido y el certificado con la IP de la red local.
            foreach (var conf in Sorted(apache ? paths.ApacheShares : paths.NginxShares, "*.conf", ".conf"))
                yield return conf;
            foreach (var share in plan.Shares.Where(s => s.Kind == Roost.Core.Share.ShareKind.Lan).OrderBy(s => s.Site, StringComparer.OrdinalIgnoreCase))
            {
                yield return ca.LanCertFile(share.Site);
                yield return ca.LanKeyFile(share.Site);
            }
        }
        else if (ReloadPlan.IsPhpMember(service.Name) && PhpOf(plan, service) is { } php)
        {
            yield return planner.PhpIniPath(php);
            foreach (var ini in Sorted(planner.PhpConfD, "*.ini", ".ini")) yield return ini;
            foreach (var ini in Sorted(planner.PhpBranchConfD(php), "*.ini", ".ini")) yield return ini;
        }
        else if (plan.Db is { Branch: { } branch } db && service.Name == $"{db.Engine.Key()}-{branch}")
        {
            yield return DbIni.PathFor(paths, db);
            yield return DbIni.UserFilePath(paths, db);
        }
        else if (service.Name == "redis" && plan.Redis is not null)
        {
            yield return RedisConf.ConfPath(paths);
            yield return RedisConf.UserFragmentPath(paths);
        }
    }

    /// <summary>La instalación de PHP de un php-cgi del plan: la que tiene ese php-cgi.exe.</summary>
    private static InstalledPhp? PhpOf(StackPlan plan, ServiceDefinition service) =>
        new[] { plan.Php }.Concat(plan.Sites.Select(s => s.Php))
            .FirstOrDefault(p => string.Equals(p.CgiExecutable, service.Executable, StringComparison.OrdinalIgnoreCase));

    /// <summary>Los ficheros con esa extensión de una carpeta (sin bajar a las de dentro), ordenados; nada si no existe.</summary>
    private static IEnumerable<string> Sorted(string directory, string pattern, string extension)
    {
        if (!Directory.Exists(directory)) return [];
        // El patrón de Windows también casa con extensiones más largas (.conf.removed): se filtra a mano.
        return Directory.EnumerateFiles(directory, pattern)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Las secciones de un fichero de opciones de MySQL que no son de clientes.</summary>
    private static readonly HashSet<string> ClientSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "client", "mysql", "mysqladmin", "mysqldump", "mysqlcheck", "mariadb-client", "mariadb-dump", "mariadb-admin",
    };

    /// <summary>
    /// PURO: el texto de un my.ini sin las secciones que solo leen los clientes (<c>[client]</c>,
    /// <c>[mysqladmin]</c>…), que es donde va la contraseña de root. Las directivas como
    /// <c>!include</c> se quedan siempre.
    /// </summary>
    internal static string ServerSections(string text)
    {
        var kept = new StringBuilder();
        var client = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                client = ClientSections.Contains(line[1..^1].Trim());
            if (!client || line.StartsWith('!')) kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }
}
