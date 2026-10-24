using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Db;

/// <summary>Lo que comparten export e import: la base de datos global en marcha, sin pintar nada (para --json), y los mensajes.</summary>
internal static class DbDumpSupport
{
    /// <summary>La base de datos global en marcha; o null y por que no.</summary>
    public static async Task<((InstalledDb Db, string Ini, int Port)? Session, string? Error)> SessionAsync(RoostPaths paths, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        var status = await client.TryStatusAsync(cancellation);
        if (status is not { Running: true, Db: not null })
            return (null, "La base de datos no esta en marcha. Arranca el stack con: roost up");
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var db = planner.ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled());
        if (db is null) return (null, "No hay base de datos global: elige una con roost db use <motor@version>.");
        return ((db, planner.DbIniPath(db), status.DbPort ?? RoostConfig.Load(paths.ConfigFile).DbPort), null);
    }

    public static string Size(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    public static string Seconds(TimeSpan took) => took.TotalSeconds < 60 ? $"{took.TotalSeconds:0.0} s" : $"{(int)took.TotalMinutes} min {took.Seconds} s";

    public static int JsonError(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, JsonOutput.Options));
        return 1;
    }

    public static bool Interactive => !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;
}

/// <summary><c>roost db export</c>: vuelca una base de datos a un .sql (o .sql.gz) con el volcador de la instalacion.</summary>
public sealed class DbExportCommand : AsyncCommand<DbExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<BASE>")]
        [Description("La base de datos que se exporta (de la global en marcha).")]
        public string Name { get; init; } = "";

        [CommandOption("-o|--out <FICHERO>")]
        [Description("Donde se guarda: .sql, o .sql.gz para comprimirlo. Por defecto, <base>-<fecha>.sql en la carpeta actual.")]
        public string? Out { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Sobrescribir el fichero si ya existe, sin preguntar.")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para scripts.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var json = settings.Json;
        int Fail(string message)
        {
            if (json) return DbDumpSupport.JsonError(message);
            AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
            return 1;
        }

        if (!DbAdmin.IsValidName(settings.Name)) return Fail($"'{settings.Name}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");
        if (DbAdmin.SystemSchemas.Contains(settings.Name)) return Fail($"{settings.Name} es del propio servidor: exporta las tuyas.");

        var paths = RoostPaths.Default();
        var file = Path.GetFullPath(settings.Out ?? DbDump.SuggestedFileName(settings.Name, DateTime.Now));
        try
        {
            var (session, error) = await DbDumpSupport.SessionAsync(paths, cancellation);
            if (session is not { } s) return Fail(error!);

            if (File.Exists(file) && !settings.Yes)
            {
                if (json || !DbDumpSupport.Interactive) return Fail($"{file} ya existe: repitelo con --yes para sobrescribirlo, o elige otro con --out.");
                if (!AnsiConsole.Confirm($"{Markup.Escape(file)} ya existe. ¿Lo sobrescribo?", defaultValue: false)) return 1;
            }

            var tool = DbTool.For(s.Db, s.Ini);
            var warnings = new List<string>();
            var lines = new Progress<string>(line => { if (line.Trim().Length > 0) lock (warnings) warnings.Add(line.Trim()); });
            DbExportResult result;
            if (json)
            {
                result = await DbDump.ExportAsync(tool, settings.Name, file, CommandRunner.Default, null, lines, cancellation);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    database = settings.Name,
                    server = s.Db.Label,
                    file = result.File,
                    bytes = result.Bytes,
                    compressed = result.Compressed,
                    seconds = Math.Round(result.Took.TotalSeconds, 1),
                    warnings,
                }, JsonOutput.Options));
                return 0;
            }

            DbExportResult? done = null;
            await AnsiConsole.Status().StartAsync($"Exportando {settings.Name}...", async ctx =>
            {
                var written = new Progress<DbTransferProgress>(p => ctx.Status($"Exportando {settings.Name}... {DbDumpSupport.Size(p.Bytes)} volcados"));
                done = await DbDump.ExportAsync(tool, settings.Name, file, CommandRunner.Default, written, lines, cancellation);
            });
            result = done!;
            AnsiConsole.MarkupLineInterpolated($"[green]Exportada {settings.Name}[/] a {result.File} [grey]({DbDumpSupport.Size(result.Bytes)}{(result.Compressed ? " comprimido" : "")}, {DbDumpSupport.Seconds(result.Took)}, con {Path.GetFileName(tool.Dump)})[/]");
            foreach (var warning in warnings.Take(5)) AnsiConsole.MarkupLineInterpolated($"[grey]{warning}[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Para traerla a otra: roost db import <base> \"{result.File}\" --create[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelado: no ha quedado ningun fichero a medias.");
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }
}

