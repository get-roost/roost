using CommunityToolkit.Mvvm.Input;

namespace Roost.Desktop.ViewModels;

/// <summary>Git-app: la tarjeta Git de Ajustes, que lleva a Ajustes › Git (<see cref="GitSettingsViewModel"/>).</summary>
public sealed partial class SettingsViewModel
{
    [RelayCommand]
    private Task GoGitAsync() => Main.NavigateToAsync(Main.GitSettings);
}
