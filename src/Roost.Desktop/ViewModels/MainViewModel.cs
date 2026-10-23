using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una entrada de la barra lateral.</summary>
public sealed partial class NavItem(string label, Geometry? icon, PageViewModel page, System.Windows.Input.ICommand navigate) : ObservableObject
{
    public string Label { get; } = label;

    public System.Windows.Input.ICommand Navigate { get; } = navigate;

    public Geometry? Icon { get; } = icon;

    public PageViewModel Page { get; } = page;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>El contador rojo (Auditoría); 0 lo oculta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;
}

/// <summary>
/// La ventana: barra lateral, pantalla actual, barra de estado y diálogos. Pregunta al daemon
/// cada pocos segundos y avisa a las pantallas solo cuando cambia algo.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    // La app puede pasar días en la bandeja: cada pocas horas se vuelve a mirar (la caché de 24 h decide si va a la red).
    private readonly DispatcherTimer _updatePoll = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Stack<DialogViewModel> _dialogs = new();
    private string? _lastSignature;

    public MainViewModel(AppServices app)
    {
        App = app;
        Stack = new StackController(app);
        Audit = new AuditState(app);
        Updates = new UpdateState(app);

        Home = new HomeViewModel(this);
        Sites = new SitesViewModel(this);
        Php = new PhpViewModel(this);
        Web = new WebServerViewModel(this);
        Databases = new DatabasesViewModel(this);
        AuditPage = new AuditViewModel(this);
        Logs = new LogsViewModel(this);
        Settings = new SettingsViewModel(this);

        NavItem Nav(string label, string icon, PageViewModel page) =>
            new(label, Application.Current?.FindResource(icon) as Geometry, page, NavigateCommand);
        MainNav = [Nav("Inicio", "IcHome", Home), Nav("Sitios", "IcGlobe", Sites)];
        var components = new List<NavItem> { Nav("PHP", "IcCode", Php), Nav("Servidor web", "IcServer", Web), Nav("Bases de datos", "IcDatabase", Databases) };
        var roost = new List<NavItem> { Nav("Auditoría", "IcShieldCheck", AuditPage), Nav("Registros", "IcFile", Logs) };

        // Las pantallas de las extensiones van al final de su sección; Ajustes, siempre la última.
        var pages = new List<Extensibility.DesktopPage>();
        foreach (var extension in app.Extensions.Of<Extensibility.IDesktopExtension>())
            pages.AddRange(app.Extensions.Guard(extension, "sus pantallas", () => extension.Pages(this).ToList()) ?? []);
        foreach (var page in pages)
        {
            Geometry? icon = null;
            try { icon = StreamGeometry.Parse(page.IconPath); } catch (Exception) { /* sin icono */ }
            (page.Section == Extensibility.DesktopSection.Components ? components : roost).Add(new NavItem(page.Label, icon, page.Page, NavigateCommand));
        }
        ExtensionPages = pages;
        roost.Add(Nav("Ajustes", "IcSliders", Settings));
        ComponentNav = components;
        RoostNav = roost;

        Current = Home;
        MainNav[0].IsActive = true;

        Stack.PropertyChanged += OnStackPropertyChanged;
        Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuditState.Findings)) RoostNav[0].Badge = Audit.RedCount;
        };
        _poll.Tick += async (_, _) => await PollAsync();
        _updatePoll.Tick += async (_, _) => await Updates.AutoCheckAsync();
    }

    public AppServices App { get; }

    public StackController Stack { get; }

    public AuditState Audit { get; }

    /// <summary>Si hay una Roost nueva (el aviso de la barra lateral y de Ajustes).</summary>
    public UpdateState Updates { get; }

    public HomeViewModel Home { get; }
    public SitesViewModel Sites { get; }
    public PhpViewModel Php { get; }
    public WebServerViewModel Web { get; }
    public DatabasesViewModel Databases { get; }
    public AuditViewModel AuditPage { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> MainNav { get; }

    /// <summary>Las pantallas de las extensiones (MainWindow registra sus vistas).</summary>
    public IReadOnlyList<Extensibility.DesktopPage> ExtensionPages { get; }
    public IReadOnlyList<NavItem> ComponentNav { get; }
    public IReadOnlyList<NavItem> RoostNav { get; }

    private IEnumerable<NavItem> AllNav => MainNav.Concat(ComponentNav).Concat(RoostNav);

    [ObservableProperty]
    public partial PageViewModel Current { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial DialogViewModel? Dialog { get; private set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>La app se está cerrando de verdad (no a la bandeja).</summary>
    public bool ExitRequested { get; set; }

    // Barra de título, barra lateral y barra de estado

    public Tone RunTone => Stack.Busy ? Tone.Warn : Stack.IsRunning ? Tone.Ok : Tone.Off;

    public string RunText => Stack.Busy ? Stack.BusyText ?? "..." : Stack.IsRunning ? "En marcha" : "Parado";

    public string RunDetail => Stack.Busy ? Stack.BusyText ?? "..."
        : Stack.IsRunning ? $"En marcha · {Groups(Stack.Status!.Services)} servicios"
        : "Parado";

    public string ToggleLabel => Stack.Busy ? Stack.BusyText ?? "..." : Stack.IsRunning ? "Parar todo" : "Iniciar todo";

    public string DaemonText => Stack.Status is { } s ? $"roostd · PID {s.DaemonPid}" : "roostd parado";

    [ObservableProperty]
    public partial string CaText { get; private set; } = "CA local: ...";

    [ObservableProperty]
    public partial string HostsText { get; private set; } = "hosts: ...";

    public string RootText => $"Roost · {App.Paths.Root}";

    /// <summary>Servicios agrupados como los enseña la UI: cada pool de PHP cuenta como uno.</summary>
    public static int Groups(IEnumerable<Roost.Daemon.ServiceDto> services) =>
        services.Select(s => s.Name.StartsWith("php-", StringComparison.Ordinal) ? s.Name[..s.Name.LastIndexOf('-')] : s.Name).Distinct().Count();

    /// <summary>Primera carga: estado del daemon, sondeo, auditoría y, si se pidió, arrancar todo.</summary>
    public async Task InitializeAsync()
    {
        await Stack.RefreshAsync();
        RefreshMachineState();
        NotifyStackChanged();
        _poll.Start();
        _ = Audit.ScanAsync();
        _ = Updates.AutoCheckAsync();
        _updatePoll.Start();
        await Current.ShownAsync();
        if (App.Prefs.StartStackOnLaunch && !Stack.IsRunning)
            await StartStackAsync();
        await ShowWelcomeAsync();
    }

    /// <summary>CA de confianza y bloque del hosts: lo que la barra de estado cuenta de la máquina.</summary>
    public void RefreshMachineState()
    {
        try
        {
            var ca = new LocalCa(App.Paths);
            CaText = !File.Exists(ca.CaCertFile) ? "CA local: aún no creada"
                : ca.IsTrusted() ? "CA local: de confianza" : "CA local: sin confianza";
        }
        catch (Exception)
        {
            CaText = "CA local: desconocida";
        }

        try
        {
            if (Roost.Core.Config.RoostConfig.Load(App.Paths.ConfigFile).UsesLocalDns)
            {
                HostsText = Roost.Core.Dns.LocalDns.IsRuleInstalled() ? "DNS local" : "DNS local: falta la regla";
                return;
            }
            var hosts = new SiteRegistry(App.Paths).Load().Select(s => s.Host).ToList();
            HostsText = hosts.Count == 0 ? "hosts: sin sitios"
                : new HostsUpdater().InSync(hosts) ? $"hosts: {hosts.Count} sitio{(hosts.Count == 1 ? "" : "s")}"
                : "hosts: sin sincronizar";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException)
        {
            HostsText = "hosts: no se puede leer";
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(PageViewModel page)
    {
        if (Current != page) Current.Hidden();
        foreach (var item in AllNav) item.IsActive = item.Page == page;
        Current = page;
        await page.ShownAsync();
    }

    public Task NavigateToAsync(PageViewModel page) => NavigateAsync(page);

    /// <summary>El aviso de versión nueva lleva a Ajustes: allí están las novedades y cómo actualizar.</summary>
    [RelayCommand]
    private Task OpenUpdatesAsync() => NavigateAsync(Settings);

    /// <summary>Registros con un fichero ya elegido (el log de errores de nginx, el de un PHP…).</summary>
    public async Task OpenLogAsync(string fileName)
    {
        await NavigateAsync(Logs);
        await Logs.SelectAsync(fileName);
    }

    [RelayCommand]
    private async Task ToggleStackAsync()
    {
        if (Stack.Busy) return;
        if (Stack.IsRunning)
            await Stack.StopAsync();
        else
            await StartStackAsync();
    }

    /// <summary>Iniciar todo; si falla, el diálogo de arranque fallido con lo que se puede hacer.</summary>
    public async Task StartStackAsync()
    {
        var failure = await Stack.StartAsync();
        if (failure is not null)
            await ShowDialogAsync(new StartErrorDialogViewModel(this, failure));
    }

    /// <summary>Aplica un cambio de configuración si el stack está en marcha; si falla, el mismo diálogo.</summary>
    public async Task ApplyAsync()
    {
        var failure = await Stack.ApplyIfRunningAsync();
        if (failure is not null)
            await ShowDialogAsync(new StartErrorDialogViewModel(this, failure));
    }

    /// <summary>
    /// PHP global: guarda <paramref name="spec"/> en roost.toml (una rama, para que las versiones
    /// nuevas de esa rama entren solas, o una instalación concreta) y lo aplica si está en marcha.
    /// </summary>
    public Task UsePhpAsync(string spec) => RunAsync("No se pudo cambiar el PHP global", async () =>
    {
        var installed = new Roost.Core.Install.PhpInstaller(App.Paths).ListInstalled();
        if (Roost.Core.Install.PhpInstaller.Match(installed, spec).Count == 0)
            throw new RoostException($"No hay ningún PHP de Roost que case con {spec}.");
        Roost.Core.Config.RoostConfig.SetPhpVersion(App.Paths.ConfigFile, spec);
        await ApplyAsync();
        NotifyConfigChanged();
    });

    /// <summary>Base de datos global (<paramref name="engine"/> null = ninguna); se aplica si está en marcha.</summary>
    public Task UseDbAsync(Roost.Core.Install.DbEngine? engine, string? branch) => RunAsync("No se pudo cambiar la base de datos", async () =>
    {
        if (engine is { } e)
            Roost.Core.Config.RoostConfig.SetDb(App.Paths.ConfigFile, Roost.Core.Install.DbEngines.Key(e), branch);
        else
            Roost.Core.Config.RoostConfig.SetDb(App.Paths.ConfigFile, Roost.Core.Config.RoostConfig.NoDb, null);
        await ApplyAsync();
        NotifyConfigChanged();
    });

    /// <summary>
    /// Algo ha cambiado en disco (roost.toml, una instalación, un sitio): las pantallas y la
    /// bandeja se rehacen aunque el daemon cuente lo mismo.
    /// </summary>
    public void NotifyConfigChanged()
    {
        _lastSignature = null;
        RefreshMachineState();
        NotifyStackChanged();
    }

    /// <summary>
    /// Deja en el hosts los .test de todos los sitios. Con el hosts del sistema Windows pide
    /// permiso de administrador (roost-helper), y solo si el bloque cambia. Lanza si se cancela.
    /// </summary>
    public async Task SyncHostsAsync()
    {
        var changed = await Task.Run(() => new SiteManager(App.Paths).SyncHosts(AppServices.HelperExe));
        RefreshMachineState();
        if (changed) Flash("hosts actualizado");
    }

    /// <summary>
    /// Cambia cómo se resuelven los .test. Al DNS local: pone la regla NRPT (un UAC) y deja de usar
    /// el hosts. Al hosts: quita la regla (un UAC) y rellena el hosts con los sitios (otro UAC si
    /// cambia). Se aplica al stack si está en marcha. Lanza si se cancela un permiso.
    /// </summary>
    public async Task SetResolverAsync(bool localDns)
    {
        var file = App.Paths.ConfigFile;
        if (localDns)
        {
            if (Roost.Core.Dns.LocalDns.ForeignRule(Roost.Core.Dns.LocalDns.ReadRules()) is { } foreign && !Roost.Core.Dns.LocalDns.IsRuleInstalled())
                throw new RoostException($"Ya hay una regla de DNS para .test que no es de Roost (apunta a {foreign.Servers}). Quítala o sigue con el fichero hosts.");
            if (!Roost.Core.Dns.LocalDns.IsRuleInstalled())
                await Task.Run(() => Roost.Core.Dns.LocalDns.InstallRule(AppServices.HelperExe));
            Roost.Core.Config.RoostConfig.SetResolver(file, Roost.Core.Config.RoostConfig.ResolverDns);
        }
        else
        {
            if (Roost.Core.Dns.LocalDns.IsRuleInstalled())
                await Task.Run(() => Roost.Core.Dns.LocalDns.RemoveRule(AppServices.HelperExe));
            Roost.Core.Config.RoostConfig.SetResolver(file, Roost.Core.Config.RoostConfig.ResolverHosts);
            await SyncHostsAsync();
        }
        await ApplyAsync();
        NotifyConfigChanged();
        Flash(localDns ? "Los .test ya los responde el DNS local" : "Los .test vuelven a ir por el hosts");
    }

    private bool _trustOffered;

    /// <summary>
    /// Si la CA local existe y el navegador aún no confía en ella, ofrece hacerlo (una vez por
    /// sesión, salvo <paramref name="force"/>: el botón de Ajustes).
    /// </summary>
    public async Task OfferTrustAsync(bool force = false)
    {
        var ca = new LocalCa(App.Paths);
        if (!force && (_trustOffered || !File.Exists(ca.CaCertFile))) return;
        if (ca.IsTrusted()) return;
        _trustOffered = true;
        if (await ShowDialogAsync(new TrustCaDialogViewModel(ca)) is not true) return;
        await RunAsync("No se ha añadido la CA", async () =>
        {
            await Task.Run(() =>
            {
                ca.LoadOrCreate();
                ca.Trust();
            });
            RefreshMachineState();
            Flash("El navegador ya confía en tus sitios .test");
        });
    }

    /// <summary>El selector de carpetas del sistema (lo pone MainWindow); null si se cancela.</summary>
    public Avalonia.Platform.Storage.IStorageProvider? StorageProvider { get; set; }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (StorageProvider is null) return null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]);
    }

    /// <summary>
    /// La bienvenida: la primera vez que se abre Roost sin ningún PHP suyo, o desde Ajustes
    /// («Importar desde Laragon…»). Espera al escaneo para saber qué hay en la máquina.
    /// </summary>
    public async Task ShowWelcomeAsync(bool force = false)
    {
        if (!force && (App.Prefs.WelcomeDone || new Roost.Core.Install.PhpInstaller(App.Paths).ListInstalled().Count > 0)) return;
        if (Audit.LastScan is null) await Audit.ScanAsync();
        var welcome = new WelcomeDialogViewModel(this);
        _ = welcome.LoadAsync();
        var plan = await ShowDialogAsync(welcome) as WelcomePlan;
        App.SavePrefs(App.Prefs with { WelcomeDone = true });
        if (plan is not null) await RunWelcomeAsync(plan);
    }

    /// <summary>Hace lo decidido en la bienvenida, cada cosa con su diálogo: PHP, base de datos, sitios y arrancar.</summary>
    public async Task RunWelcomeAsync(WelcomePlan plan)
    {
        foreach (var build in plan.Php) await Php.InstallBuildAsync(build);
        if (plan.GlobalBranch is { } branch) await UsePhpAsync(branch);

        if (plan.Db is { } db && await Databases.InstallBuildAsync(db))
        {
            if (plan.DbPort is { } port) await RunAsync("No se pudo cambiar el puerto", () =>
            {
                Roost.Core.Config.RoostConfig.SetDbPort(App.Paths.ConfigFile, port);
                return Task.CompletedTask;
            });
            await UseDbAsync(db.Engine, db.Branch);
        }

        if (plan.Sites.Count > 0)
        {
            await RunAsync("No se pudieron registrar los sitios", async () =>
            {
                var sites = new SiteManager(App.Paths);
                foreach (var folder in plan.Sites) sites.Add(folder.Name, folder.Path);
                NotifyConfigChanged();
                await SyncHostsAsync();
            });
        }

        if (new Roost.Core.Install.PhpInstaller(App.Paths).ListInstalled().Count > 0 && !Stack.IsRunning)
            await StartStackAsync();
        NotifyConfigChanged();
        if (plan.Sites.Count > 0) await OfferTrustAsync();
        await NavigateToAsync(Home);
    }

    /// <summary>El diálogo «Guardar como» del sistema; null si se cancela.</summary>
    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<(string Name, string Extension)> types)
    {
        if (StorageProvider is null) return null;
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = types[0].Extension,
            FileTypeChoices = types.Select(t => new Avalonia.Platform.Storage.FilePickerFileType(t.Name) { Patterns = [$"*.{t.Extension}"] }).ToList(),
        });
        return file is null ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
    }

    /// <summary>El portapapeles de la ventana (lo pone MainWindow al montarse).</summary>
    public Avalonia.Input.Platform.IClipboard? Clipboard { get; set; }

    /// <summary>Un aviso breve en la barra de estado («Copiado», «Base de datos lista»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlash))]
    public partial string? FlashText { get; private set; }

    public bool HasFlash => FlashText is not null;

    private CancellationTokenSource? _flash;

    public void Flash(string text)
    {
        _flash?.Cancel();
        var cts = _flash = new CancellationTokenSource();
        FlashText = text;
        _ = Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(() => FlashText = null);
        }, TaskScheduler.Default);
    }

    /// <summary>Copia al portapapeles y lo dice en la barra de estado (<paramref name="what"/>, o el texto si es corto).</summary>
    public async Task CopyAsync(string text, string? what = null)
    {
        if (Clipboard is null) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, text);
        Flash(what ?? (text.Length <= 48 ? $"Copiado: {text}" : "Copiado"));
    }

    /// <summary>Abre un diálogo encima de lo que haya y espera a que se cierre.</summary>
    public async Task<object?> ShowDialogAsync(DialogViewModel dialog)
    {
        _dialogs.Push(dialog);
        Dialog = dialog;
        try
        {
            return await dialog.Closed;
        }
        finally
        {
            _dialogs.Pop();
            Dialog = _dialogs.Count > 0 ? _dialogs.Peek() : null;
        }
    }

    /// <summary>Aviso con un solo botón.</summary>
    public Task InfoAsync(string title, string text, Tone tone = Tone.Accent, string? detail = null) =>
        ShowDialogAsync(new MessageDialogViewModel
        {
            Title = title,
            Text = text,
            Detail = detail,
            Tone = tone,
            Buttons = [new DialogButton("Cerrar", null, "primary")],
        });

    /// <summary>Confirmación: true si se pulsa <paramref name="confirm"/>.</summary>
    public async Task<bool> ConfirmAsync(string title, string text, string confirm, bool danger = false, string? subtitle = null) =>
        await ShowDialogAsync(new MessageDialogViewModel
        {
            Title = title,
            Subtitle = subtitle,
            Text = text,
            Tone = danger ? Tone.Warn : Tone.Accent,
            Buttons = [new DialogButton("Cancelar", false), new DialogButton(confirm, true, danger ? "danger-fill" : "primary")],
        }) is true;

    /// <summary>Ejecuta una acción de la UI; si lanza un error para el usuario, lo enseña en un diálogo.</summary>
    public async Task RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            await InfoAsync(what, ex.Message, Tone.Bad);
        }
    }

    private async Task PollAsync()
    {
        await Stack.RefreshAsync();
        NotifyStackChanged();
    }

    private void OnStackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RunTone));
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(RunDetail));
        OnPropertyChanged(nameof(DaemonText));
        OnPropertyChanged(nameof(ToggleLabel));
        if (e.PropertyName == nameof(StackController.Status)) NotifyStackChanged();
    }

    private void NotifyStackChanged()
    {
        // El sondeo devuelve un objeto nuevo cada vez: solo se avisa si cambia el contenido.
        var signature = Stack.Status is { } s ? System.Text.Json.JsonSerializer.Serialize(s) : "";
        if (signature == _lastSignature) return;
        _lastSignature = signature;
        foreach (var item in AllNav) item.Page.StackChanged();
        StackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cambió lo que cuenta el daemon (para la bandeja).</summary>
    public event EventHandler? StackChanged;
}
