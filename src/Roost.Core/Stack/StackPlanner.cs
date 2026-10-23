using System.Net;
using System.Net.Sockets;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Sites;

namespace Roost.Core.Stack;

/// <summary>Un sitio del plan: el sitio, el PHP que lo sirve y su URL https.</summary>
public sealed record SitePlan(Site Site, InstalledPhp Php, string Url);

/// <summary>
/// Lo que arranca <c>roost up</c>, en este orden (ROOST.md §5.5): la base de datos global si
/// la hay, un pool de php-cgi por cada rama de PHP en uso (la global y la de cada sitio que
/// fije otra) y el servidor web delante (nginx o Apache, <c>[web] server</c>), con
/// <c>http://localhost/</c> y un vhost por sitio.
/// </summary>
/// <param name="WebServer">«nginx» o «apache»: también es el nombre de su servicio.</param>
public sealed record StackPlan(
    InstalledPhp Php,
    string WebServer,
    string WebVersion,
    int HttpPort,
    int HttpsPort,
    InstalledDb? Db,
    int DbPort,
    IReadOnlyList<SitePlan> Sites,
    IReadOnlyList<ServiceDefinition> Services)
{
    public string Url => HttpPort == 80 ? "http://localhost/" : $"http://localhost:{HttpPort}/";

    /// <summary>Los puertos que tienen que estar libres antes de arrancar.</summary>
    public IReadOnlyList<int> Ports =>
        Services.Where(s => s.Port is not null).Select(s => s.Port!.Value)
            .Concat(Sites.Count > 0 ? [HttpsPort] : [])
            .Distinct()
            .ToList();
}

