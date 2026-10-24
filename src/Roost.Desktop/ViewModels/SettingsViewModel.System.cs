using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Dns;
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes › Sistema (oleada 5): roostd («Reiniciar roostd», su registro), si contesta el DNS
/// local (lo de <c>roost dns status</c>, J7) y el log de la propia app (J6).
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] public partial string DnsProbeText { get; private set; } = "";
    [ObservableProperty] public partial Tone DnsProbeTone { get; private set; } = Tone.Neutral;
    [ObservableProperty] public partial bool RestartingDaemon { get; private set; }

    public bool CanRestartDaemon => !RestartingDaemon;

    partial void OnRestartingDaemonChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRestartDaemon));
        OnPropertyChanged(nameof(DaemonText));
    }

    /// <summary>«roostd en marcha · PID 70324 · el stack, desde las 21:14», o parado.</summary>
    public string DaemonText => RestartingDaemon ? "Reiniciando roostd…"
        : Main.Stack.Status is not { } s ? "roostd no está en marcha: arranca con Iniciar todo (o roost up)."
        : s.Running ? $"roostd en marcha · PID {s.DaemonPid} · el stack, en marcha{(s.Since is { } since ? $" desde las {since.ToLocalTime():HH:mm}" : "")}"
        : $"roostd en marcha · PID {s.DaemonPid} · el stack está parado";

    public Tone DaemonTone => Main.Stack.Status is not { } s ? Tone.Off : s.Running ? Tone.Ok : Tone.Neutral;

    /// <summary>El log de la app (J6), en la carpeta de los registros.</summary>
    public string AppLogPath => Path.Combine(Paths.Logs, AppLog.FileName);

    public override void StackChanged()
    {
        OnPropertyChanged(nameof(DaemonText));
        OnPropertyChanged(nameof(DaemonTone));
    }

    /// <summary>
    /// Lo de <c>roost dns status</c>: si el DNS local contesta ahora mismo en 127.53.0.1:53 (1 s como
    /// mucho, fuera del hilo de la UI), con el modo y la regla, que ya cuenta <see cref="DnsStateText"/>.
    /// </summary>
    [RelayCommand]
    private Task CheckDnsAsync() => ProbeDnsAsync(force: true);

    /// <summary>Al entrar en Ajustes solo se pregunta si el DNS local debería estar en marcha (modo dns o el servicio en el stack).</summary>
    private async Task ProbeDnsAsync(bool force)
    {
        var dnsMode = ResolverDns;
        if (!force && !dnsMode && Main.Stack.Status?.Services.Any(s => s.Name == "dns") != true)
        {
            (DnsProbeText, DnsProbeTone) = ("El DNS local no está en marcha (no hace falta: los .test van por el hosts).", Tone.Neutral);
            return;
        }
        DnsProbeText = "Preguntando al DNS local…";
        DnsProbeTone = Tone.Neutral;
        var answers = await Task.Run(() => LocalDnsProbe.AsksAsync("roost-check.test"));
        (DnsProbeText, DnsProbeTone) = answers
            ? ($"El DNS local contesta en {LocalDns.EndPoint}: los .test resuelven a 127.0.0.1.", dnsMode ? Tone.Ok : Tone.Neutral)
            : dnsMode
                ? ($"El DNS local no contesta en {LocalDns.EndPoint}: arranca con el stack (Iniciar todo).", Main.Stack.IsRunning ? Tone.Bad : Tone.Warn)
                : ($"El DNS local no está en marcha (no hace falta: los .test van por el hosts).", Tone.Neutral);
    }

    /// <summary>
    /// «Reiniciar roostd»: para el daemon (y con él el stack: <c>POST /down</c>), lo vuelve a lanzar y,
    /// si el stack estaba en marcha, lo arranca otra vez. Para cuando roostd se ha quedado raro o
    /// después de cambiar algo que solo lee al arrancar.
    /// </summary>
    [RelayCommand]
    private async Task RestartDaemonAsync()
    {
        if (RestartingDaemon || Main.Stack.Busy) return;
        await Main.Stack.RefreshAsync();
        var wasRunning = Main.Stack.IsRunning;
        if (wasRunning && !await Main.ConfirmAsync("Reiniciar roostd",
                "Se para todo (los sitios dejan de contestar unos segundos), se vuelve a lanzar roostd y se arranca otra vez el stack.",
                "Reiniciar"))
            return;

        RestartingDaemon = true;
        try
        {
            await Main.RunAsync("No se pudo reiniciar roostd", async () =>
            {
                AppLog.Info($"Reiniciando roostd (el stack {(wasRunning ? "estaba en marcha" : "estaba parado")})");
                if (await Main.App.Daemon.TryStatusAsync() is not null) await Main.App.Daemon.DownAsync();
                // El daemon viejo tarda un poco en soltar el mutex: EnsureRunningAsync relanza si hace falta.
                await Main.App.Daemon.EnsureRunningAsync(DaemonLauncher.FromDirectory(AppServices.AppDirectory));
                await Main.Stack.RefreshAsync();
            });
        }
        finally
        {
            RestartingDaemon = false;
        }

        if (wasRunning && Main.Stack.IsDaemonUp && !Main.Stack.IsRunning)
            await Main.StartStackAsync();
        if (Main.Stack.IsDaemonUp)
        {
            Main.Flash(Main.Stack.Status is { } s ? $"roostd reiniciado · PID {s.DaemonPid}" : "roostd reiniciado");
            AppLog.Info($"roostd reiniciado (PID {Main.Stack.Status?.DaemonPid})");
        }
        StackChanged();
    }

    [RelayCommand]
    private Task OpenDaemonLogAsync() => Main.OpenLogAsync(Path.GetFileName(DaemonHost.LogFile(Paths)));

    /// <summary>J6: el log de la app, en Registros.</summary>
    [RelayCommand]
    private Task OpenAppLogAsync() => Main.OpenLogAsync(AppLog.FileName);
}
