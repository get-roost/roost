using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Un diálogo sobre la ventana (el «shade» del mockup). Se abre con
/// <see cref="MainViewModel.ShowDialogAsync"/>, que espera a que se cierre con un resultado.
/// </summary>
public abstract partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<object?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<object?> Closed => _closed.Task;

    /// <summary>El ancho del mockup: 560 px, o 720 px los que llevan logs o tablas.</summary>
    public virtual bool Wide => false;

    /// <summary>Escape y el fondo cierran el diálogo, salvo mientras hace algo que no se puede dejar a medias.</summary>
    public virtual bool CanDismiss => true;

    public void Close(object? result = null) => _closed.TrySetResult(result);

    [RelayCommand]
    private void Dismiss()
    {
        if (CanDismiss) Close();
    }
}

/// <summary>Un botón de <see cref="MessageDialogViewModel"/>: texto, lo que devuelve y su estilo.</summary>
public sealed record DialogButton(string Label, object? Result, string Classes = "", bool Left = false)
{
    public bool IsPrimary => Classes.Contains("primary", StringComparison.Ordinal);
    public bool IsDangerFill => Classes.Contains("danger-fill", StringComparison.Ordinal);
}

/// <summary>
/// Diálogo de mensaje o confirmación con el aspecto del mockup: icono en su color, título,
/// subtítulo, texto y botones al pie.
/// </summary>
public sealed partial class MessageDialogViewModel : DialogViewModel
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string? Text { get; init; }

    /// <summary>Texto en mono debajo (una ruta, un error tal cual).</summary>
    public string? Detail { get; init; }

    public Tone Tone { get; init; } = Tone.Accent;

    public required IReadOnlyList<DialogButton> Buttons { get; init; }

    public IEnumerable<DialogButton> LeftButtons => Buttons.Where(b => b.Left);

    public IEnumerable<DialogButton> RightButtons => Buttons.Where(b => !b.Left);

    [RelayCommand]
    private void Choose(DialogButton button) => Close(button.Result);
}
