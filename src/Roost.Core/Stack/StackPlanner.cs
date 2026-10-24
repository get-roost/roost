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

/// <summary>Un sitio del plan: el sitio, el PHP que lo sirve y su URL https.</summary>
public sealed record SitePlan(Site Site, InstalledPhp Php, string Url);

/// <summary>
/// Lo que arranca <c>roost up</c>, en este orden (ROOST.md §5.5): la base de datos global si
/// la hay, Redis y Mailpit si van en el stack, un pool de php-cgi por cada rama de PHP en uso (la global y la de cada sitio que
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
            .Concat(MailWebPort is { } web ? [web] : [])
            .Concat(Shares.Select(s => s.Port))
            .Distinct()
            .ToList();

    /// <summary>Compartir (oleada 6): las escuchas de lo que se está compartiendo que lleva este plan (las de sitios que siguen en él).</summary>
    public IReadOnlyList<ShareListener> Shares { get; init; } = [];

    // Servicios (oleada 3)

    /// <summary>El Redis del stack, si va (<c>[redis] enabled</c>): su servicio es <c>redis</c>.</summary>
    public InstalledRedis? Redis { get; init; }

    /// <summary>El Mailpit del stack, si va (<c>[mail] enabled</c>): su servicio es <c>mailpit</c>, con el puerto SMTP.</summary>
    public InstalledMailpit? Mailpit { get; init; }

    /// <summary>La bandeja de Mailpit (su web): no es el puerto de su servicio, pero también tiene que estar libre.</summary>
    public int? MailWebPort { get; init; }
}

/// <summary>
/// Prepara un <see cref="StackPlan"/>: genera en la raíz de Roost el php.ini de cada PHP en uso,
/// conf.d, el my.ini y el datadir de la base de datos, nginx.conf y el .conf y el certificado
/// de cada sitio. Crea la carpeta www con una portada si no existía, y valida la configuración
/// con <c>nginx -t</c> ANTES de arrancar nada (ROOST.md §5.5: si no valida, no se aplica). No
/// arranca procesos: eso es cosa del <see cref="Supervisor"/>.
/// </summary>
public sealed partial class StackPlanner(RoostPaths paths, IProcessRunner runner)
{
    /// <summary>
    /// Compartir (oleada 6): las escuchas extra del servidor web mientras se comparte un sitio
    /// (<c>roost share</c>). Las lleva el daemon en memoria; sin él, ninguna.
    /// </summary>
    public IReadOnlyList<ShareListener> Shares { get; init; } = [];

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
            throw new RoostException($"Apache {ApacheCatalog.Current.Version} no esta instalado; roost up lo instala antes de arrancar.");
        if (!config.UsesApache && nginx is null)
            throw new RoostException($"nginx {NginxCatalog.Current.Version} no esta instalado; roost up lo instala antes de arrancar.");

        var confD = PhpConfD;
        foreach (var dir in new[] { confD, paths.Logs, paths.Run })
            Directory.CreateDirectory(dir);
        EnsureUserIni();
        // El mail() de PHP a Mailpit si va en el stack; si no, se quita (lo leen php-cgi y la terminal).
        MailConf.SyncPhpIni(confD, config);

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

        // Orden de arranque (ROOST.md §5.5): DB → Redis → Mailpit → servicios de las extensiones →
        // pools de PHP → servidor web. La parada, al revés.
        var all = new List<ServiceDefinition>();
        if (db is not null)
            all.Add(PrepareDb(db, config.DbPort));
        var redis = config.RedisEnabled ? PrepareRedis(config, all) : null;
        var mailpit = config.MailEnabled ? PrepareMail(config, all) : null;
        all.AddRange(extraServices);
        all.AddRange(phpServices);
        all.Add(web);

        // Los nombres identifican a cada servicio (status, restart, logs): una extensión no puede repetir uno.
        if (all.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new RoostException($"Hay dos servicios llamados '{repeated.Key}': una extension usa un nombre que ya existe.");

        return new StackPlan(php, web.Name, webVersion, config.HttpPort, config.HttpsPort, db, config.DbPort, sitePlans, all)
        {
            Redis = redis,
            Mailpit = mailpit,
            MailWebPort = mailpit is null ? null : config.MailWebPort,
            Shares = SharesOf(sitePlans),
        };
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

    /// <summary>
    /// Puertos de la lista que ya tiene ocupados otro proceso en loopback: en 127.0.0.1 o, si la
    /// máquina tiene IPv6, en [::1] (Roost escucha en las dos, y un servidor en <c>[::]:80</c> solo
    /// se ve por la segunda).
    /// </summary>
    public static IReadOnlyList<int> BusyPorts(IEnumerable<int> ports) =>
        ports.Where(port => IsBusy(IPAddress.Loopback, port) || (Loopback.HasIpv6 && IsBusy(IPAddress.IPv6Loopback, port))).ToList();

    private static bool IsBusy(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

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
