using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Commands.Sites;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost new &lt;laravel|wordpress|symfony&gt; &lt;nombre&gt;</c> (ROOST.md: plantillas): un proyecto nuevo,
/// servido en https://&lt;nombre&gt;.test, con su base de datos y su .env o su wp-config.php. Laravel y
/// Symfony con composer create-project y el PHP elegido; WordPress, el zip de wordpress.org comprobado
/// con su SHA-1. Si algo falla (o se cancela), no queda un sitio a medias: Roost quita la carpeta, el
/// sitio y la base de datos que ha creado en este intento, y nada mas.
/// </summary>
public sealed class NewCommand : AsyncCommand<NewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLANTILLA>")]
        [Description("laravel, wordpress o symfony.")]
        public string Template { get; init; } = "";

        [CommandArgument(1, "<NOMBRE>")]
        [Description("Nombre del sitio: se sirve en https://<nombre>.test/ (minusculas, numeros y guiones).")]
        public string Name { get; init; } = "";

        [CommandOption("--php <VERSION>")]
        [Description("PHP del sitio (rama, version o instalacion). Por defecto, el global. Tiene que valer para la plantilla (Laravel 13 pide 8.3).")]
        public string? Php { get; init; }

        [CommandOption("--path <CARPETA>")]
        [Description("Carpeta del proyecto. Por defecto, <www>/<nombre>. Tiene que no existir o estar vacia.")]
        public string? Path { get; init; }

        [CommandOption("--db <NOMBRE>")]
        [Description("Base de datos del sitio (se crea si no existe). Por defecto, la del nombre (mi-tienda -> mi_tienda); en Symfony, solo con --webapp.")]
        public string? Db { get; init; }

        [CommandOption("--no-db")]
        [Description("Sin base de datos (Laravel se queda con sqlite; WordPress la pedira en su instalador).")]
        public bool NoDb { get; init; }

        [CommandOption("--webapp")]
        [Description("Symfony: tambien composer require webapp (Twig, Doctrine, formularios, seguridad...).")]
        public bool Webapp { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pregunta.")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI (nunca pregunta: hace falta --yes).")]
        public bool Json { get; init; }

        public override ValidationResult Validate()
        {
            if (ProjectTemplates.Find(Template) is null)
                return ValidationResult.Error($"'{Template}' no es una plantilla: usa laravel, wordpress o symfony.");
            if (NoDb && Db is not null) return ValidationResult.Error("--db y --no-db no van juntos.");
            if (Json && !Yes) return ValidationResult.Error("Con --json no se pregunta: anade --yes.");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var template = ProjectTemplates.Find(settings.Template)!;
        var json = settings.Json;
        DeployRunner? runner = null;
        DeployTarget? target = null;
        IReadOnlyList<DeployStep> steps = [];
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            string? database = settings.NoDb ? null
                : settings.Db ?? (template.Template != ProjectTemplate.Symfony || settings.Webapp ? DbAdmin.NameFor(settings.Name.Trim().ToLowerInvariant()) : null);
            if (database is not null && new StackPlanner(paths, ProcessRunner.Default).ResolveDb(config, new DbInstaller(paths).ListInstalled()) is null)
                throw new RoostException($"No hay base de datos en Roost para {template.Name}: instala una (roost db install mariadb@11.4) o sigue sin ella con --no-db.");

            var plan = ProjectTemplates.Plan(paths, new NewProjectRequest(template.Template, settings.Name, settings.Path, settings.Php, database, settings.Webapp));

            // Antes de tocar nada: la base de datos tiene que estar en marcha para crearla y migrar.
            var status = await DeploySupport.StatusAsync(paths, cancellation);
            target = await DeploySupport.ProbeAsync(ProjectTemplates.Target(paths, plan, DeploySupport.DbRunning(status), status?.DbPort), cancellation);
            steps = ProjectTemplates.Steps(plan, target);

            if (!json)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]{template.Name}[/] en [bold]{target.Url}/[/] [grey]({plan.Folder}, PHP {plan.Php.Version?.ToString() ?? plan.Php.Name}{(database is null ? ", sin base de datos" : $", base de datos {database}")})[/]");
                DeploySupport.Show(steps);
                if (!settings.Yes)
                {
                    if (!DeploySupport.Interactive)
                    {
                        AnsiConsole.MarkupLine("[yellow]Sin terminal no se pregunta:[/] repitelo con --yes.");
                        return 1;
                    }
                    if (!AnsiConsole.Confirm("Crear el proyecto?")) return 0;
                }
            }

            if (database is not null && !DeploySupport.DbRunning(status))
            {
                if (!json) AnsiConsole.MarkupLine("[grey]La base de datos esta parada: arranco Roost para crearla.[/]");
                status = await StartStackAsync(paths, context, json, cancellation);
                if (!DeploySupport.DbRunning(status))
                    throw new RoostException("Roost no ha arrancado con la base de datos: no se ha creado nada. Mira roost status y vuelve a intentarlo.");
                target = await DeploySupport.ProbeAsync(ProjectTemplates.Target(paths, plan, dbRunning: true, status!.DbPort), cancellation);
                steps = ProjectTemplates.Steps(plan, target);
            }

            if (template.UsesComposer && !json) AnsiConsole.MarkupLineInterpolated($"[grey]Composer baja {template.Package} de Packagist y ejecuta los scripts de la plantilla.[/]");
            runner = new DeployRunner(paths, CommandRunner.Default)
            {
                Composer = await DeploySupport.ComposerIfNeededAsync(paths, steps, cancellation),
                Http = RoostHttp.Client,
                Template = plan,
            };

            var run = await DeploySupport.RunAsync(runner, target, steps, json, cancellation);
            if (!run.Succeeded)
            {
                var undone = runner.Undo(target);
                if (json) return JsonOut(plan, target, steps, run, undone, null, null);
                var (step, outcome) = run.Failure!.Value;
                AnsiConsole.MarkupLineInterpolated($"[red]{step.Command ?? step.Label} ha fallado (ver las ultimas lineas).[/] {outcome.Note}");
                Undone(undone);
                return outcome.ExitCode is int code and > 0 and < 256 ? code : 1;
            }
            // Desde aquí el proyecto es del usuario: un fallo del hosts o del stack ya no lo deshace.
            runner = null;

            // Hecho: el nombre en el hosts (o el DNS local) y el vhost nuevo en el stack.
            var sites = new SiteManager(paths);
            string? hostsError = null;
            if (json)
            {
                try { sites.SyncHosts(SiteSupport.HelperExe); }
                catch (RoostException ex) { hostsError = ex.Message; }
                using var client = new DaemonClient(paths);
                if (await client.TryStatusAsync(cancellation) is { Running: true })
                {
                    var applied = await client.UpAsync(cancellation);
                    if (!applied.Ok) hostsError ??= applied.Error;
                }
                return JsonOut(plan, target, steps, run, [], hostsError, sites.Find(plan.Name));
            }

            AnsiConsole.MarkupLineInterpolated($"[green]{template.Name} listo[/] en {run.Elapsed.TotalSeconds:0} s: [bold]{target.Url}/[/] [grey](la salida, en {DeployLog.FilePath(paths, plan.Name)})[/]");
            if (template.Template == ProjectTemplate.WordPress)
                AnsiConsole.MarkupLineInterpolated($"[grey]Abre {target.Url}/ y sigue el instalador de WordPress (el idioma, el titulo y tu usuario): la base de datos ya esta en wp-config.php.[/]");
            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            SiteSupport.TrustHint(new LocalCa(paths));
            return await StackApply.IfRunningAsync(paths, _ => $"{target.Url}/", cancellation);
        }
        catch (RoostException ex)
        {
            var undone = runner is not null && target is not null ? runner.Undo(target) : [];
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message, undone }, JsonOutput.Options));
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            Undone(undone);
            return 1;
        }
        catch (OperationCanceledException)
        {
            var undone = runner is not null && target is not null ? runner.Undo(target) : [];
            if (!json) AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
            Undone(undone);
            return 130;
        }
    }

    /// <summary>Arranca el stack como roost up (con su salida, salvo en JSON) y devuelve el estado.</summary>
    private static async Task<StatusResponse?> StartStackAsync(RoostPaths paths, CommandContext context, bool json, CancellationToken cancellation)
    {
        if (!json)
        {
            await new Stack.UpCommand().ExecuteAsync(context, new EmptyCommandSettings(), cancellation);
        }
        else
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            await Stack.WebServerSetup.EnsureAsync(paths, config.WebServer, cancellation);
            using var starter = new DaemonClient(paths);
            await starter.EnsureRunningAsync(DaemonLauncher.ForCurrentCli(), cancellation);
            await starter.UpAsync(cancellation);
        }
        return await DeploySupport.StatusAsync(paths, cancellation);
    }

    private static void Undone(IReadOnlyList<string> undone)
    {
        if (undone.Count > 0)
            AnsiConsole.MarkupLineInterpolated($"[grey]No queda nada a medias: Roost ha quitado {string.Join(", ", undone)} (lo que habia creado en este intento).[/]");
    }

    private static int JsonOut(NewProjectPlan plan, DeployTarget target, IReadOnlyList<DeployStep> steps, DeployRun run, IReadOnlyList<string> undone, string? hostsError, Site? site)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = run.Succeeded,
            template = plan.Template.Key,
            site = plan.Name,
            host = $"{plan.Name}.test",
            url = target.Url + "/",
            folder = plan.Folder,
            documentRoot = site?.DocumentRoot,
            php = plan.Php.Name,
            database = target.Database?.Name,
            steps = DeploySupport.Json(steps, run),
            failed = run.Failure?.Step.Id,
            undone,
            hostsError,
            seconds = Math.Round(run.Elapsed.TotalSeconds, 1),
        }, JsonOutput.Options));
        return run.Succeeded ? 0 : 1;
    }
}
