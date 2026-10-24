using Roost.Core.Scanning;

namespace Roost.Core.Tests;

/// <summary>
/// Un <see cref="ICommandRunner"/> guionizado: a cada prefijo de argumentos (<c>"ls-remote --symref"</c>,
/// <c>"clone --progress"</c>) se le da el codigo de salida, stdout, stderr y, si hace falta, algo que
/// hacer con la orden (crear ficheros en la carpeta destino, como haria un clone de verdad). Gana el
/// prefijo mas largo que case y, a igual longitud, el ultimo registrado (asi un guion puede cambiar
/// la respuesta a mitad de prueba). Apunta todas las llamadas.
/// </summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    private sealed record Step(string Prefix, int ExitCode, string Stdout, string Stderr, Action<CommandSpec>? OnRun, bool TimedOut);

    private readonly List<Step> _steps = [];

    public List<CommandSpec> Calls { get; } = [];

    public FakeCommandRunner On(string argumentPrefix, int exitCode = 0, string stdout = "", string stderr = "", Action<CommandSpec>? onRun = null, bool timedOut = false)
    {
        _steps.Add(new Step(argumentPrefix, exitCode, stdout, stderr, onRun, timedOut));
        return this;
    }

    /// <summary>Las ordenes ejecutadas, cada una como sus argumentos unidos por espacios.</summary>
    public IEnumerable<string> Commands => Calls.Select(c => string.Join(' ', c.Arguments));

    public Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Calls.Add(spec);
        var joined = string.Join(' ', spec.Arguments);
        var matches = _steps.Where(s => joined.StartsWith(s.Prefix, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) throw new InvalidOperationException($"FakeCommandRunner sin guion para: {spec.Exe} {joined}");
        var longest = matches.Max(s => s.Prefix.Length);
        var step = matches.Last(s => s.Prefix.Length == longest);

        step.OnRun?.Invoke(spec);
        if (lines is not null)
        {
            foreach (var line in step.Stdout.Split('\n')) if (line.Length > 0) lines.Report(line.TrimEnd('\r'));
            foreach (var line in step.Stderr.Split('\n')) if (line.Length > 0) lines.Report(line.TrimEnd('\r'));
        }
        return Task.FromResult(new CommandResult(step.TimedOut ? CommandResult.TimedOutExitCode : step.ExitCode, step.Stdout, step.Stderr, step.TimedOut));
    }
}
