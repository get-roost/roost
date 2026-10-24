namespace Roost.Core.Services;

/// <summary>Un proceso que el supervisor mantiene vivo (ROOST.md §5.5): php-cgi, nginx…</summary>
public sealed record ServiceDefinition
{
    /// <summary>Nombre único dentro del stack: <c>php-8.4-0</c>, <c>nginx</c>.</summary>
    public required string Name { get; init; }

    public required string Executable { get; init; }

    public string Arguments { get; init; } = "";

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Fichero donde van stdout y stderr del proceso (se añade al final).</summary>
    public string? LogFile { get; init; }

    /// <summary>Puerto en el que tiene que aceptar conexiones para considerarse sano.</summary>
    public int? Port { get; init; }

    /// <summary>
    /// Cómo pedirle que se pare limpio (ROOST.md §5.5: a la base de datos se le pide
    /// <c>shutdown</c> antes de matarla). null = se mata directamente.
    /// </summary>
    public ServiceDefinition? StopCommand { get; init; }

    /// <summary>Lo que se espera a que salga solo tras <see cref="StopCommand"/> antes de matarlo.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// En Linux y macOS, la señal con la que se le pide que se pare limpio antes de matarlo (nginx y
    /// php-fpm: <see cref="StopSignal.Quit"/>). <see cref="StopSignal.Kill"/> = se mata directamente,
    /// como en Windows, donde no hay señales.
    /// </summary>
    public StopSignal StopSignal { get; init; } = StopSignal.Kill;

    /// <summary>
    /// Iguales si lanzan el mismo proceso de la misma forma, con el entorno comparado por contenido:
    /// la igualdad de serie de un record compararía el diccionario por referencia, y cada <c>up</c>
    /// crea uno nuevo. Así compara la recarga selectiva (E2) el plan nuevo con el que está en marcha:
    /// un servicio igual (también el de una extensión) no se toca.
    /// </summary>
    public bool Equals(ServiceDefinition? other) =>
        other is not null
        && Name == other.Name
        && Executable == other.Executable
        && Arguments == other.Arguments
        && WorkingDirectory == other.WorkingDirectory
        && LogFile == other.LogFile
        && Port == other.Port
        && StopTimeout == other.StopTimeout
        && StopSignal == other.StopSignal
        && Equals(StopCommand, other.StopCommand)
        && Environment.Count == other.Environment.Count
        && Environment.All(e => other.Environment.TryGetValue(e.Key, out var value) && value == e.Value);

    public override int GetHashCode() => HashCode.Combine(Name, Executable, Arguments, WorkingDirectory, Port);
}

/// <summary>Cómo se le pide a un proceso que se pare (POSIX); en Windows siempre se mata.</summary>
public enum StopSignal
{
    Kill,   // SIGKILL al árbol, sin aviso (lo de siempre)
    Term,   // SIGTERM: «termina»
    Quit,   // SIGQUIT: parada ordenada (nginx, php-fpm: acaban lo que están sirviendo)
    Int,    // SIGINT: como Ctrl+C
}

public enum ServiceState
{
    Running,     // proceso vivo
    Restarting,  // se ha caído y se va a relanzar
    Failed,      // no arranca o se cae en bucle: el supervisor deja de intentarlo
    Stopped,     // parado a propósito
}

/// <summary>Foto del estado de un servicio para <c>roost status</c>.</summary>
public sealed record ServiceStatus(
    string Name,
    ServiceState State,
    int? ProcessId,
    int Restarts,
    int? Port,
    string? LastError);

/// <summary>Un proceso lanzado; abstraído para testear el supervisor sin binarios reales.</summary>
public interface IServiceProcess : IDisposable
{
    int Id { get; }

    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellation);

    /// <summary>Mata el proceso y sus hijos. No lanza si ya había muerto.</summary>
    void Kill();
}

public interface IProcessLauncher
{
    /// <summary>Arranca el proceso; lanza si ni siquiera se puede crear (ejecutable que no existe…).</summary>
    IServiceProcess Start(ServiceDefinition definition);
}
