using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Composer;
using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Deploy;

/// <summary>Cómo acabó un paso.</summary>
public enum DeployOutcomeKind
{
    Done,

    /// <summary>No hacía falta (ya tenía APP_KEY, ya estaba el enlace): no se ha tocado nada.</summary>
    Skipped,

    Failed,
}

/// <summary>Cómo acabó un paso: bien, saltado (con por qué) o mal (con el código y las últimas líneas).</summary>
public sealed record DeployOutcome(DeployOutcomeKind Kind, string? Note = null, int? ExitCode = null)
{
    /// <summary>Las últimas líneas que dijo el paso (las que se enseñan si falla).</summary>
    public IReadOnlyList<string> Tail { get; init; } = [];

    public bool Ok => Kind != DeployOutcomeKind.Failed;

    public static DeployOutcome Done(string? note = null) => new(DeployOutcomeKind.Done, note);

    public static DeployOutcome Skipped(string note) => new(DeployOutcomeKind.Skipped, note);
}

/// <summary>
/// Lo que Roost ha creado en este intento al hacer un proyecto con una plantilla: lo único que
/// <see cref="DeployRunner.Undo"/> quita si no se acaba. Nada de lo que ya estaba.
/// </summary>
public sealed class DeployJournal
{
    /// <summary>La carpeta del proyecto la ha creado Roost (si ya existía vacía, <see cref="FolderExisted"/>).</summary>
    public bool FolderCreated { get; set; }

    public bool FolderExisted { get; set; }

    /// <summary>El sitio lo ha registrado Roost ahora (no existía).</summary>
    public bool SiteCreated { get; set; }

    /// <summary>La base de datos la ha creado Roost ahora (no existía).</summary>
    public bool DatabaseCreated { get; set; }

    /// <summary>La carpeta temporal de create-project, mientras existe.</summary>
    public string? TempFolder { get; set; }

    public bool Any => FolderCreated || SiteCreated || DatabaseCreated || TempFolder is not null;
}

/// <summary>Un paso de <see cref="DeployRunner.RunAsync"/> empieza, dice una línea o acaba.</summary>
public sealed record DeployEvent(int Index, DeployStep Step, string? Line = null, DeployOutcome? Outcome = null)
{
    public bool Started => Line is null && Outcome is null;
}

/// <summary>Lo que dejó <see cref="DeployRunner.RunAsync"/>: cada paso hecho y, si falló uno, cuál.</summary>
public sealed record DeployRun(IReadOnlyList<(DeployStep Step, DeployOutcome Outcome)> Results, int? FailedIndex, TimeSpan Elapsed)
{
    public bool Succeeded => FailedIndex is null;

    /// <summary>El paso que falló (el último que se hizo) y cómo, o null si todo fue bien.</summary>
    public (DeployStep Step, DeployOutcome Outcome)? Failure => FailedIndex is null ? null : Results[^1];
}

/// <summary>
/// Ejecuta los pasos de instalación de un sitio (<see cref="DeployPlan"/>) o de una plantilla
/// (<see cref="ProjectTemplates"/>): Composer con el composer.phar de Roost, <c>php artisan</c> y
/// <c>bin/console</c> con el PHP del sitio y el mismo php.ini que el servidor
/// (<see cref="TerminalPhp.Environment"/>), y lo de Roost (el .env, el wp-config.php, la base de
/// datos) en C#. Las órdenes van sobre <see cref="ICommandRunner"/>: salida en vivo, cancelables
/// (matan el árbol de procesos) y sin preguntas. Cada paso deja su salida en
/// <c>logs/pasos-&lt;sitio&gt;.log</c>. npm va con el Node de Roost de la carpeta (<see cref="NodeTool"/>),
/// nunca con un <c>npm</c> o un <c>node</c> del sistema.
/// </summary>
public sealed class DeployRunner(RoostPaths paths, ICommandRunner commands, IProcessRunner? processes = null)
{
    private readonly IProcessRunner _processes = processes ?? ProcessRunner.Default;

    /// <summary>Las líneas que se guardan de cada paso para contar un fallo.</summary>
    public const int TailLines = 15;

