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

    // Servicios (oleada 3): los servicios de Windows que le quitarían un puerto a Roost (E7).

    /// <summary>Un aviso por cada servicio de Windows que choca con los puertos que va a usar Roost.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> ServiceWarnings { get; private set; } = [];

    public bool HasServiceWarnings => ServiceWarnings.Count > 0;

    partial void OnServiceWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasServiceWarnings));

    [RelayCommand]
    private void OpenServicesPanel() => Services.MachineServices.OpenServicesPanel();

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
            // También las ramas EOL que se usan: los proyectos viejos de Laragon las necesitan, y si no
            // se traen se servirían con la global y se romperían. Se marcan, y nunca son la global.
            var phpFound = _main.Audit.Findings
                .Where(f => f.Component.Kind == ComponentKind.Php && f.Component.Source != "Roost")
                .Select(f => (Branch: $"{f.Component.Version.Major}.{f.Component.Version.Minor}", Eol: f.Verdict == AuditVerdict.Eol))
                .Where(x => nts.ContainsKey(x.Branch))
                .ToList();
            var used = phpFound.Select(x => x.Branch).Distinct().ToList();
            var eol = phpFound.Where(x => x.Eol).Select(x => x.Branch).ToHashSet();
            if (used.All(eol.Contains) && nts.ContainsKey("8.4")) used.Add("8.4");
            var installed = new PhpInstaller(_main.App.Paths).ListInstalled().Select(p => p.Name).ToHashSet();
            _php = used.Select(b => nts[b]).Where(b => !installed.Contains(b.InstallName)).OrderByDescending(b => b.Version).ToList();
            var current = used.Where(b => !eol.Contains(b)).ToList();
            _globalBranch = current.Contains("8.4") ? "8.4" : current.OrderByDescending(b => Version.Parse(b)).FirstOrDefault();
            PhpText = _php.Count == 0
                ? "Los PHP que usas ya están en Roost"
                : $"Instalar en Roost la última de cada rama de PHP que usas ({string.Join(", ", _php.Select(b => eol.Contains(b.Branch) ? $"{b.Version} (EOL)" : b.Version.ToString()))})";
            var eolNote = _php.Any(b => eol.Contains(b.Branch))
                ? " Las EOL ya no reciben parches: son para tus proyectos viejos, que las seguirán usando."
                : "";
            PhpHint = _globalBranch is null ? eolNote.Trim()
                : $"Y hacer global la {_globalBranch}.{eolNote}{(HasLaragon ? " Las de Laragon siguen donde están." : "")}";
            InstallPhp = PhpAvailable = _php.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            PhpText = "No se pudo leer el catálogo de PHP";
            PhpHint = ex.Message;
            InstallPhp = false;
        }

        // Los servicios de Windows (solo leyendo): un MySQL o una MariaDB que arrancan con Windows se
        // quedan el 3306 al encender el equipo aunque ahora esté libre.
        await _main.MachineServices.LoadAsync();
        var config = new Roost.Core.Config.RoostConfig();
        try { config = Roost.Core.Config.RoostConfig.Load(_main.App.Paths.ConfigFile); } catch (RoostException) { }
        var conflicts = _main.MachineServices.ConflictsWith(config, withSites: true, withDb: true);
        var dbService = conflicts.FirstOrDefault(c => c.Ports.Contains(config.DbPort) && c.Service.StartsWithWindows)?.Service;

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
                if (StackPlanner.BusyPorts([3306]).Count > 0 || dbService is not null)
                {
                    _dbPort = new[] { 3307, 3308, 3309, 3310 }.FirstOrDefault(p => StackPlanner.BusyPorts([p]).Count == 0) is var p and > 0 ? p : null;
                    DbHint = dbService is not null
                        ? $"El 3306 es del servicio de Windows {dbService.DisplayName}, que arranca con Windows: usaría el {_dbPort}."
                        : $"El 3306 está ocupado: usaría el {_dbPort}.{(HasLaragon ? " Tus bases de datos de Laragon no se tocan." : "")}";
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

        // Con la base de datos de Roost en otro puerto, ese servicio ya no le molesta a ella.
        var dbPort = _dbPort ?? config.DbPort;
        ServiceWarnings = _main.MachineServices.ConflictsWith(config with { DbPort = dbPort }, withSites: true, withDb: InstallDb || new DbInstaller(_main.App.Paths).ListInstalled().Count > 0)
            .Select(c => $"{c.Service.DisplayName} ({c.Service.Name}): {Services.MachineServices.StateText(c.Service).ToLowerInvariant()} y suele usar el {string.Join(" y el ", c.Ports)}, "
                         + $"{(c.Ports.Count == 1 ? "que es" : "que son")} {string.Join(" y ", c.Ports.Select(p => AuditViewModel.RoleOf(p, config with { DbPort = dbPort })))} de Roost. Déjalo en Manual en Servicios de Windows para que no se lo quede.")
            .ToList();
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
