using CommunityToolkit.Mvvm.Input;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes y sistema (C4): abrir la carpeta del sitio en el editor elegido en Ajustes › Programas
/// (o en el primero que haya). Sin ningún editor en el equipo, se abre en el explorador.
/// </summary>
public sealed partial class SitesViewModel
{
    [RelayCommand]
    private Task OpenInEditorAsync() => Main.RunAsync("No se pudo abrir el editor", () =>
    {
        if (Selected is { } row) Shell.OpenInEditor(row.Site.Folder);
        return Task.CompletedTask;
    });
}
