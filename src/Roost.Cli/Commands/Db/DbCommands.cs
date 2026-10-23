using System.ComponentModel;
using System.Text.Json;
using Roost.Core.Audit;
using Roost.Cli.Net;
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

/// <summary>Ajustes de los comandos que reciben un <c>motor@version</c>.</summary>
public sealed class DbSpecSettings : CommandSettings
{
    [CommandArgument(0, "<MOTOR@VERSION>")]
    [Description("mariadb@11.4, mariadb (la LTS mas nueva), mysql@8.4, mysql@8, mysql@5.7...")]
    public string Spec { get; init; } = "";
}

/// <summary>Catalogos de bases de datos: MariaDB de su API (si hay red) y MySQL fijado.</summary>
internal static class DbCatalogs
{
    public static async Task<(IReadOnlyList<DbBuild> Builds, string? MariaDbError)> LoadAsync(CancellationToken cancellation)
    {
        var builds = new List<DbBuild>();
        string? error = null;
        try
        {
            builds.AddRange(await MariaDbCatalog.FetchAsync(RoostHttp.Client, cancellation));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   && !cancellation.IsCancellationRequested)
        {
            error = $"No se pudo leer el catalogo de MariaDB (downloads.mariadb.org): {ex.Message}";
        }
        builds.AddRange(MySqlCatalog.Windows);
        return (builds, error);
    }

    public static AuditFinding Audit(DbEngine engine, Core.Models.ComponentVersion version, string path, string source) =>
        CatalogAudit.Evaluate(engine.Kind(), version, path, source);

    /// <summary>La base de datos global segun roost.toml, o null; sin lanzar si la configuracion no cuadra.</summary>
    public static InstalledDb? Global(RoostPaths paths, IReadOnlyList<InstalledDb> installed)
    {
        try { return new StackPlanner(paths, ProcessRunner.Default).ResolveDb(RoostConfig.Load(paths.ConfigFile), installed); }
        catch (RoostException) { return null; }
    }

    /// <summary>Si el stack esta en marcha, lo rearranca para aplicar la configuracion nueva.</summary>
    public static Task<int> ApplyIfRunningAsync(RoostPaths paths, CancellationToken cancellation) =>
        StackApply.IfRunningAsync(paths, s => (s.Db ?? "sin base de datos") + ".", cancellation);
}

/// <summary><c>roost db list</c>: bases de datos instaladas por Roost y disponibles, con su estado de seguridad.</summary>
public sealed class DbListCommand : AsyncCommand<DbListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de las tablas.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var installed = new DbInstaller(paths).ListInstalled();
        var global = DbCatalogs.Global(paths, installed);
        var (catalog, error) = await DbCatalogs.LoadAsync(cancellation);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = installed.Select(db =>
                {
                    var f = DbCatalogs.Audit(db.Engine, db.Version!.Value, db.Server, "Roost");
                    return new { spec = db.Spec, engine = db.Engine.Key(), version = db.Name, path = db.Directory, global = db == global, verdict = f.VerdictLabel, color = f.Color.ToString().ToLowerInvariant() };
                }),
                catalogError = error,
                catalog = catalog.Select(b =>
                {
                    var f = DbCatalogs.Audit(b.Engine, b.Version, b.FileName, "catalogo");
                    return new { spec = b.Spec, engine = b.Engine.Key(), version = b.Version.ToString(), lts = b.Lts, verdict = f.VerdictLabel, color = f.Color.ToString().ToLowerInvariant(), file = b.FileName, urls = b.Urls, sha256 = b.Sha256 };
                }),
            }, JsonOutput.Options));
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[bold]Bases de datos instaladas por Roost[/]  [grey](* = la global, la que arranca roost up)[/]");
        if (installed.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Ninguna todavia. Prueba: roost db install mariadb@11.4  o  roost db install mysql@8.4[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(""); table.AddColumn("Instalacion"); table.AddColumn("Veredicto"); table.AddColumn("Detalle");
            foreach (var db in installed)
            {
                var f = DbCatalogs.Audit(db.Engine, db.Version!.Value, db.Server, "Roost");
                table.AddRow(CatalogAudit.Dot(f.Color), Markup.Escape(db.Spec) + (db == global ? " [green]*[/]" : ""), f.VerdictLabel, Markup.Escape(f.Headline));
            }
            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Disponibles[/]  [grey](MariaDB: la ultima de cada rama estable; MySQL: las fijadas por Roost)[/]");
        if (error is not null) AnsiConsole.MarkupLineInterpolated($"[yellow]{error}[/]");

        var available = new Table().Border(TableBorder.Rounded);
        available.AddColumn(""); available.AddColumn("Motor@rama"); available.AddColumn("Version"); available.AddColumn("Soporte"); available.AddColumn("Veredicto"); available.AddColumn("Instalada");
        foreach (var b in catalog)
        {
            var f = DbCatalogs.Audit(b.Engine, b.Version, b.FileName, "catalogo");
            var mine = installed.Any(db => db.Engine == b.Engine && db.Version is { } v && v.CompareTo(b.Version) == 0);
            available.AddRow(CatalogAudit.Dot(f.Color), Markup.Escape(b.Spec), Markup.Escape(b.Version.ToString()),
                b.Lts ? "LTS" : "", f.VerdictLabel, mine ? "si" : "");
        }
        AnsiConsole.Write(available);
        AnsiConsole.MarkupLine("[grey]Instalar: roost db install <motor@version>   Elegir la global: roost db use <motor@version>[/]");
        return 0;
    }
}

