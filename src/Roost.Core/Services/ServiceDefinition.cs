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
