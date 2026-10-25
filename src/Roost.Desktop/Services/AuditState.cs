using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Desktop.Controls;

namespace Roost.Desktop.Services;

/// <summary>
/// La auditoría de la máquina: lo mismo que <c>roost audit</c>, hecho en segundo plano al abrir
/// la app y cuando se pide. De aquí salen el contador rojo de la barra lateral, la línea de la
/// bandeja y la pantalla Auditoría. Además evalúa versiones sueltas (las de los catálogos).
/// Los avisos son los del feed firmado de roost-catalog (se pregunta por uno nuevo antes de cada
/// escaneo, como mucho una vez al día) o, sin él, los que van dentro de Roost.
/// </summary>
public sealed partial class AuditState : ObservableObject
{
    private static readonly System.Globalization.CultureInfo Es = Roost.Desktop.Services.Es.Culture;

    private AuditEvaluator _evaluator;

    private readonly AppServices? _app;

    private readonly AdvisoryFeed? _feed;

    /// <param name="app">Sus extensiones aportan avisos y scanners, y su HttpClient baja el feed; sin él, solo lo empaquetado.</param>
    public AuditState(AppServices? app = null)
    {
        _app = app;
        _feed = app is null ? null : new AdvisoryFeed(app.Paths, app.Http);
        var (dataset, origin) = _feed?.LoadBest() ?? (AuditDataset.LoadEmbedded(), new DatasetOrigin(DatasetKind.Embedded));
        Dataset = Merge(dataset);
        Origin = origin;
        _evaluator = new AuditEvaluator(Dataset);
    }

