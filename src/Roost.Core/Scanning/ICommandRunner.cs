namespace Roost.Core.Scanning;

/// <summary>
/// Una orden que ejecutar: el ejecutable y sus argumentos ya partidos (van por
/// <c>ProcessStartInfo.ArgumentList</c>, así que una ruta con espacios o unas comillas dentro
/// no son problema), la carpeta de trabajo, lo que cambia en el entorno, el tiempo máximo y lo
/// que se le escribe por stdin.
/// </summary>
/// <param name="Exe">Ruta del ejecutable, o su nombre si está en el PATH.</param>
/// <param name="Arguments">Los argumentos, uno por elemento, sin entrecomillar.</param>
/// <param name="WorkingDirectory">Carpeta de trabajo; null = la del proceso actual.</param>
/// <param name="Environment">
/// Variables que se añaden o cambian sobre el entorno heredado; un valor null quita la variable.
/// </param>
/// <param name="Timeout">Tiempo máximo; null = sin límite (se corta solo con el token).</param>
/// <param name="Stdin">Texto que se escribe por la entrada estándar y se cierra; null = sin stdin.</param>
public sealed record CommandSpec(
    string Exe,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    string? Stdin = null)
{
    /// <summary>La orden tal como se enseña en un log: el ejecutable y sus argumentos, con comillas donde hay espacios.</summary>
    public string Display => string.Join(' ', new[] { Exe }.Concat(Arguments).Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
}

/// <summary>Lo que dejó una orden: el código de salida, los dos flujos enteros y si se cortó por tiempo.</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    /// <summary>true si terminó por sí misma con código 0.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>Código de salida que se da a una orden matada por tiempo (no hay otro fiable).</summary>
    public const int TimedOutExitCode = -1;
}

/// <summary>
/// Ejecuta una orden y espera a que termine, dando las líneas según salen. Es lo que
/// <see cref="IProcessRunner"/> no da (código de salida, entorno, carpeta de trabajo, líneas en
/// vivo, stdin), para git, Composer, Node y lo que venga. Al revés que aquel, SÍ lanza:
/// <see cref="RoostException"/> si el ejecutable no se puede lanzar y
/// <see cref="OperationCanceledException"/> si lo cancela el token (en los dos casos, sin dejar
/// procesos colgados). Un timeout no lanza: vuelve con <see cref="CommandResult.TimedOut"/>.
/// </summary>
public interface ICommandRunner
{
    /// <param name="lines">
    /// Recibe cada línea de stdout y de stderr según llega (también las que terminan en
    /// <c>\r</c>, como el progreso de git). null si no interesa.
    /// </param>
    Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation);
}
