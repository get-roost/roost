using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Audit;

/// <summary>Lo que ha escrito o quitado «Aplicar» o «Deshacer»: el fichero de la rama y qué ajustes.</summary>
public sealed record MitigationChange(InstalledPhp Php, string File, IReadOnlyList<string> Keys)
{
    /// <summary>La rama de la instalación («8.5»): su pool es <c>php-8.5</c>.</summary>
    public string Branch => AuditMitigations.BranchOf(Php);
}

/// <summary>
/// K4: las mitigaciones de cada PHP frente a lo que PHP aplica de verdad, y aplicarlas.
/// <list type="bullet">
/// <item><b>Comprobar</b> (<see cref="CheckAsync"/>): <c>php -i</c> del propio php.exe; en uno de Roost,
/// con su entorno (<see cref="PhpConfigurator.Command"/>: el php.ini generado y las dos conf.d, lo mismo
/// que lee su pool), y en uno de fuera (Laragon, XAMPP), con el suyo y sin el PHPRC ni el
/// PHP_INI_SCAN_DIR de quien lo lanza. Solo lee: nunca escribe en la carpeta de otro.</item>
/// <item><b>Aplicar y deshacer</b> (<see cref="Apply"/>, <see cref="Undo"/>), solo en los de Roost: en
/// <see cref="PhpMitigations"/> de su rama, nunca en un <c>99-usuario.ini</c>. No reinicia nada: eso
/// lo hacen la CLI y la app (solo el pool de esa rama), y luego vuelven a comprobar.</item>
/// </list>
/// </summary>
public sealed class AuditMitigations(RoostPaths paths, ICommandRunner runner)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly StackPlanner _planner = new(paths, ProcessRunner.Default);

    public static string BranchOf(InstalledPhp php) =>
        php.Version is { } v ? $"{v.Major}.{v.Minor}" : throw new RoostException($"No se reconoce la version de la instalacion {php.Name}.");

    /// <summary>El PHP de Roost de este hallazgo (por la ruta de su php.exe); null si es de fuera.</summary>
    public InstalledPhp? RoostPhpOf(AuditFinding finding)
    {
        if (finding.Component.Kind != ComponentKind.Php) return null;
        try
        {
            var exe = CanonicalPath.Of(finding.Component.ExecutablePath);
            return new PhpInstaller(paths).ListInstalled().FirstOrDefault(p => CanonicalPath.Comparer.Equals(CanonicalPath.Of(p.Executable), exe));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException)
        {
            return null;
        }
    }

    /// <summary>El fichero de mitigaciones de Roost de la rama de <paramref name="php"/>.</summary>
    public string FileFor(InstalledPhp php) => PhpMitigations.PathFor(_planner, php);

    /// <summary>Todos los que tienen mitigaciones, comprobados; los demás, tal cual. Nunca lanza (salvo cancelar).</summary>
    public async Task<IReadOnlyList<AuditFinding>> CheckAllAsync(IReadOnlyList<AuditFinding> findings, CancellationToken cancellation = default)
    {
        var result = new List<AuditFinding>(findings.Count);
        foreach (var f in findings) result.Add(await CheckAsync(f, cancellation));
        return result;
    }

    /// <summary>
    /// El hallazgo con el estado de cada mitigación según <c>php -i</c> (y, si es un PHP de Roost con
    /// todo mitigado, en ámbar: <see cref="AuditEvaluator.WithMitigations"/>). Si php.exe no contesta, las
    /// deja «sin comprobar» y lo dice. Nunca lanza (salvo cancelar).
    /// </summary>
    public async Task<AuditFinding> CheckAsync(AuditFinding finding, CancellationToken cancellation = default)
    {
        var original = finding.BeforeMitigations ?? finding;
        if (original.Component.Kind != ComponentKind.Php || original.Mitigations.Count == 0) return original;

        var php = RoostPhpOf(original);
        PhpIniSnapshot? snapshot = null;
        string? problem = null;
        try
        {
            var spec = php is null ? Foreign(original.Component.ExecutablePath) : new PhpConfigurator(paths, runner).Command(php, "-i");
            var result = await runner.RunAsync(spec with { Timeout = Timeout }, null, cancellation);
            snapshot = result.TimedOut ? null : PhpIniSnapshot.Parse(result.Stdout);
            if (snapshot is null) problem = result.TimedOut ? "php -i no ha contestado a tiempo" : "php -i no ha dado su configuracion";
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            problem = $"no se pudo ejecutar php -i: {ex.Message}";
        }

        if (snapshot is null)
        {
            var unchecked_ = Distinct(original.Mitigations).Select(m => new MitigationState(m, MitigationStatus.Unchecked, null, problem)).ToList();
            return AuditEvaluator.WithMitigations(original, unchecked_, [], trusted: false);
        }

        var ours = php is null ? [] : PhpMitigations.Read(FileFor(php));
        var states = Distinct(original.Mitigations)
            .Select(m => Decorate(MitigationCheck.Check(m, snapshot), php, ours, snapshot))
            .ToList();
        return AuditEvaluator.WithMitigations(original, states, snapshot.Files, trusted: php is not null);
    }

    /// <summary>
    /// Escribe en el fichero de su rama las mitigaciones que faltan (todas las que se pueden, o solo
    /// <paramref name="only"/>). <paramref name="finding"/> tiene que estar comprobado. Lanza
    /// <see cref="RoostException"/> si no es un PHP de Roost. Devuelve lo escrito (puede ser nada).
    /// </summary>
    public MitigationChange Apply(AuditFinding finding, IReadOnlyCollection<Mitigation>? only = null)
    {
        var php = RoostPhpOf(finding)
                  ?? throw new RoostException($"Solo se aplican mitigaciones en los PHP de Roost: este es de {finding.Component.Source} y Roost no escribe en su carpeta (cambialo en su php.ini).");
        var file = FileFor(php);
        var targets = finding.MitigationStates
            .Where(s => s.CanApply && (only is null || only.Any(o => o.SameAs(s.Mitigation))))
            .ToList();
        if (targets.Count == 0) return new MitigationChange(php, file, []);

        var added = targets.Select(s => new PhpMitigations.Entry(s.Mitigation.Ini, PhpMitigations.ValueToWrite(s.Mitigation, s.Effective), ReasonFor(finding, s.Mitigation)));
        PhpMitigations.Write(file, BranchOf(php), PhpMitigations.With(PhpMitigations.Read(file), added));
        return new MitigationChange(php, file, targets.Select(s => s.Mitigation.Ini).ToList());
    }

    /// <summary>
    /// Quita del fichero de su rama las de este hallazgo (o solo <paramref name="only"/>) que puso Roost.
    /// Si no queda ninguna, borra el fichero. Lanza <see cref="RoostException"/> si no es un PHP de Roost.
    /// </summary>
    public MitigationChange Undo(AuditFinding finding, IReadOnlyCollection<Mitigation>? only = null)
    {
        var php = RoostPhpOf(finding)
                  ?? throw new RoostException($"Este PHP es de {finding.Component.Source}: Roost no le ha aplicado nada.");
        var keys = (only ?? finding.Mitigations).Select(m => m.Ini).ToList();
        return UndoKeys(php, keys);
    }

    /// <summary>Quita todo lo que Roost ha aplicado en la rama de <paramref name="php"/> (borra su fichero).</summary>
    public MitigationChange UndoAll(InstalledPhp php)
    {
        var file = FileFor(php);
        var keys = PhpMitigations.Read(file).Select(e => e.Key).ToList();
        PhpMitigations.Write(file, BranchOf(php), []);
        return new MitigationChange(php, file, keys);
    }

    private MitigationChange UndoKeys(InstalledPhp php, IReadOnlyList<string> keys)
    {
        var file = FileFor(php);
        var before = PhpMitigations.Read(file);
        var after = PhpMitigations.Without(before, keys);
        if (after.Count != before.Count) PhpMitigations.Write(file, BranchOf(php), after);
        return new MitigationChange(php, file, before.Where(e => !after.Contains(e)).Select(e => e.Key).ToList());
    }

    // Lo de Roost aplicado y lo que se puede aplicar; si está aplicado y aun así no se cumple, quién lo pisa.
    private MitigationState Decorate(MitigationState state, InstalledPhp? php, IReadOnlyList<PhpMitigations.Entry> ours, PhpIniSnapshot snapshot)
    {
        if (php is null) return state;
        var applied = ours.Any(e => e.Key.Equals(state.Mitigation.Ini, StringComparison.OrdinalIgnoreCase));
        var detail = state.Detail;
        string? overriddenBy = null;
        if (applied && state.Status == MitigationStatus.Unmitigated)
        {
            var file = FileFor(php);
            var index = snapshot.Files.ToList().FindIndex(f => CanonicalPath.Comparer.Equals(SafeFull(f), SafeFull(file)));
            if (index < 0)
                detail = $"PHP no lee {file}";
            else
            {
                var later = snapshot.Files.Skip(index + 1).Select(f => (File: f, Text: SafeRead(f)));
                overriddenBy = PhpMitigations.WhoOverrides(state.Mitigation.Ini, later);
                detail = overriddenBy is { } who
                    ? $"aplicada por Roost, pero la pisa {who}"
                    : $"aplicada por Roost, pero PHP aplica '{state.Effective}'";
            }
        }
        return state with
        {
            Detail = detail,
            OverriddenBy = overriddenBy,
            AppliedByRoost = applied,
            CanApply = state.Status == MitigationStatus.Unmitigated && !applied && PhpMitigations.IsWritable(state.Mitigation),
        };
    }

    /// <summary>«CVE-2026-109619 (PHP2Shell)»: los avisos que piden esta mitigación.</summary>
    private static string ReasonFor(AuditFinding finding, Mitigation mitigation)
    {
        var ids = finding.MatchedAdvisories
            .Where(a => a.Mitigations.Any(m => m.SameAs(mitigation)))
            .Select(a => a.Aliases.FirstOrDefault(x => !x.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase)) is { } alias ? $"{a.Id} ({alias})" : a.Id)
            .Distinct()
            .ToList();
        return string.Join(", ", ids);
    }

    private static List<Mitigation> Distinct(IEnumerable<Mitigation> mitigations)
    {
        var list = new List<Mitigation>();
        foreach (var m in mitigations)
            if (!list.Any(x => x.SameAs(m))) list.Add(m);
        return list;
    }

    /// <summary>
    /// <c>php -i</c> de un PHP que no es de Roost, como lo ve su propio servidor: desde su carpeta (ahí
    /// busca su php.ini) y sin el PHPRC ni el PHP_INI_SCAN_DIR de quien lo lanza.
    /// </summary>
    public static CommandSpec Foreign(string phpExe) => new(
        phpExe,
        ["-i"],
        Path.GetDirectoryName(phpExe),
        new Dictionary<string, string?> { ["PHPRC"] = null, ["PHP_INI_SCAN_DIR"] = null },
        Timeout);

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
