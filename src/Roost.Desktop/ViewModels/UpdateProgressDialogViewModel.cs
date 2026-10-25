using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «Actualizando a Roost X»: los pasos (descargar con su progreso, verificar el SHA-256 con el
/// SHA256SUMS.txt de la release, comprobar la firma, lanzar el instalador), con el estilo del
/// diálogo de instalación. Si algo falla, lo dice y no se ejecuta nada; mientras trabaja, no se
/// puede cerrar (solo cancelar). Al terminar bien se queda abierto diciendo que Roost se cierra
/// (<see cref="CloseOnSuccess"/> false) o se cierra con true.
/// </summary>
public sealed partial class UpdateProgressDialogViewModel : DialogViewModel
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<UpdateProgressDialogViewModel, CancellationToken, Task> _work;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateProgressDialogViewModel(string title, string subtitle, long? totalBytes, IReadOnlyList<InstallStep> steps, string note,
        Func<UpdateProgressDialogViewModel, CancellationToken, Task> work)
    {
        Title = title;
        Subtitle = subtitle;
        TotalBytes = totalBytes;
        Steps = new ObservableCollection<InstallStep>(steps);
        Note = note;
        _work = work;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public long? TotalBytes { get; }

    /// <summary>Los pasos; <see cref="Detail"/> cambia la pista de uno (lo que dijo la firma).</summary>
    public ObservableCollection<InstallStep> Steps { get; }

    /// <summary>El aviso de abajo mientras trabaja («Roost se cerrará un momento; Windows te pedirá permiso»).</summary>
    public string Note { get; }

    /// <summary>Si al acabar bien se cierra solo (el zip: luego se dice dónde está) o se queda (el instalador: la app sale).</summary>
    public bool CloseOnSuccess { get; init; } = true;

    [ObservableProperty]
    public partial string Phase { get; set; } = "Preparando";

    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial bool Indeterminate { get; private set; } = true;

    [ObservableProperty]
    public partial string BytesText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed), nameof(Succeeded))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Running), nameof(Succeeded))]
    public partial bool Done { get; private set; }

    public bool Failed => Error is not null;

    public bool Running => !Done;

    public bool Succeeded => Done && !Failed;

    /// <summary>Mientras trabaja, Escape y el fondo no lo cierran; al terminar bien con la app saliendo, tampoco.</summary>
    public override bool CanDismiss => Done && (Failed || CloseOnSuccess);

    /// <summary>true si terminó bien; false si falló o se canceló.</summary>
    public Task<bool> Finished => _finished.Task;

    public async Task RunAsync()
    {
        try
        {
            await _work(this, _cancel.Token);
            foreach (var step in Steps) step.State = StepState.Done;
            Percent = 100;
            Indeterminate = false;
            Done = true;
            _finished.TrySetResult(true);
            if (CloseOnSuccess) Close(true);
        }
        catch (OperationCanceledException)
        {
            Fail("Cancelado. No se ha ejecutado nada: Roost sigue como estaba.");
        }
        catch (Exception ex) when (ex is RoostException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    private void Fail(string message)
    {
        foreach (var step in Steps.Where(s => s.State == StepState.Active)) step.State = StepState.Failed;
        Error = message;
        Phase = "No se ha actualizado";
        Indeterminate = false;
        Done = true;
        _finished.TrySetResult(false);
    }

    /// <summary>Pasa al paso <paramref name="index"/>: los anteriores quedan hechos.</summary>
    public void Step(int index, string? phase = null)
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Pending;
        if (phase is not null) Phase = phase;
        if (index > 0) Indeterminate = TotalBytes is null;
    }

    /// <summary>Cambia la pista del paso <paramref name="index"/> («· sin firma: …»), conservando su estado.</summary>
    public void Detail(int index, string hint)
    {
        var old = Steps[index];
        Steps[index] = new InstallStep(old.Label, hint) { State = old.State };
    }

    /// <summary>Bytes descargados hasta ahora.</summary>
    public void Downloaded(long bytes)
    {
        var es = Roost.Desktop.Services.Es.Culture;
        if (TotalBytes is > 0 and var total)
        {
            Indeterminate = false;
            Percent = Math.Min(100, bytes * 100.0 / total);
            BytesText = string.Format(es, "{0:0.0} de {1:0.0} MB", bytes / 1048576.0, total / 1048576.0);
        }
        else
        {
            BytesText = string.Format(es, "{0:0.0} MB", bytes / 1048576.0);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Done) Close(false);
        else _cancel.Cancel();
    }
}