    /// <summary>Los avisos con los que se evalúa (con los de las extensiones).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatasetDateText))]
    public partial AuditDataset Dataset { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginText), nameof(OriginShort))]
    public partial DatasetOrigin Origin { get; private set; }

    /// <summary>«23-oct-2026, 16:20».</summary>
    public string DatasetDateText => Dataset.Generated.ToLocalTime().ToString("d-MMM-yyyy, HH:mm", Es);

    /// <summary>De dónde salen los avisos, para Ajustes.</summary>
    public string OriginText => Origin.Kind switch
    {
        DatasetKind.Feed when Origin.Note is null => "Feed firmado de roost-catalog, que se actualiza solo cada día",
        DatasetKind.Feed => "Feed firmado de roost-catalog, pero de hace días: no se ha podido actualizar",
        DatasetKind.Embedded => "Los que van dentro de Roost: el feed firmado aún no se ha podido bajar",
        _ => "Un fichero sin firma",
    };

    /// <summary>Lo mismo en corto, para la pantalla Auditoría.</summary>
    public string OriginShort => Origin.Kind == DatasetKind.Feed ? "Feed firmado · en local" : "Incluidos en Roost · en local";

    private AuditDataset Merge(AuditDataset dataset) => _app is null ? dataset : _app.Extensions.WithAdvisories(dataset, _app.Paths);

    /// <summary>
    /// Pregunta por un feed nuevo (si el de la caché tiene más de un día, o siempre con
    /// <paramref name="force"/>) y pasa a evaluar con el mejor que haya. Nunca lanza.
    /// </summary>
    public async Task<FeedUpdate?> UpdateFeedAsync(bool force = false)
    {
        if (_feed is not { } feed) return null;
        // Sin red, el escaneo no se queda callado 20 s esperando: 8 s y se sigue con el último feed
        // bueno. «Comprobar ahora» (force) sí espera más, porque el usuario lo ha pedido.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(force ? 20 : 8));
        var update = await Task.Run(() => feed.UpdateAsync(force, timeout.Token));
        var (dataset, origin) = feed.LoadBest();
        if (dataset.Generated != Dataset.Generated || origin.Kind != Origin.Kind)
        {
            Dataset = Merge(dataset);
            _evaluator = new AuditEvaluator(Dataset);
        }
        Origin = origin;
        return update;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RedCount), nameof(AmberCount), nameof(GreenCount), nameof(Summary))]
    public partial IReadOnlyList<AuditFinding> Findings { get; private set; } = [];

    [ObservableProperty]
    public partial bool Scanning { get; private set; }

    [ObservableProperty]
    public partial DateTime? LastScan { get; private set; }

    [ObservableProperty]
    public partial string? OsDescription { get; private set; }

    // Lo ignorado (K1, AuditState.History.cs) no cuenta.
    public int RedCount => Count(AuditColor.Red);

    public int AmberCount => Count(AuditColor.Amber);

    public int GreenCount => Count(AuditColor.Green);

    /// <summary>«6 en rojo, 1 en ámbar», «Todo al día» o «Sin escanear».</summary>
    public string Summary => LastScan is null ? (Scanning ? "Escaneando..." : "Sin escanear")
        : RedCount == 0 && AmberCount == 0 ? "Todo al día"
        : string.Join(", ", new[] { RedCount > 0 ? $"{RedCount} en rojo" : null, AmberCount > 0 ? $"{AmberCount} en ámbar" : null }.Where(x => x is not null));

    private Task? _scan;

    /// <summary>
    /// Escanea la máquina y evalúa lo encontrado, después de preguntar por un feed nuevo (forzado con
    /// <paramref name="forceFeed"/>). Si ya hay un escaneo en curso, espera a ese.
    /// </summary>
    public Task ScanAsync(bool forceFeed = false) => Scanning && _scan is not null ? _scan : _scan = ScanCoreAsync(forceFeed);

    private async Task ScanCoreAsync(bool forceFeed)
    {
        Scanning = true;
        OnPropertyChanged(nameof(Summary));
        try
        {
            await UpdateFeedAsync(forceFeed);
            var result = await Task.Run(() => (_app is null ? EnvironmentScanner.CreateDefault() : _app.Extensions.Scanner(_app.Paths)).ScanAll());
            IReadOnlyList<AuditFinding> findings = _evaluator.EvaluateAll(result.Components).ToList();
            // K4: las mitigaciones de cada PHP frente a su php -i (solo lee), fuera del hilo de la UI.
            if (Mitigations is { } mitigations)
                findings = await Task.Run(() => mitigations.CheckAllAsync(findings));
            Findings = Sorted(findings);
            OsDescription = result.OsDescription;
            LastScan = DateTime.Now;
            Record();   // K1: qué ha cambiado desde la anterior
        }
        finally
        {
            Scanning = false;
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Para los tests de vistas: hallazgos ya hechos, sin escanear la máquina.</summary>
    internal void Show(IReadOnlyList<AuditFinding> findings, DateTime? when = null)
    {
        Findings = findings;
        LastScan = when ?? new DateTime(2026, 10, 23, 9, 30, 0);
        OsDescription = "Windows 11";
        Record();
        OnPropertyChanged(nameof(Summary));
    }

    private static List<AuditFinding> Sorted(IEnumerable<AuditFinding> findings) => findings
        .OrderByDescending(f => f.Color)
        .ThenBy(f => f.Component.Kind)
        .ThenByDescending(f => f.Component.Version)
        .ToList();

    // K4: mitigaciones

    /// <summary>Con qué se ejecuta <c>php -i</c>; los tests ponen uno de pega.</summary>
    internal Roost.Core.Scanning.ICommandRunner Runner { get; set; } = CommandRunner.Default;

    /// <summary>Comprobar, aplicar y deshacer las mitigaciones; null sin raíz (los tests sin app).</summary>
    public AuditMitigations? Mitigations => _app is null ? null : new AuditMitigations(_app.Paths, Runner);

    /// <summary>
    /// Vuelve a comprobar las mitigaciones de un hallazgo (tras aplicar o deshacer) y lo pone en su
    /// sitio del inventario, con los contadores al día. No es un escaneo: no cuenta para «qué ha cambiado».
    /// </summary>
    public async Task<AuditFinding> RecheckAsync(AuditFinding finding)
    {
        if (Mitigations is not { } mitigations) return finding;
        var updated = await Task.Run(() => mitigations.CheckAsync(finding));
        var path = finding.Component.ExecutablePath;
        Findings = Sorted(Findings.Select(f => f.Component.ExecutablePath == path ? updated : f));
        OnPropertyChanged(nameof(Summary));
        return updated;
    }

    /// <summary>Evalúa una versión suelta (de un catálogo o instalada por Roost).</summary>
    public AuditFinding Evaluate(ComponentKind kind, ComponentVersion version, string path = "", string source = "Roost") =>
        _evaluator.Evaluate(new DetectedComponent { Kind = kind, Version = version, ExecutablePath = path, Source = source });

    public static Tone ToneOf(AuditColor color) => color switch
    {
        AuditColor.Green => Tone.Ok,
        AuditColor.Amber => Tone.Warn,
        _ => Tone.Bad,
    };

    /// <summary>La chip de un hallazgo: «VULNERABLE · KEV», «EOL», «hay 8.4.11», «al dia».</summary>
    public static string ChipText(AuditFinding finding) => finding.Verdict switch
    {
        AuditVerdict.Vulnerable when finding.Mitigated => "VULNERABLE · mitigada",
        AuditVerdict.Vulnerable when finding.MatchedAdvisories.Any(a => a.Kev) => "VULNERABLE · KEV",
        AuditVerdict.Vulnerable => "VULNERABLE",
        AuditVerdict.Eol => "EOL",
        AuditVerdict.Unknown => "desconocida",
        _ when finding.Uncertain => "sin verificar",
        // Solo si es más nueva: con un dataset de antes de una versión, la instalada puede pasarle.
        _ when finding.BranchLatest is { } latest && ComponentVersion.TryParse(latest, out var l) && l > finding.Component.Version => $"hay {latest}",
        _ => "al día",
    };
}
