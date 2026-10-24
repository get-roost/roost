using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Platform;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes › General (E4): «Iniciar todo al iniciar sesión, sin abrir la app», una tarea programada
/// del usuario que lanza <c>roost up</c> (<see cref="LogonTask"/>). Al lado de «Abrir Roost al
/// iniciar sesión», que abre la app en la bandeja (HKCU\…\Run).
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] public partial bool StackAtLogon { get; set; }
    [ObservableProperty] public partial string StackAtLogonHint { get; private set; } = "";
    [ObservableProperty] public partial bool CanStackAtLogon { get; private set; } = true;

    private bool _loadingTask;

    /// <summary>El roost.exe que lanza la tarea: el que va junto a la app (el que lanza el daemon).</summary>
    private static string RoostExe => Path.Combine(AppServices.AppDirectory, OperatingSystem.IsWindows() ? "roost.exe" : "roost");

    partial void OnStackAtLogonChanged(bool value)
    {
        if (_loadingTask || _loading) return;
        _ = ToggleStackAtLogonAsync(value);
    }

    private async Task ToggleStackAtLogonAsync(bool on)
    {
        await Main.RunAsync(on ? "No se pudo crear la tarea al iniciar sesión" : "No se pudo quitar la tarea al iniciar sesión", async () =>
        {
            if (on) await Main.App.LogonTask.EnableAsync(RoostExe);
            else await Main.App.LogonTask.DisableAsync();
            AppLog.Info(on ? $"Tarea al iniciar sesión puesta ({LogonTask.TaskName}, {RoostExe})" : "Tarea al iniciar sesión quitada");
            Main.Flash(on ? "Al iniciar sesión, el stack arrancará sin abrir la app" : "El stack ya no arranca al iniciar sesión");
        });
        // Lo que de verdad ha quedado (si falló, la casilla vuelve atrás).
        await LoadStackAtLogonAsync();
    }

    /// <summary>Pregunta al Programador de tareas (solo lee) y deja la casilla como esté de verdad.</summary>
    private async Task LoadStackAtLogonAsync()
    {
        var why = Main.App.LogonTask.Unavailable;
        var status = why is null ? await Main.App.LogonTask.StatusAsync() : LogonTaskStatus.Missing;
        _loadingTask = true;
        try
        {
            CanStackAtLogon = why is null && status.Error is null;
            StackAtLogon = status.Exists && status.Enabled;
            StackAtLogonHint = why is not null
                ? OperatingSystem.IsWindows() ? $"Con {RoostPaths.HomeVariable} no se crea: una tarea no lleva esa variable y arrancaría la Roost de siempre." : "Solo en Windows."
                : status.Error is { } error ? $"No se ha podido preguntar al Programador de tareas: {error}"
                : status.IsStale(File.Exists) ? "La tarea lanza un roost.exe que ya no está: vuelve a marcarla."
                : status.Exists && !status.Enabled ? "La tarea está, pero desactivada en el Programador de tareas."
                : status.Exists ? $"Lanza roost up como {LogonTask.CurrentUser}, sin ventanas ni permisos de administrador (también: roost autostart)."
                : "Una tarea programada de tu usuario, sin permisos de administrador. Los sitios se sirven aunque no abras Roost.";
        }
        finally
        {
            _loadingTask = false;
        }
    }
}
