using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Spectre.Console.Cli;

namespace Roost.Extensibility;

/// <summary>Una extensión que no se ha cargado o que ha fallado al usarla, y por qué.</summary>
public sealed record ExtensionError(string Source, string Message);

/// <summary>
/// Las extensiones cargadas, y la única puerta por la que Roost las llama. Lo que falla dentro de
/// una extensión no tumba Roost: se apunta en <see cref="Errors"/> y se sigue sin ella. La excepción
/// es el stack: si una extensión no puede preparar sus servicios, <c>up</c> falla diciendo cuál.
/// </summary>
public sealed class ExtensionSet
{
    private readonly List<ExtensionError> _errors;
    private readonly object _gate = new();

    public ExtensionSet(IEnumerable<IRoostExtension> loaded, IEnumerable<ExtensionError>? errors = null)
    {
        Loaded = loaded.ToList();
        _errors = errors?.ToList() ?? [];
    }

    public static ExtensionSet Empty => new([]);

    /// <summary>Las de <see cref="RoostPaths.Extensions"/>.</summary>
    public static ExtensionSet Load(RoostPaths paths) => ExtensionLoader.Load(paths.Extensions);

    public IReadOnlyList<IRoostExtension> Loaded { get; }

    public IReadOnlyList<ExtensionError> Errors
    {
        get { lock (_gate) return [.. _errors]; }
    }

    public IEnumerable<T> Of<T>() where T : class => Loaded.OfType<T>();

    /// <summary>Los servicios de todas las extensiones para este <c>up</c>.</summary>
    public IReadOnlyList<ServiceDefinition> StackServices(StackContext context)
    {
        var services = new List<ServiceDefinition>();
        foreach (var extension in Of<IStackExtension>())
        {
            try
            {
                services.AddRange(extension.Services(context));
            }
            catch (Exception ex) when (ex is not RoostException)
            {
                throw new RoostException($"La extension {extension.Name} no ha podido preparar sus servicios: {ex.Message}");
            }
        }
        return services;
    }

    /// <summary><paramref name="dataset"/> con los avisos de las extensiones encima.</summary>
    public AuditDataset WithAdvisories(AuditDataset dataset, RoostPaths paths)
    {
        foreach (var extension in Of<IAuditExtension>())
        {
            var extra = Guard(extension, "sus avisos", () => extension.Advisories(paths));
            if (extra is not null) dataset = dataset.Merge(extra);
        }
        return dataset;
    }

    /// <summary>El motor de escaneo con los scanners de Roost y los de las extensiones.</summary>
    public EnvironmentScanner Scanner(RoostPaths paths) =>
        EnvironmentScanner.CreateDefault(Of<IAuditExtension>()
            .SelectMany(e => Guard(e, "sus scanners", () => e.Scanners(paths).ToList()) ?? []));

    /// <summary>Los comandos de las extensiones en la CLI.</summary>
    public void AddCommands(IConfigurator config)
    {
        foreach (var extension in Of<ICliExtension>())
            Guard<object?>(extension, "sus comandos", () =>
            {
                extension.AddCommands(config);
                return null;
            });
    }

    /// <summary>Llama a una extensión; si lanza, lo apunta y devuelve null.</summary>
    public T? Guard<T>(IRoostExtension extension, string what, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            lock (_gate) _errors.Add(new ExtensionError(extension.Name, $"fallo al dar {what}: {ex.Message}"));
            return default;
        }
    }
}
