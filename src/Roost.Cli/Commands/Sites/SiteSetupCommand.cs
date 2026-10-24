using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Roost.Cli.Commands.Composer;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>Lo comun de roost site setup y roost new: ensenar los pasos, elegirlos, ejecutarlos con la salida en vivo y contarlo en JSON.</summary>
internal static class DeploySupport
{
    public static bool Interactive => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    /// <summary>La base de datos global en marcha segun el daemon (y su puerto), o null si esta parada.</summary>
    public static async Task<StatusResponse?> StatusAsync(RoostPaths paths, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        return await client.TryStatusAsync(cancellation);
    }

    /// <summary>true si el stack esta en marcha con la base de datos.</summary>
    public static bool DbRunning(StatusResponse? status) => status is { Running: true, Db: not null };

    /// <summary>La base de datos mirada (si existe y si tiene tablas) para proponer los pasos.</summary>
    public static async Task<DeployTarget> ProbeAsync(DeployTarget target, CancellationToken cancellation) =>
        target.Database is { State: not DbState.Stopped } db
            ? target with { Database = await DeployProbe.ProbeAsync(db, ProcessRunner.Default, cancellation) }
            : target;

    /// <summary>
    /// Los pasos que se van a hacer: los de <paramref name="ids"/> (separados por comas) o, sin ellos,
    /// los que van marcados. Lanza <see cref="RoostException"/> si uno no existe o ahora no se puede.
    /// </summary>
    public static IReadOnlyList<DeployStep> Select(IReadOnlyList<DeployStep> steps, string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) return steps;
        var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(i => i.ToLowerInvariant()).ToList();
        foreach (var id in wanted)
        {
            var step = steps.FirstOrDefault(s => s.Id == id)
                ?? throw new RoostException($"'{id}' no es un paso de este proyecto. Los que hay: {string.Join(", ", steps.Select(s => s.Id))}.");
            if (!step.Available) throw new RoostException($"{step.Id} ahora no se puede: {step.Hint}");
        }
        return steps.Select(s => s with { Checked = s.Required || wanted.Contains(s.Id) }).ToList();
    }

    /// <summary>La tabla de los pasos: [x] marcado, [ ] no, [-] no se puede ahora.</summary>
    public static void Show(IReadOnlyList<DeployStep> steps)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn(""); table.AddColumn("Paso"); table.AddColumn("Por que");
        foreach (var step in steps)
        {
            var box = !step.Available ? "[grey][[-]][/]" : step.Checked ? "[green][[x]][/]" : "[[ ]]";
            table.AddRow(box, $"[bold]{Markup.Escape(step.Label)}[/] [grey]({step.Id})[/]", Markup.Escape(step.Hint));
        }
        AnsiConsole.Write(table);
    }

    /// <summary>Con terminal, que el usuario marque o desmarque (los que se pueden, con los marcados de entrada).</summary>
    public static IReadOnlyList<DeployStep> Ask(IReadOnlyList<DeployStep> steps)
    {
        var choosable = steps.Where(s => s.Available && !s.Required).ToList();
        if (choosable.Count == 0) return steps;
        var prompt = new MultiSelectionPrompt<DeployStep>()
            .Title("Marca los pasos (espacio para marcar, Intro para seguir):")
            .NotRequired()
            .PageSize(12)
            .UseConverter(s => $"{Markup.Escape(s.Label)} [grey]- {Markup.Escape(s.Hint)}[/]")
            .AddChoices(choosable);
        foreach (var step in choosable.Where(s => s.Checked)) prompt.Select(step);
        var chosen = AnsiConsole.Prompt(prompt).Select(s => s.Id).ToHashSet();
        return steps.Select(s => s with { Checked = s.Required || chosen.Contains(s.Id) }).ToList();
    }

    /// <summary>
    /// Ejecuta los marcados (desde el primero), con su orden y su salida en vivo en la consola (o
    /// en silencio con <paramref name="json"/>). Se para en el primero que falla.
    /// </summary>
    public static async Task<DeployRun> RunAsync(DeployRunner runner, DeployTarget target, IReadOnlyList<DeployStep> steps, bool json, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        var results = new List<(DeployStep, DeployOutcome)>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!step.Checked || !step.Available) continue;
            if (!json) AnsiConsole.MarkupLineInterpolated($"[bold]> {step.Command ?? step.Label}[/]");
            var outcome = await runner.RunStepAsync(target, step, json ? null : new ConsoleLines(), cancellation);
            results.Add((step, outcome));
            if (!json)
            {
                if (outcome.Kind == DeployOutcomeKind.Skipped) AnsiConsole.MarkupLineInterpolated($"[grey]  Saltado: {outcome.Note}[/]");
                else if (outcome.Ok) AnsiConsole.MarkupLine("[green]  Hecho.[/]");
            }
            if (!outcome.Ok) return new DeployRun(results, i, watch.Elapsed);
        }
        return new DeployRun(results, null, watch.Elapsed);
    }

    /// <summary>El texto del diseno para un paso que falla (sin tildes) y cómo repetirlo.</summary>
    public static void ReportFailure(DeployRun run, string site, IReadOnlyList<DeployStep> steps, string retry)
    {
        var (step, outcome) = run.Failure!.Value;
        AnsiConsole.MarkupLineInterpolated($"[red]{step.Command ?? step.Label} ha fallado en {site} (ver las ultimas lineas).[/] {outcome.Note}");
        var rest = steps.Skip(run.FailedIndex!.Value).Where(s => s.Checked && s.Available && !s.Required).Select(s => s.Id).ToList();
        AnsiConsole.MarkupLineInterpolated($"[grey]{retry}{(rest.Count == 0 ? "" : $": roost site setup {site} --steps {string.Join(',', rest)} --yes")}[/]");
    }

    /// <summary>Un paso en el JSON: lo que es, si iba marcado y, si se hizo, como acabo.</summary>
    public static object Json(DeployStep step, DeployOutcome? outcome) => new
    {
        id = step.Id,
        label = step.Label,
        command = step.Command,
        hint = step.Hint,
        @checked = step.Checked,
        available = step.Available,
        result = outcome is null ? null : outcome.Kind.ToString().ToLowerInvariant(),
        note = outcome?.Note,
        exitCode = outcome?.ExitCode,
        tail = outcome is { Ok: false } ? outcome.Tail : null,
    };

    public static object[] Json(IReadOnlyList<DeployStep> steps, DeployRun? run) =>
        steps.Select(s => Json(s, run?.Results.FirstOrDefault(r => r.Step.Id == s.Id).Outcome)).ToArray();

    /// <summary>Lo que necesita Composer: lo instala la primera vez (con su progreso en pantalla).</summary>
    public static async Task<Roost.Core.Composer.InstalledComposer?> ComposerIfNeededAsync(RoostPaths paths, IEnumerable<DeployStep> steps, CancellationToken cancellation)
    {
        var needs = steps.Any(s => s.Checked && s.Available && (s.Kind == DeployStepKind.ComposerInstall || s.Kind == DeployStepKind.CreateProject && s.RunsProjectCode));
        return needs ? await ComposerSetup.EnsureAsync(paths, cancellation) : Roost.Core.Composer.ComposerTool.Locate(paths);
    }
}

