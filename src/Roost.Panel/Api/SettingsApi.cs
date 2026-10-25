using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Dns;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Core.Updates;

namespace Roost.WebPanel.Api;

/// <summary>
/// Ajustes (lo del stack en roost.toml: pool, puertos, cómo se resuelven los .test), la CA local
/// (crear, confiar, dejar de confiar, renovar: «Confiar CA»), la bienvenida (sin ningún PHP de Roost)
/// y Novedades (el CHANGELOG empaquetado). Lo que es de la app de escritorio (bandeja, abrir al
/// iniciar sesión, programas) no existe aquí; lo que aún no va en el panel sale como «próximamente».
/// </summary>
public static class SettingsApi
{
    public sealed record PoolRequest(int Pool);
    public sealed record ResolverRequest(string Resolver);
    public sealed record TrustRequest(bool? System);
    public sealed record WelcomeRequest(IReadOnlyList<string> Php, string? Db);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/settings", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await GetAsync(app, ct))));
        api.MapPost("/settings/pool", (PoolRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            if (request.Pool is < 1 or > 32) throw new RoostException("El pool va de 1 a 32 procesos.");
            RoostConfig.SetPhpPool(app.Paths.ConfigFile, request.Pool);
            var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
        }));
        api.MapPost("/settings/resolver", (ResolverRequest request, CancellationToken ct) => PanelRoutes.Guard(() => ResolverAsync(app, request.Resolver, ct)));
        api.MapPost("/settings/dns-check", (CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var answers = await LocalDnsProbe.AsksAsync("roost-panel.test", ct);
            return PanelRoutes.Ok(new { answers, endPoint = LocalDns.EndPoint.ToString() });
        }));
        api.MapGet("/ca", () => PanelRoutes.Guard(() => PanelRoutes.Ok(Ca(app))));
        api.MapPost("/ca/trust", (TrustRequest request) => PanelRoutes.Guard(() => PanelRoutes.Ok(Trust(app, request.System == true))));
        api.MapPost("/ca/untrust", () => PanelRoutes.Guard(() =>
        {
            var ca = new LocalCa(app.Paths);
            var report = ca.Untrust();
            if (OperatingSystem.IsLinux() && ca.IsTrustedBySystem()) ca.UntrustSystem(app.HelperExe, app.Elevation);
            if (OperatingSystem.IsWindows())
                foreach (var profile in FirefoxTrust.AllProfiles()) FirefoxTrust.Disable(profile);
            return PanelRoutes.Ok(new { ok = true, stores = report.Stores, skipped = report.Skipped, trusted = ca.IsTrusted() });
        }));
        api.MapPost("/ca/renew", (CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var ca = new LocalCa(app.Paths);
            var sites = StackApi.SafeSites(app.Paths);
            foreach (var site in sites) ca.EnsureSiteCert(site, force: true);
            string? applied = null;
            if (await app.Daemon.TryStatusAsync(ct) is { Running: true })
            {
                var response = await app.Daemon.ReloadAsync(RoostConfig.Load(app.Paths.ConfigFile).UsesApache ? "apache" : "nginx", ct);
                applied = response.Ok ? string.Join("; ", response.Applied) : response.Error;
            }
            return PanelRoutes.Ok(new { ok = true, renewed = sites.Count, applied });
        }));
        api.MapGet("/welcome", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await WelcomeAsync(app, ct))));
        api.MapPost("/welcome/run", (WelcomeRequest request) => PanelRoutes.Guard(() => PanelRoutes.Started(Welcome(app, request))));
        api.MapGet("/news", () => PanelRoutes.Guard(() => PanelRoutes.Ok(new
        {
            version = RoostVersion.Current,
            entries = Changelog.Embedded().Select(e => new { version = e.Version, date = e.Date, items = e.Items }).ToList(),
        })));
    }

    public static async Task<object> GetAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var status = await app.Daemon.TryStatusAsync(ct);
        var dnsWhyNot = LocalDns.WhyNotAvailable();
        bool ruleInstalled = false;
        try { ruleInstalled = dnsWhyNot is null && LocalDns.IsRuleInstalled(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException) { }
        var dnsRunning = status is { Running: true } && status.Services.Any(s => s.Name == "dns" && s.State == "Running");
        var hosts = new HostsUpdater();
        return new
        {
            configFile = paths.ConfigFile, root = paths.Root, www = paths.Www, cache = paths.Cache, logs = paths.Logs,
            phpPool = config.PhpPool, httpPort = config.HttpPort, httpsPort = config.HttpsPort, dbPort = config.DbPort, webServer = config.WebServer,
            resolver = config.SitesResolver, gitExe = config.GitExe,
            dns = new { available = dnsWhyNot is null, whyNot = dnsWhyNot, ruleInstalled, running = dnsRunning, endPoint = LocalDns.EndPoint.ToString(), ruleLabel = LocalDns.RuleLabel },
            hosts = new { file = hosts.HostsFile, system = hosts.IsSystemFile, wsl = hosts.IsWindowsHostsFromWsl, helperLog = HostsUpdater.HelperLog, helper = app.HelperExe, helperExists = File.Exists(app.HelperExe) },
            ca = Ca(app),
            version = RoostVersion.Current, os = OperatingSystem.IsWindows() ? "windows" : "linux", machine = Environment.MachineName, user = Environment.UserName,
            panel = new { log = PanelHost.LogFile(paths), pid = Environment.ProcessId },
            updates = new { automatic = !UpdateChecker.AutomaticChecksOff, env = UpdateChecker.DisabledByEnvironment },
            shims = new { directory = paths.Shims, note = OperatingSystem.IsWindows() ? "Desde la terminal: roost shims install (cambia el PATH de tu usuario)." : "En Linux, los shims llegan en la próxima versión." },
        };
    }

    private static object Ca(PanelApp app)
    {
        var paths = app.Paths;
        var ca = new LocalCa(paths);
        var created = File.Exists(Path.Combine(paths.Certs, "ca", "roost-ca.crt"));
        (string Subject, string Thumbprint, DateTime NotAfter)? described = null;
        bool trusted = false, system = false;
        try
        {
            if (created) { described = ca.Describe(); trusted = ca.IsTrusted(); system = OperatingSystem.IsLinux() && ca.IsTrustedBySystem(); }
        }
        catch (Exception ex) when (ex is IOException or RoostException or System.Security.Cryptography.CryptographicException) { }
        return new
        {
            created, trusted, system, subject = described?.Subject, thumbprint = described?.Thumbprint, notAfter = described?.NotAfter,
            certFile = Path.Combine(paths.Certs, "ca", "roost-ca.crt"), keyFile = Path.Combine(paths.Certs, "ca", "roost-ca.key"),
            store = OperatingSystem.IsWindows() ? "los certificados de confianza de tu usuario (Windows)" : "Chrome, Chromium, Edge y Firefox (NSS, con certutil); y con «también el sistema», /usr/local/share/ca-certificates (curl, PHP)",
            firefox = OperatingSystem.IsWindows() ? FirefoxTrust.AllProfiles().Count : 0,
        };
    }

    /// <summary>«Continuar» en «Confiar CA»: crea la CA si falta y la pone en los almacenes del usuario (en Windows sale el aviso del sistema).</summary>
    private static object Trust(PanelApp app, bool system)
    {
        var ca = new LocalCa(app.Paths);
        ca.LoadOrCreate();
        var report = ca.Trust();
        var trusted = ca.IsTrusted();
        string? systemResult = null;
        if (system && OperatingSystem.IsLinux())
        {
            try
            {
                var run = ca.TrustSystem(app.HelperExe, app.Elevation);
                systemResult = run.Status == ElevationStatus.Done && run.ExitCode == 0 ? "en el sistema" : $"no se pudo ({run.Status}): {run.ManualCommand}";
            }
            catch (ElevationNeededException ex) { systemResult = $"{ex.Message} Orden: {ex.Command}"; }
            catch (RoostException ex) { systemResult = ex.Message; }
        }
        if (OperatingSystem.IsWindows())
            foreach (var profile in FirefoxTrust.AllProfiles()) FirefoxTrust.Enable(profile);
        return new { ok = trusted, trusted, stores = report.Stores, skipped = report.Skipped, system = systemResult, ca = Ca(app) };
    }

    /// <summary>Cómo se resuelven los .test: el hosts o el DNS local (la regla del sistema la pone el helper, con permiso).</summary>
    private static async Task<IResult> ResolverAsync(PanelApp app, string resolver, CancellationToken ct)
    {
        var paths = app.Paths;
        if (resolver is not (RoostConfig.ResolverHosts or RoostConfig.ResolverDns)) throw new RoostException("El resolver es hosts o dns.");
        if (resolver == RoostConfig.ResolverDns)
        {
            if (LocalDns.WhyNotAvailable() is { } why) throw new RoostException(why);
            if (!LocalDns.IsRuleInstalled())
            {
                try { LocalDns.InstallRule(app.HelperExe, app.Elevation); }
                catch (ElevationNeededException ex) { return PanelRoutes.Ok(new { ok = false, error = ex.Message, command = ex.Command }); }
            }
            RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverDns);
        }
        else
        {
            if (LocalDns.WhyNotAvailable() is null && LocalDns.IsRuleInstalled())
            {
                try { LocalDns.RemoveRule(app.HelperExe, app.Elevation); }
                catch (ElevationNeededException ex) { return PanelRoutes.Ok(new { ok = false, error = ex.Message, command = ex.Command }); }
            }
            RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverHosts);
        }
        var hosts = SitesApi.SyncHosts(app);
        var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
        return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, hosts, applied = applied.Applied, resolver });
    }

    /// <summary>La bienvenida: sin ningún PHP de Roost, lo que hay en el equipo y lo que propone traer.</summary>
    private static async Task<object> WelcomeAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var installed = new PhpInstaller(paths).ListInstalled();
        var scan = app.LastScan ?? await Task.Run(() =>
        {
            var result = EnvironmentScanner.CreateDefault().ScanAll();
            var findings = app.Evaluator.EvaluateAll(result.Components).ToList();
            app.LastScan = new AuditScan(DateTimeOffset.Now, result.OsDescription, findings);
            return app.LastScan;
        }, ct);
        var found = scan.Findings.Where(f => f.Component.Source != "Roost").GroupBy(f => f.Component.Source).Select(g => new
        {
            source = g.Key, components = g.Select(f => $"{StackApi.Kind(f.Component.Kind)} {f.Component.Version}").ToList(),
            red = g.Count(f => f.Color == AuditColor.Red), chip = g.Count(f => f.Verdict == AuditVerdict.Vulnerable) is var v and > 0 ? $"{v} vulnerable" : g.Count(f => f.Verdict == AuditVerdict.Eol) is var e and > 0 ? $"{e} EOL" : null,
        }).ToList();
        // Las ramas de PHP que se usan fuera y aún tienen soporte, o la última rama del catálogo.
        IReadOnlyList<PhpBuild> builds = [];
        string? catalogProblem = null;
        try { builds = (await app.PhpCatalogAsync(false, ct)).Builds; } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException) { catalogProblem = ex.Message; }
        var outside = scan.Findings.Where(f => f.Component.Kind == ComponentKind.Php && f.Component.Source != "Roost").Select(f => $"{f.Component.Version.Major}.{f.Component.Version.Minor}").Distinct().ToList();
        var branches = builds.Where(b => !b.ThreadSafe && b.Architecture == "x64").Select(b => b.Branch).Distinct().ToList();
        var proposed = outside.Where(branches.Contains).Where(b => app.Evaluate(ComponentKind.Php, builds.First(x => x.Branch == b && !x.ThreadSafe).Version).Verdict != AuditVerdict.Eol).ToList();
        if (proposed.Count == 0 && branches.Count > 0) proposed = [branches.OrderByDescending(b => b).First()];
        var dbInstalled = new DbInstaller(paths).ListInstalled();
        var dbPortBusy = StackPlanner.Probe(RoostConfig.Load(paths.ConfigFile).DbPort) != PortState.Free;
        return new
        {
            show = installed.Count == 0, found, catalogProblem, proposedPhp = proposed, phpVersions = proposed.Select(b => builds.First(x => x.Branch == b && !x.ThreadSafe).Version.ToString()).ToList(),
            db = OperatingSystem.IsWindows() ? (dbInstalled.Count > 0 ? null : "mariadb") : null, dbInstalled = dbInstalled.Count > 0, dbPortBusy, dbPort = RoostConfig.Load(paths.ConfigFile).DbPort,
            root = paths.Root, www = paths.Www,
            note = OperatingSystem.IsWindows() ? "Windows pedirá dos permisos: uno para el fichero hosts (los nombres .test) y otro para confiar en la CA local (el candado de https)." : "Para el hosts y la CA del sistema hará falta permiso de administrador (pkexec o sudo).",
        };
    }

    private static Job Welcome(PanelApp app, WelcomeRequest request)
    {
        var steps = new List<(string, string?)>();
        foreach (var b in request.Php) steps.Add(($"Instalar PHP {b}", PhpCatalog.SourceName));
        if (request.Db is not null) steps.Add(($"Instalar {request.Db}", null));
        steps.Add(("Fijar el PHP global", null));
        return app.Jobs.Start("Preparando Roost", "La bienvenida", steps, async ctx =>
        {
            var (builds, _) = await app.PhpCatalogAsync(false, ctx.Cancellation);
            var installer = new PhpInstaller(app.Paths, app.Http);
            var i = 0;
            InstalledPhp? first = null;
            foreach (var branch in request.Php)
            {
                ctx.Begin(i, $"Instalando PHP {branch}…");
                var build = PhpCatalog.Resolve(builds, branch);
                var installed = installer.IsInstalled(build) ? new PhpInstaller(app.Paths).ListInstalled().First(p => p.Name == build.InstallName)
                    : await installer.InstallAsync(build, new Progress<long>(b => ctx.Message($"PHP {branch}: {b / 1048576.0:0.0} MB")), ctx.Cancellation);
                first ??= installed;
                i++;
            }
            if (request.Db is not null)
            {
                ctx.Begin(i, $"Instalando {request.Db}…");
                var (dbBuilds, _) = await app.DbCatalogAsync(false, ctx.Cancellation);
                var build = DbCatalog.Resolve(dbBuilds, DbSpec.Parse(request.Db));
                var dbInstaller = new DbInstaller(app.Paths, app.Http);
                if (!dbInstaller.IsInstalled(build)) await dbInstaller.InstallAsync(build, new Progress<long>(b => ctx.Message($"{request.Db}: {b / 1048576.0:0.0} MB")), ctx.Cancellation);
                i++;
            }
            ctx.Begin(i, "Fijando el PHP global…");
            if (first is not null && request.Php.Count > 0) RoostConfig.SetPhpVersion(app.Paths.ConfigFile, request.Php[0]);
            ctx.Finish("Listo. Iniciar todo arranca el stack.");
            return new { php = request.Php, db = request.Db };
        });
    }
}
