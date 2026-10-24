using System.ComponentModel;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Cli.Rendering;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Platform;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost audit</c>: escanea y evalua cada componente contra los avisos: el feed firmado de
/// roost-catalog (se actualiza como mucho una vez al dia) o, sin el, el dataset empaquetado. La
/// evaluacion es siempre local. Devuelve exit code != 0 si hay algo vulnerable o EOL con certeza,
/// para servir en CI.
/// <para>
/// K4: las mitigaciones de cada PHP se comparan con lo que dice su <c>php -i</c> (mitigada / sin
/// mitigar). <c>--apply-mitigations [&lt;php&gt;]</c> escribe las que faltan en los PHP de Roost
/// (<c>conf.d/&lt;rama&gt;/50-roost-mitigaciones.ini</c>), reinicia solo el pool de esa rama y vuelve a
/// comprobar; <c>--undo-mitigations [&lt;php&gt;]</c> las quita.
/// </para>
/// </summary>
public sealed class AuditCommand : AsyncCommand<AuditCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar del semaforo.")]
        public bool Json { get; init; }

        [CommandOption("--dataset <RUTA>")]
        [Description("Carga el dataset de avisos desde un fichero externo, sin firma (por defecto, el feed firmado o el empaquetado).")]
        public string? DatasetPath { get; init; }

        [CommandOption("--offline")]
        [Description("No pregunta por un feed nuevo: usa el ultimo bueno de la cache o el empaquetado.")]
        public bool Offline { get; init; }

        [CommandOption("--only <TIPOS>")]
        [Description("Solo esos tipos (php, mysql, mariadb, nginx, apache; varios separados por comas). El resumen y el codigo de salida salen de lo filtrado.")]
        public string? Only { get; init; }

        [CommandOption("--apply-mitigations [PHP]")]
        [Description("Aplica en los PHP de Roost (o solo en ese: rama, version o instalacion) las mitigaciones que faltan, en conf.d\\<rama>\\50-roost-mitigaciones.ini; reinicia solo su pool y vuelve a comprobar.")]
        public FlagValue<string>? ApplyMitigations { get; init; }

        [CommandOption("--undo-mitigations [PHP]")]
        [Description("Quita las mitigaciones que aplico Roost (en todos sus PHP, o solo en ese) y reinicia solo su pool.")]
        public FlagValue<string>? UndoMitigations { get; init; }
    }

    /// <summary>Codigo de salida de unas opciones que no valen (p. ej. --only con un tipo que no existe).</summary>
    public const int BadUsage = 3;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!ComponentFilter.TryParse(settings.Only, out var only, out var error))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
            return BadUsage;
        }
        var apply = settings.ApplyMitigations is { IsSet: true } a ? a : null;
        var undo = settings.UndoMitigations is { IsSet: true } u ? u : null;
        if (apply is not null && undo is not null)
        {
            AnsiConsole.MarkupLine("[red]--apply-mitigations y --undo-mitigations no van juntas.[/]");
            return BadUsage;
        }

        var paths = RoostPaths.Default();
        AuditDataset dataset;
        DatasetOrigin origin;
        try
        {
            if (settings.DatasetPath is { Length: > 0 } path)
                (dataset, origin) = (AuditDataset.LoadFile(path), new DatasetOrigin(DatasetKind.File));
            else
                (dataset, origin) = await CliFeed.LoadAsync(paths, update: !settings.Offline, cancellation);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo cargar el dataset de auditoria:[/] {ex.Message}");
            return 2;
        }

        dataset = CliExtensions.Current.WithAdvisories(dataset, paths);
        var result = CliExtensions.Current.Scanner(paths).ScanAll();
        var components = only?.Apply(result.Components) ?? result.Components;
        var skipped = result.Components.Count - components.Count;
        var evaluator = new AuditEvaluator(dataset);
        IReadOnlyList<AuditFinding> findings = evaluator.EvaluateAll(components).ToList();

        // K4: cada mitigacion frente a lo que aplica PHP (php -i; solo lee).
        var mitigations = new AuditMitigations(paths, CommandRunner.Default);
        findings = await mitigations.CheckAllAsync(findings, cancellation);

        var failed = false;
        if (apply is not null || undo is not null)
        {
            // Con --json, lo que se va haciendo va a stderr: stdout es solo el JSON.
            var log = new Log(settings.Json);
            var spec = (apply ?? undo)!.Value;
            var installed = new PhpInstaller(paths).ListInstalled();
            var chosen = string.IsNullOrWhiteSpace(spec) ? installed : PhpInstaller.Match(installed, spec);
            if (chosen.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun PHP de Roost que case con '{(spec ?? "").Trim()}'.[/] Los de Roost: roost php list");
                return BadUsage;
            }

            var changes = apply is not null
                ? Apply(mitigations, findings, chosen, log, string.IsNullOrWhiteSpace(spec), ref failed)
                : Undo(mitigations, chosen, log, ref failed);
            if (changes.Count > 0)
            {
                failed |= !await RestartPoolsAsync(paths, changes, log, cancellation);
                findings = await mitigations.CheckAllAsync(findings, cancellation);   // vuelve a comprobar con lo de ahora
                var branches = changes.Select(c => c.Branch).ToHashSet();
                foreach (var f in findings.Where(f => f.MitigationStates.Count > 0 && mitigations.RoostPhpOf(f) is { } p && branches.Contains(AuditMitigations.BranchOf(p))))
                    log.Line(Summary(f));
            }
            log.Blank();
        }

        if (settings.Json)
            AuditRenderer.RenderJson(dataset, findings, origin, only, skipped);
        else
            AuditRenderer.RenderTable(dataset, findings, origin, only, skipped);

        // Exit != 0 si algo es vulnerable o EOL con certeza (lo ambar/incierto/mitigado no bloquea), o si
        // no se ha podido aplicar o deshacer lo pedido.
        return findings.Any(f => f.IsBlocking) || failed ? 1 : 0;
    }

    private static List<MitigationChange> Apply(AuditMitigations mitigations, IReadOnlyList<AuditFinding> findings,
        IReadOnlyList<InstalledPhp> chosen, Log log, bool all, ref bool failed)
    {
        var changes = new List<MitigationChange>();
        var any = false;
        foreach (var f in findings.Where(f => f.MitigationStates.Count > 0))
        {
            var php = mitigations.RoostPhpOf(f);
            if (php is null)
            {
                // Solo se dice si se pide todo: con un PHP concreto, los de fuera no vienen al caso.
                if (all && f.MitigationStates.Any(s => s.Status == MitigationStatus.Unmitigated))
                    log.Grey($"PHP {f.Component.Version} ({f.Component.Source}): Roost no escribe en su carpeta; cambialo en su php.ini ({f.Component.ExecutablePath}).");
                continue;
            }
            if (!chosen.Contains(php)) continue;
            any = true;
            try
            {
                var change = mitigations.Apply(f);
                if (change.Keys.Count == 0)
                {
                    log.Grey($"PHP {php.Name}: nada que aplicar (ya mitigadas, aplicadas por Roost o que no se pueden escribir).");
                    continue;
                }
                log.Ok($"Aplicadas en PHP {change.Branch}", $"{string.Join(", ", change.Keys)} ({change.File})");
                changes.Add(change);
            }
            catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
            {
                log.Error($"No se pudieron aplicar en PHP {php.Name}", ex.Message);
                failed = true;
            }
        }
        if (!any) log.Grey("Ningun PHP de Roost de los elegidos tiene mitigaciones pendientes: nada que aplicar.");
        return changes;
    }

    private static List<MitigationChange> Undo(AuditMitigations mitigations, IReadOnlyList<InstalledPhp> chosen, Log log, ref bool failed)
    {
        var changes = new List<MitigationChange>();
        // El fichero es de la rama: una vez por rama.
        foreach (var php in chosen.Where(p => p.Version is not null).GroupBy(AuditMitigations.BranchOf).Select(g => g.First()))
        {
            try
            {
                var change = mitigations.UndoAll(php);
                if (change.Keys.Count == 0)
                {
                    log.Grey($"PHP {change.Branch}: Roost no le habia aplicado ninguna.");
                    continue;
                }
                log.Ok($"Deshechas en PHP {change.Branch}", $"{string.Join(", ", change.Keys)} (borrado {change.File})");
                changes.Add(change);
            }
            catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
            {
                log.Error($"No se pudieron deshacer en PHP {php.Name}", ex.Message);
                failed = true;
            }
        }
        return changes;
    }

    /// <summary>
    /// Si el stack esta en marcha, reinicia solo el pool de cada rama tocada que este en marcha (como
    /// <c>roost php ini</c>); si no, se aplica en el proximo <c>roost up</c>. false si alguno falla.
    /// </summary>
    private static async Task<bool> RestartPoolsAsync(RoostPaths paths, IReadOnlyList<MitigationChange> changes, Log log, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        if (await client.TryStatusAsync(cancellation) is not { Running: true } status)
        {
            log.Grey("El stack esta parado: se aplica en el proximo roost up.");
            return true;
        }
        var ok = true;
        foreach (var branch in changes.Select(c => c.Branch).Distinct())
        {
            var pool = $"php-{branch}";
            if (!status.Services.Any(s => s.Name.StartsWith(pool + "-", StringComparison.Ordinal) && s.State == "Running"))
            {
                log.Grey($"El pool {pool} no esta en marcha: se aplica cuando lo use un sitio o el global (roost up).");
                continue;
            }
            var response = await client.RestartAsync(pool, cancellation);
            if (response.Ok) log.Ok("Reiniciado", $"solo el pool {pool} (el resto del stack sigue igual)");
            else
            {
                log.Error($"No se pudo reiniciar {pool}", response.Error ?? "");
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>«Comprobado PHP 8.5.12 (Roost): session.upload_progress.enabled mitigada (vale Off) · ...».</summary>
    private static string Summary(AuditFinding f) =>
        $"Comprobado PHP {f.Component.Version} ({f.Component.Source}): "
        + string.Join(" · ", f.MitigationStates.Select(s => $"{s.Mitigation.Ini} {s.StatusLabel}{(s.Effective is null ? "" : $" (vale {(s.Effective.Length == 0 ? "vacio" : s.Effective)})")}"));

    /// <summary>Lo que se va haciendo: con color por stdout, o en texto llano por stderr con --json.</summary>
    private sealed class Log(bool json)
    {
        public void Ok(string what, string detail)
        {
            if (json) Console.Error.WriteLine($"{what}: {detail}");
            else AnsiConsole.MarkupLineInterpolated($"[green]{what}:[/] [grey]{detail}[/]");
        }

        public void Error(string what, string detail)
        {
            if (json) Console.Error.WriteLine($"{what}: {detail}");
            else AnsiConsole.MarkupLineInterpolated($"[red]{what}:[/] {detail}");
        }

        public void Grey(string text)
        {
            if (json) Console.Error.WriteLine(text);
            else AnsiConsole.MarkupLineInterpolated($"[grey]{text}[/]");
        }

        public void Line(string text)
        {
            if (json) Console.Error.WriteLine(text);
            else AnsiConsole.MarkupLineInterpolated($"{text}");
        }

        public void Blank()
        {
            if (!json) AnsiConsole.WriteLine();
        }
    }
}
