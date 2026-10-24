using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «Quitar tienda.test» con la casilla del texto 15 del diseño de git: «Quitar también la clave SSH
/// y el token de este sitio», marcada, si tiene clave de Roost o token propios (lo que comparta con
/// otro sitio se queda, y se dice). Devuelve null si se cancela; si no, si hay que quitarlos.
/// </summary>
public sealed partial class RemoveSiteDialogViewModel(string host, string text, IReadOnlyList<string> removing, IReadOnlyList<string> kept) : DialogViewModel
{
    public string Title => $"Quitar {host}";

    public string Text => text;

    /// <summary>Lo que se quitaría con la casilla: su clave de Roost y su token.</summary>
    public IReadOnlyList<string> Removing => removing;

    /// <summary>Lo que se queda porque lo usa otro sitio.</summary>
    public IReadOnlyList<string> Kept => kept;

    public bool HasAccess => removing.Count > 0;

    public bool HasKept => kept.Count > 0;

    [ObservableProperty] public partial bool RemoveAccess { get; set; } = true;

    [RelayCommand]
    private void Confirm() => Close(HasAccess && RemoveAccess);

    [RelayCommand]
    private void Cancel() => Close();
}