/// <summary>
/// <c>roost site setup &lt;sitio&gt;</c>: los pasos de instalacion de un sitio que ya esta registrado (tras
/// clonarlo, o cuando se quiera): composer install, el .env con la base de datos de Roost, la clave,
/// las migraciones, storage:link, el .env.local de Symfony, el wp-config.php de WordPress... Los mismos
/// que propone la app. Con terminal, deja elegir; con --yes, hace los marcados sin preguntar.
/// </summary>
public sealed class SiteSetupCommand : AsyncCommand<SiteSetupCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SITIO>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandOption("--steps <PASOS>")]
        [Description("Los pasos que se hacen, separados por comas (composer, db, env, key, migrate, storage-link, wp-config, wp-loopback). Por defecto, los que propone Roost.")]
        public string? Steps { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pregunta: hace los pasos marcados (o los de --steps).")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI. Sin --yes, solo ensena los pasos; con --yes, los hace.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site setup")) return 1;

        var paths = RoostPaths.Default();
        DeployStep? current = null;
        try
        {
            var site = new SiteManager(paths).Find(settings.Name);
            if (site is null)
            {
                if (settings.Json) return JsonError($"No hay ningun sitio '{settings.Name}'.");
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }
            if (!Directory.Exists(site.Folder))
            {
                if (settings.Json) return JsonError($"La carpeta de {site.Host} ya no existe ({site.Folder}).");
                AnsiConsole.MarkupLineInterpolated($"[red]La carpeta de {site.Host} ya no existe ({site.Folder}).[/]");
                return 1;
            }

            var kind = ProjectKind.Detect(site.Folder);
            var status = await DeploySupport.StatusAsync(paths, cancellation);
            var target = await DeploySupport.ProbeAsync(DeployTarget.ForSite(paths, site, DeploySupport.DbRunning(status), status?.DbPort), cancellation);
            var steps = DeploySupport.Select(DeployPlan.For(kind, target), settings.Steps);
            var what = kind.FrameworkLabel is { } framework ? $"Proyecto {framework}" : kind.HasComposerJson ? "Proyecto con composer.json" : "Carpeta sin proyecto reconocido";

            if (steps.Count == 0)
            {
                if (settings.Json) return JsonOut(site, kind, target, steps, null, null);
                AnsiConsole.MarkupLineInterpolated($"{what} en {site.Folder}: no hay pasos que proponer. {site.Host} se sirve tal cual.");
                return 0;
            }

            if (!settings.Json)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]{site.Host}[/]: {what} [grey]({string.Join(", ", kind.Evidence)}) en {site.Folder}[/]");
                var fits = DeployPlan.PhpFits(kind.PhpConstraint, target.Php.Version);
                AnsiConsole.MarkupLineInterpolated($"[grey]PHP {target.Php.Version?.ToString() ?? target.Php.Name}, porque {target.PhpReason}{(kind.PhpConstraint is { } c ? $"; composer.json pide {c}{(fits == false ? " (no lo cumple)" : "")}" : "")}[/]");
                DeploySupport.Show(steps);
            }

            if (!settings.Yes)
            {
                if (settings.Json) return JsonOut(site, kind, target, steps, null, null);
                if (!DeploySupport.Interactive)
                {
                    AnsiConsole.MarkupLine("[yellow]Sin terminal no se pregunta:[/] repitelo con --yes (y, si quieres, --steps composer,env,...).");
                    return 1;
                }
                steps = settings.Steps is null ? DeploySupport.Ask(steps) : steps;
                if (!steps.Any(s => s.Checked && s.Available))
                {
                    AnsiConsole.MarkupLine("No has marcado ningun paso: no se hace nada.");
                    return 0;
                }
                if (settings.Steps is not null && !AnsiConsole.Confirm("Hacer estos pasos?")) return 0;
            }

            if (steps.Any(s => s.Checked && s.Available && s.RunsProjectCode) && !settings.Json)
                AnsiConsole.MarkupLineInterpolated($"[grey]{DeployPlan.ScriptsWarning}[/]");
            var runner = new DeployRunner(paths, CommandRunner.Default)
            {
                Composer = await DeploySupport.ComposerIfNeededAsync(paths, steps, cancellation),
            };

            var run = await DeploySupport.RunAsync(runner, target, steps, settings.Json, cancellation);
            current = run.Failure?.Step;
            if (settings.Json) return JsonOut(site, kind, target, steps, run, null);

            if (!run.Succeeded)
            {
                DeploySupport.ReportFailure(run, site.Name, steps, "El sitio sigue registrado: arreglalo y repite desde ese paso");
                return run.Failure!.Value.Outcome.ExitCode is int code and > 0 and < 256 ? code : 1;
            }

            var done = run.Results.Count(r => r.Outcome.Kind == DeployOutcomeKind.Done);
            var skipped = run.Results.Count(r => r.Outcome.Kind == DeployOutcomeKind.Skipped);
            AnsiConsole.MarkupLineInterpolated($"[green]Listo:[/] {done} paso(s) hecho(s){(skipped > 0 ? $" y {skipped} saltado(s)" : "")} en {run.Elapsed.TotalSeconds:0.0} s. [grey](La salida, en {DeployLog.FilePath(paths, site.Name)})[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]{target.Url}/[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            if (settings.Json) return JsonError(ex.Message);
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        catch (OperationCanceledException)
        {
            if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"[yellow]Cancelado{(current is null ? "" : $" en {current.Label}")}.[/] Se ha parado el proceso; lo que llego a hacer, hecho esta.");
            return 130;
        }
    }

    private static int JsonOut(Site site, ProjectKind kind, DeployTarget target, IReadOnlyList<DeployStep> steps, DeployRun? run, string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = error is null && (run is null || run.Succeeded),
            site = site.Name,
            host = site.Host,
            folder = site.Folder,
            url = target.Url + "/",
            project = new { type = kind.Type.ToString().ToLowerInvariant(), framework = kind.FrameworkLabel, evidence = kind.Evidence },
            php = new { name = target.Php.Name, reason = target.PhpReason, constraint = kind.PhpConstraint },
            database = target.Database is { } db ? new { name = db.Name, engine = db.Label, port = db.Port, state = db.State.ToString().ToLowerInvariant(), tables = db.Tables } : null,
            ran = run is not null,
            steps = DeploySupport.Json(steps, run),
            failed = run?.Failure?.Step.Id,
            seconds = run is null ? (double?)null : Math.Round(run.Elapsed.TotalSeconds, 1),
            error,
        }, JsonOutput.Options));
        return error is null && (run is null || run.Succeeded) ? 0 : 1;
    }

    private static int JsonError(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, JsonOutput.Options));
        return 1;
    }
}
