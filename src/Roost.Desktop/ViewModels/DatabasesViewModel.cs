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
/// <param name="UseSpec">Lo que guarda «Usar»: la rama, o la versión exacta si hay dos de la misma rama.</param>
public sealed record DbRow(InstalledDb Db, string Support, Tone SupportTone, string Data, bool InUse, string UseSpec)
{
    public string Engine => Db.Engine.DisplayName();
    public string Version => Db.Name;
    public bool NotInUse => !InUse;
    public string UseTip => $"Guarda {Db.Engine.Key()}@{UseSpec} como la base de datos global";
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

/// <summary>Un usuario del servidor en marcha, con su acceso.</summary>
public sealed record DbUserRow(DbUser User)
{
    public string Name => User.Name;
    public string Host => User.Host;
    public string Access => User.AccessText;
    public Tone AccessTone => User.AllDatabases ? Tone.Warn : User.Databases.Count == 0 ? Tone.Off : Tone.Ok;
}

/// <summary>
/// Bases de datos: la global (conexión con el puerto editable, la contraseña de root, datos y
/// ajustes), los clientes gráficos del equipo, las instaladas, el catálogo de MariaDB y MySQL,
/// y las bases de datos y los usuarios que hay dentro del servidor en marcha.
/// </summary>
public sealed partial class DatabasesViewModel : PageViewModel
{
    // Puertos habituales de MySQL y MariaDB: los que se miran en «Puertos en este equipo».
    private static readonly int[] CommonPorts = [3306, 3307, 3308, 3309, 3310, 33060, 33061];

    private IReadOnlyList<DbBuild>? _mariaDb;
    private IReadOnlyList<DbClient>? _clients;
    private RoostConfig _config = new();
    private InstalledDb? _global;
    private string? _rootPassword;

    public DatabasesViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "MariaDB o MySQL, una en marcha a la vez y solo en este equipo. Root con la contraseña que quieras, y usuarios y bases de datos por proyecto.";
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
    /// <summary>La base de datos está en el stack en marcha pero parada (Parar, o se cayó): el botón Iniciar.</summary>
    [ObservableProperty] public partial bool CanStartDb { get; private set; }
    [ObservableProperty] public partial int Port { get; private set; } = RoostConfig.DefaultDbPort;
    [ObservableProperty] public partial string EnvLine { get; private set; } = "";
    [ObservableProperty] public partial string DataPath { get; private set; } = "";
    [ObservableProperty] public partial string IniPath { get; private set; } = "";
    [ObservableProperty] public partial string UserFilePath { get; private set; } = "";

    /// <summary>«(vacía)» o puntos: la contraseña nunca se pinta en la pantalla.</summary>
    [ObservableProperty] public partial string PasswordMask { get; private set; } = "(vacía)";
    [ObservableProperty] public partial bool HasPassword { get; private set; }
    [ObservableProperty] public partial string ChangePasswordTip { get; private set; } = "";

    [ObservableProperty] public partial string PortText { get; set; } = "";
    [ObservableProperty] public partial string PortTitle { get; private set; } = "";
    [ObservableProperty] public partial string PortDetail { get; private set; } = "";
    [ObservableProperty] public partial Tone PortTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial bool CanApplyPort { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<int> Suggestions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BusyPortLine> BusyPorts { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DbClient> Clients { get; private set; } = [];

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
    [ObservableProperty] public partial IReadOnlyList<DbUserRow> Users { get; private set; } = [];
    [ObservableProperty] public partial string? UsersError { get; private set; }
    [ObservableProperty] public partial bool Refreshing { get; private set; }
    [ObservableProperty] public partial string? Installing { get; private set; }

    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasBusyPorts => BusyPorts.Count > 0;
    public bool HasClients => Clients.Count > 0;
    public bool HasExternal => External.Count > 0;
    public bool HasInstalled => Installed.Count > 0;
    public bool HasCatalogError => CatalogError is not null;
    public bool HasDatabasesError => DatabasesError is not null;
    public bool HasDatabases => Databases.Count > 0;
    public bool ShowEmptyDatabases => !HasDatabases && !HasDatabasesError;
    public bool HasUsersError => UsersError is not null;
    public bool HasUsers => Users.Count > 0;
    public bool ShowEmptyUsers => !HasUsers && !HasUsersError;
    public bool IsInstalling => Installing is not null;
    public string DatabasesTitle => _global is null ? "Bases de datos" : $"Bases de datos en {_global.Engine.DisplayName()} {_global.Branch}";

    partial void OnPortTextChanged(string value) => CheckPort();
    partial void OnShowMariaDbChanged(bool value) => BuildCatalog();
    partial void OnSuggestionsChanged(IReadOnlyList<int> value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnBusyPortsChanged(IReadOnlyList<BusyPortLine> value) => OnPropertyChanged(nameof(HasBusyPorts));
    partial void OnClientsChanged(IReadOnlyList<DbClient> value) => OnPropertyChanged(nameof(HasClients));
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

    partial void OnUsersErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUsersError));
        OnPropertyChanged(nameof(ShowEmptyUsers));
    }

