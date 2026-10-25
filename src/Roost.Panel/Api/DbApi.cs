using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.WebPanel.Api;

/// <summary>
/// Bases de datos: la global (en marcha o parada, su conexión y su <c>.env</c>), las instaladas, las
/// disponibles (MariaDB de su API, MySQL fijado), instalar (tarea larga), elegir, quitar (solo los
/// binarios), el puerto, y crear o borrar bases de datos y ver los usuarios con <see cref="DbAdmin"/>.
/// Exportar e importar, la contraseña de root y los usuarios se gestionan desde la CLI de momento.
/// </summary>
public static class DbApi
{
    public sealed record SpecRequest(string Spec);
    public sealed record PortRequest(int Port);
    public sealed record NameRequest(string Name);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/db", (bool? catalog, CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await GetAsync(app, catalog == true, false, ct))));
        api.MapPost("/db/catalog", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await GetAsync(app, true, true, ct))));
        api.MapPost("/db/install", (SpecRequest request) => PanelRoutes.Guard(() => PanelRoutes.Started(Install(app, request.Spec))));
        api.MapPost("/db/use", (SpecRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            if (request.Spec == RoostConfig.NoDb) RoostConfig.SetDb(app.Paths.ConfigFile, RoostConfig.NoDb, null);
            else
            {
                var spec = DbSpec.Parse(request.Spec);
                var match = DbInstaller.Match(new DbInstaller(app.Paths).ListInstalled(), spec);
                if (match.Count == 0) throw new RoostException($"No hay ninguna {request.Spec} instalada por Roost.");
                RoostConfig.SetDb(app.Paths.ConfigFile, match[0].Engine.Key(), match[0].Branch);
            }
            var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
        }));
        api.MapPost("/db/remove", (SpecRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var installer = new DbInstaller(app.Paths);
            var match = DbInstaller.Match(installer.ListInstalled(), DbSpec.Parse(request.Spec));
            if (match.Count == 0) throw new RoostException($"No hay ninguna {request.Spec} instalada por Roost.");
            var status = await app.Daemon.TryStatusAsync(ct);
            if (status is { Running: true } && status.Db == match[0].Label) throw new RoostException($"{match[0].Label} esta en marcha: parala antes (o elige otra).");
            installer.Remove(match[0]);
            return PanelRoutes.Ok(new { ok = true, removed = match[0].Spec, note = "Sus datos se conservan en data/." });
        }));
        api.MapPost("/db/port", (PortRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            if (request.Port is < 1 or > 65535) throw new RoostException("El puerto tiene que estar entre 1 y 65535.");
            RoostConfig.SetDbPort(app.Paths.ConfigFile, request.Port);
            var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
        }));
        api.MapPost("/db/create", (NameRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var (db, ini) = await RunningDbAsync(app, ct);
            if (!DbAdmin.IsValidName(request.Name)) throw new RoostException("El nombre solo admite letras, numeros y guion bajo (hasta 64).");
            DbAdmin.Create(db, ini, request.Name, ProcessRunner.Default);
            return PanelRoutes.Ok(new { ok = true, name = request.Name });
        }));
        api.MapPost("/db/drop", (NameRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var (db, ini) = await RunningDbAsync(app, ct);
            DbAdmin.Drop(db, ini, request.Name, ProcessRunner.Default);
            return PanelRoutes.Ok(new { ok = true, name = request.Name });
        }));
    }

    private static async Task<(InstalledDb Db, string Ini)> RunningDbAsync(PanelApp app, CancellationToken ct)
    {
        var status = await app.Daemon.TryStatusAsync(ct);
        if (status is not { Running: true, Db: not null }) throw new RoostException("La base de datos no esta en marcha.");
        var planner = new StackPlanner(app.Paths, ProcessRunner.Default);
        var db = planner.ResolveDb(RoostConfig.Load(app.Paths.ConfigFile), new DbInstaller(app.Paths).ListInstalled())
                 ?? throw new RoostException("No hay base de datos global.");
        return (db, planner.DbIniPath(db));
    }

    public static async Task<object> GetAsync(PanelApp app, bool withCatalog, bool force, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var installer = new DbInstaller(paths);
        var installed = installer.ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledDb? global = null;
        string? globalProblem = null;
        try { global = planner.ResolveDb(config, installed); } catch (RoostException ex) { globalProblem = ex.Message; }
        var status = await app.Daemon.TryStatusAsync(ct);
        var running = status is { Running: true, Db: not null } && global is not null && status.Db == global.Label;
        var dbService = status?.Services.FirstOrDefault(s => s.Name.StartsWith("mariadb-", StringComparison.Ordinal) || s.Name.StartsWith("mysql-", StringComparison.Ordinal));
        var password = global is null ? null : new DbPasswords(paths).For(global);
        IReadOnlyList<string> databases = [];
        IReadOnlyList<DbUser> users = [];
        string? adminProblem = null;
        if (running && global is not null)
        {
            try
            {
                var adminIni = planner.DbIniPath(global);
                databases = DbAdmin.List(global, adminIni, ProcessRunner.Default);
                users = DbAdmin.ListUsers(global, adminIni, ProcessRunner.Default);
            }
            catch (RoostException ex) { adminProblem = ex.Message; }
        }
        var ours = status is { Running: true } ? status.Ports ?? [] : [];
        var portState = ours.Contains(config.DbPort) ? "roost" : StackPlanner.Probe(config.DbPort) == PortState.Free ? "free" : "busy";
        var owner = portState == "busy" ? Core.Platform.PortOwners.Find(config.DbPort) : null;

        var rows = installed.OrderBy(d => d.Engine).ThenByDescending(d => d.Version).Select(d =>
        {
            var finding = d.Version is { } v ? app.Evaluate(d.Engine.Kind(), v, d.Server) : null;
            var support = finding?.Verdict == AuditVerdict.Eol ? $"EOL desde {finding.Eol:dd-MMM-yyyy}" : finding?.Eol is { } eol ? $"Soporte hasta {eol:MMM-yyyy}" : "Con soporte";
            return new { engine = d.Engine.DisplayName(), key = d.Engine.Key(), name = d.Name, version = d.Version?.ToString(), branch = d.Branch, spec = d.Spec, label = d.Label, support = support.ToLowerInvariant().Replace("eol", "EOL").Replace("soporte", "Soporte").Replace("con", "Con"), tone = finding is null ? "neutral" : StackApi.Tone(finding.Color), data = Path.Combine("data", d.Engine.Key(), d.Branch ?? ""), inUse = global?.Name == d.Name && global.Engine == d.Engine, directory = d.Directory };
        }).ToList();

        object? available = null;
        string? catalogProblem = app.DbCatalogProblem;
        if (withCatalog)
        {
            try
            {
                var (builds, _) = await app.DbCatalogAsync(force, ct);
                catalogProblem = app.DbCatalogProblem;
                available = builds.OrderBy(b => b.Engine).ThenByDescending(b => b.Version).Select(b =>
                {
                    var finding = app.Evaluate(b.Engine.Kind(), b.Version, b.FileName, "catalogo");
                    var support = finding.Verdict == AuditVerdict.Eol ? $"EOL desde {finding.Eol:dd-MMM-yyyy}" : (b.Lts ? "LTS" : "Estable") + (finding.Eol is { } eol ? $" hasta {eol:MMM-yyyy}" : "");
                    return new { engine = b.Engine.DisplayName(), key = b.Engine.Key(), version = b.Version.ToString(), branch = b.Branch, lts = b.Lts, support = support.ToLowerInvariant().Replace("eol", "EOL").Replace("lts", "LTS").Replace("estable", "Estable"), tone = StackApi.Tone(finding.Color), installed = installed.Any(d => d.Engine == b.Engine && d.Name == b.InstallName), spec = $"{b.Engine.Key()}@{b.Version}" };
                }).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException) { catalogProblem = ex.Message; }
        }

        var ini = global is null ? null : planner.DbIniPath(global);
        return new
        {
            platform = OperatingSystem.IsWindows() ? "windows" : "linux",
            global = global is null ? null : new { engine = global.Engine.DisplayName(), key = global.Engine.Key(), name = global.Name, version = global.Version?.ToString(), branch = global.Branch, label = global.Label, spec = global.Spec },
            globalProblem, none = config.DbEngine == RoostConfig.NoDb,
            running, state = dbService?.State, pid = dbService?.Pid, service = dbService?.Name,
            host = "127.0.0.1", port = config.DbPort, portState, portOwner = owner is null ? null : Core.Platform.PortOwners.Describe(owner),
            user = "root", hasPassword = password is not null, env = $"DB_HOST=127.0.0.1  DB_PORT={config.DbPort}  DB_USERNAME=root  DB_PASSWORD={password ?? ""}",
            data = global is null ? null : Path.Combine(paths.Data, global.Engine.Key(), global.Branch ?? ""), ini, userCnf = ini is null ? null : Path.Combine(Path.GetDirectoryName(ini)!, "usuario.cnf"),
            databases, users = users.Select(u => new { name = u.Name, host = u.Host, all = u.AllDatabases, databases = u.Databases }).ToList(), adminProblem,
            installed = rows, available, catalogProblem,
            jobs = app.Jobs.Running().Where(j => j.Title.Contains("MariaDB", StringComparison.Ordinal) || j.Title.Contains("MySQL", StringComparison.Ordinal)).ToList(),
        };
    }

    private static Job Install(PanelApp app, string specText)
    {
        var spec = DbSpec.Parse(specText);
        var engine = spec.Engine ?? throw new RoostException("Di el motor: mariadb@11.4 o mysql@8.4.");
        var label = $"{engine.DisplayName()} {spec}";
        return app.Jobs.Start($"Instalando {label}", "desde " + (engine == DbEngine.MariaDb ? "downloads.mariadb.org" : "cdn.mysql.com"),
        [
            ("Resolver la versión en el catálogo", null),
            ("Descargar", null),
            ("Verificar el SHA-256", "fijado en el catálogo, antes de usarlo"),
            ("Dejarla en su carpeta", Path.Combine(app.Paths.DbBin(engine.Key()), "…")),
        ], async ctx =>
        {
            ctx.Begin(0, "Consultando el catálogo…");
            var (builds, _) = await app.DbCatalogAsync(false, ctx.Cancellation);
            var build = DbCatalog.Resolve(builds, spec);
            var installer = new DbInstaller(app.Paths, app.Http);
            if (installer.IsInstalled(build)) throw new RoostException($"{build.Engine.DisplayName()} {build.Version} ya esta instalada.");
            ctx.Step(0, "done", $"{build.Version} · {build.FileName}");
            ctx.Begin(1, $"Descargando {build.FileName}…");
            var installed = await installer.InstallAsync(build, new Progress<long>(b => ctx.Message($"Descargando… {b / 1048576.0:0.0} MB")), ctx.Cancellation);
            // La primera instalada pasa a ser la global, como en la CLI (sin [db], ResolveDb coge la primera).
            ctx.Finish($"{installed.Label} instalada.");
            return new { spec = installed.Spec, label = installed.Label };
        });
    }
}