/// <summary><c>roost db install</c>: descarga, verifica (SHA-256) e instala MariaDB o MySQL.</summary>
public sealed class DbInstallCommand : AsyncCommand<DbSpecSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DbSpecSettings settings, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost db install solo funciona en Windows por ahora[/] (ROOST.md: fase 1, Windows primero).");
            return 1;
        }

        var paths = RoostPaths.Default();
        var installer = new DbInstaller(paths, RoostHttp.Client);
        try
        {
            var spec = DbSpec.Parse(settings.Spec);
            var (catalog, error) = await DbCatalogs.LoadAsync(cancellation);
            if (error is not null && spec.Engine != DbEngine.MySql)
                AnsiConsole.MarkupLineInterpolated($"[yellow]{error}[/]");
            var build = DbCatalog.Resolve(catalog, spec);

            if (installer.IsInstalled(build))
            {
                AnsiConsole.MarkupLineInterpolated($"[green]{build.Label} ya esta instalado[/] en {installer.InstallDirectory(build)}.");
                return 0;
            }

            var finding = DbCatalogs.Audit(build.Engine, build.Version, build.FileName, "catalogo");
            if (finding.Color != AuditColor.Green)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {finding.Headline}");

            AnsiConsole.MarkupLineInterpolated($"Descargando [bold]{build.FileName}[/] de {build.Urls[0].Host}");
            InstalledDb? installed = null;
            await AnsiConsole.Status().StartAsync($"Instalando {build.Label}...", async ctx =>
            {
                var progress = new Progress<long>(bytes => ctx.Status($"Instalando {build.Label}... {bytes / 1048576.0:0.0} MB descargados"));
                installed = await installer.InstallAsync(build, progress, cancellation);
            });
            AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {installed!.Label} en {installed.Directory} [grey](SHA-256 verificado)[/]");

            SmokeTest(installed);
            var global = DbCatalogs.Global(paths, installer.ListInstalled());
            if (global != installed)
                AnsiConsole.MarkupLineInterpolated($"Para usarla: [bold]roost db use {installed.Engine.Key()}@{installed.Branch}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    /// <summary>Si el servidor no responde a --version, casi siempre falta un Visual C++ Redistributable.</summary>
    private static void SmokeTest(InstalledDb db)
    {
        var output = ProcessRunner.Default.Run(db.Server, "--version", TimeSpan.FromSeconds(15));
        var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Ver", StringComparison.Ordinal));
        if (line is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]{line}[/]");
            return;
        }

        // MySQL 5.7 se compilo con Visual Studio 2013; el resto, con 2015-2022.
        var redist = db.Engine == DbEngine.MySql && db.Version is { Major: 5 }
            ? "el de Visual C++ 2013 (x64): https://aka.ms/highdpimfc2013x64enu"
            : "el de Visual C++ 2015-2022 (x64): https://aka.ms/vs/17/release/vc_redist.x64.exe";
        AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {db.Server} no responde a --version. Suele faltar el Redistributable {redist}");
    }
}

