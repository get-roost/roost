using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Correo: Mailpit en el stack (<c>[mail] enabled</c>), sus dos puertos (SMTP y bandeja), la
/// bandeja en el navegador y lo que hace falta para que lo usen PHP y Laravel. El correo se
/// queda en el equipo: Mailpit no lo manda a nadie.
/// </summary>
public sealed partial class MailViewModel : PageViewModel
{
    private RoostConfig _config = new();
    private InstalledMailpit? _mailpit;
    private bool _loading;
    private string? _shownSmtp;
    private string? _shownWeb;

    public MailViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "El correo que mandan tus proyectos se queda en una bandeja de este equipo: nada sale a internet.";
    }

    public override string Title => "Correo";

    [ObservableProperty] public partial bool Installed { get; private set; }
    [ObservableProperty] public partial string Version { get; private set; } = MailpitCatalog.Current.Version;
    [ObservableProperty] public partial bool Running { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial Tone StateTone { get; private set; } = Tone.Off;

    /// <summary>La casilla «En el stack»: cambiarla guarda <c>[mail] enabled</c> y lo aplica.</summary>
    [ObservableProperty] public partial bool InStack { get; set; }

    [ObservableProperty] public partial string SmtpText { get; set; } = "";
    [ObservableProperty] public partial string WebText { get; set; } = "";
    [ObservableProperty] public partial string SmtpChip { get; private set; } = "";
    [ObservableProperty] public partial Tone SmtpTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial string WebChip { get; private set; } = "";
    [ObservableProperty] public partial Tone WebTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial bool CanApplyPorts { get; private set; }

    [ObservableProperty] public partial string InboxUrl { get; private set; } = "";
    [ObservableProperty] public partial string EnvText { get; private set; } = "";

    /// <summary>Si el mail() de PHP va a Mailpit (el 20-roost-mail.ini del conf.d común).</summary>
    [ObservableProperty] public partial string PhpText { get; private set; } = "";
    [ObservableProperty] public partial Tone PhpTone { get; private set; } = Tone.Neutral;

    public bool NotInstalled => !Installed;
    public string CatalogVersion => MailpitCatalog.Current.Version;
    public string CatalogSha => $"SHA-256 {MailpitCatalog.Current.Sha256}";
    public string BuildHint => $"{(OsTexts.Windows ? "Windows" : "Linux")} amd64 · SHA-256 fijado";
    public string LogPath => Path.Combine("logs", "mailpit.log");
    public string DataPath => Main.App.Paths.MailpitData;
    public string PhpIniPath => Path.Combine(Main.App.Paths.Etc, "php", "conf.d", MailConf.PhpIniName);

    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(NotInstalled));
    partial void OnSmtpTextChanged(string value) => CheckPorts();
    partial void OnWebTextChanged(string value) => CheckPorts();

    partial void OnInStackChanged(bool value)
    {
        if (_loading || value == _config.MailEnabled) return;
        _ = SetInStackAsync(value);
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
        try { _config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { _config = new RoostConfig(); }
        _mailpit = new MailpitInstaller(paths).Locate();
        var service = Main.Stack.Status is { Running: true } status ? status.Services.FirstOrDefault(s => s.Name == "mailpit") : null;

        _loading = true;
        InStack = _config.MailEnabled;
        _loading = false;

        Installed = _mailpit is not null;
        Version = _mailpit?.Version ?? MailpitCatalog.Current.Version;
        Running = service?.State == "Running";
        (StateText, StateTone) = service?.State switch
        {
            "Running" => ("en marcha", Tone.Ok),
            "Restarting" => ("reiniciando", Tone.Warn),
            "Failed" => ("no arranca", Tone.Bad),
            "Stopped" => ("parado a mano", Tone.Neutral),
            _ when !Installed => ("sin instalar", Tone.Neutral),
            _ when !_config.MailEnabled => ("fuera del stack", Tone.Neutral),
            _ => ("parado", Tone.Neutral),
        };
        InboxUrl = _config.MailWebUrl;
        EnvText = MailConf.EnvLines(_config.MailSmtpPort);
        (PhpText, PhpTone) = File.Exists(PhpIniPath)
            ? ($"mail() de PHP va a Mailpit (SMTP 127.0.0.1:{_config.MailSmtpPort}), en todas las versiones y también en la terminal.", Tone.Ok)
            : _config.MailEnabled ? ("mail() de PHP irá a Mailpit en cuanto arranque el stack.", Tone.Accent)
            : (OperatingSystem.IsWindows() ? "Con Mailpit fuera del stack, mail() de PHP vuelve a como venía (en Windows, sin servidor de correo, falla)." : "Con Mailpit fuera del stack, mail() de PHP vuelve a como venía (al sendmail del sistema, si lo hay).", Tone.Neutral);
        // Lo tecleado se respeta; si no se ha tocado, sigue a roost.toml (cambiado desde la CLI, por ejemplo).
        var smtp = _config.MailSmtpPort.ToString(CultureInfo.InvariantCulture);
        var web = _config.MailWebPort.ToString(CultureInfo.InvariantCulture);
        if (SmtpText.Length == 0 || SmtpText == _shownSmtp) SmtpText = smtp;
        if (WebText.Length == 0 || WebText == _shownWeb) WebText = web;
        (_shownSmtp, _shownWeb) = (smtp, web);
        CheckPorts();
    }

    private void CheckPorts()
    {
        CanApplyPorts = false;
        var smtpOk = Parse(SmtpText, out var smtp);
        var webOk = Parse(WebText, out var web);
        (SmtpChip, SmtpTone) = smtpOk ? Check(smtp, _config.MailSmtpPort) : ("no válido", Tone.Bad);
        (WebChip, WebTone) = webOk ? Check(web, _config.MailWebPort) : ("no válido", Tone.Bad);
        if (!smtpOk || !webOk) return;
        if (smtp == web)
        {
            (WebChip, WebTone) = ("igual que el SMTP", Tone.Bad);
            return;
        }
        if ((_config with { MailEnabled = true, MailSmtpPort = smtp, MailWebPort = web }).ServicePortClash() is not null)
        {
            (SmtpChip, SmtpTone) = ("choca con otro de Roost", Tone.Bad);
            return;
        }
        CanApplyPorts = SmtpTone == Tone.Ok && WebTone == Tone.Ok && (smtp != _config.MailSmtpPort || web != _config.MailWebPort);
    }

    private static bool Parse(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private (string, Tone) Check(int port, int current)
    {
        if (port == current && Running) return ("en uso por Roost", Tone.Ok);
        if (StackPlanner.BusyPorts([port]).Count > 0)
        {
            var owner = PortOwners.Find(port);
            return (owner?.ProcessName is { } name ? $"ocupado: {name}" : "ocupado", Tone.Bad);
        }
        return ("libre", Tone.Ok);
    }

    // Acciones

    /// <summary>Lo mete en el stack (y el mail() de PHP con él) o lo saca; si hay que meterlo y no está, lo instala antes.</summary>
    private async Task SetInStackAsync(bool enabled)
    {
        if (enabled && new MailpitInstaller(Main.App.Paths).Locate() is null && !await ServiceInstall.MailpitAsync(Main))
        {
            Build();
            return;
        }
        await Main.RunAsync("No se pudo cambiar el correo", async () =>
        {
            RoostConfig.SetMail(Main.App.Paths.ConfigFile, enabled: enabled);
            // El de PHP ya, aunque el stack esté parado: la terminal lo lee igual.
            MailConf.SyncPhpIni(Path.Combine(Main.App.Paths.Etc, "php", "conf.d"), RoostConfig.Load(Main.App.Paths.ConfigFile));
            var applied = await Main.ApplyAsync();
            Main.NotifyConfigChanged();
            if (applied) Main.Flash(enabled ? "El correo de PHP va a la bandeja de Mailpit" : "Mailpit ya no va en el stack (la bandeja se queda)");
        });
        Build();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!await ServiceInstall.MailpitAsync(Main)) return;
        Build();
        if (!_config.MailEnabled) await SetInStackAsync(true);
    }

    [RelayCommand]
    private Task ApplyPortsAsync() => Main.RunAsync("No se pudieron cambiar los puertos del correo", async () =>
    {
        var smtp = int.Parse(SmtpText, CultureInfo.InvariantCulture);
        var web = int.Parse(WebText, CultureInfo.InvariantCulture);
        RoostConfig.SetMail(Main.App.Paths.ConfigFile, smtpPort: smtp, webPort: web);
        CanApplyPorts = false;
        if (_config.MailEnabled) await Main.ApplyAsync();
        Main.NotifyConfigChanged();
        Main.Flash($"Mailpit en el {smtp}: acuérdate del MAIL_PORT de tus proyectos");
    });

    [RelayCommand]
    private Task OpenInboxAsync() => Main.RunAsync("No se pudo abrir la bandeja", () =>
    {
        Shell.OpenUrl(InboxUrl);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RestartAsync() => Main.RunAsync("No se pudo reiniciar Mailpit", async () =>
    {
        var response = await Main.App.Daemon.RestartAsync("mailpit");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
        Main.Flash("Mailpit reiniciado");
    });

    [RelayCommand]
    private Task CopyEnvAsync() => Main.CopyAsync(EnvText, "Copiado el .env del correo");

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(DataPath);

    [RelayCommand]
    private Task OpenPhpIniAsync() => Main.RunAsync("No se pudo abrir el fragmento de PHP", () =>
    {
        if (!File.Exists(PhpIniPath)) throw new RoostException("No está: se escribe con Mailpit en el stack.");
        Shell.OpenFile(PhpIniPath);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task GoLogAsync() => Main.OpenLogAsync("mailpit.log");
}