    /// <summary>El Composer de Roost para los pasos que lo usan; quien llama lo instala antes (con su diálogo o su progreso).</summary>
    public InstalledComposer? Composer { get; set; }

    /// <summary>Para descargar WordPress (la plantilla).</summary>
    public HttpClient? Http { get; init; }

    /// <summary>La plantilla, para el paso que crea el proyecto.</summary>
    public NewProjectPlan? Template { get; init; }

    /// <summary>Lo que se ha creado en este intento (plantillas), para <see cref="Undo"/>.</summary>
    public DeployJournal Journal { get; } = new();

    /// <summary>
    /// Ejecuta un paso. Lo que dice va a <paramref name="lines"/> (y al log). No lanza por un fallo
    /// del paso: lo devuelve como <see cref="DeployOutcomeKind.Failed"/> con su motivo y sus últimas
    /// líneas. Sí lanza <see cref="OperationCanceledException"/> si se cancela.
    /// </summary>
    public async Task<DeployOutcome> RunStepAsync(DeployTarget target, DeployStep step, IProgress<string>? lines, CancellationToken cancellation)
    {
        using var log = DeployLog.Begin(paths, target.SiteName, step.Command ?? step.Label);
        var sink = new Sink(lines, log);
        try
        {
            var outcome = await RunCoreAsync(target, step, sink, cancellation);
            outcome = outcome.Kind == DeployOutcomeKind.Failed ? outcome with { Tail = sink.Tail } : outcome;
            log.End(outcome.Ok, outcome.Note ?? (outcome.Kind == DeployOutcomeKind.Skipped ? "saltado" : "hecho"));
            return outcome;
        }
        catch (OperationCanceledException)
        {
            log.End(false, "Cancelado.");
            throw;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            sink.Report(ex.Message);
            log.End(false, ex.Message);
            return new DeployOutcome(DeployOutcomeKind.Failed, ex.Message) { Tail = sink.Tail };
        }
    }

