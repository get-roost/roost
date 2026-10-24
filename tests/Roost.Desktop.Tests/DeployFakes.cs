using Roost.Core.Scanning;

namespace Roost.Desktop.Tests;

/// <summary>
/// Pasos y plantillas: un <see cref="ICommandRunner"/> que contesta cada llamada con el siguiente
/// resultado de la lista (y el último para las que sobren), diciendo antes sus líneas. Para ver un
/// paso que falla y, al reintentarlo, va bien. Nada se ejecuta.
/// </summary>
internal sealed class SequenceCommandRunner(params (int ExitCode, string[] Lines)[] results) : ICommandRunner
{
    private int _next;

    public List<CommandSpec> Calls { get; } = [];

    public Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Calls.Add(spec);
        var (code, output) = results[Math.Min(_next++, results.Length - 1)];
        foreach (var line in output) lines?.Report(line);
        return Task.FromResult(new CommandResult(code, string.Join('\n', output), "", false));
    }
}

/// <summary>
/// El cliente de la base de datos de los pasos: contesta a la sonda (si existe y cuántas tablas) con
/// lo que se le diga, y a crear o listar como un servidor vacío. Apunta lo que se le pide.
/// </summary>
internal sealed class FakeDbClient(bool exists = true, int tables = 0) : IProcessRunner
{
    public List<string> Calls { get; } = [];

    public string? Run(string executablePath, string arguments, TimeSpan timeout)
    {
        lock (Calls) Calls.Add(arguments);
        if (arguments.Contains("information_schema.schemata", StringComparison.Ordinal))
            return $"{(exists ? 1 : 0)}\n{tables}\nroost-ok\n";
        return "roost-ok\n";
    }
}
