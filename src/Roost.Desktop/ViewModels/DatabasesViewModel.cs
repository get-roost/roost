using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una base de datos instalada por Roost.</summary>
public sealed record DbRow(InstalledDb Db, string Support, Tone SupportTone, string Data, bool InUse)
{
    public string Engine => Db.Engine.DisplayName();
    public string Version => Db.Name;
    public bool NotInUse => !InUse;
}

/// <summary>Una versión del catálogo (MariaDB de su API, MySQL fijadas en Roost).</summary>
public sealed record DbCatalogRow(DbBuild Build, string Support, Tone Tone, bool Installed)
{
    public string Version => Build.Version.ToString();
    public bool Installable => !Installed;
}

/// <summary>Una base de datos de fuera de Roost que ha encontrado la auditoría.</summary>
public sealed record ExternalDbRow(string Label, string Where, string Chip, Tone Tone);

/// <summary>Un puerto de base de datos ocupado en la máquina, y por quién.</summary>
public sealed record BusyPortLine(int Port, string Owner);

/// <summary>Una base de datos dentro del servidor en marcha.</summary>
public sealed record DatabaseLine(string Name, string? UsedBy)
{
    public bool HasUsedBy => UsedBy is not null;
}

/// <summary>
/// Bases de datos: la global (conexión, puerto, datos), las instaladas, el catálogo de MariaDB
/// y MySQL, y las bases de datos que hay dentro del servidor en marcha.
/// </summary>
public sealed partial class DatabasesViewModel : PageViewModel
{
    // Puertos habituales de MySQL y MariaDB: los que se miran en «Ocupados en este equipo».
    private static readonly int[] CommonPorts = [3306, 3307, 3308, 3309, 3310, 33060, 33061];

    private IReadOnlyList<DbBuild>? _mariaDb;
    private RoostConfig _config = new();
    private InstalledDb? _global;

