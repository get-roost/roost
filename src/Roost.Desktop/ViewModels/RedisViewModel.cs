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
/// Redis: instalarlo, si va en el stack (<c>[redis] enabled</c>), su puerto, la consola, sus
/// ficheros y lo que hay que poner en el <c>.env</c>. Es la build MSYS2 de redis-windows, fijada
/// con su SHA-256; Memurai u otro Redis de fuera solo se detectan.
/// </summary>
public sealed partial class RedisViewModel : PageViewModel
{
    private RoostConfig _config = new();
    private InstalledRedis? _redis;
    private bool _loading;
    private string? _shownPort;

    public RedisViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "Caché, colas y sesiones para Laravel y WordPress, solo en este equipo. Lo arranca Iniciar todo si está en el stack.";
        main.MachineServices.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MachineServices.Found)) BuildOthers();
        };
    }

    public override string Title => "Redis";

    [ObservableProperty] public partial bool Installed { get; private set; }
    [ObservableProperty] public partial string Version { get; private set; } = RedisCatalog.Windows.Version;
    [ObservableProperty] public partial string Folder { get; private set; } = "";
    [ObservableProperty] public partial bool Running { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial Tone StateTone { get; private set; } = Tone.Off;
    [ObservableProperty] public partial string HeaderChip { get; private set; } = "";
    [ObservableProperty] public partial Tone HeaderTone { get; private set; } = Tone.Off;

    /// <summary>La casilla «En el stack»: cambiarla guarda <c>[redis] enabled</c> y lo aplica.</summary>
    [ObservableProperty] public partial bool InStack { get; set; }

    [ObservableProperty] public partial string PortText { get; set; } = "";
    [ObservableProperty] public partial string PortChip { get; private set; } = "";
    [ObservableProperty] public partial Tone PortTone { get; private set; } = Tone.Ok;
    [ObservableProperty] public partial bool CanApplyPort { get; private set; }
    [ObservableProperty] public partial int Port { get; private set; } = RoostConfig.DefaultRedisPort;

    /// <summary>Lo que contesta al PING («PONG», «pide contraseña»…); null mientras no se ha preguntado.</summary>
    [ObservableProperty] public partial string? PingText { get; private set; }
    [ObservableProperty] public partial Tone PingTone { get; private set; } = Tone.Off;

    [ObservableProperty] public partial string EnvText { get; private set; } = "";

    /// <summary>Otros Redis del equipo (Memurai, un servicio de Windows): Roost no los usa, pero pueden quitarle el puerto.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Others { get; private set; } = [];

    public bool HasOthers => Others.Count > 0;
    public bool HasPing => PingText is not null;
    public bool NotInstalled => !Installed;
    public string CatalogVersion => RedisCatalog.Windows.Version;
    public string CatalogSha => $"SHA-256 {RedisCatalog.Windows.Sha256}";
    public string ConfPath => RedisConf.ConfPath(Main.App.Paths);
    public string UserConfPath => RedisConf.UserFragmentPath(Main.App.Paths);
    public string DataPath => Main.App.Paths.RedisData;

    partial void OnOthersChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasOthers));
    partial void OnPingTextChanged(string? value) => OnPropertyChanged(nameof(HasPing));
    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(NotInstalled));
    partial void OnPortTextChanged(string value) => CheckPort();

    partial void OnInStackChanged(bool value)
    {
        if (_loading || value == _config.RedisEnabled) return;
        _ = SetInStackAsync(value);
    }

    public override async Task ShownAsync()
    {
        Build();
        _ = Main.MachineServices.LoadAsync();
        await PingAsync();
    }

    public override void StackChanged()
    {
        Build();
        _ = PingAsync();
    }

    private void Build()
    {
        var paths = Main.App.Paths;
        try { _config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { _config = new RoostConfig(); }
        _redis = new RedisInstaller(paths).Locate();
        var service = Main.Stack.Status is { Running: true } status ? status.Services.FirstOrDefault(s => s.Name == "redis") : null;

        _loading = true;
        InStack = _config.RedisEnabled;
        _loading = false;

        Installed = _redis is not null;
        Version = _redis?.Version ?? RedisCatalog.Windows.Version;
        Folder = _redis?.Directory ?? Path.Combine(paths.RedisBin, RedisCatalog.Windows.Version);
        Port = _config.RedisPort;
        Running = service?.State == "Running";
        (StateText, StateTone) = service?.State switch
        {
            "Running" => ("en marcha", Tone.Ok),
            "Restarting" => ("reiniciando", Tone.Warn),
            "Failed" => ("no arranca", Tone.Bad),
            "Stopped" => ("parado a mano", Tone.Neutral),
            _ when !Installed => ("sin instalar", Tone.Neutral),
            _ when !_config.RedisEnabled => ("fuera del stack", Tone.Neutral),
            _ => ("parado", Tone.Neutral),
        };
        (HeaderChip, HeaderTone) = (StateText, StateTone);
        EnvText = RedisConf.EnvLines(_config.RedisPort);
        // Lo tecleado se respeta; si no se ha tocado, sigue al de roost.toml (cambiado desde la CLI, por ejemplo).
        if (PortText.Length == 0 || PortText == _shownPort) PortText = _config.RedisPort.ToString(CultureInfo.InvariantCulture);
        _shownPort = _config.RedisPort.ToString(CultureInfo.InvariantCulture);
        CheckPort();
        BuildOthers();
    }

    private void BuildOthers() =>
        Others = Main.MachineServices.Found
            .Where(s => s.Kind == "Redis")
            .Select(s => $"{s.DisplayName} ({s.Name}): {MachineServices.StateInSentence(s)}. Roost no lo usa ni lo toca; si se queda el {_config.RedisPort}, cambia aquí el puerto o déjalo en Manual en Servicios de Windows.")
            .ToList();

    private async Task PingAsync()
    {
        if (!Running)
        {
            PingText = null;
            return;
        }
        var reply = await RedisProbe.PingAsync(_config.RedisPort);
        (PingText, PingTone) = RedisProbe.IsPong(reply) ? ("Contesta al PING (PONG)", Tone.Ok)
            : RedisProbe.NeedsPassword(reply) ? ("Contesta, pero pide contraseña (requirepass en tus ajustes)", Tone.Warn)
            : reply is null ? ("No contesta al PING", Tone.Bad)
            : ($"Contesta {reply}", Tone.Warn);
    }

    private void CheckPort()
    {
        CanApplyPort = false;
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            (PortChip, PortTone) = ("no válido", Tone.Bad);
            return;
        }
        if ((_config with { RedisEnabled = true, RedisPort = port }).ServicePortClash() is not null)
        {
            (PortChip, PortTone) = ("es de otro servicio de Roost", Tone.Bad);
            return;
        }
        if (port == _config.RedisPort && Running)
        {
            (PortChip, PortTone) = ("en uso por Roost", Tone.Ok);
            return;
        }
        if (StackPlanner.BusyPorts([port]).Count > 0)
        {
            var owner = PortOwners.Find(port);
            (PortChip, PortTone) = (owner?.ProcessName is { } name ? $"ocupado: {name}" : "ocupado", Tone.Bad);
            return;
        }
        (PortChip, PortTone) = ("libre", Tone.Ok);
        CanApplyPort = port != _config.RedisPort;
    }

    // Acciones

    /// <summary>Lo mete en el stack o lo saca; si hay que meterlo y no está, lo instala antes.</summary>
    private async Task SetInStackAsync(bool enabled)
    {
        if (enabled && new RedisInstaller(Main.App.Paths).Locate() is null && !await ServiceInstall.RedisAsync(Main))
        {
            Build();
            return;
        }
        await Main.RunAsync("No se pudo cambiar Redis", async () =>
        {
            RoostConfig.SetRedis(Main.App.Paths.ConfigFile, enabled: enabled);
            var applied = await Main.ApplyAsync();
            Main.NotifyConfigChanged();
            if (applied) Main.Flash(enabled ? "Redis va en el stack" : "Redis ya no va en el stack (sus datos se quedan)");
        });
        Build();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!await ServiceInstall.RedisAsync(Main)) return;
        Build();
        if (!_config.RedisEnabled) await SetInStackAsync(true);
    }

    [RelayCommand]
    private Task ApplyPortAsync() => Main.RunAsync("No se pudo cambiar el puerto de Redis", async () =>
    {
        var port = int.Parse(PortText, CultureInfo.InvariantCulture);
        RoostConfig.SetRedis(Main.App.Paths.ConfigFile, port: port);
        CanApplyPort = false;
        if (_config.RedisEnabled) await Main.ApplyAsync();
        Main.NotifyConfigChanged();
        Main.Flash($"Redis en el {port}: acuérdate del REDIS_PORT de tus proyectos");
    });

    [RelayCommand]
    private Task OpenCliAsync() => Main.RunAsync("No se pudo abrir redis-cli", () =>
    {
        if (_redis is not { } redis) throw new RoostException("Redis no está instalado.");
        Shell.OpenConsole(redis.Cli, $"-h 127.0.0.1 -p {_config.RedisPort}", Main.App.Paths.RedisData);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RestartAsync() => Main.RunAsync("No se pudo reiniciar Redis", async () =>
    {
        var response = await Main.App.Daemon.RestartAsync("redis");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
        Main.Flash("Redis reiniciado (se ha parado limpio: sus datos están en disco)");
    });

    [RelayCommand]
    private Task CopyEnvAsync() => Main.CopyAsync(EnvText, "Copiado el .env de Redis");

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(DataPath);

    [RelayCommand]
    private Task OpenConfAsync() => Main.RunAsync("No se pudo abrir redis.conf", () =>
    {
        if (!File.Exists(ConfPath)) throw new RoostException("Todavía no se ha generado: se escribe al arrancar Redis (Iniciar todo).");
        Shell.OpenFile(ConfPath);
        return Task.CompletedTask;
    });

    /// <summary>99-usuario.conf: lo crea con ejemplos si aún no existe.</summary>
    [RelayCommand]
    private Task EditUserConfAsync() => Main.RunAsync("No se pudo abrir tus ajustes de Redis", () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserConfPath)!);
        if (!File.Exists(UserConfPath)) File.WriteAllText(UserConfPath, RedisConf.UserFragmentTemplate);
        Shell.OpenFile(UserConfPath);
        Main.Flash("Lo que cambies se aplica al reiniciar Redis");
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task GoLogAsync() => Main.OpenLogAsync("redis.log");

    [RelayCommand]
    private void OpenServicesPanel() => MachineServices.OpenServicesPanel();
}
