using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una fila del inventario de la auditoría.</summary>
public sealed partial class AuditRow(AuditFinding finding) : ObservableObject
{
    public AuditFinding Finding { get; } = finding;
    public string Name => AuditViewModel.KindName(Finding.Component.Kind);
    // Raw es la línea entera del banner («PHP 8.3.18 (cli) (built: …»): la versión, limpia.
    public string Version => Finding.Component.Version.ToString();
    public required string Variant { get; init; }
    public string Source => Finding.Component.Source;
    public string Verdict => AuditState.ChipText(Finding);
    public Tone Tone => AuditState.ToneOf(Finding.Color);
    public string Detail => Finding.Headline;
    public bool IsRoost => Source == "Roost";

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>Un aviso (CVE) que casa con el componente elegido.</summary>
public sealed record AdvisoryLine(string Id, string Meta, bool Kev, string Title);

/// <summary>
/// Auditoría: todo lo instalado en la máquina, de Roost o no, frente a los avisos de seguridad y
/// las fechas de fin de vida (lo mismo que <c>roost audit</c>, en local y sin red).
/// </summary>
public sealed partial class AuditViewModel : PageViewModel
{
    public AuditViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "Todo lo instalado en este equipo, de Roost o no, frente a los avisos de seguridad y las fechas de fin de vida.";
        main.Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AuditState.Findings) or nameof(AuditState.Scanning) or nameof(AuditState.LastScan)) Build();
            if (e.PropertyName is nameof(AuditState.Dataset) or nameof(AuditState.Origin))
            {
                OnPropertyChanged(nameof(DatasetDate));
                OnPropertyChanged(nameof(DatasetOrigin));
            }
        };
    }

    public override string Title => "Auditoría";

    /// <summary>La fecha de los avisos con los que se evalúa.</summary>
    public string DatasetDate => Main.Audit.DatasetDateText;

    /// <summary>«Feed firmado · en local».</summary>
    public string DatasetOrigin => Main.Audit.OriginShort;

    [ObservableProperty] public partial IReadOnlyList<AuditRow> Rows { get; private set; } = [];
    [ObservableProperty] public partial AuditRow? Selected { get; private set; }
    [ObservableProperty] public partial int Red { get; private set; }
    [ObservableProperty] public partial int Amber { get; private set; }
    [ObservableProperty] public partial int Green { get; private set; }
    [ObservableProperty] public partial string ScanText { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<AdvisoryLine> Advisories { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> Mitigations { get; private set; } = [];
    [ObservableProperty] public partial string? RealFix { get; private set; }
    [ObservableProperty] public partial string LongText { get; private set; } = "";
    [ObservableProperty] public partial string? ActionLabel { get; private set; }

    [ObservableProperty] public partial bool ShowAll { get; set; } = true;
    [ObservableProperty] public partial bool ShowRed { get; set; }
    [ObservableProperty] public partial bool ShowAmber { get; set; }
    [ObservableProperty] public partial bool ShowGreen { get; set; }
    [ObservableProperty] public partial bool IncludeOutside { get; set; } = true;

    public bool Scanning => Main.Audit.Scanning;
    public bool HasSelection => Selected is not null;
    public bool HasAdvisories => Advisories.Count > 0;
    public bool HasMitigations => Mitigations.Count > 0;
    public bool HasAction => ActionLabel is not null;
    public bool HasRows => Rows.Count > 0;
    public string SelectedTitle => Selected is { } s ? $"{s.Name} {s.Version} · {s.Source}" : "";

    // Los cuatro filtros son excluyentes: encender uno apaga los demás (como los RadioButton).
    partial void OnShowAllChanged(bool value) => Filter(value, all: true);
    partial void OnShowRedChanged(bool value) => Filter(value, red: true);
    partial void OnShowAmberChanged(bool value) => Filter(value, amber: true);
    partial void OnShowGreenChanged(bool value) => Filter(value, green: true);

    private void Filter(bool on, bool all = false, bool red = false, bool amber = false, bool green = false)
    {
        if (!on) return;
        ShowAll = all;
        ShowRed = red;
        ShowAmber = amber;
        ShowGreen = green;
        Build();
    }
    partial void OnIncludeOutsideChanged(bool value) => Build();
    partial void OnRowsChanged(IReadOnlyList<AuditRow> value) => OnPropertyChanged(nameof(HasRows));

    partial void OnSelectedChanged(AuditRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
    }

    public override async Task ShownAsync()
    {
        Build();
        if (Main.Audit.LastScan is null && !Main.Audit.Scanning) await Main.Audit.ScanAsync();
    }

    private void Build()
    {
        var audit = Main.Audit;
        Red = audit.RedCount;
        Amber = audit.AmberCount;
        Green = audit.GreenCount;
        OnPropertyChanged(nameof(Scanning));
        ScanText = audit.Scanning ? "Escaneando el equipo..."
            : audit.LastScan is { } when ? $"Escaneado a las {when:HH:mm}" + (audit.OsDescription is { } os ? $" · {os}" : "")
            : "Sin escanear";

        var rows = audit.Findings
            .Where(f => IncludeOutside || f.Component.Source == "Roost")
            .Where(f => ShowAll
                        || ShowRed && f.Color == AuditColor.Red
                        || ShowAmber && f.Color == AuditColor.Amber
                        || ShowGreen && f.Color == AuditColor.Green)
            .Select(f => new AuditRow(f) { Variant = Variant(f.Component) })
            .ToList();
        Rows = rows;
        var keep = rows.FirstOrDefault(r => r.Finding.Component.ExecutablePath == Selected?.Finding.Component.ExecutablePath) ?? rows.FirstOrDefault();
        Pick(keep);
    }

    private static string Variant(DetectedComponent c)
    {
        var parts = new List<string>();
        if (c.Extra.TryGetValue("threadSafety", out var ts)) parts.Add(ts.ToUpperInvariant());
        if (c.Architecture is { } arch) parts.Add(arch);
        if (c.Sapi is { } sapi) parts.Add(sapi);
        if (c.IsOnPath) parts.Add("en el PATH");
        return string.Join(" · ", parts);
    }

    private void Pick(AuditRow? row)
    {
        foreach (var r in Rows) r.IsSelected = r == row;
        Selected = row;
        if (row is null)
        {
            Advisories = [];
            Mitigations = [];
            ActionLabel = null;
            Notify();
            return;
        }

        var f = row.Finding;
        LongText = string.Join(" ", new[] { f.Headline }.Concat(f.Notes));
        Advisories = f.MatchedAdvisories.Select(a => new AdvisoryLine(
            a.Id,
            string.Join(" · ", a.Aliases.Append(Gravity(a))),
            a.Kev, a.Title)).ToList();
        Mitigations = f.Mitigations.Select(m => m.ToString()).ToList();
        var fixedIn = f.MatchedAdvisories.SelectMany(a => a.FixedIn.Values).Distinct().Order().ToList();
        RealFix = fixedIn.Count == 0 ? null
            : $"Arreglo real: {KindName(f.Component.Kind)} {string.Join(", ", fixedIn.Take(fixedIn.Count - 1))}{(fixedIn.Count > 1 ? " o " : "")}{fixedIn[^1]}";

        var latest = f.BranchLatest is { } l && ComponentVersion.TryParse(l, out var lv) ? lv : (ComponentVersion?)null;
        ActionLabel = f.Component.Kind switch
        {
            ComponentKind.Php when f.Verdict != AuditVerdict.Eol && latest is { } v && v > f.Component.Version => $"Instalar {v}",
            ComponentKind.Php when f.Color != AuditColor.Green => "Elegir otra versión",
            ComponentKind.MariaDb or ComponentKind.MySql when f.Color != AuditColor.Green => "Ver bases de datos",
            _ => null,
        };
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasAdvisories));
        OnPropertyChanged(nameof(HasMitigations));
        OnPropertyChanged(nameof(HasAction));
    }

    /// <summary>«CVSS 9.2» o, si el aviso no trae puntuación, su gravedad («gravedad alta»).</summary>
    private static string Gravity(Roost.Core.Audit.Advisory a) => a.Cvss > 0
        ? $"CVSS {a.Cvss.ToString("0.0", CultureInfo.InvariantCulture)}"
        : a.Severity?.ToLowerInvariant() switch
        {
            "critical" => "gravedad crítica",
            "high" => "gravedad alta",
            "medium" => "gravedad media",
            "low" => "gravedad baja",
            _ => "sin puntuación",
        };

    public static string KindName(ComponentKind kind) => kind switch
    {
        ComponentKind.Php => "PHP",
        ComponentKind.MySql => "MySQL",
        ComponentKind.MariaDb => "MariaDB",
        _ => kind.ToString(),
    };

    // Acciones

    [RelayCommand]
    private void Choose(AuditRow row) => Pick(row);

    [RelayCommand]
    private Task RescanAsync() => Main.Audit.ScanAsync();

    [RelayCommand]
    private async Task ActAsync()
    {
        if (Selected is not { } row) return;
        var f = row.Finding;
        if (f.Component.Kind == ComponentKind.Php)
        {
            var branch = $"{f.Component.Version.Major}.{f.Component.Version.Minor}";
            if (ActionLabel?.StartsWith("Instalar", StringComparison.Ordinal) == true)
                await Main.Php.InstallLatestAsync(branch);
            else
                await Main.NavigateToAsync(Main.Php);
        }
        else
        {
            await Main.NavigateToAsync(Main.Databases);
        }
    }

    [RelayCommand]
    private Task ExportAsync() => Main.RunAsync("No se pudo exportar la auditoría", async () =>
    {
        var path = await Main.SaveFileAsync("Exportar la auditoría", $"auditoria-roost-{DateTime.Now:yyyy-MM-dd}",
            [("Markdown", "md"), ("JSON", "json")]);
        if (path is null) return;

        var audit = Main.Audit;
        var text = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? AuditReport.ToJson(audit.Dataset, audit.Findings, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }, audit.Origin)
            : AuditReport.ToMarkdown(audit.Dataset, audit.Findings, DateTimeOffset.Now, Environment.MachineName, audit.Origin);
        await File.WriteAllTextAsync(path, text);
        Main.Flash($"Exportada a {Path.GetFileName(path)}");
    });
}
