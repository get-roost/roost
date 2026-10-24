using Roost.Core.Scanning;

namespace Roost.Desktop.Tests;

/// <summary>
/// Un <see cref="ICommandRunner"/> para las vistas: da sus primeras líneas al momento y se queda
/// «en marcha» hasta que el test dice cómo acaba (<see cref="Finish"/>) o hasta que se cancela, como
/// un proceso de verdad. Nada se ejecuta.
/// </summary>
internal sealed class ScriptedCommandRunner(params string[] firstLines) : ICommandRunner
{
    private readonly TaskCompletionSource<CommandResult> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProgress<string>? _lines;

    public List<CommandSpec> Calls { get; } = [];

    public bool WasCanceled { get; private set; }

    public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        Calls.Add(spec);
        _lines = lines;
        foreach (var line in firstLines) lines?.Report(line);
        using var registration = cancellation.Register(() =>
        {
            WasCanceled = true;
            _end.TrySetCanceled(cancellation);
        });
        return await _end.Task;
    }

    /// <summary>Otra línea, como si la escribiera el proceso.</summary>
    public void Report(string line) => _lines?.Report(line);

    public void Finish(int exitCode, string stderr = "") => _end.TrySetResult(new CommandResult(exitCode, "", stderr, false));
}
