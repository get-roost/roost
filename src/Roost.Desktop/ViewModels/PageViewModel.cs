using CommunityToolkit.Mvvm.ComponentModel;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Una pantalla de la barra lateral. Se crea una vez y se conserva al navegar, así que lo que
/// tenga seleccionado sigue ahí al volver.
/// </summary>
public abstract partial class PageViewModel(MainViewModel main) : ObservableObject
{
    protected MainViewModel Main { get; } = main;

    public abstract string Title { get; }

    [ObservableProperty]
    public partial string? Subtitle { get; protected set; }

    /// <summary>Al entrar en la pantalla: recargar lo que se lee del disco o de la red.</summary>
    public virtual Task ShownAsync() => Task.CompletedTask;

    /// <summary>Al salir de la pantalla (para dejar de vigilar ficheros y demás).</summary>
    public virtual void Hidden()
    {
    }

    /// <summary>Cuando cambia lo que cuenta el daemon (cada pocos segundos, solo si cambia algo).</summary>
    public virtual void StackChanged()
    {
    }
}
