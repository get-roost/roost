using System.Collections.Concurrent;
using Roost.Core;

namespace Roost.WebPanel;

/// <summary>Un paso del diálogo «Instalando»: pendiente, en curso, hecho o fallido.</summary>
public sealed record JobStep(string Label, string? Detail, string State);

/// <summary>Lo que cuenta <c>GET /api/jobs/{id}</c>: el progreso de una tarea larga (instalar, importar…).</summary>
public sealed record JobView(
    string Id,
    string Title,
    string? Subtitle,
    string State,              // running | done | failed | cancelled
    string? Message,
    double? Progress,          // 0..1 si se sabe
    IReadOnlyList<JobStep> Steps,
    string? Error,
    object? Result,
    bool CanCancel);

/// <summary>Lo que ve la tarea mientras corre: pasos, mensaje y progreso.</summary>
public sealed class JobContext(Job job)
{
    public CancellationToken Cancellation => job.Cancellation.Token;

    public void Step(int index, string state, string? detail = null) => job.SetStep(index, state, detail);

    public void Message(string message) => job.Message = message;

    public void Progress(double? fraction) => job.Progress = fraction;

    /// <summary>Marca el paso <paramref name="index"/> en curso y los anteriores hechos.</summary>
    public void Begin(int index, string? message = null)
    {
        for (var i = 0; i < index; i++) job.SetStep(i, "done", null);
        job.SetStep(index, "active", null);
        if (message is not null) job.Message = message;
    }

    /// <summary>Marca todos los pasos hechos.</summary>
    public void Finish(string? message = null)
    {
        for (var i = 0; i < job.StepCount; i++) job.SetStep(i, "done", null);
        if (message is not null) job.Message = message;
        job.Progress = 1;
    }
}

public sealed class Job
{
    private readonly object _lock = new();
    private readonly List<JobStep> _steps;
    private string _state = "running";
    private string? _error;
    private object? _result;
    private string? _message;
    private double? _progress;

    internal CancellationTokenSource Cancellation { get; } = new();

    public string Id { get; } = Guid.NewGuid().ToString("n")[..12];
    public string Title { get; }
    public string? Subtitle { get; }
    public bool CanCancel { get; }
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; private set; }

    public Job(string title, string? subtitle, IReadOnlyList<(string Label, string? Detail)> steps, bool canCancel)
    {
        Title = title;
        Subtitle = subtitle;
        CanCancel = canCancel;
        _steps = steps.Select(s => new JobStep(s.Label, s.Detail, "pending")).ToList();
    }

    public int StepCount => _steps.Count;

    public string? Message
    {
        get { lock (_lock) return _message; }
        set { lock (_lock) _message = value; }
    }

    public double? Progress
    {
        get { lock (_lock) return _progress; }
        set { lock (_lock) _progress = value; }
    }

    public bool IsRunning { get { lock (_lock) return _state == "running"; } }

    internal void SetStep(int index, string state, string? detail)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _steps.Count) return;
            _steps[index] = _steps[index] with { State = state, Detail = detail ?? _steps[index].Detail };
        }
    }

    internal void Complete(object? result)
    {
        lock (_lock)
        {
            _state = "done";
            _result = result;
            Finished = DateTimeOffset.UtcNow;
        }
    }

    internal void Fail(string error, bool cancelled)
    {
        lock (_lock)
        {
            _state = cancelled ? "cancelled" : "failed";
            _error = error;
            Finished = DateTimeOffset.UtcNow;
            for (var i = 0; i < _steps.Count; i++)
                if (_steps[i].State == "active") _steps[i] = _steps[i] with { State = cancelled ? "pending" : "failed" };
        }
    }

    public JobView View()
    {
        lock (_lock)
            return new JobView(Id, Title, Subtitle, _state, _message, _progress, _steps.ToList(), _error, _result, CanCancel && _state == "running");
    }
}

/// <summary>
/// Las tareas largas del panel (instalar PHP o una base de datos, Xdebug, escanear): corren en el
/// proceso del panel, la página las sondea por id y puede cancelarlas. Las acabadas se guardan un rato
/// para que la página que se recarga vea el resultado.
/// </summary>
public sealed class Jobs
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job Start(string title, string? subtitle, IReadOnlyList<(string Label, string? Detail)> steps,
        Func<JobContext, Task<object?>> work, bool canCancel = true)
    {
        var job = new Job(title, subtitle, steps, canCancel);
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(new JobContext(job));
                job.Complete(result);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                job.Fail("Cancelado. No queda nada a medias.", cancelled: true);
            }
            catch (RoostException ex)
            {
                job.Fail(ex.Message, cancelled: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
            {
                job.Fail(ex.Message, cancelled: false);
            }
            finally
            {
                Prune();
            }
        });
        return job;
    }

    public Job? Find(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Las tareas en curso (para la nota «Instalando en segundo plano…» de cada pantalla).</summary>
    public IReadOnlyList<JobView> Running() => _jobs.Values.Where(j => j.IsRunning).OrderBy(j => j.Created).Select(j => j.View()).ToList();

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsRunning || !job.CanCancel) return false;
        job.Cancellation.Cancel();
        return true;
    }

    private void Prune()
    {
        var limit = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var (id, job) in _jobs)
            if (job.Finished is { } finished && finished < limit) _jobs.TryRemove(id, out _);
    }
}