/// <summary>
/// <c>roost db import</c>: carga un .sql, .sql.gz o .zip en una base de datos, en streaming y con su
/// progreso. Nunca sobre una del sistema; si no existe, con --create; si tiene tablas, pregunta.
/// </summary>
public sealed class DbImportCommand : AsyncCommand<DbImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<BASE>")]
        [Description("La base de datos en la que se importa (letras, numeros y _).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "<FICHERO>")]
        [Description("El volcado: .sql, .sql.gz o un .zip con un solo .sql dentro.")]
        public string File { get; init; } = "";

        [CommandOption("--create")]
        [Description("Crearla (utf8mb4) si no existe.")]
        public bool Create { get; init; }

        [CommandOption("--compat")]
        [Description("Adaptar al vuelo lo que este servidor no entiende de un volcado de otro motor (colaciones utf8mb4_0900_* o uca1400, NO_AUTO_CREATE_USER...).")]
        public bool Compat { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No preguntar si ya tiene tablas (para scripts).")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para scripts.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var json = settings.Json;
        int Fail(string message)
        {
            if (json) return DbDumpSupport.JsonError(message);
            AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
            return 1;
        }

        if (!DbAdmin.IsValidName(settings.Name)) return Fail($"'{settings.Name}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");
        if (DbAdmin.SystemSchemas.Contains(settings.Name)) return Fail($"{settings.Name} es del propio servidor: nunca se importa encima.");
        var file = Path.GetFullPath(settings.File);
        if (!System.IO.File.Exists(file)) return Fail($"No existe {file}.");

        var paths = RoostPaths.Default();
        var created = false;
        var started = false;
        (InstalledDb Db, string Ini, int Port) s = default;
        try
        {
            var (session, error) = await DbDumpSupport.SessionAsync(paths, cancellation);
            if (session is null) return Fail(error!);
            s = session.Value;
            var tool = DbTool.For(s.Db, s.Ini);
            var runner = ProcessRunner.Default;

            var info = await DbDump.SniffAsync(file, cancellation);
            if (!info.LooksLikeSql) return Fail($"{Path.GetFileName(file)} no parece un volcado SQL (ni .sql, ni .sql.gz, ni un .zip con un .sql).");

            var target = DbAdmin.Inspect(s.Db, s.Ini, settings.Name, runner);
            if (!target.Exists && !settings.Create)
                return Fail($"No existe {settings.Name} en {s.Db.Label}: anade --create para crearla.");
            if (target.Exists && !target.IsEmpty && !settings.Yes)
            {
                var question = $"{settings.Name} ya tiene {target.Tables} tabla(s): las que tambien vengan en el fichero se sustituyen y el resto se queda.";
                if (json || !DbDumpSupport.Interactive) return Fail(question + " Repitelo con --yes para importar encima.");
                if (!AnsiConsole.Confirm(Markup.Escape(question) + " ¿Importar encima?", defaultValue: false)) return 1;
            }

            // Lo que este servidor no entenderia del fichero (un volcado de MySQL 8 en MariaDB, o al reves).
            var plan = SqlCompat.PlanFor(s.Db.Engine, s.Db.Version, DbAdmin.Collations(s.Db, s.Ini, runner));
            var issues = SqlCompat.Issues(info, plan);
            var compat = settings.Compat;
            if (issues.Count > 0 && !compat && !json)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {(info.Origin ?? "el fichero")} trae cosas que {s.Db.Label} no entiende:");
                foreach (var issue in issues) AnsiConsole.MarkupLineInterpolated($"  - {issue.What} [grey]({issue.Fix})[/]");
                if (DbDumpSupport.Interactive)
                    compat = AnsiConsole.Confirm("¿Lo adapto al vuelo? (el fichero no se toca)", defaultValue: true);
                else
                    AnsiConsole.MarkupLine("[grey]Sigo sin adaptarlo; para adaptarlo al vuelo, repitelo con --compat.[/]");
            }

            if (!target.Exists)
            {
                DbAdmin.Create(s.Db, s.Ini, settings.Name, runner);
                created = true;
            }

            DbImportResult result;
            started = true;
            if (json)
            {
                result = await DbDump.ImportAsync(tool, settings.Name, file, compat ? plan : null, CommandRunner.Default, null, null, cancellation);
            }
            else
            {
                DbImportResult? done = null;
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask($"Importando en {Markup.Escape(settings.Name)}", maxValue: 100);
                        var read = new Progress<DbTransferProgress>(p =>
                        {
                            if (p.Percent is { } percent) task.Value = Math.Max(task.Value, percent);
                            task.Description = $"Importando en {Markup.Escape(settings.Name)} [grey]{DbDumpSupport.Size(p.Bytes)} de {DbDumpSupport.Size(p.Total ?? p.Bytes)}[/]";
                        });
                        done = await DbDump.ImportAsync(tool, settings.Name, file, compat ? plan : null, CommandRunner.Default, read, null, cancellation);
                        task.Value = 100;
                    });
                result = done!;
            }

            var tables = DbAdmin.Inspect(s.Db, s.Ini, settings.Name, runner).Tables;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    database = settings.Name,
                    server = s.Db.Label,
                    file = result.File,
                    bytes = result.Bytes,
                    created,
                    tables,
                    compat,
                    origin = info.Origin,
                    seconds = Math.Round(result.Took.TotalSeconds, 1),
                    notes = result.Notes,
                    issues = compat ? [] : issues.Select(i => i.What).ToArray(),
                }, JsonOutput.Options));
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Importado[/] {Path.GetFileName(file)} en [bold]{settings.Name}[/] [grey]({tables} tabla(s){(created ? ", creada para esto" : "")}, {DbDumpSupport.Size(result.Bytes)}, {DbDumpSupport.Seconds(result.Took)})[/]");
            foreach (var note in result.Notes) AnsiConsole.MarkupLineInterpolated($"[grey]{note}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Fail(started || created ? "Cancelado: se ha parado el cliente. " + Undo(s, settings.Name, created) : "Cancelado: no se ha importado nada.");
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var message = ex is InvalidDataException ? $"{Path.GetFileName(file)} esta roto: {ex.Message}" : ex.Message;
            if (started || created) message += " " + Undo(s, settings.Name, created);
            return Fail(message.Trim());
        }
    }

    /// <summary>Si se creo para esto, se borra (no queda nada a medias); si ya existia, lo dice.</summary>
    private static string Undo((InstalledDb Db, string Ini, int Port) s, string name, bool created)
    {
        if (s.Db is null) return "";
        if (!created) return $"{name} puede haber quedado a medias: lo que llego a importarse, ahi esta.";
        try
        {
            DbAdmin.Drop(s.Db, s.Ini, name, ProcessRunner.Default);
            return $"Se ha borrado {name}, que se habia creado para esto: no queda nada a medias.";
        }
        catch (RoostException)
        {
            return $"{name} se habia creado para esto y no se ha podido borrar: borrala con roost db drop {name}.";
        }
    }
}
