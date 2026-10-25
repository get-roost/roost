using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Servidor web: el que arranca (nginx o Apache, <c>[web] server</c>), sus puertos y, en una
/// pestaña cada uno, lo instalado con su configuración y el catálogo. Los dos sirven los mismos
/// PHP (los pools de php-cgi); Apache lee además los <c>.htaccess</c>.
/// </summary>
public sealed partial class WebServerViewModel : PageViewModel
{
    private RoostConfig _config = new();
    private InstalledNginx? _nginx;
    private InstalledApache? _apache;
    private bool _loading;

    public WebServerViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "Un solo servidor atiende localhost y los sitios .test. Puedes tener los dos instalados y elegir cuál arranca.";
    }

    public override string Title => "Servidor web";

    [ObservableProperty] public partial string HttpText { get; set; } = "";
    [ObservableProperty] public partial string HttpsText { get; set; } = "";
    [ObservableProperty] public partial string HttpChip { get; private set; } = "";
    [ObservableProperty] public partial Tone HttpTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial string HttpsChip { get; private set; } = "";
    [ObservableProperty] public partial Tone HttpsTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial bool CanApplyPorts { get; private set; }

    [ObservableProperty] public partial bool NginxInstalled { get; private set; }
    [ObservableProperty] public partial bool NginxRunning { get; private set; }
    [ObservableProperty] public partial string NginxState { get; private set; } = "";
    [ObservableProperty] public partial Tone NginxStateTone { get; private set; } = Tone.Off;
    [ObservableProperty] public partial string NginxVersion { get; private set; } = NginxCatalog.Current.Version;
    [ObservableProperty] public partial string NginxFolder { get; private set; } = "";
    [ObservableProperty] public partial string Listen { get; private set; } = "";
    [ObservableProperty] public partial string Modules { get; private set; } = "";
    [ObservableProperty] public partial string? TestOutput { get; private set; }
    [ObservableProperty] public partial Tone TestTone { get; private set; } = Tone.Ok;

    [ObservableProperty] public partial bool ApacheInstalled { get; private set; }
    [ObservableProperty] public partial bool ApacheRunning { get; private set; }
    [ObservableProperty] public partial string ApacheState { get; private set; } = "";
    [ObservableProperty] public partial Tone ApacheStateTone { get; private set; } = Tone.Off;
    [ObservableProperty] public partial string ApacheFolder { get; private set; } = "";
    [ObservableProperty] public partial string? ApacheTestOutput { get; private set; }
    [ObservableProperty] public partial Tone ApacheTestTone { get; private set; } = Tone.Ok;

    /// <summary>El radio «nginx» de Servidor activo (el otro es <see cref="UseApache"/>).</summary>
    [ObservableProperty] public partial bool UseNginx { get; set; } = true;
    [ObservableProperty] public partial bool UseApache { get; set; }

    /// <summary>La del Apache que hay (en Linux, el de la distro dice la suya) o, sin él, la del catálogo.</summary>
    public string ApacheVersion => _apache?.Version ?? ApacheCatalog.Current.Version;
    // Lo que cambia con el sistema: en Windows, nginx de nginx.org, Apache de Apache Lounge y PHP por php-cgi;
    // en Linux, nginx de get-roost/roost-builds, el apache2 de la distro (no se descarga) y PHP por php-fpm.
    private static bool DistroApache => ApacheCatalog.Current.IsDistro;
    public string NginxBlurb => $"Rápido y ligero. PHP NTS a través de los pools de {OsTexts.PhpWorker}";
    public string ApacheBlurb => "Lee los .htaccess (WordPress, Laravel...). Los mismos PHP, y cada sitio con el suyo" + (DistroApache ? ". Solo puertos desde el 1024" : "");
    public string NginxSource => OsTexts.Windows ? "nginx.org" : "get-roost/roost-builds";
    public string NginxVerified => OsTexts.Windows ? "SHA-256 y firma PGP verificados" : "SHA-256 fijado y attestation";
    public string NginxSourceNote => OsTexts.Windows
        ? "nginx.org no publica checksums: Roost solo instala versiones cuya firma PGP se ha verificado, y cada versión nueva llega con una actualización de Roost."
        : "El tar.gz estático de get-roost/roost-builds, con su SHA-256 fijado en Roost y su attestation; cada versión nueva llega con una actualización de Roost.";
    public string ApacheOrigin => DistroApache ? "de la distro" : "Apache Lounge · Win64";
    public string ApacheVerified => DistroApache ? "paquete del sistema" : "SHA-256 y firma PGP verificados";
    public string ApachePhp => $"Por mod_proxy_fcgi a los mismos pools de {OsTexts.PhpWorker} que nginx: el PHP NTS de siempre, y cada sitio con su versión (sin mod_php ni builds TS).";
    public string ApacheSource => DistroApache ? "la distro" : "Apache Lounge";
    public string ApacheAvailableHint => DistroApache ? "el apache2 de la distro" : "Win64 · la recomendada";
    public string ApacheNotInstalled => DistroApache
        ? $"Apache no está en el sistema, y Roost usa el de la distro (no instala paquetes del sistema). Instálalo con: {LinuxApache.InstallHint}."
        : "Apache aún no está instalado: se instala al elegirlo arriba como servidor activo, o desde Disponibles.";
    public string ApacheSourceNote => DistroApache
        ? "Roost no descarga Apache: usa el apache2 de la distro, lanzado con tus permisos y su propia configuración (no la de /etc/apache2). Como usuario, solo puertos de 1024 en adelante."
        : $"Apache no publica binarios para Windows; los de Apache Lounge son el estándar de hecho. Roost solo instala versiones cuya firma PGP se ha verificado. Necesitan el {ApacheCatalog.Current.Redistributable}.";
    public string ApacheTestLabel => $"Validar configuración ({(_apache?.IsDistro == true ? Path.GetFileName(_apache.Executable) : "httpd")} -t)";
    public string NginxConfPath => Path.Combine("etc", "nginx", "nginx.conf");
    public string NginxSitesPath => Path.Combine("etc", "nginx", "sites", "*.conf");
    public string NginxErrorLogPath => Path.Combine("logs", "nginx-error.log");
    public string ApacheConfPath => Path.Combine("etc", "apache", "httpd.conf");
    public string ApacheSitesPath => Path.Combine("etc", "apache", "sites", "*.conf");
    public string ApacheErrorLogPath => Path.Combine("logs", "apache-error.log");
    public string ApacheModules => string.Join(", ", ApacheConf.Modules.Where(m => m is "rewrite" or "ssl" or "http2" or "proxy_fcgi" or "headers" or "expires" or "deflate"))
                                   + $" y {ApacheConf.Modules.Count - 7} más";
    public bool HasApacheTestOutput => ApacheTestOutput is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApache))]
    public partial bool ShowNginx { get; set; } = true;

    public bool ShowApache { get => !ShowNginx; set => ShowNginx = !value; }

    public bool HasTestOutput => TestOutput is not null;

    public string CatalogVersion => NginxCatalog.Current.Version;

    partial void OnHttpTextChanged(string value) => CheckPorts();
    partial void OnHttpsTextChanged(string value) => CheckPorts();
    partial void OnTestOutputChanged(string? value) => OnPropertyChanged(nameof(HasTestOutput));
    partial void OnApacheTestOutputChanged(string? value) => OnPropertyChanged(nameof(HasApacheTestOutput));

    partial void OnUseApacheChanged(bool value)
    {
        if (_loading || value == _config.UsesApache) return;
        _ = SwitchServerAsync(value ? RoostConfig.WebApache : RoostConfig.WebNginx);
    }

    public override Task ShownAsync()
    {
        Build();
        return Task.CompletedTask;
    }

    public override void StackChanged() => Build();

    private void Build()
    {
        var paths = Main.App.Paths;
        var status = Main.Stack.Status;
        try { _config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { _config = new RoostConfig(); }
        _nginx = new NginxInstaller(paths).Find(NginxCatalog.Current);
        _apache = new ApacheInstaller(paths).Find(ApacheCatalog.Current);

        _loading = true;
        UseApache = _config.UsesApache;
        UseNginx = !_config.UsesApache;
        _loading = false;

        NginxInstalled = _nginx is not null;
        NginxRunning = status?.Running == true && status.Services.Any(s => s.Name == "nginx" && s.State == "Running");
        (NginxState, NginxStateTone) = NginxRunning ? ("en marcha", Tone.Ok)
            : !NginxInstalled ? (_config.UsesApache ? ("sin instalar", Tone.Neutral) : ("se instala al iniciar", Tone.Neutral))
            : _config.UsesApache ? ("instalado", Tone.Neutral) : ("parado", Tone.Neutral);

        ApacheInstalled = _apache is not null;
        ApacheRunning = status?.Running == true && status.Services.Any(s => s.Name == "apache" && s.State == "Running");
        // En Linux es el Apache de la distro: Roost no lo instala, se pone con el gestor de paquetes.
        (ApacheState, ApacheStateTone) = ApacheRunning ? ("en marcha", Tone.Ok)
            : !ApacheInstalled ? (ApacheCatalog.Current.IsDistro ? ("no está en el sistema", Tone.Neutral) : ("se instala al elegirlo", Tone.Neutral))
            : _config.UsesApache ? ("parado", Tone.Neutral) : ("instalado", Tone.Neutral);
        OnPropertyChanged(nameof(ApacheVersion));
        OnPropertyChanged(nameof(ApacheTestLabel));
        ApacheFolder = _apache?.Directory ?? Path.Combine(paths.ApacheBin, ApacheCatalog.Current.Version);
        if (!_tabChosen && ShowNginx == _config.UsesApache)
        {
            _loading = true;
            ShowNginx = !_config.UsesApache;
            _loading = false;
        }
        NginxVersion = _nginx?.Version ?? NginxCatalog.Current.Version;
        NginxFolder = _nginx?.Directory ?? Path.Combine(paths.NginxBin, NginxCatalog.Current.Version);
        var hasSites = SafeSites().Count > 0;
        Listen = hasSites
            ? $"127.0.0.1:{_config.HttpPort} · 127.0.0.1:{_config.HttpsPort} (solo este equipo)"
            : $"127.0.0.1:{_config.HttpPort} (solo este equipo; el {_config.HttpsPort} se abre con el primer sitio)";
        if (_nginx is not null && Modules.Length == 0) _ = LoadModulesAsync(_nginx);

        var current = (_config.HttpPort.ToString(CultureInfo.InvariantCulture), _config.HttpsPort.ToString(CultureInfo.InvariantCulture));
        if (HttpText.Length == 0) HttpText = current.Item1;
        if (HttpsText.Length == 0) HttpsText = current.Item2;
        CheckPorts();
    }

    // La pestaña sigue al servidor activo hasta que el usuario elige otra.
    private bool _tabChosen;

    partial void OnShowNginxChanged(bool value)
    {
        if (!_loading) _tabChosen = true;
    }

    private async Task LoadModulesAsync(InstalledNginx nginx)
    {
        // nginx -V: versión de OpenSSL y módulos con los que se compiló.
        var output = await Task.Run(() => ProcessRunner.Default.Run(nginx.Executable, "-V", TimeSpan.FromSeconds(10)));
        if (output is null) return;
        var parts = new List<string>();
        if (output.Contains("--with-http_ssl_module", StringComparison.Ordinal)) parts.Add("SSL");
        if (output.Contains("--with-http_v2_module", StringComparison.Ordinal)) parts.Add("HTTP/2");
        if (output.Contains("--with-http_v3_module", StringComparison.Ordinal)) parts.Add("HTTP/3");
        var openssl = Regex.Match(output, @"OpenSSL (\d+\.\d+(\.\d+)?)");
        Modules = string.Join(", ", parts) + (openssl.Success ? $" · OpenSSL {openssl.Groups[1].Value}" : "");
    }

    private void CheckPorts()
    {
        CanApplyPorts = false;
        var http = Check(HttpText, _config.HttpPort, out var httpOk);
        var https = Check(HttpsText, _config.HttpsPort, out var httpsOk);
        (HttpChip, HttpTone) = http;
        (HttpsChip, HttpsTone) = https;
        if (!httpOk || !httpsOk) return;
        if (HttpText == HttpsText)
        {
            (HttpsChip, HttpsTone) = ("igual que HTTP", Tone.Bad);
            return;
        }
        CanApplyPorts = HttpText != _config.HttpPort.ToString(CultureInfo.InvariantCulture)
                        || HttpsText != _config.HttpsPort.ToString(CultureInfo.InvariantCulture);
    }

    private (string, Tone) Check(string text, int current, out bool ok)
    {
        ok = false;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return ("no válido", Tone.Bad);
        if (port == _config.DbPort)
            return ("es de la base de datos", Tone.Bad);
        if (port == current && (NginxRunning || ApacheRunning))
        {
            ok = true;
            return ("en uso por Roost", Tone.Ok);
        }
        if (StackPlanner.BusyPorts([port]).Count > 0)
        {
            var owner = PortOwners.Find(port);
            return (owner?.ProcessName is { } name ? $"ocupado: {name}" : "ocupado", Tone.Bad);
        }
        ok = true;
        return ("libre", Tone.Ok);
    }

    private IReadOnlyList<Site> SafeSites()
    {
        try { return new SiteRegistry(Main.App.Paths).Load(); }
        catch (Exception ex) when (ex is IOException or RoostException) { return []; }
    }

    // Acciones

    [RelayCommand]
    private Task ApplyPortsAsync() => Main.RunAsync("No se pudieron cambiar los puertos", async () =>
    {
        var http = int.Parse(HttpText, CultureInfo.InvariantCulture);
        var https = int.Parse(HttpsText, CultureInfo.InvariantCulture);
        RoostConfig.SetWebPorts(Main.App.Paths.ConfigFile, http, https);
        await Main.ApplyAsync();
        Main.NotifyConfigChanged();
    });

    [RelayCommand]
    private Task TestConfigAsync() => Main.RunAsync("No se pudo validar la configuración", async () =>
    {
        if (_nginx is not { } nginx) throw new RoostException("nginx no está instalado: se instala solo al pulsar Iniciar todo.");
        var planner = new StackPlanner(Main.App.Paths, ProcessRunner.Default);
        if (!File.Exists(planner.NginxConfPath))
            throw new RoostException("Aún no hay configuración de nginx: se genera al pulsar Iniciar todo.");
        var output = await Task.Run(() => planner.TestNginx(nginx));
        TestOutput = output?.Trim() ?? "nginx -t no respondió.";
        TestTone = output?.Contains("test is successful", StringComparison.OrdinalIgnoreCase) == true ? Tone.Ok : Tone.Bad;
    });

    /// <summary>
    /// «Recargar»: el daemon valida la configuración (el mismo <c>-t</c> que «Validar») y el
    /// servidor web la lee otra vez en caliente, sin cortar las conexiones (<c>POST /reload</c>). Si
    /// no vale, sigue con la que tenía y el diálogo dice por qué. Sirve para lo editado a mano
    /// fuera de las marcas de un vhost.
    /// </summary>
    private async Task ReloadWebAsync(string server, string label)
    {
        var response = await Main.App.Daemon.ReloadAsync(server);
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
        var done = response.Applied.FirstOrDefault() ?? "";
        Main.Flash(done.StartsWith("recargado", StringComparison.Ordinal) ? $"{label} recargado sin cortar"
            : done.StartsWith("arrancado", StringComparison.Ordinal) ? $"{label} arrancado"
            : $"{label} reiniciado (no se pudo recargar en caliente)");
    }

    [RelayCommand]
    private Task ReloadAsync() => Main.RunAsync("No se pudo recargar nginx", () => ReloadWebAsync(RoostConfig.WebNginx, "nginx"));

    [RelayCommand]
    private Task OpenConfAsync() => Main.RunAsync("No se pudo abrir nginx.conf", () =>
    {
        var conf = new StackPlanner(Main.App.Paths, ProcessRunner.Default).NginxConfPath;
        if (!File.Exists(conf)) throw new RoostException("Aún no hay nginx.conf: se genera al pulsar Iniciar todo.");
        Shell.OpenFile(conf);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenFolderAsync() => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        Shell.OpenFolder(NginxFolder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task GoSitesAsync() => Main.NavigateToAsync(Main.Sites);

    [RelayCommand]
    private Task GoErrorLogAsync() => Main.OpenLogAsync("nginx-error.log");

    /// <summary>
    /// Cambia el servidor web activo: lo instala si falta (con su diálogo), lo guarda en roost.toml
    /// y, si el stack está en marcha, el daemon cambia solo el servidor web (PHP y la base de datos
    /// siguen). Si se cancela la instalación, se queda como estaba.
    /// </summary>
    private async Task SwitchServerAsync(string server)
    {
        var apache = server == RoostConfig.WebApache;
        if (apache && _apache is null) await InstallApacheAsync();
        if (!apache && _nginx is null) await InstallNginxAsync();
        var installed = apache
            ? new ApacheInstaller(Main.App.Paths).Find(ApacheCatalog.Current) is not null
            : new NginxInstaller(Main.App.Paths).Find(NginxCatalog.Current) is not null;
        if (installed)
        {
            await Main.RunAsync("No se pudo cambiar el servidor web", async () =>
            {
                RoostConfig.SetWebServer(Main.App.Paths.ConfigFile, server);
                _tabChosen = false;
                ShowNginx = !apache;
                await Main.ApplyAsync();
                Main.NotifyConfigChanged();
                Main.Flash(apache ? "Ahora sirve Apache (lee los .htaccess)" : "Ahora sirve nginx");
            });
        }
        Build();
    }

    [RelayCommand]
    private Task TestApacheConfigAsync() => Main.RunAsync("No se pudo validar la configuración", async () =>
    {
        if (_apache is not { } apache) throw new RoostException("Apache no está instalado: instálalo desde Disponibles o eligiéndolo arriba.");
        var planner = new StackPlanner(Main.App.Paths, ProcessRunner.Default);
        if (!File.Exists(planner.ApacheConfPath))
            throw new RoostException("Aún no hay configuración de Apache: se genera al iniciar con Apache como servidor activo.");
        var output = await Task.Run(() => planner.TestApache(apache));
        ApacheTestOutput = output?.Trim() ?? $"{(_apache?.IsDistro == true ? Path.GetFileName(_apache.Executable) : "httpd")} -t no respondió.";
        ApacheTestTone = output?.Contains("Syntax OK", StringComparison.OrdinalIgnoreCase) == true ? Tone.Ok : Tone.Bad;
    });

    [RelayCommand]
    private Task ReloadApacheAsync() => Main.RunAsync("No se pudo recargar Apache", () => ReloadWebAsync(RoostConfig.WebApache, "Apache"));

    [RelayCommand]
    private Task OpenApacheConfAsync() => Main.RunAsync("No se pudo abrir httpd.conf", () =>
    {
        var conf = new StackPlanner(Main.App.Paths, ProcessRunner.Default).ApacheConfPath;
        if (!File.Exists(conf)) throw new RoostException("Aún no hay httpd.conf: se genera al iniciar con Apache como servidor activo.");
        Shell.OpenFile(conf);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenApacheFolderAsync() => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        Shell.OpenFolder(ApacheFolder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task GoApacheErrorLogAsync() => Main.OpenLogAsync("apache-error.log");

    /// <summary>
    /// Enseña el diálogo de instalación y espera al resultado aunque se haya pasado a segundo
    /// plano («Seguir en segundo plano»): entonces el error, o el éxito, no se ha visto aún y se dice.
    /// </summary>
    private async Task RunInstallAsync(InstallDialogViewModel dialog, string what, string where)
    {
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        Main.NotifyConfigChanged();
        Build();
        if (shown is null && dialog.Failed)
            await Main.InfoAsync($"No se instaló {what}", dialog.Error!, Tone.Bad);
        else if (shown is null && !dialog.Failed)
            await Main.InfoAsync($"{what} instalado", $"Ya está en {where}.", Tone.Ok);
    }

    [RelayCommand]
    private async Task InstallApacheAsync()
    {
        var build = ApacheCatalog.Current;
        if (build.IsDistro)
        {
            // Linux: el Apache es el de la distro, y Roost no instala paquetes del sistema.
            await Main.InfoAsync("Apache es el de tu sistema",
                $"Roost usa el Apache de tu distribución con su propia configuración y tus permisos (solo en puertos a partir del 1024). Instálalo con: {LinuxApache.InstallHint}. Después vuelve a elegirlo.", Tone.Neutral);
            return;
        }
        // Apache de Apache Lounge no arranca sin el VC++ 14.50 o posterior: avisar antes, con el enlace.
        if (!await VcRedistGuard.ConfirmAsync(Main, $"Apache {build.Version}", forApache: true)) return;
        var installer = new ApacheInstaller(Main.App.Paths, Main.App.Http);
        var dialog = new InstallDialogViewModel(
            $"Instalando Apache {build.Version}",
            $"Win64 · desde {build.Url.Host}",
            null,
            [
                new("Versión fijada en Roost", "· firma PGP de Apache Lounge verificada"),
                new("Descargando el zip", null),
                new("Verificar el SHA-256", "· antes de extraer nada"),
                new($"Extraer en {Path.Combine(Main.App.Paths.ApacheBin, build.Version)}", null),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Extrayendo");
            });
        await RunInstallAsync(dialog, $"Apache {build.Version}", Path.Combine(Main.App.Paths.ApacheBin, build.Version));
    }

    [RelayCommand]
    private async Task InstallNginxAsync()
    {
        var build = NginxCatalog.Current;
        // nginx no lo necesita, pero el PHP que va a servir sí: mejor saberlo antes de empezar.
        if (!await VcRedistGuard.ConfirmAsync(Main, $"nginx {build.Version}", forApache: false, who: "El PHP que sirve nginx")) return;
        var installer = new NginxInstaller(Main.App.Paths, Main.App.Http);
        var dialog = new InstallDialogViewModel(
            $"Instalando nginx {build.Version}",
            $"rama estable · desde {build.Url.Host}",
            null,
            [
                new("Versión fijada en Roost", "· firma PGP verificada"),
                new("Descargando el zip oficial", null),
                new("Verificar el SHA-256", "· antes de extraer nada"),
                new($"Extraer en {Path.Combine(Main.App.Paths.NginxBin, build.Version)}", null),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Extrayendo");
            });
        await RunInstallAsync(dialog, $"nginx {build.Version}", Path.Combine(Main.App.Paths.NginxBin, build.Version));
    }
}
