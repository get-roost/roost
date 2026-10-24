using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Git;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>Un paso de una operación de git: su texto, una pista que cambia mientras va («1.284 objetos») y su estado.</summary>
public sealed partial class GitStep(string label, string? hint = null) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; set; } = hint;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

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

/// <summary>
/// «Clonando…», «Actualizando…», «Cambiando de rama…»: los pasos de una operación de git con su
/// porcentaje (el de <c>--progress</c>), la promesa de lo que pasa si falla, segundo plano y Cancelar
/// (que mata git y todo lo que lanzó). Si falla, dice por qué con el texto del diseño (lo elige quien
/// lo abre, por <see cref="GitFailure.Kind"/>), la línea de git y un botón al registro del sitio. Se
/// cierra solo si acaba bien.
/// </summary>
public sealed partial class GitTaskDialogViewModel : DialogViewModel
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<GitTaskDialogViewModel, CancellationToken, Task> _work;
    private readonly Func<Exception, string> _explain;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="explain">El texto para el usuario de un fallo (con tildes; el de Core no las lleva).</param>
    public GitTaskDialogViewModel(string title, string subtitle, IReadOnlyList<GitStep> steps,
        Func<GitTaskDialogViewModel, CancellationToken, Task> work, Func<Exception, string> explain)
    {
        Title = title;
        Subtitle = subtitle;
        Steps = steps;
        _work = work;
        _explain = explain;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<GitStep> Steps { get; }

    /// <summary>Lo que se promete abajo mientras va («Si algo falla, no queda nada a medias…»).</summary>
    public string Note { get; init; } = "Si algo falla, no queda nada a medias.";

    /// <summary>Lo que se dice si se cancela.</summary>
    public string CanceledText { get; init; } = "Cancelado. No se ha quedado nada a medias.";

    /// <summary>«Seguir en segundo plano»: quien lo abre espera a <see cref="Finished"/> y avisa del resultado.</summary>
    public bool CanBackground { get; init; }

    /// <summary>«Abrir el registro» al fallar (el <c>git-&lt;sitio&gt;.log</c>); null lo oculta.</summary>
    public Func<Task>? OpenLog { get; init; }

    public bool HasLog => OpenLog is not null;

    [ObservableProperty] public partial string Phase { get; private set; } = "Preparando";

    [ObservableProperty] public partial double Percent { get; private set; }

    [ObservableProperty] public partial bool Indeterminate { get; private set; } = true;

    /// <summary>Lo de la derecha de la fase: «12.40 MiB | 3.10 MiB/s», «40 %».</summary>
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    /// <summary>La última línea de git, en pequeño (lo que está haciendo).</summary>
    [ObservableProperty] public partial string LastLine { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    public partial string? Error { get; private set; }

    /// <summary>La línea de git que lo explica (en mono, debajo del texto).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    public partial string? ErrorDetail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Running), nameof(ShowCancel), nameof(ShowBackground), nameof(ShowLog))]
    public partial bool Done { get; private set; }

    /// <summary>Si se puede cancelar ahora (tras clonar, registrar el sitio ya no se deja a medias).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCancel))]
    public partial bool Cancelable { get; set; } = true;

    public bool Failed => Error is not null;

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    public bool Running => !Done;

    public bool ShowCancel => Running && Cancelable;

    public bool ShowBackground => Running && CanBackground;

    public bool ShowLog => Done && Failed && HasLog;

    public bool HasLastLine => LastLine.Length > 0;

    partial void OnLastLineChanged(string value) => OnPropertyChanged(nameof(HasLastLine));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ShowLog));

    /// <summary>Mientras va, ni Escape ni el fondo lo cierran.</summary>
    public override bool CanDismiss => Done;

    /// <summary>true si terminó bien; false si falló o se canceló.</summary>
    public Task<bool> Finished => _finished.Task;

    /// <summary>Lanza el trabajo. Se cierra solo si acaba bien; si falla, enseña por qué.</summary>
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
            Fail(CanceledText, null);
        }
        catch (Exception ex) when (ex is RoostException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            Fail(_explain(ex), (ex as GitException)?.Failure.Detail);
        }
    }

    private void Fail(string message, string? detail)
    {
        foreach (var step in Steps.Where(s => s.State == StepState.Active)) step.State = StepState.Failed;
        Error = message;
        ErrorDetail = string.IsNullOrWhiteSpace(detail) ? null : "git: " + detail;
        Phase = "No se ha terminado";
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
        ProgressText = "";
        LastLine = "";
        Indeterminate = true;
    }

    /// <summary>Lo que cuenta <c>--progress</c>: la fase, el porcentaje total y lo que lleva.</summary>
    public void Progress(CloneProgress progress)
    {
        Phase = progress.Stage;
        Indeterminate = false;
        Percent = progress.Overall;
        ProgressText = progress.Detail is { } detail ? $"{detail} · {progress.Overall} %" : $"{progress.Overall} %";
    }

    /// <summary>Una línea de git (también las de progreso). Devuelve el total de objetos si la línea lo dice.</summary>
    public int? Line(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;
        LastLine = text.Length > 140 ? text[..140] + "…" : text;
        return ObjectsPattern().Match(text) is { Success: true } m && int.TryParse(m.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : null;
    }

    [GeneratedRegex(@"^(?:remote: )?(?:Receiving|Counting|Enumerating) objects:\s*\d+%\s*\(\d+/(?<total>\d+)\)")]
    private static partial Regex ObjectsPattern();

    [RelayCommand]
    private void Background()
    {
        if (CanBackground && Running) Close(null);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Done) Close(false);
        else if (Cancelable) _cancel.Cancel();
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        if (OpenLog is null) return;
        Close(false);
        await OpenLog();
    }
}