    partial void OnUsersChanged(IReadOnlyList<DbUserRow> value)
    {
        OnPropertyChanged(nameof(HasUsers));
        OnPropertyChanged(nameof(ShowEmptyUsers));
    }

    partial void OnInstallingChanged(string? value) => OnPropertyChanged(nameof(IsInstalling));

    public override async Task ShownAsync()
    {
        Build();
        if (_clients is null)
        {
            // Rutas típicas y el registro, solo lectura; una vez por sesión.
            _clients = await Task.Run(DbClients.Detect);
            Clients = _clients;
        }
        if (_mariaDb is null)
        {
            await LoadCatalogAsync();
            // El catálogo dice qué ramas son LTS: el soporte de las instaladas también cambia.
            Build();
        }
        // Al volver a la pantalla se vuelve a leer: una base de datos creada desde Nuevo sitio o
        // desde la CLI aparece sin más.
        await LoadServerAsync();
    }

    public override void StackChanged()
    {
        var wasRunning = DbRunning;
        Build();
        if (DbRunning != wasRunning) _ = LoadServerAsync();
    }

    private void Build()
    {
        var paths = Main.App.Paths;
        var status = Main.Stack.Status;
        try { _config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { _config = new RoostConfig(); }
        var installed = new DbInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        try { _global = planner.ResolveDb(_config, installed); } catch (RoostException) { _global = null; }
        try { _rootPassword = _global is null ? null : new DbPasswords(paths).For(_global); } catch (RoostException) { _rootPassword = null; }

        HasGlobal = _global is not null;
        var service = _global is null ? null : $"{_global.Engine.Key()}-{_global.Branch}";
        DbRunning = status?.Running == true && status.Services.Any(s => s.Name == service && s.State == "Running");
        // Parada a mano con el stack en marcha (o caída): el daemon la puede volver a arrancar.
        CanStartDb = status?.Running == true && status.Services.Any(s => s.Name == service && s.State != "Running");
        Port = DbRunning ? status!.DbPort ?? _config.DbPort : _config.DbPort;
        HeaderTitle = DbRunning ? "En marcha" : "Base de datos global";
        HeaderChip = _global is null ? "ninguna" : _global.Label;
        HeaderTone = _global is null ? Tone.Off : DbRunning ? Tone.Ok : Tone.Neutral;
        HasPassword = _rootPassword is not null;
        PasswordMask = HasPassword ? "••••••••" : "(vacía)";
        ChangePasswordTip = DbRunning ? "Poner, cambiar o quitar la contraseña de root" : "Arranca la base de datos para cambiarla (Iniciar todo)";
        EnvLine = $"DB_HOST=127.0.0.1  DB_PORT={Port}  DB_USERNAME=root  DB_PASSWORD={_rootPassword}";
        DataPath = _global is null ? "" : DbIni.DataDir(paths, _global);
        IniPath = _global is null ? "" : planner.DbIniPath(_global);
        UserFilePath = _global is null ? "" : DbIni.UserFilePath(paths, _global);
        OnPropertyChanged(nameof(DatabasesTitle));

        Installed = installed.Select(db =>
        {
            var (support, tone) = Support(db.Engine, db.Branch, lts: null);
            // Con dos versiones de la misma rama, «Usar» tiene que fijar la exacta; si no, la rama,
            // para que las versiones nuevas de esa rama entren solas.
            var twins = installed.Count(other => other.Engine == db.Engine && other.Branch == db.Branch);
            return new DbRow(db, support, tone, $"data\\{db.Engine.Key()}\\{db.Branch}", db.Directory == _global?.Directory,
                twins > 1 ? db.Name : db.Branch ?? db.Name);
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
            (PortTitle, PortDetail, PortTone) = ($"El {port} es del servidor web", "Lo usa el servidor web de Roost (HTTP o HTTPS).", Tone.Bad);
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

        (PortTitle, PortDetail, PortTone) = ($"El {port} está libre", port == _config.DbPort ? "Es el que tiene configurado Roost." : "Puedes usarlo: Aplicar lo guarda y rearranca la base de datos.", Tone.Ok);
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

    /// <summary>Las bases de datos y los usuarios del servidor en marcha (fuera del hilo de la UI).</summary>
    private async Task LoadServerAsync()
    {
        DatabasesError = null;
        UsersError = null;
        if (_global is null || !DbRunning)
        {
            Databases = [];
            Users = [];
            DatabasesError = _global is null ? null : "Arranca Roost para ver y crear bases de datos.";
            UsersError = _global is null ? null : "Arranca Roost para ver y crear usuarios.";
            return;
        }

        var db = _global;
        var ini = IniPath;
        Refreshing = true;
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

        try
        {
            var users = await Task.Run(() => DbAdmin.ListUsers(db, ini, ProcessRunner.Default));
            Users = users.Select(u => new DbUserRow(u)).ToList();
        }
        catch (RoostException ex)
        {
            Users = [];
            UsersError = ex.Message;
        }
        finally
        {
            Refreshing = false;
        }
    }

    private InstalledDb RequireRunning()
    {
        if (_global is null || !DbRunning) throw new RoostException("La base de datos no está en marcha: pulsa Iniciar todo.");
        return _global;
    }

    private DbConnection Connection(string? database) =>
        new(_global!.Engine, "127.0.0.1", Port, DbAdmin.RootUser, _rootPassword ?? "", database, $"Roost · {_global.Label}");

    private static string EnvFor(int port, string user, string password, string? database) =>
        $"DB_HOST=127.0.0.1\nDB_PORT={port}\n{(database is null ? "" : $"DB_DATABASE={database}\n")}DB_USERNAME={user}\nDB_PASSWORD={password}";

    // Acciones

    [RelayCommand]
    private Task CopyAsync(string text) => Main.CopyAsync(text);

    [RelayCommand]
    private Task CopyPasswordAsync() => _rootPassword is null ? Task.CompletedTask : Main.CopyAsync(_rootPassword, "Contraseña de root copiada");

    [RelayCommand]
    private Task RefreshAsync() => LoadServerAsync();

    [RelayCommand]
    private Task RestartAsync() => Main.RunAsync("No se pudo reiniciar la base de datos", async () =>
    {
        if (_global is null) return;
        var response = await Main.App.Daemon.RestartAsync($"{_global.Engine.Key()}-{_global.Branch}");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
    });

    /// <summary>
    /// Para solo la base de datos, limpia (como <c>roost stop mariadb-11.4</c>): PHP y el servidor
    /// web siguen, y la configuración no cambia. Vuelve con Iniciar, o con Iniciar todo.
    /// </summary>
    [RelayCommand]
    private Task StopAsync() => Main.RunAsync("No se pudo parar la base de datos", async () =>
    {
        if (_global is null) return;
        var response = await Main.App.Daemon.StopServiceAsync($"{_global.Engine.Key()}-{_global.Branch}");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
        Main.Flash("Base de datos parada");
    });

    /// <summary>Vuelve a arrancar la base de datos parada con Parar (el stack tiene que estar en marcha).</summary>
    [RelayCommand]
    private Task StartAsync() => Main.RunAsync("No se pudo iniciar la base de datos", async () =>
    {
        if (_global is null) return;
        var response = await Main.App.Daemon.StartServiceAsync($"{_global.Engine.Key()}-{_global.Branch}");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
    });

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

    /// <summary>La contraseña de root: en el servidor, en lo que guarda Roost y en el my.ini (DbAdmin.ChangeRootPassword).</summary>
    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (_global is null || !DbRunning)
        {
            await Main.InfoAsync("La base de datos no está en marcha", "La contraseña de root se cambia con ella arrancada: pulsa Iniciar todo.", Tone.Warn);
            return;
        }

        var dialog = new DbPasswordDialogViewModel("Contraseña de root", $"{_global.Label} en 127.0.0.1:{Port}. Se cambia en todas las cuentas root del servidor.", forRoot: true);
        if (await Main.ShowDialogAsync(dialog) is not DbPasswordChoice choice) return;

        var db = _global;
        var port = Port;
        await Main.RunAsync("No se pudo cambiar la contraseña de root", async () =>
        {
            await Task.Run(() => DbAdmin.ChangeRootPassword(Main.App.Paths, db, port, choice.Password, choice.SaveOnly, ProcessRunner.Default));
            Main.NotifyConfigChanged();
            Build();
            Main.Flash(choice.Password.Length == 0 ? "root vuelve a ir sin contraseña" : choice.SaveOnly ? "Contraseña guardada en Roost" : "Contraseña de root cambiada");
        });
    }

    [RelayCommand]
    private Task UseAsync(DbRow row) => Main.UseDbAsync(row.Db.Engine, row.UseSpec);

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
    private Task InstallAsync(DbCatalogRow row) => InstallBuildAsync(row.Build, offerUse: true);

    /// <summary>
    /// Instala una versión del catálogo con el diálogo Instalando y comprueba que el servidor
    /// responde (si no, suele faltar el Visual C++ Redistributable). true si acaba bien.
    /// </summary>
    /// <param name="offerUse">
    /// Decir «Pulsa Usar» si no queda como global. La pantalla lo pide; la bienvenida no, porque
    /// la pone ella misma acto seguido.
    /// </param>
    public async Task<bool> InstallBuildAsync(DbBuild build, bool offerUse = false)
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
        InstalledDb? installed = null;
        var dialog = new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"{(build.Lts ? "LTS · " : "")}x64 · desde {build.Urls[0].Host}",
            null,
            steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                installed = await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
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
        {
            await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
            return false;
        }
        if (!dialog.Finished.Result) return false;

        if (installed is { } db && !await SmokeTestAsync(db)) return true;
        if (offerUse && _global is null)
            await Main.InfoAsync($"{build.Label} instalada", "Pulsa Usar para que sea la base de datos global; luego Iniciar todo la arranca.", Tone.Ok);
        return true;
    }

    /// <summary>Lo mismo que hace la CLI tras instalar: si el servidor no contesta a --version, nombra el Redistributable que falta.</summary>
    private async Task<bool> SmokeTestAsync(InstalledDb db)
    {
        var smoke = await Task.Run(() => DbAdmin.SmokeTest(db, ProcessRunner.Default));
        if (smoke.Ok) return true;

        var result = await Main.ShowDialogAsync(new MessageDialogViewModel
        {
            Title = $"{db.Label} está instalada, pero no arranca",
            Subtitle = "Falta una biblioteca de Windows",
            Text = $"{Path.GetFileName(db.Server)} no responde a --version. Casi siempre es que falta el Visual C++ Redistributable {smoke.MissingRedistributable}: "
                   + "instálalo (un minuto, de Microsoft) y vuelve a pulsar Iniciar todo. La instalación de la base de datos es correcta.",
            Detail = smoke.RedistributableUrl,
            Tone = Tone.Warn,
            Buttons = [new DialogButton("Cerrar", null), new DialogButton("Descargar el Redistributable", "download", "primary")],
        });
        if (result is "download") Shell.OpenUrl(smoke.RedistributableUrl!);
        return false;
    }

    [RelayCommand]
    private Task CreateDatabaseAsync() => Main.RunAsync("No se pudo crear la base de datos", async () =>
    {
        var db = RequireRunning();
        var name = NewDatabase.Trim();
        var ini = IniPath;
        await Task.Run(() => DbAdmin.Create(db, ini, name, ProcessRunner.Default));
        NewDatabase = "";
        Main.Flash($"Base de datos {name} lista (utf8mb4)");
        await LoadServerAsync();
    });

    [RelayCommand]
    private async Task DropDatabaseAsync(DatabaseLine line)
    {
        if (!await Main.ConfirmAsync($"Borrar {line.Name}",
                $"Se borra {line.Name} y todas sus tablas. No se puede deshacer.{(line.UsedBy is null ? "" : $" ({line.UsedBy})")}",
                "Borrar", danger: true))
            return;
        await Main.RunAsync($"No se pudo borrar {line.Name}", async () =>
        {
            var db = RequireRunning();
            var ini = IniPath;
            await Task.Run(() => DbAdmin.Drop(db, ini, line.Name, ProcessRunner.Default));
            Main.Flash($"Base de datos {line.Name} borrada");
            await LoadServerAsync();
        });
    }

    [RelayCommand]
    private Task OpenConsoleAsync(string? database) => Main.RunAsync("No se pudo abrir la consola SQL", () =>
    {
        var db = RequireRunning();
        Shell.OpenConsole(db.Client, DbAdmin.ConsoleArguments(IniPath, database), Main.App.Paths.Www);
        return Task.CompletedTask;
    });

    /// <summary>HeidiSQL y DBeaver se abren ya conectados; TablePlus se abre y la URL queda en el portapapeles.</summary>
    [RelayCommand]
    private Task OpenClientAsync(DbClient client) => Main.RunAsync($"No se pudo abrir {client.Name}", async () =>
    {
        RequireRunning();
        var connection = Connection(null);
        if (!client.AcceptsConnection)
            await Main.CopyAsync(connection.Url, $"URL de conexión copiada: en {client.Name}, Nueva conexión › desde URL");
        DbClients.Open(client, connection);
    });

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (DataPath.Length > 0) Shell.OpenFolder(DataPath);
    }

    [RelayCommand]
    private Task OpenIniAsync() => Main.RunAsync("No se pudo abrir el my.ini", () =>
    {
        if (!File.Exists(IniPath)) throw new RoostException("Todavía no se ha generado: se escribe al arrancar la base de datos (Iniciar todo).");
        Shell.OpenFile(IniPath);
        return Task.CompletedTask;
    });

    /// <summary>usuario.cnf: lo crea con ejemplos si aún no existe (lo normal es que lo haya dejado el primer arranque).</summary>
    [RelayCommand]
    private Task EditUserFileAsync() => Main.RunAsync("No se pudo abrir usuario.cnf", () =>
    {
        if (UserFilePath.Length == 0) return Task.CompletedTask;
        Directory.CreateDirectory(Path.GetDirectoryName(UserFilePath)!);
        if (!File.Exists(UserFilePath)) File.WriteAllText(UserFilePath, DbIni.UserFileTemplate);
        Shell.OpenFile(UserFilePath);
        Main.Flash("Lo que cambies se aplica al reiniciar la base de datos");
        return Task.CompletedTask;
    });

    // Usuarios

    [RelayCommand]
    private async Task NewUserAsync(string? database)
    {
        if (_global is null || !DbRunning)
        {
            await Main.InfoAsync("La base de datos no está en marcha", "Los usuarios se crean con ella arrancada: pulsa Iniciar todo.", Tone.Warn);
            return;
        }

        var dialog = new DbUserDialogViewModel(_global.Label, Databases.Select(d => d.Name).ToList(), database);
        if (await Main.ShowDialogAsync(dialog) is not NewDbUser user) return;

        var db = _global;
        var ini = IniPath;
        await Main.RunAsync($"No se pudo crear el usuario {user.Name}", async () =>
        {
            await Task.Run(() => DbAdmin.CreateUser(db, ini, user.Name, user.Password, user.Database, ProcessRunner.Default));
            await LoadServerAsync();
            var env = EnvFor(Port, user.Name, user.Password, user.Database);
            var choice = await Main.ShowDialogAsync(new MessageDialogViewModel
            {
                Title = $"Usuario {user.Name} creado",
                Subtitle = user.Database is null ? "Con acceso a todas las bases de datos" : $"Con acceso a {user.Database}",
                Text = "Para el .env de tu proyecto:",
                Detail = env,
                Tone = Tone.Ok,
                Buttons = [new DialogButton("Cerrar", null), new DialogButton("Copiar el .env", "copy", "primary")],
            });
            if (choice is "copy") await Main.CopyAsync(env, $"Copiado el .env de {user.Name}");
        });
    }

    [RelayCommand]
    private async Task ChangeUserPasswordAsync(DbUserRow row)
    {
        if (_global is null || !DbRunning) return;
        var dialog = new DbPasswordDialogViewModel($"Contraseña de {row.Name}", $"La cuenta '{row.Name}'@'{row.Host}' en {_global.Label}.", forRoot: false);
        if (await Main.ShowDialogAsync(dialog) is not DbPasswordChoice choice) return;

        var db = _global;
        var ini = IniPath;
        await Main.RunAsync($"No se pudo cambiar la contraseña de {row.Name}", async () =>
        {
            await Task.Run(() => DbAdmin.SetUserPassword(db, ini, row.Name, row.Host, choice.Password, ProcessRunner.Default));
            Main.Flash($"Contraseña de {row.Name} cambiada");
        });
    }

    [RelayCommand]
    private async Task DropUserAsync(DbUserRow row)
    {
        if (!await Main.ConfirmAsync($"Borrar el usuario {row.Name}",
                $"Se borra la cuenta '{row.Name}'@'{row.Host}' y sus permisos. Sus bases de datos se quedan.", "Borrar", danger: true))
            return;
        await Main.RunAsync($"No se pudo borrar el usuario {row.Name}", async () =>
        {
            var db = RequireRunning();
            var ini = IniPath;
            await Task.Run(() => DbAdmin.DropUser(db, ini, row.Name, row.Host, ProcessRunner.Default));
            Main.Flash($"Usuario {row.Name} borrado");
            await LoadServerAsync();
        });
    }
}
