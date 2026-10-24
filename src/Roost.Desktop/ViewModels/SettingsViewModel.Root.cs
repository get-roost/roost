using CommunityToolkit.Mvvm.Input;
using Roost.Core.Platform;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes › Rutas (J2): «Cambiar…» la raíz de Roost (<see cref="ChangeRootDialogViewModel"/>). La
/// carpeta de proyectos va siempre dentro, en <c>www</c>, así que se cambia con ella.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>De dónde sale la raíz: la de siempre, <c>[paths] root</c> o <c>ROOST_HOME</c>.</summary>
    public string RootSourceText
    {
        get
        {
            var home = Environment.GetEnvironmentVariable(RoostPaths.HomeVariable);
            var custom = !RoostPaths.SameFolder(Paths.Root, RoostPaths.HomeRoot());
            return custom ? $"[paths] root en {Paths.ConfigFile}"
                : string.IsNullOrWhiteSpace(home) ? "La de siempre" : $"La de {RoostPaths.HomeVariable}";
        }
    }

    [RelayCommand]
    private async Task ChangeRootAsync()
    {
        if (await Main.ShowDialogAsync(new ChangeRootDialogViewModel(Main)) is not true) return;
        // Todo lo abierto apunta a la raíz de antes: la app se vuelve a abrir con la nueva.
        if (App.Relaunch is { } relaunch) relaunch();
        else Main.Flash("Cierra Roost y vuelve a abrirlo para usar la raíz nueva");
    }
}
