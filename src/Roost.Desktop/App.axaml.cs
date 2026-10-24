using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

namespace Roost.Desktop;

/// <summary>
/// Arranque de la app: la ventana (oculta si se abre con <c>--minimized</c>, como al iniciar
/// sesión), el icono de la bandeja con su menú y el cierre a la bandeja.
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _main;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private string? _traySignature;

    /// <summary>La instancia única de esta sesión; la deja Program antes de arrancar Avalonia.</summary>
    public static SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var services = new AppServices();
            StartLog(services, desktop.Args);
            Relaunch = RelaunchApp;
            ApplyTheme(services.Prefs.Theme);
            _main = new MainViewModel(services);
            _window = new MainWindow { DataContext = _main, Icon = AppIcon.Window() };
            _window.Closing += OnWindowClosing;

            CreateTray();
            _main.StackChanged += (_, _) =>
            {
                UpdateTray();
                AlertServicesDown();
            };
            _main.Stack.PropertyChanged += (_, _) => UpdateTray();
            _main.Audit.PropertyChanged += (_, e) =>
            {
                UpdateTray();
                if (e.PropertyName == nameof(AuditState.Changes)) AlertAuditRed();
            };
            _main.Updates.PropertyChanged += (_, _) => UpdateTray();

            var args = StartupArgs.Parse(desktop.Args);
            if (!args.Minimized) _window.Show();
            Instance?.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _notifier.Dispose();
                services.Dispose();
                AppLog.Stop("Roost se cierra");
            };
            _ = StartupAsync(_main.InitializeAsync(), args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tras la primera carga: renovar el php de la terminal si es de otra versión y, si la app
    /// vuelve de una actualización (<c>--updated</c>, el marcador de <c>state/</c> o
    /// <c>--update-failed</c>), contarlo y arrancar el stack si tocaba. El shim va primero: el
    /// aviso espera a que se cierre, y con <c>--minimized</c> la ventana puede tardar en verse.
    /// </summary>
    private async Task StartupAsync(Task initialize, StartupArgs args)
    {
        try { await initialize; }
        catch (Exception) { /* la primera carga ya enseña sus propios errores */ }
        if (_main is null) return;
        try { await RenewShimsAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* se repite la próxima vez */ }
        try { await _main.Updates.HandleStartupAsync(_main, args); }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException) { /* no impide usar la app */ }
        await AfterRootChangeAsync();
    }

    /// <summary>
    /// J2: después de cambiar la raíz, la app se vuelve a abrir (todo lo que tenía abierto apuntaba a
    /// la de antes). Suelta su instancia única antes de lanzar la nueva, para que esa no la encuentre.
    /// </summary>
    public static Action? Relaunch { get; private set; }

    private void RelaunchApp()
    {
        if (_main is null || _desktop is null || Environment.ProcessPath is not { } exe) return;
        Instance?.Dispose();
        Instance = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        _main.ExitRequested = true;
        _window?.Close();
        _desktop.Shutdown();
    }

    /// <summary>J2: al abrirse tras cambiar la raíz, lo cuenta y arranca el stack si estaba en marcha.</summary>
    private async Task AfterRootChangeAsync()
    {
        if (_main is null || RootChangeMarker.Take(_main.App.Paths) is not { } marker) return;
        AppLog.Info($"Abierta con la raíz nueva ({marker.To}; antes, {marker.From})");
        _main.Flash($"Roost usa ahora {marker.To}");
        if (marker.StackWasRunning && !_main.Stack.IsRunning) await _main.StartStackAsync();
    }

    /// <summary>
    /// Si los shims están puestos y su <c>php.exe</c> no es el <c>roost-shim.exe</c> de esta
    /// versión (la app acaba de actualizarse), se vuelven a poner. Si una terminal lo tiene en
    /// uso, falla sin tocar nada y se repite la próxima vez.
    /// </summary>
    private async Task RenewShimsAsync()
    {
        if (_main is null) return;
        var paths = _main.App.Paths;
        var renewed = await Task.Run(() =>
        {
            var shims = new Roost.Core.Shims.ShimInstaller(paths);
            if (!shims.IsInstalled || !shims.IsOutdated(AppServices.AppDirectory)) return false;
            try
            {
                shims.Install(AppServices.AppDirectory);
                return true;
            }
            catch (RoostException)
            {
                return false;
            }
        });
        if (renewed) _main.Flash("El php de la terminal ya es el de esta versión");
    }

    /// <summary>
    /// J6: el log de la app (<c>logs/roost-desktop.log</c>). Además del arranque y los errores de
    /// los diálogos, lo que falla sin que nadie lo recoja y los avisos de Avalonia.
    /// </summary>
    private static void StartLog(AppServices services, string[]? args)
    {
        AppLog.Start(services.Paths, services.Version, args);
        Avalonia.Logging.Logger.Sink = new AppLog.AvaloniaSink(Avalonia.Logging.Logger.Sink);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Error sin recoger (la app se cierra)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Una tarea en segundo plano falló sin que nadie lo viera", e.Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => AppLog.Error("Error sin recoger en la ventana", e.Exception);
    }

    /// <summary>"system", "light" o "dark".</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is null || _main.ExitRequested) return;
        e.Cancel = true;
        if (!_main.App.Prefs.CloseToTray)
            _ = ExitAsync();
        else if (_main.App.Prefs.TrayHintShown)
            _window?.Hide();
        else
            _ = FirstHideToTrayAsync();
    }

    /// <summary>
    /// La primera vez que la X deja Roost en la bandeja, se dice dónde está: Windows 11 esconde los
    /// iconos nuevos tras la flecha ^ de la bandeja, y sin aviso parece que la app se ha cerrado.
    /// </summary>
    private async Task FirstHideToTrayAsync()
    {
        if (_main is null) return;
        var choice = await _main.ShowDialogAsync(new MessageDialogViewModel
        {
            Title = "Roost sigue en la bandeja",
            Subtitle = "Cerrar la ventana no cierra Roost: tus sitios se siguen sirviendo.",
            Text = "Está en la bandeja del sistema, junto al reloj. Si no ves el icono, está tras la flecha ^: "
                   + "arrástralo a la barra de tareas para tenerlo siempre a la vista. Para salir del todo, clic derecho "
                   + "en el icono → «Salir de Roost». En Ajustes puedes hacer que la X cierre la app.",
            Buttons =
            [
                new DialogButton("Salir de Roost", "exit", Left: true),
                new DialogButton("Entendido", "ok", "primary"),
            ],
        });
        _main.App.SavePrefs(_main.App.Prefs with { TrayHintShown = true });
        if (choice is "exit") await ExitAsync();
        else if (choice is "ok") _window?.Hide();
    }

    /// <summary>
    /// Salir de Roost. Si el stack está en marcha, se pregunta: puede seguir sirviendo los sitios
    /// sin la app (lo lleva roostd, como cuando se arranca con la CLI) o pararse antes de salir.
    /// </summary>
    private async Task ExitAsync()
    {
        if (_main is null || _desktop is null) return;
        await _main.Stack.RefreshAsync();
        if (_main.Stack.IsRunning)
        {
            ShowWindow();
            var choice = await _main.ShowDialogAsync(new MessageDialogViewModel
            {
                Title = "Roost sigue en marcha",
                Subtitle = "Tus sitios se siguen sirviendo aunque cierres la app.",
                Text = "Puedes pararlo todo antes de salir, o dejarlo en marcha y pararlo luego desde la CLI (roost down) o volviendo a abrir Roost."
                       + (_main.Sites.HasOwnShares ? " Lo que estás compartiendo desde la app deja de compartirse al salir, en los dos casos." : ""),
                Buttons =
                [
                    new DialogButton("Cancelar", null, Left: true),
                    new DialogButton("Salir y dejarlo en marcha", "keep"),
                    new DialogButton("Parar todo y salir", "stop", "primary"),
                ],
            });
            if (choice is null) return;
            // Compartir: lo que abrió la app deja de compartirse al salir (y quita su regla del cortafuegos).
            await _main.Sites.StopOwnSharesAsync();
            if (choice is "stop") await _main.Stack.StopAsync();
        }

        _main.ExitRequested = true;
        _window?.Close();
        _desktop.Shutdown();
    }

    // Bandeja

    private readonly TrayAlerts _alerts = new();
    private readonly ITrayNotifier _notifier = new TrayBalloon();
    private bool _trayReading;
    private bool _trayDirty;

    private void CreateTray()
    {
        _tray = new TrayIcon { ToolTipText = "Roost", Icon = AppIcon.Tray(null), IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    /// <summary>
    /// Rehace el menú de la bandeja, solo si ha cambiado algo de lo que enseña. Lo del disco se lee
    /// fuera del hilo de la UI (A16): si llega otro aviso mientras se lee, se vuelve a leer al acabar,
    /// una sola vez.
    /// </summary>
    private void UpdateTray()
    {
        if (_tray is null || _main is null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTray);
            return;
        }
        if (_trayReading)
        {
            _trayDirty = true;
            return;
        }
        _ = RefreshTrayAsync();
    }

    private async Task RefreshTrayAsync()
    {
        if (_tray is null || _main is null) return;
        _trayReading = true;
        try
        {
            do
            {
                _trayDirty = false;
                var main = _main;
                var ui = TraySnapshot.Capture(main);
                var paths = main.App.Paths;
                var snapshot = await Task.Run(() => TraySnapshot.Read(paths, ui,
                    version => main.Audit.Evaluate(ComponentKind.Php, version).Verdict == Roost.Core.Audit.AuditVerdict.Eol));
                if (snapshot.Signature == _traySignature) continue;
                _traySignature = snapshot.Signature;
                _tray.ToolTipText = $"Roost · {snapshot.Headline}";
                _tray.Icon = AppIcon.Tray(ui.Busy ? Color.Parse("#E29A00") : snapshot.Running ? Color.Parse("#22A05B") : null);
                _tray.Menu = BuildMenu(snapshot);
            }
            while (_trayDirty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("No se pudo rehacer el menú de la bandeja", ex);
        }
        finally
        {
            _trayReading = false;
        }
    }

    private NativeMenu BuildMenu(TraySnapshot snapshot)
    {
        var main = _main!;
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem($"Roost · {snapshot.Headline}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(snapshot.Ui.ToggleLabel, () => main.ToggleStackCommand.ExecuteAsync(null), enabled: !snapshot.Ui.Busy));
        menu.Add(Item($"Reiniciar {snapshot.WebLabel}", () => RestartWebAsync(snapshot), enabled: snapshot.Running && !snapshot.Ui.Busy));
        menu.Add(Item("Abrir Roost", () => { ShowWindow(); return Task.CompletedTask; }));
        menu.Add(Item($"Abrir {snapshot.WebUrl.TrimEnd('/')}", () => main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(snapshot.WebUrl); return Task.CompletedTask; })));
        menu.Add(Item("Nuevo sitio…", async () =>
        {
            ShowWindow();
            await main.NewSiteShortcutCommand.ExecuteAsync(null);
        }));
        menu.Add(Item("Registros", async () =>
        {
            ShowWindow();
            await main.LogsShortcutCommand.ExecuteAsync(null);
        }));

        if (snapshot.Sites.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(new NativeMenuItem("Sitios") { IsEnabled = false });
            foreach (var url in snapshot.Sites)
                menu.Add(Item(url, () => main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(url); return Task.CompletedTask; })));
        }

        menu.Add(new NativeMenuItemSeparator());
        // PHP global: cada instalación, con su versión exacta (J4). Elegir la mejor de una rama guarda la rama.
        var php = new NativeMenu();
        foreach (var install in snapshot.Php)
        {
            var item = Item(install.Label, () => main.UsePhpAsync(install.Spec));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = install.Name == snapshot.GlobalPhp;
            php.Add(item);
        }
        if (snapshot.Php.Count == 0) php.Add(new NativeMenuItem("Ningún PHP instalado") { IsEnabled = false });
        menu.Add(new NativeMenuItem($"PHP global{(snapshot.GlobalPhpLabel is { } g ? $" · {g}" : "")}") { Menu = php });

        // Servidor web: cambiarlo (J4) lleva a su pantalla, que instala el que falte con su diálogo.
        var web = new NativeMenu();
        foreach (var (server, label) in new[] { (RoostConfig.WebNginx, "nginx"), (RoostConfig.WebApache, snapshot.ApacheInstalled ? "Apache" : "Apache (se instala)") })
        {
            var item = Item(label, () => SwitchWebAsync(server));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = snapshot.WebServer == server;
            web.Add(item);
        }
        menu.Add(new NativeMenuItem($"Servidor web · {snapshot.WebLabel}") { Menu = web });

        var db = new NativeMenu();
        foreach (var (engine, branch, label) in snapshot.Databases)
        {
            var item = Item(label, () => main.UseDbAsync(engine, branch));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = label == snapshot.GlobalDb;
            db.Add(item);
        }
        var none = Item("Ninguna", () => main.UseDbAsync(null, null));
        none.ToggleType = MenuItemToggleType.Radio;
        none.IsChecked = snapshot.GlobalDb is null;
        db.Add(none);
        menu.Add(new NativeMenuItem($"Base de datos · {snapshot.GlobalDb ?? "ninguna"}") { Menu = db });

        menu.Add(Item("Abrir terminal", () => main.RunAsync("No se pudo abrir la terminal", () => { Shell.OpenTerminal(main.App.Paths); return Task.CompletedTask; })));
        menu.Add(Item("Abrir carpeta www", () => main.RunAsync("No se pudo abrir la carpeta", () => { Shell.OpenFolder(main.App.Paths.Www); return Task.CompletedTask; })));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item($"Auditoría: {snapshot.Ui.AuditSummary.ToLowerInvariant()}", async () =>
        {
            ShowWindow();
            await main.NavigateToAsync(main.AuditPage);
        }));
        // Abre el modal «Roost X disponible», el mismo que la barra lateral y Ajustes.
        if (snapshot.Ui.NewVersion is { } latest)
            menu.Add(Item($"Hay una Roost nueva: {latest}", async () =>
            {
                ShowWindow();
                await main.OpenUpdatesCommand.ExecuteAsync(null);
            }));
        menu.Add(Item("Salir de Roost", ExitAsync));
        return menu;
    }

    /// <summary>«Reiniciar nginx» (o Apache): solo el servidor web, como el botón de Inicio.</summary>
    private Task RestartWebAsync(TraySnapshot snapshot) => _main!.RunAsync($"No se pudo reiniciar {snapshot.WebLabel}", async () =>
    {
        var response = await _main.App.Daemon.RestartAsync(snapshot.WebService);
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await _main.Stack.RefreshAsync();
        _main.Flash($"{snapshot.WebLabel} reiniciado");
    });

    /// <summary>Cambiar de servidor web desde la bandeja: la pantalla Servidor web lo hace (instala, guarda y rearranca).</summary>
    private async Task SwitchWebAsync(string server)
    {
        if (_main is null) return;
        ShowWindow();
        await _main.NavigateToAsync(_main.Web);
        _main.Web.UseApache = server == RoostConfig.WebApache;
    }

    /// <summary>J4: un servicio que pasa a Failed avisa una vez (Ajustes › General lo apaga).</summary>
    private void AlertServicesDown()
    {
        if (_main is null) return;
        var alert = _alerts.ServicesDown(_main.Stack.Status);
        if (alert is null || !_main.App.Prefs.NotifyServiceDown) return;
        AppLog.Warn(alert.Title);
        _notifier.Show(alert, () => Dispatcher.UIThread.Post(async () =>
        {
            ShowWindow();
            await _main.NavigateToAsync(_main.Home);
        }));
    }

    /// <summary>K5: la auditoría con algo en rojo que no lo estaba en la anterior avisa una vez.</summary>
    private void AlertAuditRed()
    {
        if (_main is null || !_main.App.Prefs.NotifyAuditRed) return;
        if (TrayAlerts.AuditRed(_main.Audit.Changes) is not { } alert) return;
        _notifier.Show(alert, () => Dispatcher.UIThread.Post(async () =>
        {
            ShowWindow();
            await _main.NavigateToAsync(_main.AuditPage);
        }));
    }

    private static NativeMenuItem Item(string header, Func<Task> action, bool enabled = true)
    {
        var item = new NativeMenuItem(header) { IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        return item;
    }
}
