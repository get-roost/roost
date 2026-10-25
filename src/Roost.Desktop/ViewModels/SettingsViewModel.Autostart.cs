using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Platform;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes › General (E4): «Iniciar todo al iniciar sesión, sin abrir la app», una tarea programada
/// del usuario que lanza <c>roost up</c> (<see cref="LogonTask"/>); en Linux, la unidad de
/// <c>systemd --user</c> de <c>roost autostart</c>, o su .desktop si no hay sesión de systemd
/// (<see cref="UserUnit"/>). Al lado de «Abrir Roost al iniciar sesión», que abre la app en la bandeja.
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
        var what = OperatingSystem.IsWindows() ? "la tarea" : "el arranque";
        await Main.RunAsync(on ? $"No se pudo crear {what} al iniciar sesión" : $"No se pudo quitar {what} al iniciar sesión", async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                if (on) await Main.App.LogonTask.EnableAsync(RoostExe);
                else await Main.App.LogonTask.DisableAsync();
                AppLog.Info(on ? $"Tarea al iniciar sesión puesta ({LogonTask.TaskName}, {RoostExe})" : "Tarea al iniciar sesión quitada");
            }
            else if (on)
            {
                var put = await Main.App.UserUnit.EnableAsync(RoostExe);
                AppLog.Info($"Arranque al iniciar sesión puesto ({put.File}, {RoostExe}){(put.Error is { } why ? $"; sin systemd de usuario: {why}" : "")}");
            }
            else
            {
                await Main.App.UserUnit.DisableAsync();
                AppLog.Info("Arranque al iniciar sesión quitado");
            }
            Main.Flash(on ? "Al iniciar sesión, el stack arrancará sin abrir la app" : "El stack ya no arranca al iniciar sesión");
        });
        // Lo que de verdad ha quedado (si falló, la casilla vuelve atrás).
        await LoadStackAtLogonAsync();
    }

    /// <summary>Pregunta al Programador de tareas (solo lee) y deja la casilla como esté de verdad.</summary>
    private async Task LoadStackAtLogonAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            LoadUserUnit();
            return;
        }
        var why = Main.App.LogonTask.Unavailable;
        var status = why is null ? await Main.App.LogonTask.StatusAsync() : LogonTaskStatus.Missing;
        _loadingTask = true;
        try
        {
            CanStackAtLogon = why is null && status.Error is null;
            StackAtLogon = status.Exists && status.Enabled;
            StackAtLogonHint = why is not null
                ? OperatingSystem.IsWindows() ? $"Con {RoostPaths.HomeVariable} no se crea: una tarea no lleva esa variable y arrancaría la Roost de siempre." : why
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

    /// <summary>Linux: lee la unidad o el .desktop (solo ficheros, sin systemctl) y deja la casilla como esté.</summary>
    private void LoadUserUnit()
    {
        var status = Main.App.UserUnit.Status();
        _loadingTask = true;
        try
        {
            CanStackAtLogon = UserUnit.WhyNot() is null && status.Error is null;
            StackAtLogon = status.Exists && status.Enabled;
            StackAtLogonHint = status.Error is { } error ? $"No se ha podido leer el arranque al iniciar sesión: {error}"
                : status.IsStale(File.Exists) ? "Lanza un roost que ya no está: vuelve a marcarla."
                : status.Exists && !status.Enabled ? $"La unidad {UserUnit.UnitName} está, pero desactivada (systemctl --user enable {UserUnit.UnitName})."
                : status.Kind == AutostartKind.Desktop ? "Un .desktop en ~/.config/autostart, que lanza el escritorio al entrar (aquí no hay sesión de usuario de systemd)."
                : status.Exists ? $"La unidad de usuario {UserUnit.UnitName} lanza roost up al iniciar sesión y roost down al cerrarla, sin permisos de administrador (también: roost autostart)."
                : "Una unidad de usuario de systemd (o, sin ella, un .desktop del escritorio), sin permisos de administrador. Los sitios se sirven aunque no abras Roost.";
        }
        finally
        {
            _loadingTask = false;
        }
    }
}
