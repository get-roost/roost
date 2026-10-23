using System.Runtime.InteropServices;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Un candidato a binario: una ruta (que puede no existir aún) y su origen declarado
/// ("PATH", "Laragon", "XAMPP"…). El origen lo decide quien enumera, por la fuente.
/// </summary>
public readonly record struct Candidate(string Path, string Source);

/// <summary>
/// Base común de los scanners: descubre candidatos, los deduplica por ruta real,
/// ejecuta el binario con timeout para leer su versión y delega el parseo. Contiene
/// TODAS las excepciones: el contrato de <see cref="IComponentScanner.Scan"/> es que
/// no lance nunca, ni siquiera durante la enumeración diferida.
/// </summary>
public abstract class ComponentScannerBase : IComponentScanner
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    protected ComponentScannerBase(IProcessRunner runner) => Runner = runner;

    protected IProcessRunner Runner { get; }

    public abstract ComponentKind Kind { get; }

    /// <summary>Soportamos Windows, Linux y macOS; cada scanner adapta sus rutas por dentro.</summary>
    public virtual bool SupportsCurrentOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Flag que se le pasa al binario para que imprima su versión (<c>-v</c>, <c>--version</c>).</summary>
    protected abstract string VersionArguments { get; }

    /// <summary>Nombres del ejecutable para resolver cuál es el "activo" del PATH.</summary>
    protected abstract IReadOnlyList<string> PathExecutableNames { get; }

    /// <summary>Rutas candidatas (con su origen). Pueden no existir ni ser válidas.</summary>
    protected abstract IEnumerable<Candidate> EnumerateCandidates();

    /// <summary>Construye el componente a partir de la salida de versión. <c>null</c> si no vale.</summary>
    protected abstract DetectedComponent? Build(Candidate candidate, string? versionOutput, bool isOnPath);

    /// <summary>La ruta real con la que se quitan duplicados (los tests la sustituyen para simular enlaces).</summary>
    protected virtual string RealPath(string path) => CanonicalPath.Of(path);

    /// <summary>
    /// Lo instalado por el propio Roost en <c>bin/&lt;carpeta&gt;/*/&lt;ejecutable&gt;</c>, con origen
    /// "Roost". Se saltan sus carpetas de trabajo (<c>.tmp-…</c>, <c>.trash-…</c>), que pueden
    /// tener un binario a medio extraer o borrar. Nunca lanza: sin raíz calculable, nada.
    /// </summary>
    protected static IEnumerable<Candidate> RoostCandidates(string binFolder, string relativeExecutable)
    {
        string bin;
        try { bin = Path.Combine(RoostPaths.Default().Root, "bin", binFolder); }
        catch { return []; }

        return Globber.Expand(Path.Combine(bin, "*", relativeExecutable))
            .Where(p => !Path.GetRelativePath(bin, p).StartsWith('.'))
            .Select(p => new Candidate(p, "Roost"));
    }

    public IEnumerable<DetectedComponent> Scan()
    {
        var results = new List<DetectedComponent>();
        if (!SupportsCurrentOs) return results;

        string? active = null;
        try { active = PathProbe.ResolveActive(PathExecutableNames); }
        catch { /* el PATH no debe tumbar el escaneo */ }

        List<Candidate> candidates;
        try { candidates = EnumerateCandidates().ToList(); }
        catch { return results; }

        string? activeReal = null;
        try { activeReal = active is null ? null : RealPath(active); }
        catch { /* sin ruta real, no se marca ninguno como activo */ }

        // Por la ruta real: el mismo binario visto por otra fuente, o por otro enlace
        // (/bin/php y /usr/bin/php con usrmerge), cuenta una vez, con el origen del primero.
        var seen = new HashSet<string>(CanonicalPath.Comparer);
        foreach (var candidate in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate.Path) || !File.Exists(candidate.Path))
                    continue;

                var resolved = Path.GetFullPath(candidate.Path);
                var real = RealPath(resolved);
                if (!seen.Add(real)) continue; // ya lo vimos por otra fuente

                var output = Runner.Run(resolved, VersionArguments, VersionTimeout);
                var onPath = activeReal is not null && CanonicalPath.Comparer.Equals(activeReal, real);

                var component = Build(candidate with { Path = resolved }, output, onPath);
                if (component is not null) results.Add(component);
            }
            catch
            {
                // Un binario problemático se salta; el resto del escaneo continúa.
            }
        }

        return results;
    }
}
