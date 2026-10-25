using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes: lo de la app (inicio, bandeja, tema) en state/desktop.json y lo del stack (puertos,
/// pool) en roost.toml, más rutas, la CA local, cómo se resuelven los .test y los avisos.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private bool _loading;

    public SettingsViewModel(MainViewModel main) : base(main)
    {
        Subtitle = $"Se guardan en {main.App.Paths.ConfigFile}, que también puedes editar a mano.";
        main.Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AuditState.Dataset) or nameof(AuditState.Origin))
            {
                OnPropertyChanged(nameof(DatasetText));
                OnPropertyChanged(nameof(DatasetSource));
            }
        };
        Version = main.App.Version;
    }

    public override string Title => "Ajustes";

    public string DatasetText => $"Datos de {Main.Audit.DatasetDateText} · se evalúan en local";

    /// <summary>De dónde salen los avisos (el feed firmado o los que van dentro de Roost).</summary>
    public string DatasetSource => Main.Audit.OriginText;

    /// <summary>Las extensiones cargadas y las que no, para «Acerca de».</summary>
    public string ExtensionsText
    {
        get
        {
            var set = Main.App.Extensions;
            if (set.Loaded.Count == 0 && set.Errors.Count == 0) return $"Extensiones: ninguna (se buscan en {Paths.Extensions})";
            var loaded = set.Loaded.Select(e => $"{e.Name} {e.Version}");
            var failed = set.Errors.Select(e => $"{e.Source} no carga ({e.Message})");
            return "Extensiones: " + string.Join(" · ", loaded.Concat(failed));
        }
    }

    public string Version { get; }

    public RoostPaths Paths => Main.App.Paths;

    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool ShimsEnabled { get; set; }
    [ObservableProperty] public partial string? ShimsWarning { get; private set; }
    [ObservableProperty] public partial bool StartStackOnLaunch { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;
    [ObservableProperty] public partial bool ThemeSystem { get; set; } = true;

    // La app en Linux (L2): sin anfitrión StatusNotifier no hay bandeja, y la X cierra la app aunque la casilla esté puesta.
    [ObservableProperty] public partial bool TrayUnavailable { get; private set; }
    [ObservableProperty] public partial string TrayHint { get; private set; } = "";

    /// <summary>«Abrir Roost al iniciar sesión»: en Windows, HKCU\Run; en Linux, ~/.config/autostart. En otros sistemas, nada.</summary>
    public bool CanAutostart => Autostart.Supported;
    [ObservableProperty] public partial bool ThemeLight { get; set; }
    [ObservableProperty] public partial bool ThemeDark { get; set; }

    // Ajustes y sistema (J4, K5): los avisos de la bandeja.
    [ObservableProperty] public partial bool NotifyServiceDown { get; set; } = true;
    [ObservableProperty] public partial bool NotifyAuditRed { get; set; } = true;

    /// <summary>Los atajos de la ventana (J5), para la tarjeta General.</summary>
    public string ShortcutsText => MainViewModel.ShortcutsText;

    [ObservableProperty] public partial string HttpText { get; set; } = "";
    [ObservableProperty] public partial string HttpsText { get; set; } = "";
    [ObservableProperty] public partial string PoolText { get; set; } = "";
    [ObservableProperty] public partial string? PortsError { get; private set; }

    [ObservableProperty] public partial string CacheSize { get; private set; } = "";
    [ObservableProperty] public partial string CaText { get; private set; } = "";
    [ObservableProperty] public partial bool CaTrusted { get; private set; }
    [ObservableProperty] public partial bool CaExists { get; private set; }
    [ObservableProperty] public partial string HostsFileText { get; private set; } = "";
    [ObservableProperty] public partial bool ResolverHosts { get; set; } = true;
    [ObservableProperty] public partial bool ResolverDns { get; set; }
    [ObservableProperty] public partial string DnsStateText { get; private set; } = "";

    public bool HasPortsError => PortsError is not null;
    public bool HasShimsWarning => ShimsWarning is not null;
    public string ShimsText => $"php en la terminal según la carpeta del proyecto (pone {Paths.Shims} en tu PATH)";
    public bool CaNotTrusted => !CaTrusted;

    partial void OnPortsErrorChanged(string? value) => OnPropertyChanged(nameof(HasPortsError));
    partial void OnCaTrustedChanged(bool value) => OnPropertyChanged(nameof(CaNotTrusted));

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _ = ToggleAsync("No se pudo cambiar el inicio con Windows", () => Autostart.Set(value),
            () => StartWithWindows = Autostart.IsEnabled());
    }

    /// <summary>
    /// Una casilla que hace algo en el sistema: tras intentarlo, enseña lo que de verdad ha
    /// quedado. Si falla, vuelve atrás en vez de quedarse marcada sin haber hecho nada.
    /// </summary>
    private async Task ToggleAsync(string error, Action action, Action showReal)
    {
        await Main.RunAsync(error, () => Task.Run(action));
        _loading = true;
        try { showReal(); }
        finally { _loading = false; }
    }

    partial void OnShimsWarningChanged(string? value) => OnPropertyChanged(nameof(HasShimsWarning));

    // Lo mismo que roost shims install / remove: el shim de php y su carpeta en el PATH del usuario.
    partial void OnShimsEnabledChanged(bool value)
    {
        if (_loading) return;
        var shims = new ShimInstaller(Paths);
        _ = ToggleAsync(value ? "No se pudieron poner los shims" : "No se pudieron quitar los shims", () =>
        {
            if (value)
            {
                shims.Install(AppServices.AppDirectory);
                var planner = new StackPlanner(Paths, ProcessRunner.Default);
                planner.EnsureUserIni();
                foreach (var php in new PhpInstaller(Paths).ListInstalled()) planner.WritePhpIni(php);
                UserPath.AddFirst(shims.Directory);
            }
            else
            {
                UserPath.Remove(shims.Directory);
                shims.Remove();
            }
        }, () =>
        {
            ShimsEnabled = shims.IsInstalled && UserPath.Contains(UserPath.Read(), shims.Directory);
            ShimsWarning = ShimsEnabled ? Shadowing() : null;
            if (value && ShimsEnabled) Main.Flash("php ya sigue la carpeta en las terminales nuevas");
        });
    }

    /// <summary>Un php.exe en el PATH del sistema gana al shim (Windows mira ese PATH antes que el del usuario). En Linux lo dice <c>roost shims status</c>.</summary>
    private static string? Shadowing()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var dir = machine.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "php.exe")));
        return dir is null ? null
            : $"{Path.Combine(dir, "php.exe")} está en el PATH del sistema, que Windows mira antes que el tuyo: ese php ganará al de Roost. Quítalo de ahí (hace falta ser administrador).";
    }

    partial void OnResolverDnsChanged(bool value)
    {
        if (_loading) return;
        _ = ChangeResolverAsync(value);
    }

    private async Task ChangeResolverAsync(bool dns)
    {
        var ask = dns
            ? Main.ConfirmAsync("Usar el DNS local para los .test",
                $"{OsTexts.WillAskAdmin} una sola vez, para {(OperatingSystem.IsWindows() ? "una regla" : "un drop-in de systemd-resolved")} que manda las preguntas de .test a {Roost.Core.Dns.LocalDns.EndPoint}. "
                + "Desde entonces, crear o quitar un sitio ya no lo pide. Los nombres los responde Roost mientras el stack está en marcha.", "Continuar")
            : Main.ConfirmAsync("Volver al fichero hosts",
                $"{OsTexts.WillAskAdmin} para quitar {(OperatingSystem.IsWindows() ? "la regla de DNS" : "el drop-in de systemd-resolved")} y, después, para escribir los sitios en el hosts.", "Continuar");
        var ok = await ask;
        if (ok)
            await Main.RunAsync("No se pudo cambiar cómo se resuelven los .test", () => Main.SetResolverAsync(dns));
        // Lo que de verdad ha quedado (si se canceló un permiso, sigue como estaba).
        await ShownAsync();
    }

    partial void OnStartStackOnLaunchChanged(bool value) => SavePrefs();
    partial void OnCloseToTrayChanged(bool value) => SavePrefs();
    partial void OnNotifyServiceDownChanged(bool value) => SavePrefs();
    partial void OnNotifyAuditRedChanged(bool value) => SavePrefs();
    partial void OnThemeSystemChanged(bool value) => Theme(value, "system");
    partial void OnThemeLightChanged(bool value) => Theme(value, "light");
    partial void OnThemeDarkChanged(bool value) => Theme(value, "dark");

    private void Theme(bool on, string theme)
    {
        if (!on || _loading) return;
        App.ApplyTheme(theme);
        SavePrefs();
    }

    private void SavePrefs()
    {
        if (_loading) return;
        var theme = ThemeLight ? "light" : ThemeDark ? "dark" : "system";
        Main.App.SavePrefs(Main.App.Prefs with
        {
            Theme = theme, CloseToTray = CloseToTray, StartStackOnLaunch = StartStackOnLaunch,
            NotifyServiceDown = NotifyServiceDown, NotifyAuditRed = NotifyAuditRed,
        });
        Main.NotifyPrefsChanged();
    }

    public override async Task ShownAsync()
    {
        _loading = true;
        try
        {
            var prefs = Main.App.Prefs;
            StartWithWindows = Autostart.Supported && Autostart.IsEnabled();
            TrayUnavailable = !TrayHost.Status.Available;
            TrayHint = TrayHost.Status.Reason is { } reason ? $"No hay bandeja en este escritorio, así que la X cierra Roost (el stack sigue en marcha). {reason}" : "";
            var shims = new ShimInstaller(Paths);
            ShimsEnabled = shims.IsInstalled && UserPath.Contains(UserPath.Read(), shims.Directory);
            ShimsWarning = ShimsEnabled ? Shadowing() : null;
            StartStackOnLaunch = prefs.StartStackOnLaunch;
            CloseToTray = prefs.CloseToTray;
            ThemeSystem = prefs.Theme is not ("light" or "dark");
            ThemeLight = prefs.Theme == "light";
            ThemeDark = prefs.Theme == "dark";
            NotifyServiceDown = prefs.NotifyServiceDown;
            NotifyAuditRed = prefs.NotifyAuditRed;
            LoadUpdatePrefs();

            RoostConfig config;
            try { config = RoostConfig.Load(Paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
            HttpText = config.HttpPort.ToString(CultureInfo.InvariantCulture);
            HttpsText = config.HttpsPort.ToString(CultureInfo.InvariantCulture);
            PoolText = config.PhpPool.ToString(CultureInfo.InvariantCulture);
            ResolverDns = config.UsesLocalDns;
            ResolverHosts = !config.UsesLocalDns;
            var rule = Roost.Core.Dns.LocalDns.IsRuleInstalled();
            var ruleName = OperatingSystem.IsWindows() ? "la regla de Windows" : "el drop-in de systemd-resolved";
            DnsStateText = config.UsesLocalDns
                ? rule ? $"En uso: {(OperatingSystem.IsWindows() ? "regla" : "drop-in")} .test → {Roost.Core.Dns.LocalDns.EndPoint} puesto." : $"En uso, pero falta {ruleName}: los .test no resolverán (vuelve a elegirlo)."
                : rule ? $"{char.ToUpperInvariant(ruleName[0])}{ruleName[1..]} sigue puesto, pero Roost usa el hosts." : "";
            PortsError = null;
        }
        finally
        {
            _loading = false;
        }

        CacheSize = FolderSize(Paths.Downloads);
        BuildCa();
        BuildFirefox();
        try
        {
            var hosts = new HostsUpdater();
            HostsFileText = hosts.IsSystemFile ? hosts.HostsFile : $"{hosts.HostsFile} (de pruebas, ROOST_HOSTS_FILE)";
        }
        catch (Exception ex) when (ex is IOException or RoostException) { HostsFileText = ""; }

        // Ajustes y sistema: los programas del equipo, roostd, el DNS local y la tarea al iniciar sesión.
        StackChanged();
        await LoadProgramsAsync();
        await LoadStackAtLogonAsync();
        await ProbeDnsAsync(force: false);
    }

    private void BuildCa()
    {
        var ca = new LocalCa(Paths);
        var info = ca.Describe();
        CaExists = info is not null;
        CaText = info is { } i ? $"{i.Subject} · ECDSA P-256 · hasta {i.NotAfter:yyyy}" : "Aún no creada: se crea con el primer sitio";
        try { CaTrusted = ca.IsTrusted(); } catch (Exception) { CaTrusted = false; }
    }

    private static string FolderSize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "vacía";
            var bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return bytes == 0 ? "vacía" : string.Format(Roost.Desktop.Services.Es.Culture, "{0:0.0} MB", bytes / 1048576.0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Acciones

    [RelayCommand]
    private Task OpenConfigAsync() => Main.RunAsync("No se pudo abrir roost.toml", () =>
    {
        // Si aún no existe, se escribe la plantilla comentada (como al usar roost php use).
        if (!File.Exists(Paths.ConfigFile))
        {
            var config = RoostConfig.Load(Paths.ConfigFile);
            RoostConfig.SetPhpPool(Paths.ConfigFile, config.PhpPool);
        }
        Shell.OpenFile(Paths.ConfigFile);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenFolderAsync(string folder) => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        Shell.OpenFolder(folder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (!await Main.ConfirmAsync("Vaciar la caché de descargas",
                $"Se borran los zips descargados ({CacheSize}) de {Paths.Downloads}. Lo instalado no se toca; si reinstalas algo, se vuelve a descargar.",
                "Vaciar", danger: true))
            return;
        await Main.RunAsync("No se pudo vaciar la caché", () =>
        {
            // Todo, también las carpetas y los .part- de una descarga cortada: el tamaño los cuenta.
            if (Directory.Exists(Paths.Downloads))
            {
                foreach (var file in Directory.EnumerateFiles(Paths.Downloads)) File.Delete(file);
                foreach (var dir in Directory.EnumerateDirectories(Paths.Downloads)) Directory.Delete(dir, recursive: true);
            }
            CacheSize = FolderSize(Paths.Downloads);
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private Task ApplyPortsAsync() => Main.RunAsync("No se pudieron guardar los puertos", async () =>
    {
        PortsError = null;
        if (!int.TryParse(HttpText, out var http) || !int.TryParse(HttpsText, out var https) || !int.TryParse(PoolText, out var pool))
        {
            PortsError = "Todos tienen que ser números.";
            return;
        }
        // Lo mismo que comprueba Servidor web, antes de escribir nada.
        if (http is < 1 or > 65535 || https is < 1 or > 65535)
        {
            PortsError = "Los puertos van del 1 al 65535.";
            return;
        }
        if (http == https)
        {
            PortsError = "El puerto HTTP y el HTTPS tienen que ser distintos.";
            return;
        }

        // El de la base de datos se cambia en su pantalla; aquí solo se evita pisarlo.
        RoostConfig current;
        try { current = RoostConfig.Load(Paths.ConfigFile); } catch (RoostException) { current = new RoostConfig(); }
        if (http == current.DbPort || https == current.DbPort)
        {
            PortsError = $"El {current.DbPort} es el de la base de datos (se cambia en Bases de datos).";
            return;
        }

        // Un puerto nuevo que ya tiene otro proceso: se dice aquí, antes de guardar, y no como un
        // arranque fallido con la configuración ya cambiada. Los que no cambian son de Roost.
        foreach (var (port, now, role) in new[] { (http, current.HttpPort, "HTTP"), (https, current.HttpsPort, "HTTPS") })
        {
            if (port == now || StackPlanner.BusyPorts([port]).Count == 0) continue;
            var owner = PortOwners.Find(port);
            PortsError = $"El puerto {role} {port} está ocupado por {owner?.ProcessName ?? "otro proceso"}{(owner is null ? "" : $" (PID {owner.Pid})")}. Elige otro o para ese programa.";
            return;
        }

        try
        {
            RoostConfig.SetWebPorts(Paths.ConfigFile, http, https);
            RoostConfig.SetPhpPool(Paths.ConfigFile, pool);
        }
        catch (RoostException ex)
        {
            PortsError = ex.Message;
            return;
        }
        var applied = await Main.ApplyAsync();
        Main.NotifyConfigChanged();
        if (applied) Main.Flash("Puertos guardados");
    });

    /// <summary>El puerto de la base de datos vive en su pantalla: aquí solo se remite.</summary>
    [RelayCommand]
    private Task GoDatabasesAsync() => Main.NavigateToAsync(Main.Databases);

    [RelayCommand]
    private Task ShowPortOwnersAsync()
    {
        int.TryParse(HttpText, out var http);
        int.TryParse(HttpsText, out var https);
        RoostConfig current;
        try { current = RoostConfig.Load(Paths.ConfigFile); } catch (RoostException) { current = new RoostConfig(); }
        var lines = new[] { (http, "HTTP"), (https, "HTTPS"), (current.DbPort, "base de datos") }
            .Select(p =>
            {
                if (StackPlanner.BusyPorts([p.Item1]).Count == 0) return $"{p.Item1} ({p.Item2}): libre";
                var owner = PortOwners.Find(p.Item1);
                return $"{p.Item1} ({p.Item2}): {owner?.ProcessName ?? "otro proceso"}{(owner is null ? "" : $" (PID {owner.Pid})")}";
            });
        return Main.InfoAsync("Quién ocupa cada puerto", "Los puertos que tiene configurados Roost, en 127.0.0.1:", detail: string.Join(Environment.NewLine, lines));
    }

    [RelayCommand]
    private Task RenewAllAsync() => Main.RunAsync("No se pudieron renovar los certificados", async () =>
    {
        var ca = new LocalCa(Paths);
        var sites = new SiteRegistry(Paths).Load();
        foreach (var site in sites) ca.EnsureSiteCert(site, force: true);
        await Main.ApplyAsync();
        Main.NotifyConfigChanged();
        Main.Flash($"{sites.Count} certificado{(sites.Count == 1 ? "" : "s")} renovado{(sites.Count == 1 ? "" : "s")}");
    });

    [RelayCommand]
    private async Task TrustAsync()
    {
        await Main.OfferTrustAsync(force: true);
        BuildCa();
    }

    /// <summary>Hay Firefox (algún perfil en profiles.ini): «Configurar Firefox» se puede pulsar.</summary>
    [ObservableProperty] public partial bool HasFirefox { get; private set; }

    [ObservableProperty] public partial string FirefoxTip { get; private set; } = "";

    private void BuildFirefox()
    {
        if (!OperatingSystem.IsWindows())
        {
            // En Linux la CA entra en cada perfil de Firefox con certutil al confiar (NssTrust): no hay user.js que tocar.
            HasFirefox = false;
            FirefoxTip = "En Linux, al confiar en la CA entra también en cada perfil de Firefox (con certutil): no hay nada que configurar.";
            return;
        }
        var profiles = FirefoxTrust.Profiles(FirefoxTrust.DefaultRoot());
        HasFirefox = profiles.Count > 0;
        var done = profiles.Count(FirefoxTrust.IsEnabled);
        FirefoxTip = profiles.Count == 0 ? "No se ha encontrado Firefox en este equipo"
            : done == profiles.Count ? "Firefox ya usa las CA de Windows: confía en la de Roost si confías en ella aquí"
            : "Firefox usa su propio almacén: con esto lee las CA de confianza de Windows (hay que reiniciarlo)";
    }

    [RelayCommand]
    private async Task ConfigureFirefoxAsync()
    {
        await Main.ConfigureFirefoxAsync();
        BuildFirefox();
    }

    [RelayCommand]
    private async Task UntrustAsync()
    {
        if (!await Main.ConfirmAsync("Dejar de confiar en la CA de Roost",
                OperatingSystem.IsWindows()
                    ? "Se quita de los certificados de confianza de tu usuario (Windows pedirá confirmarlo). El navegador volverá a avisar en tus sitios .test."
                    : $"Se quita de {OsTexts.TrustStore}. El navegador volverá a avisar en tus sitios .test.",
                "Dejar de confiar", danger: true))
            return;
        await Main.RunAsync("No se pudo quitar la CA", async () =>
        {
            await Task.Run(() =>
            {
                new LocalCa(Paths).Untrust();
                // Lo de Firefox (el user.js) lo puso Roost para esta CA en Windows: se quita también.
                if (OperatingSystem.IsWindows())
                    foreach (var profile in FirefoxTrust.Profiles(FirefoxTrust.DefaultRoot())) FirefoxTrust.Disable(profile);
            });
            Main.RefreshMachineState();
        });
        BuildCa();
        BuildFirefox();
    }

    [RelayCommand]
    private Task ShowHostsBlockAsync() => Main.RunAsync("No se pudo leer el hosts", () =>
    {
        var hosts = new HostsUpdater();
        var names = hosts.Current();
        var block = names.Count == 0 ? "(vacío: aún no hay sitios en el hosts)"
            : string.Join(Environment.NewLine, new[] { "# roost:begin" }.Concat(names.Select(n => $"127.0.0.1 {n}")).Append("# roost:end"));
        return Main.InfoAsync("El bloque de Roost en el hosts", $"En {hosts.HostsFile}. roost-helper solo toca estas líneas.", detail: block);
    });

    [RelayCommand]
    private Task OpenHelperLogAsync() => Main.OpenLogAsync(LogsViewModel.HelperLogKey);

    /// <summary>«Comprobar ahora»: pide el feed sin mirar la caché, vuelve a evaluar y dice qué ha pasado.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        var update = await Main.Audit.UpdateFeedAsync(force: true);
        await Main.Audit.ScanAsync();
        switch (update?.Status)
        {
            case FeedUpdateStatus.Updated:
                Main.Flash("Avisos actualizados: hay un feed nuevo");
                break;
            case FeedUpdateStatus.NotModified or FeedUpdateStatus.Fresh:
                Main.Flash("Los avisos ya estaban al día");
                break;
            case FeedUpdateStatus.Failed:
                await Main.InfoAsync("No se pudo comprobar el feed de avisos",
                    $"{update.Detail ?? "Sin detalle."} Sigue valiendo el último que había: {Main.Audit.OriginShort}.", Tone.Warn);
                break;
        }
    }

    [RelayCommand]
    private Task GoAuditAsync() => Main.NavigateToAsync(Main.AuditPage);

    [RelayCommand]
    private Task OpenRepoAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        Shell.OpenUrl("https://github.com/get-roost/roost");
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task ImportLaragonAsync() => Main.ShowWelcomeAsync(force: true);
}
