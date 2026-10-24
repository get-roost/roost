using System.Net;
using System.Net.Sockets;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Core.Stack;

// PHP: el PHP global y el de cada sitio, el php.ini y las conf.d de cada instalación, y su pool.
public sealed partial class StackPlanner
{
    /// <summary>
    /// El PHP global: el que case con <c>[php] version</c> (preferido el NTS, que es el que
    /// usa nginx) o, sin esa clave, el más nuevo instalado.
    /// </summary>
    public InstalledPhp ResolvePhp(RoostConfig config, IReadOnlyList<InstalledPhp> installed)
    {
        if (installed.Count == 0)
            throw new RoostException("No hay ningun PHP instalado por Roost. Instala uno: roost php install 8.4");

        var candidates = config.PhpVersion is { } spec ? PhpInstaller.Match(installed, spec) : installed;
        if (candidates.Count == 0)
        {
            throw new RoostException(
                $"{paths.ConfigFile} pide PHP {config.PhpVersion}, pero no hay ningun PHP de Roost que case. "
                + $"Instala uno (roost php install {config.PhpVersion}) o elige otro (roost php use <version>).");
        }

        return Best(candidates);
    }

    /// <summary>El PHP de un sitio: el que fija (<c>--php</c>) o, si no fija ninguno, el global.</summary>
    public InstalledPhp ResolveSitePhp(Site site, InstalledPhp global, IReadOnlyList<InstalledPhp> installed)
    {
        if (site.Php is not { } spec) return global;
        var candidates = PhpInstaller.Match(installed, spec);
        if (candidates.Count == 0)
        {
            throw new RoostException(
                $"El sitio {site.Host} pide PHP {spec}, pero no hay ningun PHP de Roost que case. "
                + $"Instala uno (roost php install {spec}) o cambia el del sitio (roost site add {site.Name} --php <version>).");
        }
        return Best(candidates);
    }

    /// <summary>
    /// Por qué no se puede quitar <paramref name="target"/>, o null si se puede: no se quita el
    /// PHP global, ni uno que pida un sitio si no queda otro que case con lo que pide (el siguiente
    /// <c>up</c> fallaría). Lo usa <c>roost php remove</c>; la app hace lo mismo y además ofrece mover los sitios.
    /// </summary>
    public string? WhyNotRemove(InstalledPhp target, RoostConfig config, IReadOnlyList<InstalledPhp> installed, IReadOnlyList<Site> sites)
    {
        InstalledPhp? global = null;
        try { global = ResolvePhp(config, installed); } catch (RoostException) { }
        if (global?.Name == target.Name)
            return $"PHP {target.Name} es el PHP global: sirve localhost y los sitios sin PHP propio. Elige otro antes (roost php use <version>) y luego quitalo.";

        var remaining = installed.Where(p => p.Name != target.Name).ToList();
        var stranded = sites
            .Where(s => s.Php is { } spec && PhpInstaller.Match(installed, spec).Any(p => p.Name == target.Name)
                        && PhpInstaller.Match(remaining, spec).Count == 0)
            .Select(s => s.Name)
            .ToList();
        return stranded.Count == 0 ? null
            : $"Lo usa{(stranded.Count == 1 ? "" : "n")} {string.Join(", ", stranded)} y no queda otro PHP que case con lo que pide{(stranded.Count == 1 ? "" : "n")}. "
              + $"Cambialo{(stranded.Count == 1 ? "" : "s")} antes (roost site add <sitio> --php <version>) o instala otro de esa rama.";
    }

    private static InstalledPhp Best(IEnumerable<InstalledPhp> candidates) => PhpInstaller.Best(candidates);

    private static string Branch(InstalledPhp php) =>
        php.Version is { } v ? $"{v.Major}.{v.Minor}" : throw new RoostException($"No se reconoce la version de la instalacion {php.Name}.");

    /// <summary>La carpeta conf.d que PHP lee en todas las versiones (<c>PHP_INI_SCAN_DIR</c>).</summary>
    public string PhpConfD => Path.Combine(paths.Etc, "php", "conf.d");

    /// <summary>La conf.d de una rama (<c>conf.d/8.4</c>): solo la leen las instalaciones de esa rama, después de la común.</summary>
    public string PhpBranchConfD(InstalledPhp php) => Path.Combine(PhpConfD, Branch(php));

    /// <summary>
    /// Lo que va en <c>PHP_INI_SCAN_DIR</c> para una instalación: la conf.d común y la de su rama, en
    /// ese orden (PHP lee varias separadas por <c>;</c> en Windows, cada una por orden alfabético), así
    /// que lo de la rama manda. Lo usan su pool de php-cgi y la terminal (<c>TerminalPhp</c>).
    /// </summary>
    public string PhpScanDir(InstalledPhp php) => string.Join(Path.PathSeparator, PhpConfD, PhpBranchConfD(php));

