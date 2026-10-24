using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Cómo está la base de datos global para el panel de un sitio.</summary>
public enum SiteDbMode
{
    /// <summary>No hay ninguna instalada, o Roost no arranca ninguna.</summary>
    None,

    /// <summary>Está instalada y elegida, pero parada.</summary>
    Stopped,

    /// <summary>En marcha: se puede crear y abrir.</summary>
    Running,
}

/// <summary>
/// La base de datos del sitio desde su panel: la suya (la que se eligió en Nuevo sitio o la que
/// sale de su nombre, <see cref="SiteManager.DatabaseOf"/>), si existe ya en la global, «Crear»,
/// «Abrir consola» y la línea del .env. Si la base de datos está parada o no hay ninguna, el panel
/// lo dice y ofrece lo que toca (Iniciar todo, ir a Bases de datos) en vez de fallar.
/// </summary>
public sealed partial class SitesViewModel
{
    private InstalledDb? _siteDb;
    private int _siteDbPort = RoostConfig.DefaultDbPort;

    // Las bases de datos del servidor en marcha (se leen fuera del hilo de la UI); null = aún no se
    // sabe. Si el cliente no contesta, no se vuelve a probar hasta que cambie el stack.
    private IReadOnlyList<string>? _dbNames;
    private bool _dbNamesTried;
    private bool _dbNamesLoading;

    [ObservableProperty] public partial string SiteDbName { get; private set; } = "";

    /// <summary>Dónde: «en MariaDB 11.4.13 · 127.0.0.1:3308»; vacío si no hay ninguna.</summary>
    [ObservableProperty] public partial string SiteDbWhere { get; private set; } = "";