    public DatabasesViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "MariaDB o MySQL, una en marcha a la vez. Solo escucha en este equipo; usuario root, sin contraseña.";
        main.Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuditState.Findings)) BuildExternal();
        };
    }

    public override string Title => "Bases de datos";

    [ObservableProperty] public partial string HeaderTitle { get; private set; } = "Base de datos global";
    [ObservableProperty] public partial string HeaderChip { get; private set; } = "";
    [ObservableProperty] public partial Tone HeaderTone { get; private set; } = Tone.Off;
    [ObservableProperty] public partial bool HasGlobal { get; private set; }
    [ObservableProperty] public partial bool DbRunning { get; private set; }
    [ObservableProperty] public partial int Port { get; private set; } = RoostConfig.DefaultDbPort;
    [ObservableProperty] public partial string EnvLine { get; private set; } = "";
    [ObservableProperty] public partial string DataPath { get; private set; } = "";
    [ObservableProperty] public partial string IniPath { get; private set; } = "";

    [ObservableProperty] public partial string PortText { get; set; } = "";
    [ObservableProperty] public partial string PortTitle { get; private set; } = "";
    [ObservableProperty] public partial string PortDetail { get; private set; } = "";
    [ObservableProperty] public partial Tone PortTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial bool CanApplyPort { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<int> Suggestions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BusyPortLine> BusyPorts { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<DbRow> Installed { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ExternalDbRow> External { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DbCatalogRow> Catalog { get; private set; } = [];
    [ObservableProperty] public partial string CatalogSource { get; private set; } = "";
    [ObservableProperty] public partial string? CatalogError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMySql))]
    public partial bool ShowMariaDb { get; set; } = true;

    public bool ShowMySql { get => !ShowMariaDb; set => ShowMariaDb = !value; }

    [ObservableProperty] public partial IReadOnlyList<DatabaseLine> Databases { get; private set; } = [];
    [ObservableProperty] public partial string? DatabasesError { get; private set; }
    [ObservableProperty] public partial string NewDatabase { get; set; } = "";
    [ObservableProperty] public partial string? Installing { get; private set; }

    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasBusyPorts => BusyPorts.Count > 0;
    public bool HasExternal => External.Count > 0;
    public bool HasInstalled => Installed.Count > 0;
    public bool HasCatalogError => CatalogError is not null;
    public bool HasDatabasesError => DatabasesError is not null;
    public bool HasDatabases => Databases.Count > 0;
    public bool ShowEmptyDatabases => !HasDatabases && !HasDatabasesError;
    public bool IsInstalling => Installing is not null;
    public string DatabasesTitle => _global is null ? "Bases de datos" : $"Bases de datos en {_global.Engine.DisplayName()} {_global.Branch}";

    partial void OnPortTextChanged(string value) => CheckPort();
    partial void OnShowMariaDbChanged(bool value) => BuildCatalog();
    partial void OnSuggestionsChanged(IReadOnlyList<int> value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnBusyPortsChanged(IReadOnlyList<BusyPortLine> value) => OnPropertyChanged(nameof(HasBusyPorts));
    partial void OnExternalChanged(IReadOnlyList<ExternalDbRow> value) => OnPropertyChanged(nameof(HasExternal));
    partial void OnInstalledChanged(IReadOnlyList<DbRow> value) => OnPropertyChanged(nameof(HasInstalled));
    partial void OnCatalogErrorChanged(string? value) => OnPropertyChanged(nameof(HasCatalogError));
    partial void OnDatabasesErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDatabasesError));
        OnPropertyChanged(nameof(ShowEmptyDatabases));
    }

    partial void OnDatabasesChanged(IReadOnlyList<DatabaseLine> value)
    {
        OnPropertyChanged(nameof(HasDatabases));
        OnPropertyChanged(nameof(ShowEmptyDatabases));
    }
    partial void OnInstallingChanged(string? value) => OnPropertyChanged(nameof(IsInstalling));

    public override async Task ShownAsync()
    {
        Build();
        if (_mariaDb is null)
        {
            await LoadCatalogAsync();
            // El catálogo dice qué ramas son LTS: el soporte de las instaladas también cambia.
            Build();
        }
        await LoadDatabasesAsync();
    }

    public override void StackChanged()
    {
        var wasRunning = DbRunning;
        Build();
        if (DbRunning != wasRunning) _ = LoadDatabasesAsync();
    }

    private void Build()
    {
        var paths = Main.App.Paths;
        var status = Main.Stack.Status;
        try { _config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { _config = new RoostConfig(); }
        var installed = new DbInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        try { _global = planner.ResolveDb(_config, installed); } catch (RoostException) { _global = null; }

        HasGlobal = _global is not null;
        var service = _global is null ? null : $"{_global.Engine.Key()}-{_global.Branch}";
        DbRunning = status?.Running == true && status.Services.Any(s => s.Name == service && s.State == "Running");
        Port = DbRunning ? status!.DbPort ?? _config.DbPort : _config.DbPort;
        HeaderTitle = DbRunning ? "En marcha" : "Base de datos global";
        HeaderChip = _global is null ? "ninguna" : _global.Label;
        HeaderTone = _global is null ? Tone.Off : DbRunning ? Tone.Ok : Tone.Neutral;
        EnvLine = $"DB_HOST=127.0.0.1  DB_PORT={Port}  DB_USERNAME=root  DB_PASSWORD=";
        DataPath = _global is null ? "" : Path.Combine(paths.Data, _global.Engine.Key(), _global.Branch!);
        IniPath = _global is null ? "" : planner.DbIniPath(_global);
        OnPropertyChanged(nameof(DatabasesTitle));

        Installed = installed.Select(db =>
        {
            var (support, tone) = Support(db.Engine, db.Branch, lts: null);
            return new DbRow(db, support, tone, $"data\\{db.Engine.Key()}\\{db.Branch}", db.Directory == _global?.Directory);
        }).ToList();

        if (PortText.Length == 0 || PortText == _config.DbPort.ToString(CultureInfo.InvariantCulture))
            PortText = _config.DbPort.ToString(CultureInfo.InvariantCulture);
        CheckPort();
        BuildBusyPorts();
        BuildCatalog();
        BuildExternal();
    }

    private void CheckPort()
    {
        Suggestions = [];
        CanApplyPort = false;
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            (PortTitle, PortDetail, PortTone) = ("Puerto no válido", "Un número entre 1 y 65535.", Tone.Bad);
            return;
        }

        if (port == _config.HttpPort || port == _config.HttpsPort)
        {
            (PortTitle, PortDetail, PortTone) = ($"El {port} es de nginx", "Lo usa el servidor web de Roost.", Tone.Bad);
            return;
        }

        if (port == _config.DbPort && DbRunning)
        {
            (PortTitle, PortDetail, PortTone) = ($"El {port} lo usa Roost", $"{_global?.Label} escucha en 127.0.0.1:{port}.", Tone.Ok);
            return;
        }

        if (StackPlanner.BusyPorts([port]).Count > 0)
        {
            var owner = PortOwners.Find(port);
            (PortTitle, PortDetail, PortTone) = ($"El {port} está ocupado",
                owner is null ? "Lo tiene otro proceso." : $"Lo tiene {owner.ProcessName ?? "otro proceso"} (PID {owner.Pid}).", Tone.Bad);
            Suggestions = new[] { 3307, 3308, 3309, 3310, 3311, 33061, 3406 }
                .Where(p => p != _config.HttpPort && p != _config.HttpsPort && StackPlanner.BusyPorts([p]).Count == 0)
                .Take(3)
                .ToList();
            return;
        }

        (PortTitle, PortDetail, PortTone) = ($"El {port} está libre", port == _config.DbPort ? "Es el que tiene configurado Roost." : "Puedes usarlo.", Tone.Ok);
        CanApplyPort = port != _config.DbPort;
    }

    private void BuildBusyPorts()
    {
        var ownPid = Main.Stack.Status?.Services.FirstOrDefault(s => s.Port == Port && !s.Name.StartsWith("php-", StringComparison.Ordinal))?.Pid;
        BusyPorts = CommonPorts
            .Where(p => StackPlanner.BusyPorts([p]).Count > 0)
            .Select(p =>
            {
                var owner = PortOwners.Find(p);
                var text = owner is null ? "otro proceso"
                    : owner.Pid == ownPid ? $"Roost ({_global?.Label})"
                    : $"{owner.ProcessName ?? "otro proceso"} (PID {owner.Pid})";
                return new BusyPortLine(p, text);
            })
            .ToList();
    }

    private async Task LoadCatalogAsync()
    {
        CatalogError = null;
        try
        {
            _mariaDb = await MariaDbCatalog.FetchAsync(Main.App.Http);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            _mariaDb = [];
            CatalogError = $"No se pudo leer el catálogo de MariaDB (downloads.mariadb.org): {ex.Message}";
        }
        BuildCatalog();
    }

    private void BuildCatalog()
    {
        var installed = Installed.Select(r => (r.Db.Engine, r.Db.Name)).ToHashSet();
        var builds = ShowMariaDb ? _mariaDb ?? [] : MySqlCatalog.Windows;
        Catalog = builds
            .OrderByDescending(b => b.Version)
            .Select(b =>
            {
                var (support, tone) = Support(b.Engine, b.Branch, b.Lts);
                return new DbCatalogRow(b, support, tone, installed.Contains((b.Engine, b.InstallName)));
            })
            .ToList();
        CatalogSource = ShowMariaDb
            ? "downloads.mariadb.org · las ramas estables, en su última versión"
            : "Versiones fijadas en Roost y verificadas con la firma GPG de Oracle";
    }

    private void BuildExternal()
    {
        External = Main.Audit.Findings
            .Where(f => f.Component.Kind is ComponentKind.MariaDb or ComponentKind.MySql && f.Component.Source != "Roost")
            .Select(f => new ExternalDbRow(
                $"{(f.Component.Kind == ComponentKind.MariaDb ? "MariaDB" : "MySQL")} {f.Component.Version} ({f.Component.Source})",
                Path.GetDirectoryName(Path.GetDirectoryName(f.Component.ExecutablePath)) ?? f.Component.ExecutablePath,
                AuditState.ChipText(f), AuditState.ToneOf(f.Color)))
            .ToList();
    }

    private (string Text, Tone Tone) Support(DbEngine engine, string? branch, bool? lts)
    {
        var es = CultureInfo.GetCultureInfo("es-ES");
        if (branch is null || !Main.Audit.Dataset.Products.TryGetValue(engine.Key(), out var product)
                           || !product.Branches.TryGetValue(branch, out var info))
            return ("Sin datos de soporte", Tone.Warn);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (info.Status == "eol" || info.Eol is { } e && e <= today)
            return (info.Eol is { } eol ? $"EOL desde {eol.ToString("MMM-yyyy", es)}" : "EOL", Tone.Bad);
        var isLts = lts ?? (_mariaDb?.FirstOrDefault(b => b.Engine == engine && b.Branch == branch)?.Lts
                            ?? MySqlCatalog.Windows.FirstOrDefault(b => b.Engine == engine && b.Branch == branch)?.Lts ?? false);
        return (info.Eol is { } end ? $"{(isLts ? "LTS hasta" : "Hasta")} {end.ToString("MMM-yyyy", es)}" : "Con soporte", Tone.Ok);
    }

    private async Task LoadDatabasesAsync()
    {
        DatabasesError = null;
        if (_global is null || !DbRunning)
        {
            Databases = [];
            DatabasesError = _global is null ? null : "Arranca Roost para ver y crear bases de datos.";
            return;
        }

        var db = _global;
        var ini = IniPath;
        try
        {
            var names = await Task.Run(() => DbAdmin.List(db, ini, ProcessRunner.Default));
            IReadOnlyList<Site> sites = [];
            try { sites = new SiteRegistry(Main.App.Paths).Load(); } catch (Exception ex) when (ex is IOException or RoostException) { }
            Databases = names.Select(n => new DatabaseLine(n,
                sites.FirstOrDefault(s => s.Name.Replace('-', '_') == n) is { } site ? $"parece de {site.Host}" : null)).ToList();
        }
        catch (RoostException ex)
        {
            Databases = [];
            DatabasesError = ex.Message;
        }
    }

    // Acciones

    [RelayCommand]
    private Task CopyAsync(string text) => Main.CopyAsync(text);

    [RelayCommand]
    private Task RestartAsync() => Main.RunAsync("No se pudo reiniciar la base de datos", async () =>
    {
        if (_global is null) return;
        var response = await Main.App.Daemon.RestartAsync($"{_global.Engine.Key()}-{_global.Branch}");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
    });

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!await Main.ConfirmAsync("Parar la base de datos",
                "Roost seguirá sirviendo PHP, sin base de datos (como roost db use none). Para volver a tenerla, pulsa Usar en la lista de instaladas.",
                "Parar"))
            return;
        await Main.UseDbAsync(null, null);
    }

    [RelayCommand]
    private Task ApplyPortAsync() => Main.RunAsync("No se pudo cambiar el puerto", async () =>
    {
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return;
        RoostConfig.SetDbPort(Main.App.Paths.ConfigFile, port);
        await Main.ApplyAsync();
        Main.NotifyConfigChanged();
    });

    [RelayCommand]
    private void UseSuggestion(int port) => PortText = port.ToString(CultureInfo.InvariantCulture);

    [RelayCommand]
    private Task UseAsync(DbRow row) => Main.UseDbAsync(row.Db.Engine, row.Db.Branch);

    [RelayCommand]
    private async Task RemoveAsync(DbRow row)
    {
        if (row.InUse)
        {
            await Main.InfoAsync($"{row.Db.Label} es la base de datos global", "Elige otra (o ninguna) antes de quitarla.", Tone.Warn);
            return;
        }

        var data = Path.Combine(Main.App.Paths.Data, row.Db.Engine.Key(), row.Db.Branch!);
        if (!await Main.ConfirmAsync($"Quitar {row.Db.Label}",
                $"Se borran sus binarios ({row.Db.Directory}). Sus datos ({data}) se quedan: Roost nunca borra datos.", "Quitar", danger: true))
            return;
        await Main.RunAsync($"No se pudo quitar {row.Db.Label}", () =>
        {
            new DbInstaller(Main.App.Paths).Remove(row.Db);
            return Task.CompletedTask;
        });
        Main.NotifyConfigChanged();
        Build();
    }

    [RelayCommand]
    private Task InstallAsync(DbCatalogRow row) => InstallBuildAsync(row.Build);

    /// <summary>Instala una versión del catálogo con el diálogo Instalando (también desde la bienvenida). true si acaba bien.</summary>
    public async Task<bool> InstallBuildAsync(DbBuild build)
    {
        if (Installing is not null) return false;
        var installer = new DbInstaller(Main.App.Paths, Main.App.Http);
        var steps = new List<InstallStep>
        {
            new("Catálogo leído", $"· {build.Version} es la última de la rama {build.Branch}"),
            new("Descargando el zip oficial", null),
            new("Verificar el SHA-256", "· antes de extraer nada"),
            new($"Extraer en {installer.InstallDirectory(build)}", null),
        };
        var dialog = new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"{(build.Lts ? "LTS · " : "")}x64 · desde {build.Urls[0].Host}",
            null,
            steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Extrayendo");
            });

        Installing = build.Label;
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        Installing = null;
        Main.NotifyConfigChanged();
        Build();
        if (shown is null && dialog.Failed)
            await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
        else if (dialog.Finished.Result && _global is null)
            await Main.InfoAsync($"{build.Label} instalada", "Pulsa Usar para que sea la base de datos global; luego Iniciar todo la arranca.", Tone.Ok);
        return dialog.Finished.Result;
    }

    [RelayCommand]
    private Task CreateDatabaseAsync() => Main.RunAsync("No se pudo crear la base de datos", async () =>
    {
        if (_global is null || !DbRunning) throw new RoostException("La base de datos no está en marcha: pulsa Iniciar todo.");
        var name = NewDatabase.Trim();
        var db = _global;
        var ini = IniPath;
        await Task.Run(() => DbAdmin.Create(db, ini, name, ProcessRunner.Default));
        NewDatabase = "";
        Main.Flash($"Base de datos {name} lista (utf8mb4)");
        await LoadDatabasesAsync();
    });

    [RelayCommand]
    private Task OpenConsoleAsync(string? database) => Main.RunAsync("No se pudo abrir la consola SQL", () =>
    {
        if (_global is null || !DbRunning) throw new RoostException("La base de datos no está en marcha: pulsa Iniciar todo.");
        Shell.OpenConsole(_global.Client, DbAdmin.ConsoleArguments(IniPath, database), Main.App.Paths.Www);
        return Task.CompletedTask;
    });
}
