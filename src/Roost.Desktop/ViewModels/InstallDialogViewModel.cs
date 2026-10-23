using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>Un paso de la instalación: pendiente, en curso, hecho o fallido.</summary>
public sealed partial class InstallStep(string label, string? hint) : ObservableObject
{
    public string Label { get; } = label;

    public string? Hint { get; } = hint;

    public bool HasHint => Hint is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone), nameof(IsActive), nameof(IsDone))]
    public partial StepState State { get; set; }

    public bool IsActive => State == StepState.Active;

    public bool IsDone => State == StepState.Done;

    public Tone Tone => State switch
    {
        StepState.Done => Tone.Ok,
        StepState.Active => Tone.Accent,
        StepState.Failed => Tone.Bad,
        _ => Tone.Neutral,
    };
}

public enum StepState
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>
/// «Instalando…»: descarga con su progreso, los pasos (verificar el SHA-256 antes de extraer,
/// extraer, comprobar) y la promesa de que si algo falla no queda nada a medias. Se puede
/// seguir en segundo plano o cancelar. Lo comparten PHP, las bases de datos y nginx.
/// </summary>
public sealed partial class InstallDialogViewModel : DialogViewModel
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<InstallDialogViewModel, CancellationToken, Task> _work;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InstallDialogViewModel(
        string title, string subtitle, long? totalBytes, IReadOnlyList<InstallStep> steps,
        Func<InstallDialogViewModel, CancellationToken, Task> work)
    {
        Title = title;
        Subtitle = subtitle;
        TotalBytes = totalBytes;
        Steps = steps;
        _work = work;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public long? TotalBytes { get; }

    public IReadOnlyList<InstallStep> Steps { get; }

    /// <summary>Casilla opcional («Al terminar, pasar a 8.3.36 los sitios…»); null la oculta.</summary>
    public string? OptionText { get; init; }

    public bool HasOption => OptionText is not null;

    [ObservableProperty]
    public partial bool OptionChecked { get; set; }

    [ObservableProperty]
    public partial string Phase { get; private set; } = "Preparando";

    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial bool Indeterminate { get; private set; } = true;

    [ObservableProperty]
    public partial string BytesText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Running))]
    public partial bool Done { get; private set; }

    public bool Failed => Error is not null;

    public bool Running => !Done;

    /// <summary>La pantalla que lo lanzó lo usa para la línea de «instalando en segundo plano».</summary>
    public string ShortStatus => Done ? "" : TotalBytes is > 0 && !Indeterminate ? $"{Title}... {Percent:0} %" : $"{Title}...";

    /// <summary>Mientras instala, Escape y el fondo no lo cierran: o segundo plano, o cancelar.</summary>
    public override bool CanDismiss => Done;

    /// <summary>true si terminó bien; false si falló o se canceló.</summary>
    public Task<bool> Finished => _finished.Task;

    /// <summary>Lanza el trabajo. El diálogo se cierra solo si acaba bien; si falla, enseña el error.</summary>
    public async Task RunAsync()
    {
        try
        {
            await _work(this, _cancel.Token);
            foreach (var step in Steps) step.State = StepState.Done;
            Phase = "Hecho";
            Percent = 100;
            Indeterminate = false;
            Done = true;
            _finished.TrySetResult(true);
            Close(true);
        }
        catch (OperationCanceledException)
        {
            Fail("Cancelado. No se ha quedado nada a medias.");
        }
        catch (Exception ex) when (ex is RoostException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(ShortStatus));
        }
    }

    private void Fail(string message)
    {
        foreach (var step in Steps.Where(s => s.State == StepState.Active)) step.State = StepState.Failed;
        Error = message;
        Phase = "No se ha instalado";
        Done = true;
        _finished.TrySetResult(false);
    }

    /// <summary>Pasa al paso <paramref name="index"/>: los anteriores quedan hechos.</summary>
    public void Step(int index, string? phase = null)
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Pending;
        if (phase is not null) Phase = phase;
        if (index > 1) Indeterminate = TotalBytes is null;
        OnPropertyChanged(nameof(ShortStatus));
    }

    /// <summary>Bytes descargados hasta ahora (lo llama el <c>IProgress</c> del instalador).</summary>
    public void Downloaded(long bytes)
    {
        var es = CultureInfo.GetCultureInfo("es-ES");
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
        OnPropertyChanged(nameof(ShortStatus));
    }

    [RelayCommand]
    private void Background() => Close(null);

    [RelayCommand]
    private void Cancel()
    {
        if (Done) Close(false);
        else _cancel.Cancel();
    }

    /// <summary>"32.5MB" (como lo da windows.php.net) en bytes; null si no se entiende.</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        var (number, factor) = t.EndsWith("GB") ? (t[..^2], 1L << 30) : t.EndsWith("MB") ? (t[..^2], 1L << 20) : t.EndsWith("KB") ? (t[..^2], 1L << 10) : (t, 1L);
        return double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? (long)(value * factor) : null;
    }
}