    /// <summary>Lo que hay que saber (parada, no instalada, aún no existe); vacío si nada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSiteDbHint))]
    public partial string SiteDbHint { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSiteDbChip))]
    public partial string SiteDbChip { get; private set; } = "";

    [ObservableProperty] public partial Tone SiteDbTone { get; private set; } = Tone.Neutral;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteDbRunning), nameof(SiteDbStopped), nameof(SiteDbMissing), nameof(SiteDbCanCreate), nameof(SiteDbCanOpen), nameof(HasSiteDbEnv))]
    public partial SiteDbMode SiteDbMode { get; private set; }

    /// <summary>true si existe en el servidor, false si no, null si no se sabe (parada, o el cliente no contesta).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteDbCanCreate), nameof(SiteDbCanOpen))]
    public partial bool? SiteDbExists { get; private set; }

    public bool HasSiteDbHint => SiteDbHint.Length > 0;
    public bool HasSiteDbChip => SiteDbChip.Length > 0;
    public bool SiteDbRunning => SiteDbMode == SiteDbMode.Running;
    public bool SiteDbStopped => SiteDbMode == SiteDbMode.Stopped;
    public bool SiteDbMissing => SiteDbMode == SiteDbMode.None;

    /// <summary>«Crear»: en marcha y sin saber que ya exista.</summary>
    public bool SiteDbCanCreate => SiteDbRunning && SiteDbExists != true;

    /// <summary>«Abrir consola»: en marcha y sin saber que no exista.</summary>
    public bool SiteDbCanOpen => SiteDbRunning && SiteDbExists != false;

    public bool HasSiteDbEnv => SiteDbMode != SiteDbMode.None;

    public string CreateSiteDbLabel => $"Crear {SiteDbName}";

    partial void OnSiteDbNameChanged(string value) => OnPropertyChanged(nameof(CreateSiteDbLabel));

    /// <summary>Lo de la base de datos vuelve a leerse: al volver a la pantalla o si cambia el stack.</summary>
    private void ForgetDatabases()
    {
        _dbNames = null;
        _dbNamesTried = false;
    }

    private void PickDatabase(Site site, RoostConfig config)
    {
        var paths = Main.App.Paths;
        SiteDbName = SiteManager.DatabaseOf(site);
        var installed = new DbInstaller(paths).ListInstalled();
        try { _siteDb = new StackPlanner(paths, ProcessRunner.Default).ResolveDb(config, installed); }
        catch (RoostException) { _siteDb = null; }

        if (_siteDb is not { } db)
        {
            SiteDbMode = SiteDbMode.None;
            SiteDbWhere = "";
            SiteDbHint = installed.Count == 0 ? "No hay ninguna base de datos instalada: instala MariaDB o MySQL en Bases de datos."
                : config.DbEngine == RoostConfig.NoDb ? "Roost no arranca ninguna (está en «ninguna»): elige cuál usar en Bases de datos."
                : "La base de datos que pide roost.toml no está instalada: instálala o elige otra en Bases de datos.";
            (SiteDbChip, SiteDbTone) = ("", Tone.Neutral);
            SiteDbExists = null;
            return;
        }

        var status = Main.Stack.Status;
        var service = $"{db.Engine.Key()}-{db.Branch}";
        var running = status?.Running == true && status.Services.Any(s => s.Name == service && s.State == "Running");
        _siteDbPort = running ? status!.DbPort ?? config.DbPort : config.DbPort;
        SiteDbWhere = $"en {db.Label} · 127.0.0.1:{_siteDbPort}";
        if (!running)
        {
            SiteDbMode = SiteDbMode.Stopped;
            SiteDbHint = $"{db.Label} está parada: arranca Roost para crear o abrir {SiteDbName}.";
            (SiteDbChip, SiteDbTone) = ("parada", Tone.Off);
            SiteDbExists = null;
            return;
        }

        SiteDbMode = SiteDbMode.Running;
        ShowDbExists();
        if (_dbNames is null && !_dbNamesTried) _ = LoadDatabasesAsync();
    }

    private void ShowDbExists()
    {
        SiteDbExists = _dbNames is null ? null : _dbNames.Contains(SiteDbName, StringComparer.OrdinalIgnoreCase);
        (SiteDbChip, SiteDbTone, SiteDbHint) = SiteDbExists switch
        {
            true => ("existe", Tone.Ok, ""),
            false => ("aún no existe", Tone.Neutral, $"Aún no existe en {_siteDb?.Label}: Crear la deja lista (utf8mb4)."),
            null => ("", Tone.Neutral, ""),
        };
    }

    /// <summary>Las bases de datos del servidor en marcha, fuera del hilo de la UI. Si el cliente no contesta, se ofrece crear y abrir.</summary>
    private async Task LoadDatabasesAsync()
    {
        if (_dbNamesLoading || _siteDb is not { } db) return;
        _dbNamesLoading = true;
        _dbNamesTried = true;
        try
        {
            var ini = new StackPlanner(Main.App.Paths, ProcessRunner.Default).DbIniPath(db);
            _dbNames = await Task.Run(() => DbAdmin.List(db, ini, ProcessRunner.Default));
        }
        catch (RoostException)
        {
            _dbNames = null;
        }
        finally
        {
            _dbNamesLoading = false;
        }
        if (SiteDbRunning) ShowDbExists();
    }

    private (InstalledDb Db, string Ini) RequireSiteDb()
    {
        if (_siteDb is not { } db || !SiteDbRunning)
            throw new RoostException("La base de datos no está en marcha: pulsa Iniciar todo.");
        return (db, new StackPlanner(Main.App.Paths, ProcessRunner.Default).DbIniPath(db));
    }

    /// <summary>La línea del .env del proyecto: la base de datos del sitio con root (y su contraseña, si la tiene).</summary>
    public string SiteEnv()
    {
        string? password = null;
        try { password = _siteDb is null ? null : new DbPasswords(Main.App.Paths).For(_siteDb); } catch (RoostException) { }
        return $"DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT={_siteDbPort}\nDB_DATABASE={SiteDbName}\nDB_USERNAME={DbAdmin.RootUser}\nDB_PASSWORD={password}";
    }

    // Acciones

    [RelayCommand]
    private Task CreateSiteDbAsync() => Main.RunAsync($"No se pudo crear {SiteDbName}", async () =>
    {
        var (db, ini) = RequireSiteDb();
        var name = SiteDbName;
        await Task.Run(() => DbAdmin.Create(db, ini, name, ProcessRunner.Default));
        Main.Flash($"Base de datos {name} lista (utf8mb4)");
        ForgetDatabases();
        await LoadDatabasesAsync();
    });

    [RelayCommand]
    private Task OpenSiteDbConsoleAsync() => Main.RunAsync("No se pudo abrir la consola SQL", () =>
    {
        var (db, ini) = RequireSiteDb();
        var folder = Selected is { } row && Directory.Exists(row.Site.Folder) ? row.Site.Folder : Main.App.Paths.Www;
        Shell.OpenConsole(db.Client, DbAdmin.ConsoleArguments(ini, SiteDbName), folder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopySiteEnvAsync() => Selected is { } row ? Main.CopyAsync(SiteEnv(), $"Copiado el .env de {row.Host}") : Task.CompletedTask;

    [RelayCommand]
    private Task StartForDbAsync() => Main.StartStackAsync();

    [RelayCommand]
    private Task GoDatabasesAsync() => Main.NavigateToAsync(Main.Databases);
}