    /// <summary>El fragmento del usuario, <c>conf.d/99-usuario.ini</c>; lo crea con la plantilla si no existe.</summary>
    public string EnsureUserIni()
    {
        Directory.CreateDirectory(PhpConfD);
        var userIni = Path.Combine(PhpConfD, PhpIni.UserFragmentName);
        if (!File.Exists(userIni)) File.WriteAllText(userIni, PhpIni.UserFragmentTemplate);
        return userIni;
    }

    /// <summary>El fragmento del usuario de la rama, <c>conf.d/&lt;rama&gt;/99-usuario.ini</c>; lo crea con la plantilla si no existe.</summary>
    public string EnsureBranchUserIni(InstalledPhp php)
    {
        var dir = PhpBranchConfD(php);
        Directory.CreateDirectory(dir);
        var userIni = Path.Combine(dir, PhpIni.UserFragmentName);
        if (!File.Exists(userIni)) File.WriteAllText(userIni, PhpIni.BranchFragmentTemplate(Branch(php)));
        return userIni;
    }

    /// <summary>El php.ini generado de una instalación.</summary>
    public string PhpIniPath(InstalledPhp php) => Path.Combine(paths.Etc, "php", php.Name, "php.ini");

    /// <summary>Las extensiones que ha elegido el usuario para una instalación (también Xdebug).</summary>
    public string PhpExtensionChoicePath(InstalledPhp php) => Path.Combine(paths.Etc, "php", php.Name, PhpExtensionChoice.FileName);

    /// <summary>Los ajustes rápidos de una instalación (<c>memory_limit</c>…).</summary>
    public string PhpSettingsPath(InstalledPhp php) => Path.Combine(paths.Etc, "php", php.Name, PhpSettings.FileName);

    /// <summary>
    /// (Re)genera el php.ini de la instalación con las extensiones, Xdebug y los ajustes rápidos que
    /// ha elegido el usuario, y devuelve su ruta. Crea además la conf.d de su rama. Lo hace cada
    /// <c>up</c>; la app y <c>roost php ext/xdebug</c> lo llaman también al cambiar algo, y luego
    /// reinician solo ese pool.
    /// </summary>
    public string WritePhpIni(InstalledPhp php)
    {
        var ini = PhpIniPath(php);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        EnsureUserIni();
        EnsureBranchUserIni(php);
        File.WriteAllText(ini, PhpIni.Render(
            php.Name,
            php.Directory,
            PhpIni.AvailableExtensions(php.Directory),
            PhpConfD,
            Path.Combine(paths.Logs, $"php-{Branch(php)}-error.log"),
            PhpExtensionChoice.Load(PhpExtensionChoicePath(php)),
            CaBundle.Write(paths),
            PhpSettings.Load(PhpSettingsPath(php)),
            new XdebugInstaller(paths).Locate(php),
            PhpBranchConfD(php)));
        return ini;
    }

    /// <summary>php.ini de la instalación y los php-cgi de su pool (ROOST.md §5.5 y §5.10).</summary>
    private (NginxUpstream Upstream, List<ServiceDefinition> Services) PreparePhp(InstalledPhp php, int poolSize, string confD)
    {
        var version = php.Version!.Value;
        if (!File.Exists(php.CgiExecutable))
            throw new RoostException($"{php.Name} no trae php-cgi.exe: no puede servir peticiones web.");

        // php.ini generado (se pisa siempre); lo del usuario va en conf.d, la común y la de la rama.
        var ini = WritePhpIni(php);
        var branch = Branch(php);
        var scanDir = string.Join(Path.PathSeparator, confD, PhpBranchConfD(php));

        var ports = PhpPool.Ports(version, poolSize);
        var services = new List<ServiceDefinition>();
        for (var i = 0; i < ports.Count; i++)
        {
            services.Add(new ServiceDefinition
            {
                Name = $"php-{branch}-{i}",
                Executable = php.CgiExecutable,
                Arguments = $"-b 127.0.0.1:{ports[i]} -c \"{ini}\"",
                WorkingDirectory = php.Directory,
                Environment = new Dictionary<string, string>
                {
                    // php-cgi atiende una petición cada vez y sale tras estas; el supervisor lo relanza.
                    // Cada uno con su número, para que no se reciclen todos a la vez (PhpPool.MaxRequests).
                    ["PHP_FCGI_MAX_REQUESTS"] = PhpPool.MaxRequests(i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["PHP_INI_SCAN_DIR"] = scanDir,
                },
                LogFile = Path.Combine(paths.Logs, $"php-{branch}-{i}.log"),
                Port = ports[i],
            });
        }

        return (new NginxUpstream(PhpPool.UpstreamName(version), ports), services);
    }
}
