using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo encontrado en la máquina, agrupado por de dónde viene (Laragon, Program Files…).</summary>
public sealed record FoundGroup(string Source, string Detail, string? Chip, Tone Tone)
{
    public bool HasChip => Chip is not null;
}

/// <summary>Una carpeta de www de Laragon que se puede registrar como sitio.</summary>
public sealed partial class FolderChoice(string name, string path) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Url => $"https://{Name}.test";

    [ObservableProperty] public partial bool Checked { get; set; } = true;
}

/// <summary>Lo que se ha decidido en la bienvenida, para que lo haga <see cref="MainViewModel.RunWelcomeAsync"/>.</summary>
public sealed record WelcomePlan(
    IReadOnlyList<PhpBuild> Php,
    string? GlobalBranch,
    DbBuild? Db,
    int? DbPort,
    IReadOnlyList<FolderChoice> Sites);

/// <summary>
/// La bienvenida del primer arranque (y «Importar desde Laragon…» en Ajustes): qué hay en la
/// máquina y qué traer a Roost (las ramas de PHP que se usan, MariaDB, las carpetas de
/// Laragon como sitios .test). Roost se instala al lado: no mueve ni cambia nada de fuera.
/// </summary>
public sealed partial class WelcomeDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private IReadOnlyList<PhpBuild> _php = [];
    private DbBuild? _db;
    private int? _dbPort;
    private string? _globalBranch;

    public WelcomeDialogViewModel(MainViewModel main)
    {
        _main = main;
        var findings = main.Audit.Findings.Where(f => f.Component.Source != "Roost").ToList();
        Found = findings.GroupBy(f => f.Component.Source).Select(Describe).ToList();

        // La www de Laragon sale de la ruta de sus binarios: C:\laragon\bin\php\... → C:\laragon\www.
        var laragon = findings.Select(f => f.Component.ExecutablePath)
            .Select(p => Regex.Match(p, @"^(.*?\\laragon)\\bin\\", RegexOptions.IgnoreCase))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
        LaragonWww = laragon is null ? null : System.IO.Path.Combine(laragon, "www");
        if (LaragonWww is not null && Directory.Exists(LaragonWww))
        {
            Folders = Directory.EnumerateDirectories(LaragonWww)
                .Select(d => (Dir: d, Name: SiteName(System.IO.Path.GetFileName(d))))
                .Where(x => x.Name is not null && !x.Dir.Split(System.IO.Path.DirectorySeparatorChar)[^1].StartsWith('.'))
                .Select(x => new FolderChoice(x.Name!, x.Dir))
                .ToList();
        }
        RegisterSites = Folders.Count > 0;
        HasLaragon = laragon is not null;
    }

    public override bool Wide => true;

    public override bool CanDismiss => false;

    public IReadOnlyList<FoundGroup> Found { get; }

    public bool FoundNothing => Found.Count == 0;

    public bool HasLaragon { get; }

    public string? LaragonWww { get; }

    public IReadOnlyList<FolderChoice> Folders { get; } = [];

    public bool HasFolders => Folders.Count > 0;

    public string Subtitle => HasLaragon
        ? "Ya tienes Laragon. Roost se instala al lado: no mueve ni cambia nada suyo."
        : "Roost se instala al lado de lo que ya tengas: no mueve ni cambia nada de fuera.";

    public string RootPath => _main.App.Paths.Root;

    public string WwwPath => _main.App.Paths.Www;

    [ObservableProperty] public partial int Step { get; private set; } = 1;
    [ObservableProperty] public partial bool InstallPhp { get; set; } = true;
    [ObservableProperty] public partial bool RegisterSites { get; set; }
    [ObservableProperty] public partial bool InstallDb { get; set; } = true;
    [ObservableProperty] public partial string PhpText { get; private set; } = "Instalar PHP (leyendo el catálogo...)";
    [ObservableProperty] public partial string PhpHint { get; private set; } = "";
    [ObservableProperty] public partial string DbText { get; private set; } = "Instalar MariaDB (leyendo el catálogo...)";
    [ObservableProperty] public partial string DbHint { get; private set; } = "";
    [ObservableProperty] public partial bool Ready { get; private set; }
    [ObservableProperty] public partial bool PhpAvailable { get; private set; }
    [ObservableProperty] public partial bool DbAvailable { get; private set; }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool HasStep2 => RegisterSites && HasFolders;
    public string StepText => $"Paso {Step} de {(HasStep2 ? 2 : 1)}";
    public bool ShowNext => IsStep1 && HasStep2;
    public bool ShowStart => !ShowNext;
    public string SitesText => LaragonWww is null ? "" : $"Registrar como sitios .test las carpetas de {LaragonWww}";

    partial void OnStepChanged(int value) => NotifySteps();
    partial void OnRegisterSitesChanged(bool value) => NotifySteps();

    private void NotifySteps()
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(HasStep2));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(ShowNext));
        OnPropertyChanged(nameof(ShowStart));
    }

    private FoundGroup Describe(IGrouping<string, AuditFinding> group)
    {
        var parts = new List<string>();
        var php = group.Where(f => f.Component.Kind == ComponentKind.Php).Select(f => f.Component.Version).OrderBy(v => v).ToList();
        if (php.Count == 1) parts.Add($"PHP {php[0]}");
        else if (php.Count > 1) parts.Add($"{php.Count} PHP ({php[0]} a {php[^1]})");
        parts.AddRange(group.Where(f => f.Component.Kind != ComponentKind.Php).Select(f => $"{AuditViewModel.KindName(f.Component.Kind)} {f.Component.Version}"));

        var vulnerable = group.Count(f => f.Verdict == AuditVerdict.Vulnerable);
        var eol = group.Count(f => f.Verdict == AuditVerdict.Eol);
        var chip = string.Join(" · ", new[] { vulnerable > 0 ? $"{vulnerable} vulnerable{(vulnerable == 1 ? "" : "s")}" : null, eol > 0 ? $"{eol} EOL" : null }.Where(x => x is not null));
        return new FoundGroup(group.Key, string.Join(" · ", parts), chip.Length == 0 ? null : chip, vulnerable + eol > 0 ? Tone.Bad : Tone.Ok);
    }

    /// <summary>"Mi Proyecto_2" → "mi-proyecto-2"; null si no queda un nombre válido.</summary>
    public static string? SiteName(string folder)
    {
        var name = Regex.Replace(folder.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        return Site.IsValidName(name) ? name : null;
    }

    /// <summary>Lee los catálogos y prepara las propuestas (las ramas de PHP en uso, la MariaDB LTS y su puerto).</summary>
    public async Task LoadAsync()
    {
        try
        {
            var catalog = await WindowsPhpCatalog.FetchAsync(_main.App.Http);
            var nts = catalog.Where(b => !b.ThreadSafe && b.Architecture == "x64").GroupBy(b => b.Branch).ToDictionary(g => g.Key, g => g.First());
            var used = _main.Audit.Findings
                .Where(f => f.Component.Kind == ComponentKind.Php && f.Component.Source != "Roost" && f.Verdict != AuditVerdict.Eol)
                .Select(f => $"{f.Component.Version.Major}.{f.Component.Version.Minor}")
                .Where(nts.ContainsKey)
                .Distinct()
                .ToList();
            if (used.Count == 0 && nts.ContainsKey("8.4")) used.Add("8.4");
            var installed = new PhpInstaller(_main.App.Paths).ListInstalled().Select(p => p.Name).ToHashSet();
            _php = used.Select(b => nts[b]).Where(b => !installed.Contains(b.InstallName)).OrderByDescending(b => b.Version).ToList();
            _globalBranch = used.Contains("8.4") ? "8.4" : used.OrderByDescending(b => Version.Parse(b)).FirstOrDefault();
            PhpText = _php.Count == 0
                ? "Los PHP que usas ya están en Roost"
                : $"Instalar en Roost la última de cada rama de PHP que usas ({string.Join(", ", _php.Select(b => b.Version))})";
            PhpHint = _globalBranch is null ? "" : $"Y hacer global la {_globalBranch}.{(HasLaragon ? " Las de Laragon siguen donde están." : "")}";
            InstallPhp = PhpAvailable = _php.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            PhpText = "No se pudo leer el catálogo de PHP";
            PhpHint = ex.Message;
            InstallPhp = false;
        }

        try
        {
            var mariadb = await MariaDbCatalog.FetchAsync(_main.App.Http);
            _db = mariadb.Where(b => b.Lts).OrderByDescending(b => b.Branch == "11.4").ThenByDescending(b => b.Version).FirstOrDefault();
            var hasDb = new DbInstaller(_main.App.Paths).ListInstalled().Count > 0;
            if (_db is null || hasDb)
            {
                DbText = hasDb ? "Ya hay una base de datos en Roost" : "No hay ninguna MariaDB LTS en el catálogo";
                InstallDb = false;
                _db = null;
            }
            else
            {
                DbText = $"Instalar MariaDB {_db.Branch} (LTS)";
                DbAvailable = true;
                if (StackPlanner.BusyPorts([3306]).Count > 0)
                {
                    _dbPort = new[] { 3307, 3308, 3309, 3310 }.FirstOrDefault(p => StackPlanner.BusyPorts([p]).Count == 0) is var p and > 0 ? p : null;
                    DbHint = $"El 3306 está ocupado: usaría el {_dbPort}.{(HasLaragon ? " Tus bases de datos de Laragon no se tocan." : "")}";
                }
                else
                {
                    DbHint = "En el 3306, solo en 127.0.0.1. Usuario root, sin contraseña.";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            DbText = "No se pudo leer el catálogo de MariaDB";
            DbHint = ex.Message;
            InstallDb = false;
        }
        Ready = true;
    }

    [RelayCommand]
    private void Skip() => Close(null);

    [RelayCommand]
    private void Next()
    {
        if (HasStep2) Step = 2;
    }

    [RelayCommand]
    private void Back() => Step = 1;

    [RelayCommand]
    private void Start() => Close(new WelcomePlan(
        InstallPhp ? _php : [],
        InstallPhp ? _globalBranch : null,
        InstallDb ? _db : null,
        InstallDb ? _dbPort : null,
        RegisterSites ? Folders.Where(f => f.Checked).ToList() : []));
}
