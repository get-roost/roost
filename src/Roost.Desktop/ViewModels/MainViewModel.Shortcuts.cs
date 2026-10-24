using CommunityToolkit.Mvvm.Input;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Ajustes y sistema (oleada 5), J5: los atajos de teclado de la ventana. Los pone MainWindow
/// (KeyBindings) y se cuentan en el tooltip de su entrada de la barra lateral y en Ajustes.
/// Con un diálogo abierto no hacen nada: Escape es del diálogo y lo demás esperaría a que se cierre.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Los atajos, para Ajustes › General y los tooltips.</summary>
    public const string ShortcutsText = "Ctrl+N nuevo sitio · F5 actualizar la pantalla · Ctrl+L registros · Ctrl+, ajustes";

    /// <summary>Ctrl+N: Sitios y el diálogo «Nuevo sitio», como el botón de Inicio.</summary>
    [RelayCommand]
    private async Task NewSiteShortcutAsync()
    {
        if (HasDialog) return;
        await NavigateAsync(Sites);
        await Sites.NewSiteCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// F5: vuelve a leer lo que enseña la pantalla actual (y lo que cuenta el daemon). En Auditoría,
    /// vuelve a escanear el equipo, como su botón.
    /// </summary>
    [RelayCommand]
    private async Task RefreshPageAsync()
    {
        if (HasDialog) return;
        await Stack.RefreshAsync();
        NotifyConfigChanged();
        if (Current == AuditPage)
            await Audit.ScanAsync();
        else
            await Current.ShownAsync();
    }

    /// <summary>Ctrl+L: Registros.</summary>
    [RelayCommand]
    private Task LogsShortcutAsync() => HasDialog ? Task.CompletedTask : NavigateAsync(Logs);

    /// <summary>Ctrl+,: Ajustes (como en VS Code y compañía).</summary>
    [RelayCommand]
    private Task SettingsShortcutAsync() => HasDialog ? Task.CompletedTask : NavigateAsync(Settings);
}

/// <summary>El tooltip de las entradas de la barra lateral que tienen atajo (J5).</summary>
public sealed partial class NavItem
{
    public string? Tip => Page switch
    {
        SitesViewModel => "Sitios · Ctrl+N: nuevo sitio",
        LogsViewModel => "Registros · Ctrl+L",
        SettingsViewModel => "Ajustes · Ctrl+,",
        AuditViewModel => "Auditoría · F5 en la pantalla: volver a escanear",
        _ => null,
    };
}
