using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Updates;
using Roost.Daemon;

namespace Roost.WebPanel;

/// <summary>
/// Lo que comparten todas las rutas del panel: las rutas de esta Roost, el cliente del daemon, el
/// <c>HttpClient</c> a internet (catálogos e instaladores), las tareas largas y las cachés de lo que
/// cuesta pedir (los catálogos de PHP y de bases de datos, el último escaneo de la auditoría).
/// Es lo que en la app es <c>AppServices</c>, sin nada de Avalonia.
/// </summary>
public sealed class PanelApp : IDisposable
{
    private readonly Lazy<AdvisoryFeed> _feed;

    public PanelApp(RoostPaths paths, HttpClient? http = null, DaemonClient? daemon = null)
    {
        Paths = paths;
        Http = http ?? CreateHttp();
        Daemon = daemon ?? new DaemonClient(paths);
        // El panel no tiene terminal: pkexec sin agente → sudo -n, o el aviso con la orden exacta.
        Elevation = new Elevation(hasTerminal: () => false);
        // El feed se construye tarde y LoadBest no va a la red: con una raíz temporal (los tests) no pide nada.
        _feed = new Lazy<AdvisoryFeed>(() => new AdvisoryFeed(Paths, Http));
    }

    public RoostPaths Paths { get; }
    public HttpClient Http { get; }
    public DaemonClient Daemon { get; }
    public Jobs Jobs { get; } = new();
    public Elevation Elevation { get; }
    public Action<string> Log { get; set; } = _ => { };

    /// <summary>roost-helper: junto a roost o, en Linux, el del paquete en /usr/libexec/roost.</summary>
    public string HelperExe => HelperLocator.Find(AppContext.BaseDirectory);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("roost-panel/" + RoostVersion.Current);
        return http;
    }

    // Cachés

    private readonly SemaphoreSlim _phpCatalogLock = new(1, 1);
    private (IReadOnlyList<PhpBuild> Builds, DateTimeOffset When)? _phpCatalog;

    /// <summary>El catálogo de PHP (windows.php.net o roost-builds), como mucho una vez cada 6 h salvo que se fuerce.</summary>
    public async Task<(IReadOnlyList<PhpBuild> Builds, DateTimeOffset When)> PhpCatalogAsync(bool force, CancellationToken cancellation)
    {
        await _phpCatalogLock.WaitAsync(cancellation);
        try
        {
            if (!force && _phpCatalog is { } cached && DateTimeOffset.UtcNow - cached.When < TimeSpan.FromHours(6)) return cached;
            var builds = await PhpCatalog.FetchAsync(Http, cancellation);
            _phpCatalog = (builds, DateTimeOffset.UtcNow);
            return _phpCatalog.Value;
        }
        finally
        {
            _phpCatalogLock.Release();
        }
    }

    public (IReadOnlyList<PhpBuild> Builds, DateTimeOffset When)? PhpCatalogCached => _phpCatalog;

    private readonly SemaphoreSlim _dbCatalogLock = new(1, 1);
    private (IReadOnlyList<DbBuild> Builds, DateTimeOffset When)? _dbCatalog;

    /// <summary>El catálogo de bases de datos: MariaDB de su API y MySQL fijado en Roost.</summary>
    public async Task<(IReadOnlyList<DbBuild> Builds, DateTimeOffset When)> DbCatalogAsync(bool force, CancellationToken cancellation)
    {
        await _dbCatalogLock.WaitAsync(cancellation);
        try
        {
            if (!force && _dbCatalog is { } cached && DateTimeOffset.UtcNow - cached.When < TimeSpan.FromHours(6)) return cached;
            var builds = new List<DbBuild>();
            string? problem = null;
            try { builds.AddRange(await MariaDbCatalog.FetchAsync(Http, cancellation)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException) { problem = ex.Message; }
            builds.AddRange(MySqlCatalog.Current);
            DbCatalogProblem = problem;
            _dbCatalog = (builds, DateTimeOffset.UtcNow);
            return _dbCatalog.Value;
        }
        finally
        {
            _dbCatalogLock.Release();
        }
    }

    public string? DbCatalogProblem { get; private set; }

    private AuditEvaluator? _evaluator;
    private readonly object _auditLock = new();

    /// <summary>El feed de avisos (la caché o el empaquetado, sin red), y con él el evaluador.</summary>
    public AdvisoryFeed Feed => _feed.Value;

    public (AuditDataset Dataset, DatasetOrigin Origin) LoadDataset()
    {
        lock (_auditLock)
        {
            var best = Feed.LoadBest();
            Dataset = best;
            _evaluator = new AuditEvaluator(best.Dataset);
            return best;
        }
    }

    public (AuditDataset Dataset, DatasetOrigin Origin)? Dataset { get; private set; }

    public AuditEvaluator Evaluator
    {
        get
        {
            lock (_auditLock)
            {
                if (_evaluator is null) LoadDataset();
                return _evaluator!;
            }
        }
    }

    /// <summary>Una versión suelta (PHP o base de datos) por el evaluador, como <c>CatalogAudit</c> en la CLI.</summary>
    public AuditFinding Evaluate(ComponentKind kind, ComponentVersion version, string path = "", string source = "Roost") =>
        Evaluator.Evaluate(new DetectedComponent { Kind = kind, Version = version, ExecutablePath = path, Source = source });

    /// <summary>El último escaneo de la auditoría (la pantalla Auditoría y los avisos de Inicio).</summary>
    public AuditScan? LastScan { get; set; }

    public void Dispose()
    {
        Daemon.Dispose();
        Http.Dispose();
    }
}

/// <summary>El resultado de un escaneo de la auditoría, tal como lo guarda el panel.</summary>
public sealed record AuditScan(DateTimeOffset When, string OsDescription, IReadOnlyList<AuditFinding> Findings);