    /// <summary>
    /// Ejecuta en orden los pasos marcados y disponibles desde <paramref name="from"/>, y se para en
    /// el primero que falla. Lanza <see cref="OperationCanceledException"/> si se cancela.
    /// </summary>
    public async Task<DeployRun> RunAsync(DeployTarget target, IReadOnlyList<DeployStep> steps, IProgress<DeployEvent>? events, CancellationToken cancellation, int from = 0)
    {
        var watch = Stopwatch.StartNew();
        var results = new List<(DeployStep, DeployOutcome)>();
        for (var i = from; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!step.Checked || !step.Available) continue;
            events?.Report(new DeployEvent(i, step));
            var index = i;
            var outcome = await RunStepAsync(target, step, new LineEvents(events, index, step), cancellation);
            results.Add((step, outcome));
            events?.Report(new DeployEvent(i, step, Outcome: outcome));
            if (!outcome.Ok) return new DeployRun(results, i, watch.Elapsed);
        }
        return new DeployRun(results, null, watch.Elapsed);
    }

    /// <summary>
    /// Deshace lo que Roost creó en este intento (<see cref="Journal"/>): da de baja el sitio, borra
    /// la base de datos que creó (solo esa) y la carpeta que creó (si ya existía vacía, la deja
    /// vacía). Nunca toca lo que ya estaba. Devuelve lo que ha quitado, para contarlo.
    /// </summary>
    public IReadOnlyList<string> Undo(DeployTarget target)
    {
        var undone = new List<string>();
        if (Journal.SiteCreated)
        {
            try
            {
                new SiteManager(paths).Remove(target.SiteName);
                undone.Add($"el sitio {target.Host}");
            }
            catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException) { }
            Journal.SiteCreated = false;
        }
        if (Journal.DatabaseCreated && target.Database is { } db)
        {
            try
            {
                DbAdmin.Drop(db.Db, db.IniPath, db.Name, _processes);
                undone.Add($"la base de datos {db.Name}");
            }
            catch (RoostException) { /* se queda: es una base de datos vacía o a medias, y se dice abajo */ }
            Journal.DatabaseCreated = false;
        }
        if (Journal.TempFolder is { } temp)
        {
            InstallFiles.TryDeleteDirectory(temp);
            Journal.TempFolder = null;
        }
        if (Journal.FolderCreated)
        {
            InstallFiles.TryDeleteDirectory(target.Folder);
            if (Journal.FolderExisted) Directory.CreateDirectory(target.Folder);
            undone.Add(Journal.FolderExisted ? $"lo que se habia puesto en {target.Folder}" : $"la carpeta {target.Folder}");
            Journal.FolderCreated = false;
        }
        return undone;
    }

    private Task<DeployOutcome> RunCoreAsync(DeployTarget target, DeployStep step, Sink lines, CancellationToken cancellation) => step.Kind switch
    {
        DeployStepKind.CreateProject => CreateProjectAsync(target, lines, cancellation),
        DeployStepKind.Database => Task.FromResult(CreateDatabase(target, lines)),
        DeployStepKind.ComposerInstall => ComposerAsync(target, ComposerTool.Arguments(ComposerAction.Install), target.Folder, lines, cancellation, echo: false),
        DeployStepKind.Env => Task.FromResult(step.File == ".env.local" ? SymfonyEnv(target, lines) : LaravelEnv(target, step, lines)),
        DeployStepKind.AppKey => AppKeyAsync(target, lines, cancellation),
        DeployStepKind.Migrate => File.Exists(Path.Combine(target.Folder, "artisan"))
            ? PhpAsync(target, ["artisan", "migrate", "--no-interaction"], lines, cancellation)
            : PhpAsync(target, [Path.Combine("bin", "console"), "doctrine:migrations:migrate", "--no-interaction", "--allow-no-migration"], lines, cancellation),
        DeployStepKind.StorageLink => StorageLinkAsync(target, lines, cancellation),
        DeployStepKind.WpConfig => Task.FromResult(WriteWpConfig(target, lines)),
        DeployStepKind.WpLoopback => Task.FromResult(WriteLoopbackPlugin(target, lines)),
        DeployStepKind.Npm => NpmAsync(target, lines, cancellation),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    // --- La plantilla ---

    private async Task<DeployOutcome> CreateProjectAsync(DeployTarget target, Sink lines, CancellationToken cancellation)
    {
        var plan = Template ?? throw new InvalidOperationException("Crear el proyecto necesita la plantilla (DeployRunner.Template).");
        var folder = plan.Folder;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new RoostException($"{folder} ya no esta vacia: no se crea el proyecto encima.");
        if (File.Exists(folder)) throw new RoostException($"{folder} es un fichero, no una carpeta.");
        var parent = Path.GetDirectoryName(folder) ?? folder;
        Directory.CreateDirectory(parent);

        // Un intento cancelado deja su temporal (lo quita Undo); si se reintenta, se quita antes.
        if (Journal.TempFolder is { } previous)
        {
            InstallFiles.TryDeleteDirectory(previous);
            Journal.TempFolder = null;
        }

        if (plan.Template.UsesComposer)
        {
            // En una carpeta temporal al lado y renombrada al final: si falla o se cancela, no queda nada.
            var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
            Journal.TempFolder = temp;
            lines.Report($"En {temp} (pasa a {folder} al acabar)");
            var outcome = await ComposerAsync(target, ["create-project", plan.Template.Package!, temp, ComposerTool.NoProgress], parent, lines, cancellation, echo: false);
            if (outcome.Ok && plan.Webapp)
            {
                // Sin los compose.yaml de Docker de las recetas: Roost ya da la base de datos (y Flex no pregunta).
                outcome = await ComposerAsync(target, ["config", "--json", "extra.symfony.docker", "false"], temp, lines, cancellation);
                if (outcome.Ok) outcome = await ComposerAsync(target, ["require", ComposerTool.NoProgress, "webapp"], temp, lines, cancellation);
            }
            if (!outcome.Ok)
            {
                InstallFiles.TryDeleteDirectory(temp);
                Journal.TempFolder = null;
                return outcome;
            }
            Journal.FolderExisted = Directory.Exists(folder);
            if (Journal.FolderExisted) Directory.Delete(folder);
            InstallFiles.MoveDirectory(temp, folder);
            Journal.TempFolder = null;
            Journal.FolderCreated = true;
            lines.Report($"Proyecto en {folder}");
        }
        else
        {
            var http = Http ?? throw new InvalidOperationException("Descargar WordPress necesita un HttpClient (DeployRunner.Http).");
            Journal.FolderExisted = Directory.Exists(folder);
            if (Journal.FolderExisted) Directory.Delete(folder);
            try
            {
                await new WordPressInstaller(paths, http).InstallAsync(folder, lines, cancellation);
            }
            catch
            {
                if (Journal.FolderExisted && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                throw;
            }
            Journal.FolderCreated = true;
        }

        // Registrado ya, con su base de datos: si un paso de después falla, Undo lo quita.
        var sites = new SiteManager(paths);
        var change = sites.Add(plan.Name, folder, root: null, php: plan.PhpSpec ?? "global");
        Journal.SiteCreated = change.Created;
        if (plan.Database is { } database) sites.SetDatabase(plan.Name, database);
        lines.Report($"Sitio {change.Site.Host} registrado: raiz web {(change.Site.Root is null ? "la carpeta" : change.Site.Root + "/")}.");
        return DeployOutcome.Done();
    }

    // --- La base de datos ---

    private DeployOutcome CreateDatabase(DeployTarget target, Sink lines)
    {
        if (target.Database is not { } db) return DeployOutcome.Skipped("No hay base de datos en Roost.");
        if (db.State == DbState.Stopped) throw new RoostException($"{db.Label} esta parada: arranca Roost (roost up) y repite este paso.");
        if (DbAdmin.List(db.Db, db.IniPath, _processes).Contains(db.Name, StringComparer.OrdinalIgnoreCase))
            return DeployOutcome.Skipped($"{db.Name} ya existe en {db.Label}.");
        DbAdmin.Create(db.Db, db.IniPath, db.Name, _processes);
        Journal.DatabaseCreated = true;
        lines.Report($"Base de datos {db.Name} creada en {db.Label} (utf8mb4).");
        return DeployOutcome.Done();
    }

    // --- Los .env ---

    private DeployOutcome LaravelEnv(DeployTarget target, DeployStep step, Sink lines)
    {
        var path = Path.Combine(target.Folder, ".env");
        var example = Path.Combine(target.Folder, ".env.example");
        EnvFile env;
        bool bom, overwrite;
        string from;
        if (File.Exists(path))
        {
            (env, bom) = EnvFile.Load(path);
            // El de una plantilla es de Roost; el que ya tenía el usuario, no se pisa.
            overwrite = step.EnvMode == EnvMode.Fresh;
            from = overwrite ? ".env (recien creado)" : ".env (ya estaba: solo se anade lo que falta)";
        }
        else if (File.Exists(example))
        {
            (env, bom) = EnvFile.Load(example);
            overwrite = true;
            from = ".env.example";
        }
        else
        {
            env = EnvFile.Parse(MinimalLaravelEnv(target.SiteName));
            bom = false;
            overwrite = true;
            from = "una plantilla minima (no hay .env.example)";
        }

        lines.Report($"Desde {from}:");
        var changes = env.Merge(DeployPlan.LaravelEnv(target), overwrite);
        Report(lines, env, changes);
        var wrote = !File.Exists(path) || changes.Any(c => c.Change is EnvChange.Added or EnvChange.Changed);
        if (wrote) env.Save(path, bom);
        return wrote ? DeployOutcome.Done() : DeployOutcome.Skipped("Ya tenia todo lo de Roost.");
    }

    private DeployOutcome SymfonyEnv(DeployTarget target, Sink lines)
    {
        var path = Path.Combine(target.Folder, ".env.local");
        var exists = File.Exists(path);
        var (env, bom) = exists
            ? EnvFile.Load(path)
            : (EnvFile.Parse("# Lo de este equipo, que no va a git (Symfony lo lee despues de .env). Lo creo Roost.\n"), false);
        var values = DeployPlan.SymfonyEnv(target, ProjectKind.Detect(target.Folder).HasDoctrine).ToList();
        // Sin APP_SECRET en ningún .env, Symfony avisa (y firma con una vacía): uno al azar, solo en este equipo.
        if (!HasSecret(target.Folder)) values.Add(new("APP_SECRET", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))));
        lines.Report(exists ? "En .env.local (ya estaba: solo se anade lo que falta):" : "Nuevo .env.local:");
        var changes = env.Merge(values, overwrite: !exists);
        Report(lines, env, changes);
        var wrote = !exists || changes.Any(c => c.Change is EnvChange.Added or EnvChange.Changed);
        if (wrote) env.Save(path, bom);
        return wrote ? DeployOutcome.Done() : DeployOutcome.Skipped("Ya tenia todo lo de Roost.");
    }

    /// <summary>true si algún .env de Symfony (los que lee en dev) tiene APP_SECRET con algo.</summary>
    private static bool HasSecret(string folder)
    {
        foreach (var name in new[] { ".env.local", ".env.dev.local", ".env.dev", ".env" })
        {
            var file = Path.Combine(folder, name);
            if (File.Exists(file) && !string.IsNullOrEmpty(EnvFile.Load(file).Env.Get("APP_SECRET"))) return true;
        }
        return false;
    }

    /// <summary>Cuenta qué se ha puesto, sin enseñar nunca una contraseña.</summary>
    private static void Report(Sink lines, EnvFile env, IReadOnlyList<(string Key, EnvChange Change)> changes)
    {
        foreach (var (key, change) in changes)
        {
            var value = env.Get(key) ?? "";
            var shown = key.EndsWith("PASSWORD", StringComparison.Ordinal) || key == "APP_SECRET" || key == "DATABASE_URL" && value.Contains(':') && value.Contains('@')
                ? Mask(key, value)
                : value;
            lines.Report(change switch
            {
                EnvChange.Added => $"  + {key}={shown}",
                EnvChange.Changed => $"  ~ {key}={shown}",
                EnvChange.Kept => $"  = {key} (se deja el que tenia)",
                _ => $"  = {key}={shown}",
            });
        }
    }

    private static string Mask(string key, string value) => key switch
    {
        "APP_SECRET" => "(uno nuevo, al azar)",
        "DATABASE_URL" => System.Text.RegularExpressions.Regex.Replace(value, @"://([^:@/]+):[^@]*@", "://$1:***@"),
        _ => value.Length == 0 || value == "null" ? (value.Length == 0 ? "(vacia)" : value) : "(la que guarda Roost)",
    };

    /// <summary>Un .env de Laravel mínimo, para el proyecto que no trae .env.example.</summary>
    public static string MinimalLaravelEnv(string siteName) => $"""
        # Lo creo Roost: el proyecto no trae .env.example.
        APP_NAME={EnvFile.Quote(siteName)}
        APP_ENV=local
        APP_KEY=
        APP_DEBUG=true
        APP_URL=http://localhost

        LOG_CHANNEL=stack
        LOG_LEVEL=debug

        """.Replace("\r\n", "\n");

    // --- Composer, artisan y bin/console ---

    /// <param name="echo">Cuenta la orden antes de ejecutarla (las de dentro de un paso; la del paso ya la enseña quien llama).</param>
    private async Task<DeployOutcome> ComposerAsync(DeployTarget target, IReadOnlyList<string> arguments, string directory, Sink lines, CancellationToken cancellation, bool echo = true)
    {
        var composer = Composer ?? throw new RoostException("Composer no esta instalado: instalalo antes (roost composer install).");
        ComposerTool.CheckPhp(target.Php);
        var spec = ComposerTool.Command(paths, target.Php, composer, arguments, directory);
        if (echo) lines.Report($"> {ComposerTool.Display(arguments)}");
        return Outcome(await commands.RunAsync(spec, lines, cancellation), ComposerTool.Display(arguments));
    }

    private async Task<DeployOutcome> PhpAsync(DeployTarget target, IReadOnlyList<string> arguments, Sink lines, CancellationToken cancellation)
    {
        var environment = new Dictionary<string, string?>();
        foreach (var (name, value) in TerminalPhp.Environment(paths, target.Php)) environment[name] = value;
        var spec = new CommandSpec(target.Php.Executable, arguments, target.Folder, environment);
        return Outcome(await commands.RunAsync(spec, lines, cancellation), "php " + string.Join(' ', arguments.Select(a => a.Replace('\\', '/'))));
    }

    private async Task<DeployOutcome> AppKeyAsync(DeployTarget target, Sink lines, CancellationToken cancellation)
    {
        var env = Path.Combine(target.Folder, ".env");
        if (!File.Exists(env))
            throw new RoostException("No hay .env: key:generate escribe la clave en el. Haz antes el paso del .env.");
        if (!string.IsNullOrEmpty(EnvFile.Load(env).Env.Get("APP_KEY")))
            return DeployOutcome.Skipped("Ya tiene APP_KEY: no se cambia (cambiarla invalida las sesiones y lo cifrado).");
        return await PhpAsync(target, ["artisan", "key:generate", "--no-interaction"], lines, cancellation);
    }

    private async Task<DeployOutcome> StorageLinkAsync(DeployTarget target, Sink lines, CancellationToken cancellation)
    {
        var link = Path.Combine(target.Folder, "public", "storage");
        if (Directory.Exists(link) || File.Exists(link)) return DeployOutcome.Skipped("public/storage ya esta.");
        return await PhpAsync(target, ["artisan", "storage:link", "--no-interaction"], lines, cancellation);
    }

    // --- npm ---

    /// <summary>
    /// Las dependencias como fija el lockfile y, si hay script <c>build</c>, el build, con el Node de Roost
    /// de la carpeta. Se lee el package.json ahora (en una plantilla, lo acaba de crear create-project).
    /// </summary>
    private async Task<DeployOutcome> NpmAsync(DeployTarget target, Sink lines, CancellationToken cancellation)
    {
        var choice = target.Node
            ?? throw new RoostException(target.NodeProblem ?? $"No hay ningun Node de Roost: instala uno (roost node install {NodeCatalog.Default.Major}).");
        var project = NodeProject.Read(target.Folder);
        if (!project.HasPackageJson) return DeployOutcome.Skipped("No hay package.json: no hay nada que instalar.");
        if (project.Unreadable) throw new RoostException("El package.json del proyecto no es JSON valido: arreglalo y repite el paso.");
        if (NodeTool.WhyNot(project.Manager, choice.Node) is { } why) throw new RoostException(why);

        lines.Report($"Con {choice.Node.Label} ({choice.Reason}).");
        var install = NodeTool.InstallArguments(project, clean: true);
        var display = NodeTool.Display(project.Manager, install);
        lines.Report($"> {display}");
        var outcome = Outcome(await commands.RunAsync(NodeTool.Command(paths, choice.Node, project.Manager, install, target.Folder), lines, cancellation), display);
        if (!outcome.Ok || !project.HasScript("build")) return outcome;

        var build = NodeTool.RunArguments("build");
        display = NodeTool.Display(project.Manager, build);
        lines.Report($"> {display}");
        return Outcome(await commands.RunAsync(NodeTool.Command(paths, choice.Node, project.Manager, build, target.Folder), lines, cancellation), display);
    }

    private static DeployOutcome Outcome(CommandResult result, string what) =>
        result.Succeeded ? DeployOutcome.Done()
        : result.TimedOut ? new DeployOutcome(DeployOutcomeKind.Failed, $"{what} se ha pasado del tiempo y se ha parado.", result.ExitCode)
        : new DeployOutcome(DeployOutcomeKind.Failed, $"{what} termino con el codigo {result.ExitCode}.", result.ExitCode);

    // --- WordPress ---

    private static DeployOutcome WriteWpConfig(DeployTarget target, Sink lines)
    {
        var config = Path.Combine(target.Folder, WpConfig.FileName);
        if (File.Exists(config)) return DeployOutcome.Skipped("Ya hay wp-config.php: no se toca.");
        var sample = Path.Combine(target.Folder, WpConfig.SampleFileName);
        if (!File.Exists(sample)) throw new RoostException($"No esta {WpConfig.SampleFileName}: no hay de donde sacar el wp-config.php.");
        if (target.Database is not { } db) throw new RoostException("No hay base de datos en Roost: el instalador de WordPress te la pedira.");

        // Un BOM delante de <?php sale como texto en cada página: nunca en un .php.
        var text = File.ReadAllText(sample, new UTF8Encoding(false)).TrimStart('﻿');
        var host = WpConfig.Host(db.Port);
        var result = WpConfig.FromSample(text, new WpDatabase(db.Name, DbAdmin.RootUser, db.Password ?? "", host));
        var temp = config + ".roost-tmp";
        File.WriteAllText(temp, result, new UTF8Encoding(false));
        File.Move(temp, config);
        lines.Report($"  DB_NAME={db.Name}");
        lines.Report($"  DB_USER={DbAdmin.RootUser}");
        lines.Report($"  DB_PASSWORD={(db.Password is null ? "(vacia)" : "(la que guarda Roost)")}");
        lines.Report($"  DB_HOST={host}");
        lines.Report($"  {WpConfig.SaltNames.Count} claves y salts nuevas (al azar, generadas en este equipo)");
        return DeployOutcome.Done();
    }

    private static DeployOutcome WriteLoopbackPlugin(DeployTarget target, Sink lines)
    {
        var dir = Path.Combine(target.Folder, "wp-content", "mu-plugins");
        var file = Path.Combine(dir, WpConfig.LoopbackPluginName);
        if (File.Exists(file)) return DeployOutcome.Skipped("El mu-plugin ya esta.");
        if (!Directory.Exists(Path.Combine(target.Folder, "wp-content"))) throw new RoostException("No hay wp-content: no parece un WordPress.");
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, WpConfig.LoopbackPlugin.Replace("\r\n", "\n"), new UTF8Encoding(false));
        lines.Report($"  wp-content/mu-plugins/{WpConfig.LoopbackPluginName}");
        return DeployOutcome.Done();
    }

    /// <summary>Cada línea va al log, a quien escuche y a las últimas que se guardan para contar un fallo.</summary>
    private sealed class Sink(IProgress<string>? listener, DeployLog.Session log) : IProgress<string>
    {
        private readonly Queue<string> _tail = new();

        public IReadOnlyList<string> Tail
        {
            get { lock (_tail) return _tail.ToList(); }
        }

        public void Report(string value)
        {
            log.Report(value);
            lock (_tail)
            {
                if (value.Trim().Length > 0)
                {
                    _tail.Enqueue(value);
                    while (_tail.Count > TailLines) _tail.Dequeue();
                }
            }
            listener?.Report(value);
        }
    }

    private sealed class LineEvents(IProgress<DeployEvent>? events, int index, DeployStep step) : IProgress<string>
    {
        public void Report(string value) => events?.Report(new DeployEvent(index, step, value));
    }
}

