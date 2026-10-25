using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Stack;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// BD-2: exportar, importar o traer de Laragon, en directo. Los pasos (los de
/// <see cref="InstallDialogViewModel"/>), una barra con los bytes del fichero frente a su tamaño
/// (o solo los escritos, al exportar), lo que dicen el cliente o el volcador, «Cancelar» que mata
/// el árbol de procesos y, al acabar, el resultado; no se cierra solo, para poder leerlo. El
/// trabajo lo hace quien lo abre (<see cref="DatabasesViewModel"/>), que devuelve el texto final.
/// </summary>
public sealed partial class DbTransferDialogViewModel : DialogViewModel
{
    private const int MaxLines = 300;

    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<DbTransferDialogViewModel, CancellationToken, Task<string>> _work;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _watch = new();

    public DbTransferDialogViewModel(string title, string subtitle, IEnumerable<InstallStep> steps,
        Func<DbTransferDialogViewModel, CancellationToken, Task<string>> work)
    {
        Title = title;
        Subtitle = subtitle;
        Steps = new ObservableCollection<InstallStep>(steps);
        _work = work;
    }

    public override bool Wide => true;

    /// <summary>Mientras trabaja, ni Escape ni el fondo lo cierran: o termina, o se cancela.</summary>
    public override bool CanDismiss => Done;

    public string Title { get; }

    public string Subtitle { get; }

    public ObservableCollection<InstallStep> Steps { get; }

    public ObservableCollection<LogLine> Lines { get; } = [];

    public bool HasLines => Lines.Count > 0;

    /// <summary>Lo que pasa si se cancela (al importar, lo ya importado se queda); null lo oculta.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

    /// <summary>Lo que se ha visto por el camino (colaciones adaptadas, CREATE DATABASE quitados…).</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty] public partial string Phase { get; private set; } = "Preparando";
    [ObservableProperty] public partial double Percent { get; private set; }
    [ObservableProperty] public partial bool Indeterminate { get; private set; } = true;
    [ObservableProperty] public partial string BytesText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Running), nameof(Failed))]
    public partial bool Done { get; private set; }

    public bool Running => !Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    public partial bool Succeeded { get; private set; }

    public bool Failed => Done && !Succeeded;

    [ObservableProperty] public partial bool Canceled { get; private set; }
    [ObservableProperty] public partial string ResultText { get; private set; } = "";
    [ObservableProperty] public partial Tone ResultTone { get; private set; } = Tone.Neutral;

    /// <summary>true si terminó bien. Se cumple al acabar, aunque el diálogo ya esté cerrado.</summary>
    public Task<bool> Finished => _finished.Task;

    /// <summary>Lanza el trabajo. Una vez, antes o después de enseñar el diálogo.</summary>
    public async Task RunAsync()
    {
        _watch.Start();
        try
        {
            var text = await _work(this, _cancel.Token);
            foreach (var step in Steps) step.State = StepState.Done;
            Succeeded = true;
            Percent = 100;
            Indeterminate = false;
            Phase = "Hecho";
            (ResultText, ResultTone) = ($"{text} ({Took()}).", Tone.Ok);
        }
        catch (OperationCanceledException)
        {
            MarkFailed();
            Canceled = true;
            Phase = "Cancelado";
            (ResultText, ResultTone) = (CanceledText ?? "Cancelado: se ha parado el proceso y todo lo que había lanzado.", Tone.Warn);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MarkFailed();
            Phase = "No se ha terminado";
            (ResultText, ResultTone) = (FailureText is { } prefix ? $"{prefix} {ex.Message}" : ex.Message, Tone.Bad);
        }
        finally
        {
            Done = true;
            _finished.TrySetResult(Succeeded);
        }
    }

    /// <summary>Lo que se dice al cancelar; quien trabaja lo cambia según lo que haya pasado (si se borró la base de datos nueva…).</summary>
    public string? CanceledText { get; set; }

    /// <summary>Lo que va delante del error (lo que ha pasado con lo hecho hasta ahí).</summary>
    public string? FailureText { get; set; }

    private void MarkFailed()
    {
        foreach (var step in Steps.Where(s => s.State == StepState.Active)) step.State = StepState.Failed;
    }

    private string Took()
    {
        var elapsed = _watch.Elapsed;
        return elapsed.TotalSeconds < 60
            ? string.Format(Roost.Desktop.Services.Es.Culture, "{0:0.0} s", elapsed.TotalSeconds)
            : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
    }

    /// <summary>Pasa al paso <paramref name="index"/>: los anteriores quedan hechos.</summary>
    public void Step(int index, string? phase = null)
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Pending;
        if (phase is not null) Phase = phase;
    }

    /// <summary>Un paso más al final (los de Laragon salen según se sabe qué hay que hacer); devuelve su índice.</summary>
    public int AddStep(string label, string? hint = null)
    {
        Steps.Add(new InstallStep(label, hint));
        return Steps.Count - 1;
    }

    /// <summary>Los bytes del fichero hasta ahora frente al total (sin total, solo los bytes).</summary>
    public void Progress(DbTransferProgress progress, string? verb = null)
    {
        if (Done) return;   // un aviso que llega tarde, de otro hilo
        var es = Roost.Desktop.Services.Es.Culture;
        if (progress.Percent is { } percent)
        {
            Indeterminate = false;
            Percent = percent;
            BytesText = $"{Size(progress.Bytes, es)} de {Size(progress.Total!.Value, es)}";
        }
        else
        {
            Indeterminate = true;
            BytesText = $"{Size(progress.Bytes, es)}{(verb is null ? "" : " " + verb)}";
        }
    }

    /// <summary>Vuelve a la barra sin datos (entre un paso y otro).</summary>
    public void ResetProgress()
    {
        Indeterminate = true;
        Percent = 0;
        BytesText = "";
    }

    public static string Size(long bytes, IFormatProvider format) =>
        bytes >= 1L << 30 ? string.Format(format, "{0:0.0} GB", bytes / (double)(1L << 30))
        : bytes >= 1 << 20 ? string.Format(format, "{0:0.0} MB", bytes / (double)(1 << 20))
        : $"{Math.Max(bytes == 0 ? 0 : 1, bytes / 1024)} KB";

    /// <summary>Una línea del cliente, del volcador o de la copia de Laragon (llega de otro hilo).</summary>
    public void Line(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Dispatcher.UIThread.Post(() =>
        {
            Lines.Add(new LogLine(line, CommandOutputDialogViewModel.LevelOf(line)));
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
            OnPropertyChanged(nameof(HasLines));
        });
    }

    public void AddNote(string note)
    {
        Notes.Add(note);
        OnPropertyChanged(nameof(HasNotes));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Done) Close(Succeeded);
        else
        {
            Phase = "Cancelando…";
            _cancel.Cancel();
        }
    }

    [RelayCommand]
    private void Finish()
    {
        if (Done) Close(Succeeded);
    }
}

/// <summary>
/// Un <see cref="IProgress{T}"/> para la UI: en su hilo avisa en el acto (así los pasos salen en
/// orden) y desde otro lo pasa al hilo de la UI.
/// </summary>
internal sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        if (Dispatcher.UIThread.CheckAccess()) handler(value);
        else Dispatcher.UIThread.Post(() => handler(value));
    }
}