using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Updates;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Ajustes › Acerca de Roost: la versión instalada y si hay una nueva (<see cref="UpdateState"/>).</summary>
public sealed partial class SettingsViewModel
{
    public UpdateState Updates => Main.Updates;

    /// <summary>Mirar solo si hay una Roost nueva (preferencia de la app, activada por defecto).</summary>
    [ObservableProperty]
    public partial bool CheckUpdatesOnLaunch { get; set; } = true;

    /// <summary><c>ROOST_NO_UPDATE_CHECK</c> manda sobre la casilla: lo explica una línea debajo.</summary>
    public bool UpdatesDisabledByEnvironment => UpdateChecker.DisabledByEnvironment;

    public string UpdatesDisabledText => $"Desactivado en este equipo con la variable {UpdateChecker.DisableVariable}.";

    partial void OnCheckUpdatesOnLaunchChanged(bool value)
    {
        if (_loading) return;
        Main.App.SavePrefs(Main.App.Prefs with { CheckUpdates = value });
    }

    private void LoadUpdatePrefs()
    {
        CheckUpdatesOnLaunch = Main.App.Prefs.CheckUpdates;
        Updates.RefreshText();
    }

    /// <summary>«Buscar actualizaciones»: pregunta ya, sin mirar la caché.</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var result = await Updates.CheckAsync(force: true);
        if (result.UpdateAvailable) return;   // el aviso de arriba ya lo cuenta
        if (result.Status == UpdateStatus.UpToDate) Main.Flash($"Roost {result.Installed} está al día");
        else if (result.Status == UpdateStatus.NoReleases) Main.Flash("Aún no hay versiones publicadas");
    }

    /// <summary>«Actualizar ahora…»: el modal «Roost X disponible», el mismo que abren la barra lateral y la bandeja.</summary>
    [RelayCommand]
    private Task UpdateNowAsync() => Updates.OpenAsync(Main);

    [RelayCommand]
    private Task OpenReleaseAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (Updates.Latest is { } latest) Shell.OpenUrl(latest.Page.ToString());
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task DownloadReleaseAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (Updates.DownloadUrl is { } url) Shell.OpenUrl(url.ToString());
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopyUpdateAsync() =>
        Updates.Command is { } command ? Main.CopyAsync(command, "Comando copiado") : Task.CompletedTask;
}