/// <summary><c>roost db remove</c>: quita los binarios de una base de datos de Roost. Los datos se quedan.</summary>
public sealed class DbRemoveCommand : Command<DbSpecSettings>
{
    public override int Execute(CommandContext context, DbSpecSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var installer = new DbInstaller(paths);
        try
        {
            var spec = DbSpec.Parse(settings.Spec);
            var matches = DbInstaller.Match(installer.ListInstalled(), spec);
            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ninguna base de datos de Roost que case con '{settings.Spec}'.[/] Mira las instaladas con: roost db list");
                return 1;
            }
            if (matches.Count > 1)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]'{settings.Spec}' casa con varias:[/] {string.Join(", ", matches.Select(m => m.Spec))}. Indica la version exacta.");
                return 1;
            }

            var db = matches[0];
            installer.Remove(db);
            AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {db.Label}.");
            var data = Path.Combine(paths.Data, db.Engine.Key(), db.Branch!);
            if (Directory.Exists(data))
                AnsiConsole.MarkupLineInterpolated($"[grey]Sus datos siguen en {data}: Roost nunca borra datos. Borralos a mano si ya no los quieres.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost db use</c>: fija la base de datos global (o ninguna) y la aplica si el stack esta en marcha.</summary>
public sealed class DbUseCommand : AsyncCommand<DbUseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<MOTOR@VERSION>")]
        [Description("La que arranca roost up: mariadb@11.4, mysql@8.4, mysql@5.7... o none (ninguna).")]
        public string Spec { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            if (settings.Spec.Trim().Equals(RoostConfig.NoDb, StringComparison.OrdinalIgnoreCase))
            {
                RoostConfig.SetDb(paths.ConfigFile, RoostConfig.NoDb, null);
                AnsiConsole.MarkupLineInterpolated($"[green]Sin base de datos:[/] roost up ya no arranca ninguna [grey](guardado en {paths.ConfigFile})[/]");
                return await DbCatalogs.ApplyIfRunningAsync(paths, cancellation);
            }

            var spec = DbSpec.Parse(settings.Spec);
            var matches = DbInstaller.Match(new DbInstaller(paths).ListInstalled(), spec);
            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ninguna base de datos de Roost que case con '{settings.Spec}'.[/] Instalala: roost db install {settings.Spec}");
                return 1;
            }

            var chosen = matches.OrderByDescending(m => m.Version).First();
            RoostConfig.SetDb(paths.ConfigFile, chosen.Engine.Key(), spec.Version);
            AnsiConsole.MarkupLineInterpolated($"[green]Base de datos global:[/] {chosen.Label} [grey](guardado en {paths.ConfigFile})[/]");
            return await DbCatalogs.ApplyIfRunningAsync(paths, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost db create</c>: crea una base de datos (utf8mb4) en la base de datos global, que tiene que estar en marcha.</summary>
public sealed class DbCreateCommand : AsyncCommand<DbCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre de la base de datos: letras, numeros y _ (por ejemplo tienda).")]
        public string Name { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!DbAdmin.IsValidName(settings.Name))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]'{settings.Name}' no vale como nombre:[/] usa solo letras, numeros y _ (hasta 64).");
            return 1;
        }

        var paths = RoostPaths.Default();
        using var client = new DaemonClient(paths);
        var status = await client.TryStatusAsync(cancellation);
        if (status is not { Running: true, Db: not null })
        {
            AnsiConsole.MarkupLine("[red]La base de datos no esta en marcha.[/] Arranca el stack con: roost up");
            return 1;
        }

        try
        {
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var db = planner.ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled())
                ?? throw new RoostException("No hay base de datos global: elige una con roost db use <motor@version>.");

            DbAdmin.Create(db, planner.DbIniPath(db), settings.Name, ProcessRunner.Default);

            AnsiConsole.MarkupLineInterpolated($"[green]Base de datos {settings.Name} lista[/] en {status.Db}.");
            AnsiConsole.MarkupLineInterpolated($"[grey]Conexion: host 127.0.0.1, puerto {status.DbPort}, usuario root, sin contrasena.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
