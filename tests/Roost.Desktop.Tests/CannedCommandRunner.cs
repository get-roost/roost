using Roost.Core.Scanning;

namespace Roost.Desktop.Tests;

/// <summary>
/// Un <see cref="ICommandRunner"/> que contesta al momento con lo guardado para el primer argumento
/// de la orden (<c>-v</c>, <c>-i</c>, <c>-m</c>): para el php.exe de las pantallas de PHP. Apunta las
/// llamadas; una orden sin respuesta guardada hace fallar el test. Nada se ejecuta.
/// </summary>
internal sealed class CannedCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, (string Stdout, string Stderr)> _answers = [];

    public List<CommandSpec> Calls { get; } = [];

    public CannedCommandRunner On(string firstArgument, string stdout, string stderr = "")
    {
        _answers[firstArgument] = (stdout, stderr);
        return this;
    }

    public Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Calls.Add(spec);
        var first = spec.Arguments.FirstOrDefault() ?? "";
        if (!_answers.TryGetValue(first, out var answer))
            throw new InvalidOperationException($"CannedCommandRunner sin respuesta para: {spec.Display}");
        return Task.FromResult(new CommandResult(0, answer.Stdout, answer.Stderr, false));
    }
}
