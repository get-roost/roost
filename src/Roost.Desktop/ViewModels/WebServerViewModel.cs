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
/// Servidor web: el que arranca (nginx; Apache llega más adelante), sus puertos, el nginx
/// instalado con su configuración y el catálogo. La pestaña Apache explica qué hará falta.
/// </summary>
public sealed partial class WebServerViewModel : PageViewModel
{
    private RoostConfig _config = new();
    private InstalledNginx? _nginx;

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
    [ObservableProperty] public partial string NginxVersion { get; private set; } = NginxCatalog.Windows.Version;
    [ObservableProperty] public partial string NginxFolder { get; private set; } = "";
    [ObservableProperty] public partial string Listen { get; private set; } = "";
    [ObservableProperty] public partial string Modules { get; private set; } = "";
    [ObservableProperty] public partial string? TestOutput { get; private set; }
    [ObservableProperty] public partial Tone TestTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial string ApacheNote { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApache))]
    public partial bool ShowNginx { get; set; } = true;

    public bool ShowApache { get => !ShowNginx; set => ShowNginx = !value; }

    public bool HasTestOutput => TestOutput is not null;

    public string CatalogVersion => NginxCatalog.Windows.Version;

    partial void OnHttpTextChanged(string value) => CheckPorts();
    partial void OnHttpsTextChanged(string value) => CheckPorts();
    partial void OnTestOutputChanged(string? value) => OnPropertyChanged(nameof(HasTestOutput));

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
        _nginx = new NginxInstaller(paths).Find(NginxCatalog.Windows);

        NginxInstalled = _nginx is not null;
        NginxRunning = status?.Running == true && status.Services.Any(s => s.Name == "nginx" && s.State == "Running");
        (NginxState, NginxStateTone) = NginxRunning ? ("en marcha", Tone.Ok) : NginxInstalled ? ("parado", Tone.Neutral) : ("se instala al iniciar", Tone.Neutral);
        NginxVersion = _nginx?.Version ?? NginxCatalog.Windows.Version;
        NginxFolder = _nginx?.Directory ?? Path.Combine(paths.NginxBin, NginxCatalog.Windows.Version);
        var hasSites = SafeSites().Count > 0;
        Listen = hasSites
            ? $"127.0.0.1:{_config.HttpPort} · 127.0.0.1:{_config.HttpsPort} (solo este equipo)"
            : $"127.0.0.1:{_config.HttpPort} (solo este equipo; el {_config.HttpsPort} se abre con el primer sitio)";
        if (_nginx is not null && Modules.Length == 0) _ = LoadModulesAsync(_nginx);

        var current = (_config.HttpPort.ToString(CultureInfo.InvariantCulture), _config.HttpsPort.ToString(CultureInfo.InvariantCulture));
        if (HttpText.Length == 0) HttpText = current.Item1;
        if (HttpsText.Length == 0) HttpsText = current.Item2;
        CheckPorts();
        BuildApacheNote();
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
        if (port == current && NginxRunning)
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

    private void BuildApacheNote()
    {
        // Con Apache (mod_php) cada PHP en uso necesita su build TS.
        var installed = new PhpInstaller(Main.App.Paths).ListInstalled();
        var inUse = new List<string>();
        var status = Main.Stack.Status;
        if (status?.Php is { } php) inUse.Add(php);
        if (status is not null) inUse.AddRange(status.Sites.Select(s => s.Php));
        if (inUse.Count == 0 && installed.Count > 0) inUse.Add(installed[0].Name);
        var missing = inUse.Distinct()
            .Select(name => name.Split('-')[0])
            .Where(version => !installed.Any(i => i.ThreadSafe && i.Name.StartsWith(version + "-", StringComparison.Ordinal)))
            .Distinct()
            .ToList();
        ApacheNote = missing.Count == 0
            ? "Tus PHP en uso ya tienen su build TS: Apache podría usarlos tal cual."
            : $"Para usar Apache con tus sitios harían falta PHP {string.Join(" y ", missing.Select(v => v + " TS"))} (ahora tienes las NTS).";
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

    [RelayCommand]
    private Task ReloadAsync() => Main.RunAsync("No se pudo recargar nginx", async () =>
    {
        var response = await Main.App.Daemon.RestartAsync("nginx");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
        Main.Flash("nginx recargado");
    });

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

    [RelayCommand]
    private async Task InstallNginxAsync()
    {
        var build = NginxCatalog.Windows;
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
        var run = dialog.RunAsync();
        await Main.ShowDialogAsync(dialog);
        await run;
        Main.NotifyConfigChanged();
    }
}