/// <summary>
/// Prepara un <see cref="StackPlan"/>: genera en la raíz de Roost el php.ini de cada PHP en uso,
/// conf.d, el my.ini y el datadir de la base de datos, nginx.conf y el .conf y el certificado
/// de cada sitio. Crea la carpeta www con una portada si no existía, y valida la configuración
/// con <c>nginx -t</c> ANTES de arrancar nada (ROOST.md §5.5: si no valida, no se aplica). No
/// arranca procesos: eso es cosa del <see cref="Supervisor"/>.
/// </summary>
public sealed class StackPlanner(RoostPaths paths, IProcessRunner runner)
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

    /// <summary>
    /// La base de datos global: null con <c>[db] engine = "none"</c> o si Roost no tiene ninguna
    /// instalada; sin <c>[db]</c>, la más nueva (MariaDB antes que MySQL); con <c>[db]</c>, la más
    /// nueva que case, o un error si no hay ninguna.
    /// </summary>
    public InstalledDb? ResolveDb(RoostConfig config, IReadOnlyList<InstalledDb> installed)
    {
        if (config.DbEngine == RoostConfig.NoDb) return null;
        if (config.DbEngine is null && config.DbVersion is null) return installed.FirstOrDefault();

        DbEngine? engine = DbEngines.TryParse(config.DbEngine, out var e) ? e : null;
        var spec = new DbSpec(engine, config.DbVersion);
        return DbInstaller.Match(installed, spec).OrderByDescending(db => db.Version).FirstOrDefault()
            ?? throw new RoostException(
                $"{paths.ConfigFile} pide la base de datos {spec}, pero Roost no tiene ninguna instalada que case. "
                + $"Instalala (roost db install {spec}) o elige otra (roost db use <motor@version>, o roost db use none).");
    }

    /// <summary>my.ini de la base de datos de una rama: <c>etc/&lt;motor&gt;/&lt;rama&gt;/my.ini</c>.</summary>
    public string DbIniPath(InstalledDb db) => Path.Combine(paths.Etc, db.Engine.Key(), db.Branch!, "my.ini");

    /// <summary>URL https de un sitio, con el puerto si no es el 443.</summary>
    public static string SiteUrl(Site site, int httpsPort) =>
        httpsPort == 443 ? $"https://{site.Host}/" : $"https://{site.Host}:{httpsPort}/";

    public StackPlan Prepare(
        RoostConfig config,
        InstalledPhp php,
        InstalledNginx? nginx,
        InstalledDb? db = null,
        IReadOnlyList<Site>? sites = null,
        IReadOnlyList<InstalledPhp>? installedPhp = null,
        LocalCa? ca = null,
        IReadOnlyList<ServiceDefinition>? extraServices = null,
        InstalledApache? apache = null)
    {
        sites ??= [];
        extraServices ??= [];
        installedPhp ??= [php];
        if (sites.Count > 0 && ca is null)
            throw new ArgumentException("Con sitios hace falta la CA local para sus certificados.", nameof(ca));

        if (config.UsesApache && apache is null)
            throw new RoostException($"Apache {ApacheCatalog.Windows.Version} no esta instalado; roost up lo instala antes de arrancar.");
        if (!config.UsesApache && nginx is null)
            throw new RoostException($"nginx {NginxCatalog.Windows.Version} no esta instalado; roost up lo instala antes de arrancar.");

        var confD = PhpConfD;
        foreach (var dir in new[] { confD, paths.Logs, paths.Run })
            Directory.CreateDirectory(dir);
        EnsureUserIni();

        // Un pool por rama: primero la del PHP global, luego la de cada sitio que fije otra.
        // Si dos piden la misma rama, comparten el pool del primero.
        var pools = new List<InstalledPhp> { php };
        var sitePlans = new List<SitePlan>();
        foreach (var site in sites)
        {
            var wanted = ResolveSitePhp(site, php, installedPhp);
            var pool = pools.FirstOrDefault(p => Branch(p) == Branch(wanted));
            if (pool is null) pools.Add(pool = wanted);
            sitePlans.Add(new SitePlan(site, pool, SiteUrl(site, config.HttpsPort)));
        }

        var upstreams = new List<NginxUpstream>();
        var phpServices = new List<ServiceDefinition>();
        foreach (var pool in pools)
        {
            var (upstream, services) = PreparePhp(pool, config.PhpPool, confD);
            upstreams.Add(upstream);
            phpServices.AddRange(services);
        }

        EnsureWww();

        // El servidor web: su configuración (validada antes de arrancar nada) y su servicio.
        var (web, webVersion) = config.UsesApache
            ? (PrepareApache(config, apache!, upstreams, sitePlans, ca), apache!.Version)
            : (PrepareNginx(config, nginx!, upstreams, sitePlans, ca), nginx!.Version);

        // Orden de arranque (ROOST.md §5.5): DB → servicios de las extensiones → pools de PHP →
        // servidor web. La parada, al revés.
        var all = new List<ServiceDefinition>();
        if (db is not null)
            all.Add(PrepareDb(db, config.DbPort));
        all.AddRange(extraServices);
        all.AddRange(phpServices);
        all.Add(web);

        // Los nombres identifican a cada servicio (status, restart, logs): una extensión no puede repetir uno.
        if (all.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new RoostException($"Hay dos servicios llamados '{repeated.Key}': una extension usa un nombre que ya existe.");

        return new StackPlan(php, web.Name, webVersion, config.HttpPort, config.HttpsPort, db, config.DbPort, sitePlans, all);
    }

    /// <summary>nginx.conf, un .conf por sitio, <c>nginx -t</c> y su servicio.</summary>
    private ServiceDefinition PrepareNginx(RoostConfig config, InstalledNginx nginx, List<NginxUpstream> upstreams, List<SitePlan> sitePlans, LocalCa? ca)
    {
        foreach (var dir in new[] { Path.Combine(paths.Etc, "nginx"), paths.NginxSites, Path.Combine(paths.Run, "nginx-temp") })
            Directory.CreateDirectory(dir);

        // Un .conf por sitio: nuevo la primera vez; después solo se regenera el bloque de Roost.
        foreach (var plan in sitePlans)
        {
            var (certFile, keyFile) = ca!.EnsureSiteCert(plan.Site);
            var input = new SiteConf.Input(
                plan.Site, PhpPool.UpstreamName(plan.Php.Version!.Value), config.HttpPort, config.HttpsPort,
                certFile, keyFile, nginx.FastCgiParams);
            var file = Path.Combine(paths.NginxSites, plan.Site.Name + ".conf");
            File.WriteAllText(file, File.Exists(file) ? SiteConf.Merge(File.ReadAllText(file), input, file) : SiteConf.RenderNew(input));
        }

        var conf = NginxConfPath;
        File.WriteAllText(conf, NginxConf.Render(new NginxConfInput(
            nginx.MimeTypes,
            nginx.FastCgiParams,
            paths.Run,
            paths.Logs,
            paths.Www,
            config.HttpPort,
            upstreams,
            upstreams[0].Name,
            paths.NginxSites)));

        var test = TestNginx(nginx);
        if (test is null || !test.Contains("test is successful", StringComparison.OrdinalIgnoreCase))
            throw new RoostException($"nginx no acepta la configuracion generada ({conf}):\n{test?.Trim() ?? "nginx -t no respondio"}");

        return new ServiceDefinition
        {
            Name = "nginx",
            Executable = nginx.Executable,
            Arguments = NginxArguments,
            WorkingDirectory = nginx.Directory,
            LogFile = Path.Combine(paths.Logs, "nginx-stdout.log"),
            Port = config.HttpPort,
        };
    }

    /// <summary>
    /// httpd.conf, un .conf por sitio, <c>httpd -t</c> y su servicio. El PHP va por los mismos
    /// pools de php-cgi que nginx (<see cref="ApacheConf"/>).
    /// </summary>
    private ServiceDefinition PrepareApache(RoostConfig config, InstalledApache apache, List<NginxUpstream> pools, List<SitePlan> sitePlans, LocalCa? ca)
    {
        foreach (var dir in new[] { Path.Combine(paths.Etc, "apache"), paths.ApacheSites })
            Directory.CreateDirectory(dir);

        foreach (var plan in sitePlans)
        {
            var (certFile, keyFile) = ca!.EnsureSiteCert(plan.Site);
            var input = new ApacheSiteConf.Input(
                plan.Site, PhpPool.UpstreamName(plan.Php.Version!.Value), config.HttpPort, config.HttpsPort, certFile, keyFile);
            var file = Path.Combine(paths.ApacheSites, plan.Site.Name + ".conf");
            File.WriteAllText(file, File.Exists(file) ? ApacheSiteConf.Merge(File.ReadAllText(file), input, file) : ApacheSiteConf.RenderNew(input));
        }

        var conf = ApacheConfPath;
        File.WriteAllText(conf, ApacheConf.Render(new ApacheConfInput(
            apache.Directory,
            paths.Run,
            paths.Logs,
            paths.Www,
            config.HttpPort,
            sitePlans.Count > 0 ? config.HttpsPort : null,
            pools,
            pools[0].Name,
            paths.ApacheSites)));

        var test = TestApache(apache);
        if (test is null || !test.Contains("Syntax OK", StringComparison.OrdinalIgnoreCase))
            throw new RoostException($"Apache no acepta la configuracion generada ({conf}):\n{test?.Trim() ?? "httpd -t no respondio"}");

        // Apache se para matando su árbol de procesos y el pidfile se queda: sin borrarlo, cada
        // arranque avisaría en el log de un «Unclean shutdown» que no es tal.
        var pidFile = Path.Combine(paths.Run, "apache.pid");
        if (File.Exists(pidFile)) File.Delete(pidFile);

        return new ServiceDefinition
        {
            Name = "apache",
            Executable = apache.Executable,
            Arguments = ApacheArguments,
            WorkingDirectory = apache.Directory,
            LogFile = Path.Combine(paths.Logs, "apache-stdout.log"),
            Port = config.HttpPort,
        };
    }

    /// <summary>El httpd.conf que genera cada <c>up</c> con Apache.</summary>
    public string ApacheConfPath => Path.Combine(paths.Etc, "apache", "httpd.conf");

    /// <summary>Los argumentos con los que se arranca (y se valida) Apache.</summary>
    public string ApacheArguments => $"-f \"{ApacheConf.Path(ApacheConfPath)}\"";

    /// <summary><c>httpd -t</c> sobre la configuración generada: su salida, o null si no responde.</summary>
    public string? TestApache(InstalledApache apache) => runner.Run(apache.Executable, "-t " + ApacheArguments, TimeSpan.FromSeconds(15));

    private static string Branch(InstalledPhp php) =>
        php.Version is { } v ? $"{v.Major}.{v.Minor}" : throw new RoostException($"No se reconoce la version de la instalacion {php.Name}.");

    /// <summary>El nginx.conf que genera cada <c>up</c>.</summary>
    public string NginxConfPath => Path.Combine(paths.Etc, "nginx", "nginx.conf");

    /// <summary>
    /// Los argumentos con los que se arranca (y se valida) nginx. El prefijo es la raíz de Roost:
    /// nginx abre logs/error.log relativo a él antes de leer la configuración.
    /// </summary>
    public string NginxArguments =>
        $"-p \"{NginxConf.Nginx(paths.Root).TrimEnd('/')}/\" -c \"{NginxConf.Nginx(NginxConfPath)}\"";

    /// <summary><c>nginx -t</c> sobre la configuración generada: su salida, o null si no responde.</summary>
    public string? TestNginx(InstalledNginx nginx) => runner.Run(nginx.Executable, "-t " + NginxArguments, TimeSpan.FromSeconds(15));

    /// <summary>La carpeta conf.d que PHP lee en todas las versiones (<c>PHP_INI_SCAN_DIR</c>).</summary>
    public string PhpConfD => Path.Combine(paths.Etc, "php", "conf.d");

    /// <summary>El fragmento del usuario, <c>conf.d/99-usuario.ini</c>; lo crea con la plantilla si no existe.</summary>
    public string EnsureUserIni()
    {
        Directory.CreateDirectory(PhpConfD);
        var userIni = Path.Combine(PhpConfD, PhpIni.UserFragmentName);
        if (!File.Exists(userIni)) File.WriteAllText(userIni, PhpIni.UserFragmentTemplate);
        return userIni;
    }

    /// <summary>El php.ini generado de una instalación.</summary>
    public string PhpIniPath(InstalledPhp php) => Path.Combine(paths.Etc, "php", php.Name, "php.ini");

    /// <summary>Las extensiones que ha elegido el usuario para una instalación.</summary>
    public string PhpExtensionChoicePath(InstalledPhp php) => Path.Combine(paths.Etc, "php", php.Name, PhpExtensionChoice.FileName);

    /// <summary>
    /// (Re)genera el php.ini de la instalación con las extensiones que ha elegido el usuario y
    /// devuelve su ruta. Lo hace cada <c>up</c>; la app lo llama también al cambiar una
    /// extensión, y luego reinicia solo ese pool.
    /// </summary>
    public string WritePhpIni(InstalledPhp php)
    {
        var ini = PhpIniPath(php);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, PhpIni.Render(
            php.Name,
            php.Directory,
            PhpIni.AvailableExtensions(php.Directory),
            PhpConfD,
            Path.Combine(paths.Logs, $"php-{Branch(php)}-error.log"),
            PhpExtensionChoice.Load(PhpExtensionChoicePath(php))));
        return ini;
    }

    /// <summary>php.ini de la instalación y los php-cgi de su pool (ROOST.md §5.5 y §5.10).</summary>
    private (NginxUpstream Upstream, List<ServiceDefinition> Services) PreparePhp(InstalledPhp php, int poolSize, string confD)
    {
        var version = php.Version!.Value;
        if (!File.Exists(php.CgiExecutable))
            throw new RoostException($"{php.Name} no trae php-cgi.exe: no puede servir peticiones web.");

        // php.ini generado (se pisa siempre); lo del usuario va en conf.d.
        var ini = WritePhpIni(php);
        var branch = Branch(php);

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
                    ["PHP_FCGI_MAX_REQUESTS"] = "500",
                    ["PHP_INI_SCAN_DIR"] = confD,
                },
                LogFile = Path.Combine(paths.Logs, $"php-{branch}-{i}.log"),
                Port = ports[i],
            });
        }

        return (new NginxUpstream(PhpPool.UpstreamName(version), ports), services);
    }

    /// <summary>
    /// Genera el my.ini de la base de datos, crea su datadir la primera vez y devuelve su
    /// servicio. Se para limpio: se le pide <c>shutdown</c> con el admin y se espera.
    /// </summary>
    private ServiceDefinition PrepareDb(InstalledDb db, int port)
    {
        if (db.Version is not { } version || db.Branch is not { } branch)
            throw new RoostException($"No se reconoce la version de la instalacion {db.Label}.");

        var ini = DbIniPath(db);
        var datadir = Path.Combine(paths.Data, db.Engine.Key(), branch);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, DbIni.Render(db.Engine, version, db.Directory, datadir, port));
        EnsureDataDir(db, ini, datadir);

        var name = $"{db.Engine.Key()}-{branch}";
        var log = Path.Combine(paths.Logs, $"{name}.log");
        return new ServiceDefinition
        {
            Name = name,
            Executable = db.Server,
            // --console: el log del servidor va a stderr, que el supervisor guarda en el log.
            Arguments = $"--defaults-file=\"{ini}\" --console",
            WorkingDirectory = db.Directory,
            LogFile = log,
            Port = port,
            StopCommand = new ServiceDefinition
            {
                Name = name + "-shutdown",
                Executable = db.Admin,
                Arguments = $"--defaults-file=\"{ini}\" shutdown",
                WorkingDirectory = db.Directory,
                LogFile = log,
            },
            StopTimeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Crea el datadir de la rama la primera vez (root sin contraseña), en una carpeta temporal
    /// que se renombra al final. Un datadir que ya existe no se toca nunca; si existe pero no
    /// tiene pinta de datadir, se para: Roost no toca datos que no reconoce.
    /// </summary>
    private void EnsureDataDir(InstalledDb db, string ini, string datadir)
    {
        if (Directory.Exists(Path.Combine(datadir, "mysql"))) return;
        if (Directory.Exists(datadir) && Directory.EnumerateFileSystemEntries(datadir).Any())
        {
            throw new RoostException(
                $"{datadir} existe pero no parece un datadir de {db.Engine.DisplayName()} (no tiene la carpeta mysql). "
                + "Revisalo a mano: Roost no toca datos que no reconoce.");
        }

        var parent = Path.GetDirectoryName(datadir)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var output = db.Engine == DbEngine.MariaDb
                ? runner.Run(Path.Combine(db.Directory, "bin", "mariadb-install-db.exe"),
                    $"--datadir=\"{DbIni.Slashes(temp)}\"", TimeSpan.FromMinutes(2))
                // --defaults-file tiene que ir el primero; --datadir manda sobre el del fichero.
                : runner.Run(db.Server,
                    $"--defaults-file=\"{ini}\" --initialize-insecure --datadir=\"{DbIni.Slashes(temp)}\"", TimeSpan.FromMinutes(2));

            if (!Directory.Exists(Path.Combine(temp, "mysql")))
            {
                var tail = string.Join("\n  ", (output ?? "no respondio").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(8));
                throw new RoostException($"No se pudo crear el datadir de {db.Label} en {datadir}:\n  {tail}");
            }

            if (Directory.Exists(datadir)) Directory.Delete(datadir); // estaba vacío
            Directory.Move(temp, datadir);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }

    /// <summary>La primera vez crea www con una portada; una carpeta www que ya existe no se toca.</summary>
    private void EnsureWww()
    {
        if (Directory.Exists(paths.Www)) return;
        Directory.CreateDirectory(paths.Www);
        File.WriteAllText(Path.Combine(paths.Www, "index.php"), """
            <?php // Portada creada por Roost la primera vez. Puedes borrarla o cambiarla. ?>
            <!doctype html>
            <meta charset="utf-8">
            <title>Roost</title>
            <h1>Roost funciona</h1>
            <p>PHP <?= PHP_VERSION ?> (<?= PHP_SAPI ?>) servido por <?= htmlspecialchars(explode(' ', $_SERVER['SERVER_SOFTWARE'] ?? 'Roost')[0]) ?>.</p>
            <p>Pon tus proyectos en <code><?= htmlspecialchars(__DIR__) ?></code>: cada carpeta se ve en http://<?= htmlspecialchars($_SERVER['HTTP_HOST'] ?? 'localhost') ?>/&lt;carpeta&gt;/.</p>

            """);
    }

    /// <summary>Puertos de la lista que ya tiene ocupados otro proceso en 127.0.0.1.</summary>
    public static IReadOnlyList<int> BusyPorts(IEnumerable<int> ports) =>
        ports.Where(port =>
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
        }).ToList();

    /// <summary>true si algo acepta conexiones en 127.0.0.1:<paramref name="port"/>.</summary>
    public static async Task<bool> AcceptsConnectionsAsync(int port, CancellationToken cancellation = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
