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
            ApplyTheme(services.Prefs.Theme);
            _main = new MainViewModel(services);
            _window = new MainWindow { DataContext = _main, Icon = AppIcon.Window() };
            _window.Closing += OnWindowClosing;

            CreateTray();
            _main.StackChanged += (_, _) => UpdateTray();
            _main.Stack.PropertyChanged += (_, _) => UpdateTray();
            _main.Audit.PropertyChanged += (_, _) => UpdateTray();
            _main.Updates.PropertyChanged += (_, _) => UpdateTray();

            if (!desktop.Args?.Contains("--minimized") ?? true) _window.Show();
            Instance?.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                services.Dispose();
            };
            _ = _main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
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
                Text = "Puedes pararlo todo antes de salir, o dejarlo en marcha y pararlo luego desde la CLI (roost down) o volviendo a abrir Roost.",
                Buttons =
                [
                    new DialogButton("Cancelar", null, Left: true),
                    new DialogButton("Salir y dejarlo en marcha", "keep"),
                    new DialogButton("Parar todo y salir", "stop", "primary"),
                ],
            });
            if (choice is null) return;
            if (choice is "stop") await _main.Stack.StopAsync();
        }

        _main.ExitRequested = true;
        _window?.Close();
        _desktop.Shutdown();
    }

    // Bandeja

    private void CreateTray()
    {
        _tray = new TrayIcon { ToolTipText = "Roost", Icon = AppIcon.Tray(null), IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    /// <summary>Rehace el menú de la bandeja, solo si ha cambiado algo de lo que enseña.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _main is null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTray);
            return;
        }

        var snapshot = TraySnapshot.Read(_main);
        var signature = snapshot.Signature;
        if (signature == _traySignature) return;
        _traySignature = signature;

        _tray.ToolTipText = $"Roost · {snapshot.Headline}";
        _tray.Icon = AppIcon.Tray(_main.Stack.Busy ? Color.Parse("#E29A00") : _main.Stack.IsRunning ? Color.Parse("#22A05B") : null);
        _tray.Menu = BuildMenu(snapshot);
    }

    private NativeMenu BuildMenu(TraySnapshot snapshot)
    {
        var main = _main!;
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem($"Roost · {snapshot.Headline}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(main.ToggleLabel, () => main.ToggleStackCommand.ExecuteAsync(null), enabled: !main.Stack.Busy));
        menu.Add(Item("Abrir Roost", () => { ShowWindow(); return Task.CompletedTask; }));

        if (snapshot.Sites.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(new NativeMenuItem("Sitios") { IsEnabled = false });
            foreach (var url in snapshot.Sites)
                menu.Add(Item(url, () => main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(url); return Task.CompletedTask; })));
        }

        menu.Add(new NativeMenuItemSeparator());
        var php = new NativeMenu();
        foreach (var (branch, label, eol) in snapshot.PhpBranches)
        {
            var item = Item(eol ? $"{label}  (EOL)" : label, () => main.UsePhpAsync(branch));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = branch == snapshot.GlobalPhpBranch;
            php.Add(item);
        }
        if (snapshot.PhpBranches.Count == 0) php.Add(new NativeMenuItem("Ningún PHP instalado") { IsEnabled = false });
        menu.Add(new NativeMenuItem($"PHP global{(snapshot.GlobalPhpBranch is { } g ? $" · {g}" : "")}") { Menu = php });

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
        menu.Add(Item($"Auditoría: {main.Audit.Summary.ToLowerInvariant()}", async () =>
        {
            ShowWindow();
            await main.NavigateToAsync(main.AuditPage);
        }));
        if (main.Updates.HasUpdate)
            menu.Add(Item($"Hay una Roost nueva: {main.Updates.LatestVersion}", async () =>
            {
                ShowWindow();
                await main.NavigateToAsync(main.Settings);
            }));
        menu.Add(Item("Salir de Roost", ExitAsync));
        return menu;
    }

    private static NativeMenuItem Item(string header, Func<Task> action, bool enabled = true)
    {
        var item = new NativeMenuItem(header) { IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>Lo que enseña la bandeja, leído de una vez; su firma dice si hay que rehacer el menú.</summary>
    private sealed record TraySnapshot(
        string Headline,
        IReadOnlyList<string> Sites,
        IReadOnlyList<(string Branch, string Label, bool Eol)> PhpBranches,
        string? GlobalPhpBranch,
        IReadOnlyList<(DbEngine Engine, string Branch, string Label)> Databases,
        string? GlobalDb,
        string Extra)
    {
        public string Signature =>
            string.Join("|", Headline, string.Join(",", Sites), string.Join(",", PhpBranches), GlobalPhpBranch,
                string.Join(",", Databases.Select(d => d.Label)), GlobalDb, Extra);

        public static TraySnapshot Read(MainViewModel main)
        {
            var paths = main.App.Paths;
            RoostConfig config;
            try { config = RoostConfig.Load(paths.ConfigFile); }
            catch (RoostException) { config = new RoostConfig(); }

            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var installedPhp = new PhpInstaller(paths).ListInstalled();
            string? globalPhp = null;
            try { globalPhp = HomeViewModel.Branch(planner.ResolvePhp(config, installedPhp).Name); } catch (RoostException) { }

            var branches = installedPhp
                .Where(p => p.Version is not null)
                .GroupBy(p => HomeViewModel.Branch(p.Name))
                .Select(g =>
                {
                    var newest = g.Max(p => p.Version!.Value);
                    var eol = main.Audit.Evaluate(ComponentKind.Php, newest).Verdict == Roost.Core.Audit.AuditVerdict.Eol;
                    return (Branch: g.Key, Label: newest.ToString(), Eol: eol, Version: newest);
                })
                .OrderByDescending(b => b.Version)
                .Select(b => (b.Branch, b.Label, b.Eol))
                .ToList();

            var installedDb = new DbInstaller(paths).ListInstalled();
            string? globalDb = null;
            try
            {
                if (planner.ResolveDb(config, installedDb) is { } db) globalDb = $"{db.Engine.DisplayName()} {db.Branch}";
            }
            catch (RoostException) { }
            var databases = installedDb
                .Where(d => d.Branch is not null)
                .Select(d => (d.Engine, Branch: d.Branch!, Label: $"{d.Engine.DisplayName()} {d.Branch}"))
                .Distinct()
                .ToList();

            IReadOnlyList<string> sites = [];
            try { sites = new SiteRegistry(paths).Load().Select(s => StackPlanner.SiteUrl(s, config.HttpsPort).TrimEnd('/')).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException) { }

            var stack = main.Stack;
            var headline = stack.Busy ? stack.BusyText ?? "..."
                : stack.IsRunning ? $"En marcha · PHP {globalPhp ?? "?"} · {globalDb ?? "sin base de datos"}"
                : "Parado";
            return new TraySnapshot(headline, sites, branches, globalPhp, databases, globalDb,
                $"{main.ToggleLabel}|{main.Audit.Summary}|{(main.Updates.HasUpdate ? main.Updates.LatestVersion : "")}");
        }
    }
}
