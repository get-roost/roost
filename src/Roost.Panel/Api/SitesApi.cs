using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Daemon;

namespace Roost.WebPanel.Api;

/// <summary>
/// Sitios: la lista con su PHP, su certificado y si están en el hosts; dar de alta (lo que hace
/// «Crear y abrir» en la app: registrar, hosts, aplicar al stack, crear la base de datos), quitar,
/// cambiar el PHP o la raíz web, y el hosts. Todo sobre <see cref="SiteManager"/>, como la CLI.
/// </summary>
public static class SitesApi
{
    public sealed record NewSiteRequest(string Name, string? Folder, string? Root, string? Php, string? Database, bool CreateDatabase);
    public sealed record SitePhpRequest(string? Php);
    public sealed record SiteRootRequest(string? Root);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/sites", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await ListAsync(app, ct))));
        api.MapGet("/sites/preview", (string? name, string? folder, string? root) => PanelRoutes.Guard(() => PanelRoutes.Ok(Preview(app, name, folder, root))));
        api.MapGet("/sites/hosts-block", () => PanelRoutes.Guard(() =>
            PanelRoutes.Ok(new { block = HostsUpdater.ManualBlock(SiteManager.HostNames(StackApi.SafeSites(app.Paths))), file = new HostsUpdater().HostsFile })));
        api.MapPost("/sites", (NewSiteRequest request, CancellationToken ct) => PanelRoutes.Guard(() => CreateAsync(app, request, ct)));
        api.MapPost("/sites/hosts", (CancellationToken ct) => PanelRoutes.Guard(() => Task.FromResult(PanelRoutes.Ok(new { hosts = SyncHosts(app) }))));
        api.MapDelete("/sites/{name}", (string name, CancellationToken ct) => PanelRoutes.Guard(() => RemoveAsync(app, name, ct)));
        api.MapPost("/sites/{name}/php", (string name, SitePhpRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var sites = new SiteManager(app.Paths);
            var site = sites.Find(name) ?? throw new RoostException($"No hay ningun sitio '{name}'.");
            sites.Add(site.Name, site.Folder, site.Root, string.IsNullOrWhiteSpace(request.Php) ? "global" : request.Php);
            var applied = await ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
        }));
        api.MapPost("/sites/{name}/root", (string name, SiteRootRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            new SiteManager(app.Paths).SetRoot(name, request.Root);
            var applied = await ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
        }));
        api.MapGet("/sites/{name}/conf", (string name) => PanelRoutes.Guard(() =>
        {
            var sites = new SiteManager(app.Paths);
            var site = sites.Find(name) ?? throw new RoostException($"No hay ningun sitio '{name}'.");
            var file = sites.ConfFile(site);
            return PanelRoutes.Ok(new { file, text = File.Exists(file) ? File.ReadAllText(file) : null });
        }));
    }

    public static async Task<object> ListAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var sites = StackApi.SafeSites(paths);
        var installed = new PhpInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledPhp? global = null;
        try { global = installed.Count == 0 ? null : planner.ResolvePhp(config, installed); } catch (RoostException) { }
        var ca = new LocalCa(paths);
        var status = await app.Daemon.TryStatusAsync(ct);
        var hostsState = StackApi.HostsState(config, sites);
        IReadOnlyList<string> inHosts = [];
        try { if (!config.UsesLocalDns) inHosts = new HostsUpdater().Current(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException) { }

        var rows = sites.Select(s =>
        {
            InstalledPhp? php = null;
            string? phpProblem = null;
            try { php = global is null ? null : planner.ResolveSitePhp(s, global, installed); } catch (RoostException ex) { phpProblem = ex.Message; }
            var finding = php?.Version is { } v ? app.Evaluate(ComponentKind.Php, v, php.Executable) : null;
            var certFile = ca.SiteCertFile(s.Name);
            var certNames = File.Exists(certFile) ? ca.SiteCertNames(s.Name) : [];
            var running = status is { Running: true } && status.Sites.Any(x => x.Name == s.Name);
            var folderExists = Directory.Exists(s.Folder);
            var rootExists = Directory.Exists(s.DocumentRoot);
            var kind = folderExists ? ProjectKind.Detect(s.Folder) : null;
            return new
            {
                name = s.Name, host = s.Host, folder = s.Folder, root = s.Root, documentRoot = s.DocumentRoot,
                php = s.Php, phpLabel = s.Php is null ? $"{(php is null ? "?" : StackApi.Branch(php))} (global)" : php is null ? s.Php : php.Version?.ToString() ?? php.Name,
                phpName = php?.Name, phpBranch = php is null ? null : StackApi.Branch(php), phpEol = finding?.Verdict == Roost.Core.Audit.AuditVerdict.Eol, phpProblem,
                https = StackPlanner.SiteUrl(s, config.HttpsPort), http = config.HttpPort == 80 ? $"http://{s.Host}/" : $"http://{s.Host}:{config.HttpPort}/",
                cert = File.Exists(certFile) ? (certNames.Count > 0 ? string.Join(", ", certNames) : "emitido") : "se emite al iniciar",
                certEmitted = File.Exists(certFile),
                inHosts = config.UsesLocalDns || inHosts.Contains(s.Host, StringComparer.OrdinalIgnoreCase),
                aliases = s.Aliases ?? [], database = s.Database, databaseGuess = SiteManager.DatabaseOf(s),
                git = s.Git is null ? null : new { remote = s.Git.Remote, branch = s.Git.Branch, provider = s.Git.Provider.ToString() },
                parkedIn = s.ParkedIn, running, folderExists, rootExists,
                project = kind is null ? null : new { type = kind.Type.ToString(), webRoot = kind.WebRoot, composer = kind.HasComposerJson, vendor = kind.HasVendor, phpConstraint = kind.PhpConstraint, packageJson = kind.HasPackageJson },
                typicalRoots = folderExists ? SiteManager.TypicalRoots.Where(r => Directory.Exists(Path.Combine(s.Folder, r))).ToList() : [],
                conf = new SiteManager(paths).ConfFile(s, config),
            };
        }).ToList();

        return new
        {
            sites = rows, hosts = hostsState, resolver = config.SitesResolver, www = paths.Www, httpsPort = config.HttpsPort,
            phpOptions = installed.Select(p => new { name = p.Name, label = $"{p.Version} {(p.ThreadSafe ? "TS" : "NTS")}" + (app.Evaluate(ComponentKind.Php, p.Version ?? default, p.Executable).Verdict == Roost.Core.Audit.AuditVerdict.Eol ? " (EOL)" : "") }).ToList(),
            globalPhp = global is null ? null : new { name = global.Name, branch = StackApi.Branch(global) },
            elevationNote = OperatingSystem.IsWindows() ? "Windows pedirá permiso de administrador para el hosts." : "Hace falta permiso de administrador para el hosts (pkexec o sudo).",
        };
    }

    /// <summary>Lo que el diálogo «Nuevo sitio» enseña bajo cada campo: si existe, si tiene public/, qué proyecto es y qué PHP pide.</summary>
    private static object Preview(PanelApp app, string? name, string? folder, string? root)
    {
        var paths = app.Paths;
        name = (name ?? "").Trim().ToLowerInvariant();
        var validName = name.Length > 0 && Site.IsValidName(name);
        var existing = validName ? new SiteManager(paths).Find(name) : null;
        var target = string.IsNullOrWhiteSpace(folder) ? (validName ? Path.Combine(paths.Www, name) : null) : folder.Trim();
        var exists = target is not null && Directory.Exists(target);
        var kind = exists ? ProjectKind.Detect(target!) : null;
        var suggestedRoot = kind?.WebRoot;
        var installed = new PhpInstaller(paths).ListInstalled();
        string? recommended = null;
        string? recommendedWhy = null;
        if (kind?.PhpConstraint is { } constraint)
        {
            // La instalación más nueva cuya rama cumple el mínimo del composer.json (una lectura simple: ^8.2 → 8.2).
            var digits = new string(constraint.TakeWhile(c => char.IsDigit(c) || c == '.' || c == '^' || c == '>' || c == '=' || c == '~' || c == ' ').ToArray()).Trim('^', '>', '=', '~', ' ');
            if (ComponentVersion.TryParse(digits.Count(c => c == '.') >= 2 ? digits : digits + ".0", out var min))
            {
                var best = installed.Where(p => p.Version is { } v && v >= min).OrderByDescending(p => p.Version).FirstOrDefault();
                if (best is not null) { recommended = best.Name; recommendedWhy = $"recomendada por composer.json ({constraint})"; }
            }
        }
        return new
        {
            name, validName, nameHint = !validName ? (name.Length == 0 ? "" : "Solo minúsculas, números y guiones.") : existing is not null ? $"Ya existe {name}.test: se actualizará." : $"Se servirá en https://{name}.test",
            folder = target, exists,
            folderHint = target is null ? "" : !exists ? "No existe: se creará con un index.php." : suggestedRoot is not null && string.IsNullOrEmpty(root) ? $"Existe y tiene {suggestedRoot}/: será la raíz web." : "Existe: se sirve tal cual.",
            suggestedRoot, project = kind?.Type.ToString(), phpConstraint = kind?.PhpConstraint, recommended, recommendedWhy,
            typicalRoots = exists ? SiteManager.TypicalRoots.Where(r => Directory.Exists(Path.Combine(target!, r))).ToList() : [],
            hostsNote = RoostConfig.Load(paths.ConfigFile).UsesLocalDns ? "DNS local: resuelve sin tocar el hosts." : OperatingSystem.IsWindows() ? "Windows pedirá permiso de administrador" : "pedirá permiso de administrador (pkexec o sudo)",
        };
    }

    /// <summary>«Crear y abrir»: registrar, hosts (si cambia), aplicar al stack si está en marcha y, si se pide, crear la base de datos.</summary>
    private static async Task<IResult> CreateAsync(PanelApp app, NewSiteRequest request, CancellationToken ct)
    {
        var paths = app.Paths;
        var sites = new SiteManager(paths);
        var change = sites.Add(request.Name.Trim().ToLowerInvariant(), NullIfEmpty(request.Folder), NullIfEmpty(request.Root), NullIfEmpty(request.Php));
        var site = change.Site;
        if (!string.IsNullOrWhiteSpace(request.Database) && request.Database != SiteManager.DatabaseOf(site))
            site = sites.SetDatabase(site.Name, request.Database.Trim());

        var hosts = SyncHosts(app);
        var applied = await ApplyIfRunningAsync(app, ct);

        object? database = null;
        if (request.CreateDatabase)
            database = await TryCreateDatabaseAsync(app, SiteManager.DatabaseOf(site), ct);

        var config = RoostConfig.Load(paths.ConfigFile);
        var caTrusted = false;
        try { caTrusted = new LocalCa(paths).IsTrusted(); } catch (Exception ex) when (ex is IOException or RoostException or System.Security.Cryptography.CryptographicException) { }
        return PanelRoutes.Ok(new
        {
            site = new { name = site.Name, host = site.Host, folder = site.Folder, documentRoot = site.DocumentRoot, created = change.Created, folderCreated = change.FolderCreated },
            url = StackPlanner.SiteUrl(site, config.HttpsPort), hosts, applied = new { ok = applied.Ok, error = applied.Error, applied = applied.Applied, running = applied.Status?.Running ?? false }, database, caTrusted,
        });
    }

    private static async Task<IResult> RemoveAsync(PanelApp app, string name, CancellationToken ct)
    {
        var sites = new SiteManager(app.Paths);
        if (sites.Find(name) is null) throw new RoostException($"No hay ningun sitio '{name}'.");
        sites.Remove(name);
        var hosts = SyncHosts(app);
        var applied = await ApplyIfRunningAsync(app, ct);
        return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, hosts, applied = applied.Applied });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// El hosts, como <c>SiteSupport.SyncHosts</c>: nada con el DNS local; nada si está al día; si no, el helper con
    /// permiso de administrador (UAC; en Linux pkexec sin agente o sudo -n, y si no hay forma, la orden exacta para hacerlo a mano).
    /// </summary>
    public static object SyncHosts(PanelApp app)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        if (config.UsesLocalDns) return new { ok = true, mode = "dns", changed = false, message = "DNS local: los .test ya resuelven, no hace falta tocar el hosts." };
        var names = SiteManager.HostNames(StackApi.SafeSites(paths));
        var updater = new HostsUpdater { Elevation = app.Elevation };
        try
        {
            if (updater.InSync(names)) return new { ok = true, mode = "hosts", changed = false, message = $"hosts al día ({updater.HostsFile})", file = updater.HostsFile };
            updater.Sync(names, app.HelperExe, Path.Combine(paths.Run, "hosts-request.txt"));
            return new { ok = true, mode = "hosts", changed = true, message = $"hosts actualizado ({updater.HostsFile})", file = updater.HostsFile };
        }
        catch (ElevationNeededException ex)
        {
            return new { ok = false, mode = "hosts", changed = false, message = ex.Message, command = ex.Command, file = updater.HostsFile, block = HostsUpdater.ManualBlock(names) };
        }
        catch (RoostException ex)
        {
            return new { ok = false, mode = "hosts", changed = false, message = ex.Message, file = updater.HostsFile, block = HostsUpdater.ManualBlock(names) };
        }
    }

    /// <summary>Si el stack está en marcha, le aplica el cambio (<c>/up</c>: el daemon solo toca lo que cambia).</summary>
    public static async Task<CommandResponse> ApplyIfRunningAsync(PanelApp app, CancellationToken ct)
    {
        var status = await app.Daemon.TryStatusAsync(ct);
        if (status is not { Running: true }) return new CommandResponse(true, null, status ?? new StatusResponse(0, false, null, null, null, null, null, [], []));
        return await app.Daemon.UpAsync(ct);
    }

    private static async Task<object> TryCreateDatabaseAsync(PanelApp app, string name, CancellationToken ct)
    {
        try
        {
            var status = await app.Daemon.TryStatusAsync(ct);
            if (status is not { Running: true, Db: not null }) return new { ok = false, name, message = "La base de datos no está en marcha: créala desde Bases de datos cuando lo esté." };
            var paths = app.Paths;
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var db = planner.ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled())
                     ?? throw new RoostException("No hay base de datos global.");
            var ini = planner.DbIniPath(db);
            if (DbAdmin.List(db, ini, ProcessRunner.Default).Contains(name, StringComparer.OrdinalIgnoreCase))
                return new { ok = true, name, message = $"La base de datos {name} ya existía." };
            DbAdmin.Create(db, ini, name, ProcessRunner.Default);
            return new { ok = true, name, message = $"Base de datos {name} creada." };
        }
        catch (RoostException ex)
        {
            return new { ok = false, name, message = ex.Message };
        }
    }
}