/// <summary>La salida de los pasos de un sitio, en <c>logs/pasos-&lt;sitio&gt;.log</c> (la enseña Registros).</summary>
public static class DeployLog
{
    public static string FilePath(RoostPaths paths, string site) => Path.Combine(paths.Logs, $"pasos-{site}.log");

    public static Session Begin(RoostPaths paths, string site, string step)
    {
        try
        {
            Directory.CreateDirectory(paths.Logs);
            var writer = new StreamWriter(new FileStream(FilePath(paths, site), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {step}");
            return new Session(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Session(null);   // sin log: el paso sigue igual
        }
    }

    /// <summary>Las líneas de un paso; síncrono, escribe en el acto. El log nunca tumba el paso.</summary>
    public sealed class Session(StreamWriter? writer) : IProgress<string>, IDisposable
    {
        private readonly Lock _lock = new();

        public void Report(string value)
        {
            lock (_lock)
            {
                try { writer?.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {Git.GitLog.Redact(value)}"); }
                catch (IOException) { }
            }
        }

        public void End(bool ok, string summary)
        {
            lock (_lock)
            {
                try { writer?.WriteLine($"=== {(ok ? "ok" : "FALLO")}: {summary}"); writer?.WriteLine(); }
                catch (IOException) { }
            }
        }

        public void Dispose()
        {
            lock (_lock) writer?.Dispose();
        }
    }
}
