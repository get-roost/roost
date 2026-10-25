using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Audit;
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
/// El estado general (lo que en la app sondea <c>MainViewModel</c> cada 3 s: el daemon, la CA, el
/// hosts, la raíz) y las órdenes del stack por <see cref="DaemonClient"/>: arrancar (con las mismas
/// comprobaciones que <c>roost up</c> y <c>StackController</c>: configuración, un PHP, el servidor web
/// instalado), parar, reiniciar, parar y arrancar un servicio. Y la pantalla Inicio.
/// </summary>
public static class StackApi
{
    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/status", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await StatusAsync(app, ct))));
        api.MapGet("/home", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await HomeAsync(app, ct))));
        api.MapPost("/stack/up", (CancellationToken ct) => PanelRoutes.Guard(() => UpAsync(app, ct)));
        api.MapPost("/stack/down", (CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            if (await app.Daemon.TryStatusAsync(ct) is null) return PanelRoutes.Ok(new { ok = true, status = (StatusResponse?)null });
            var response = await app.Daemon.DownAsync(ct);
            return PanelRoutes.Ok(new { ok = response.Ok, error = response.Error, status = response.Status });
        }));
        api.MapPost("/stack/restart", (CancellationToken ct) => PanelRoutes.Guard(async () => Command(await Running(app, ct).RestartAllAsync(ct))));
        api.MapPost("/stack/restart/{service}", (string service, CancellationToken ct) => PanelRoutes.Guard(async () => Command(await Running(app, ct).RestartAsync(service, ct))));
        api.MapPost("/stack/reload/{service}", (string service, CancellationToken ct) => PanelRoutes.Guard(async () => Command(await Running(app, ct).ReloadAsync(service, ct))));
        api.MapPost("/stack/stop/{service}", (string service, CancellationToken ct) => PanelRoutes.Guard(async () => Command(await Running(app, ct).StopServiceAsync(service, ct))));
        api.MapPost("/stack/start/{service}", (string service, CancellationToken ct) => PanelRoutes.Guard(async () => Command(await Running(app, ct).StartServiceAsync(service, ct))));
    }

    private static DaemonClient Running(PanelApp app, CancellationToken ct)
    {
        if (app.Daemon.TryStatusAsync(ct).GetAwaiter().GetResult() is not { Running: true })
            throw new RoostException("Roost esta parado. Arrancalo con «Iniciar todo».");
        return app.Daemon;
    }

    private static IResult Command(CommandResponse response) =>
        PanelRoutes.Ok(new { ok = response.Ok, error = response.Error, status = response.Status, applied = response.Applied });

    /// <summary>El estado que sondea la página: el daemon y lo que va en la barra de estado.</summary>
    public static async Task<object> StatusAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var status = await app.Daemon.TryStatusAsync(ct);
        var config = RoostConfig.Load(paths.ConfigFile);
        var ca = new LocalCa(paths);
        var caCreated = File.Exists(Path.Combine(paths.Certs, "ca", "roost-ca.crt"));
        bool caTrusted = false;
        try { caTrusted = caCreated && ca.IsTrusted(); } catch (Exception ex) when (ex is IOException or RoostException or System.Security.Cryptography.CryptographicException) { }

        var sites = SafeSites(paths);
        var hosts = HostsState(config, sites);
        var jobs = app.Jobs.Running();
        return new
        {
            daemon = status,
            root = paths.Root,
            www = paths.Www,
            configFile = paths.ConfigFile,
            logs = paths.Logs,
            os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos",
            version = Core.Updates.RoostVersion.Current,
            resolver = config.SitesResolver,
            ca = new { created = caCreated, trusted = caTrusted },
            hosts,
            sites = sites.Count,
            jobs,
            welcome = new PhpInstaller(paths).ListInstalled().Count == 0,
        };
    }

    public static IReadOnlyList<Site> SafeSites(RoostPaths paths)
    {
        try { return new SiteRegistry(paths).Load(); }
        catch (Exception ex) when (ex is IOException or RoostException) { return []; }
    }

    /// <summary>Lo del hosts: al día, qué falta, y si es el fichero del sistema (pide permiso).</summary>
    public static object HostsState(RoostConfig config, IReadOnlyList<Site> sites)
    {
        if (config.UsesLocalDns) return new { mode = "dns", inSync = true, missing = Array.Empty<string>(), file = (string?)null, system = false };
        try
        {
            var updater = new HostsUpdater();
            var names = SiteManager.HostNames(sites);
            var current = updater.Current();
            var missing = names.Where(n => !current.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            return new { mode = "hosts", inSync = updater.InSync(names), missing, file = updater.HostsFile, system = updater.IsSystemFile };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException)
        {
            return new { mode = "hosts", inSync = false, missing = Array.Empty<string>(), file = (string?)null, system = true, error = ex.Message };
        }
    }

    /// <summary>
    /// <c>POST /stack/up</c>: lo mismo que <c>roost up</c> y <c>StackController.StartAsync</c>: comprueba la
    /// configuración y que haya un PHP, instala el servidor web si falta (la primera vez), asegura el daemon
    /// y le pide <c>/up</c>. Si el daemon rechaza el cambio, dice qué puertos están ocupados y por quién.
    /// </summary>
    private static async Task<IResult> UpAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var installed = new PhpInstaller(paths).ListInstalled();
        if (installed.Count == 0)
            throw new RoostException("No hay ningun PHP instalado por Roost. Instala uno en la pantalla PHP.");
        planner.ResolvePhp(config, installed);
        planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());

        await EnsureWebServerAsync(app, config, ct);
        await GrantLowPortsAsync(app, config, ct);

        var wasRunning = (await app.Daemon.EnsureRunningAsync(PanelLauncherForDaemon(), ct)).Running;
        var response = await app.Daemon.UpAsync(ct);
        if (response.Ok)
            return PanelRoutes.Ok(new { ok = true, status = response.Status, applied = response.Applied, wasRunning });

        var failure = Failure(app, response.Error ?? "El daemon no ha dicho por qué.", config, response.Status, response.Applied.Count > 0, wasRunning);
        return PanelRoutes.Ok(new { ok = false, status = response.Status, applied = response.Applied, wasRunning, failure });
    }

    /// <summary>El daemon se lanza con el mismo <c>roost</c> que corre el panel (<c>roost daemon</c>).</summary>
    private static System.Diagnostics.ProcessStartInfo PanelLauncherForDaemon() => DaemonLauncher.ForCurrentCli();

    private static async Task EnsureWebServerAsync(PanelApp app, RoostConfig config, CancellationToken ct)
    {
        if (config.UsesApache)
        {
            if (!OperatingSystem.IsWindows()) throw new RoostException("Apache en Linux llega en la proxima version: elige nginx en Servidor web.");
            var installer = new ApacheInstaller(app.Paths, app.Http);
            if (installer.Find(ApacheCatalog.Current) is null) await installer.InstallAsync(ApacheCatalog.Current, null, ct);
        }
        else
        {
            var installer = new NginxInstaller(app.Paths, app.Http);
            if (installer.Find(NginxCatalog.Current) is null) await installer.InstallAsync(NginxCatalog.Current, null, ct);
        }
    }

    /// <summary>Linux: con un puerto web por debajo del 1024, nginx necesita cap_net_bind_service (lo pone el helper).</summary>
    private static async Task GrantLowPortsAsync(PanelApp app, RoostConfig config, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || config.UsesApache) return;
        if (config.HttpPort >= 1024 && config.HttpsPort >= 1024) return;
        if (new NginxInstaller(app.Paths).Find(NginxCatalog.Current) is not { } nginx) return;
        if (await NginxCapability.IsGrantedAsync(CommandRunner.Default, nginx.Executable, ct)) return;
        NginxCapability.Request(app.HelperExe, nginx.Executable, app.Elevation);
    }

    /// <summary>Por qué no arrancó (el diálogo «Error de arranque»): el texto del daemon y los puertos ocupados con su dueño y una alternativa libre.</summary>
    public static object Failure(PanelApp app, string error, RoostConfig config, StatusResponse status, bool partial, bool wasRunning)
    {
        var ours = status.Running ? status.Ports ?? status.Services.Where(s => s.Port is not null).Select(s => s.Port!.Value).ToList() : [];
        var wanted = new List<(int Port, string Role, string Key, int[] Alternatives)>
        {
            (config.HttpPort, config.UsesApache ? "HTTP de Apache" : "HTTP de nginx", "[web] port", [8080, 8000, 8081, 8888, 8088]),
        };
        if (SafeSites(app.Paths).Count > 0)
            wanted.Add((config.HttpsPort, "HTTPS de los sitios .test", "[web] https_port", [8443, 4443, 9443, 10443]));
        if (config.DbEngine != RoostConfig.NoDb && new DbInstaller(app.Paths).ListInstalled().Count > 0)
            wanted.Add((config.DbPort, "la base de datos", "[db] port", [3307, 3308, 3309, 3310, 3406]));

        var busy = new List<object>();
        foreach (var (port, role, key, alternatives) in wanted)
        {
            if (ours.Contains(port) || StackPlanner.BusyPorts([port]).Count == 0) continue;
            int? suggestion = alternatives.Where(p => p != config.HttpPort && p != config.HttpsPort && p != config.DbPort)
                .FirstOrDefault(p => StackPlanner.BusyPorts([p]).Count == 0) is var free and > 0 ? free : null;
            var owner = PortOwners.Find(port);
            busy.Add(new { port, role, key, suggestion, owner = owner is null ? null : PortOwners.Describe(owner) });
        }

        var first = error.Split(' ', 2)[0];
        var service = first.StartsWith("php-", StringComparison.Ordinal) ? "PHP"
            : first is "nginx" or "mariadb" or "mysql" ? first
            : first is "apache" ? "Apache"
            : null;
        var title = status.Running && partial ? "El cambio se ha aplicado a medias"
            : status.Running ? "No se ha aplicado el cambio"
            : service is null ? "No se pudo arrancar Roost" : $"No se pudo arrancar {service}";
        var logTail = TailOfLog(app.Paths, 12);
        return new { title, message = error, busyPorts = busy, wasRunning, stillRunning = status.Running, logTail };
    }

    private static IReadOnlyList<string> TailOfLog(RoostPaths paths, int lines)
    {
        try
        {
            var file = DaemonHost.LogFile(paths);
            if (!File.Exists(file)) return [];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (IOException) { return []; }
    }

    // Inicio

    /// <summary>La pantalla Inicio: los accesos, la tabla de servicios (como <c>HomeViewModel</c>), los sitios y la auditoría.</summary>
    public static async Task<object> HomeAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var status = await app.Daemon.TryStatusAsync(ct);
        var config = RoostConfig.Load(paths.ConfigFile);
        var installedPhp = new PhpInstaller(paths).ListInstalled();
        var installedDb = new DbInstaller(paths).ListInstalled();
        var sites = SafeSites(paths);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledPhp? globalPhp = null;
        try { globalPhp = installedPhp.Count == 0 ? null : planner.ResolvePhp(config, installedPhp); } catch (RoostException) { }
        InstalledDb? globalDb = null;
        try { globalDb = planner.ResolveDb(config, installedDb); } catch (RoostException) { }

        var rows = new List<object>();
        if (status is { Running: true })
        {
            // En marcha: lo que cuenta el daemon, agrupado como la app (base de datos, PHP por rama, servidor web).
            foreach (var group in status.Services.GroupBy(s => GroupOf(s.Name)))
            {
                var items = group.ToList();
                var alive = items.Count(s => s.State == "Running");
                var restarts = items.Sum(s => s.Restarts);
                var tone = items.All(s => s.State == "Running") ? "ok" : items.Any(s => s.State == "Failed") ? "bad" : items.Any(s => s.State == "Restarting") ? "warn" : "off";
                var stateText = items.Count == 1
                    ? items[0].State switch { "Running" => "En marcha", "Failed" => "Fallando", "Restarting" => "Reiniciando", _ => "Parado" }
                    : $"{alive} de {items.Count} en marcha";
                var ports = items.Where(s => s.Port is not null).Select(s => s.Port!.Value).OrderBy(p => p).ToList();
                var listen = ports.Count == 0 ? "" : ports.Count == 1 ? $"127.0.0.1:{ports[0]}" : $"{ports[0]}–{ports[^1]}";
                var key = group.Key;
                if (key.StartsWith("php-", StringComparison.Ordinal))
                {
                    var branch = key[4..];
                    var php = installedPhp.FirstOrDefault(p => p.Name == status.Php && Branch(p) == branch)
                              ?? installedPhp.Where(p => Branch(p) == branch).OrderByDescending(p => p.Version).FirstOrDefault();
                    var usedBy = sites.Where(s => status.Sites.Any(x => x.Name == s.Name && Branch(x.Php) == branch)).Select(s => s.Host).ToList();
                    var isGlobal = Branch(status.Php) == branch;
                    var finding = php?.Version is { } v ? app.Evaluate(ComponentKind.Php, v, php.Executable) : null;
                    var procs = !OperatingSystem.IsWindows() && items.Count == 1 ? "php-fpm" : $"{items.Count} procesos php-cgi";
                    var uses = isGlobal ? (usedBy.Count == 0 ? "lo usa localhost" : $"lo usan localhost, {string.Join(", ", usedBy)}") : usedBy.Count == 0 ? "ningún sitio lo usa" : $"lo usa {string.Join(", ", usedBy)}";
                    rows.Add(new
                    {
                        name = $"PHP {branch}", tag = isGlobal ? "global" : finding?.Verdict == AuditVerdict.Eol ? "EOL" : null, tagTone = isGlobal ? "accent" : "bad",
                        description = $"{procs} · {uses}", version = php is null ? status.Php : $"{php.Version} {(php.ThreadSafe ? "TS" : "NTS")}",
                        listen, state = stateText, tone, restarts, service = key, running = alive > 0,
                    });
                }
                else if (key is "nginx" or "apache")
                {
                    rows.Add(new
                    {
                        name = key == "apache" ? "Apache" : "nginx", tag = (string?)null, tagTone = (string?)null,
                        description = sites.Count == 0 ? "localhost" : $"localhost y {sites.Count} {(sites.Count == 1 ? "sitio" : "sitios")} .test",
                        version = status.WebVersion ?? "", listen = $":{config.HttpPort}" + (sites.Count > 0 ? $" · :{config.HttpsPort}" : ""),
                        state = stateText, tone, restarts, service = key, running = alive > 0,
                    });
                }
                else if (key is "mariadb" or "mysql")
                {
                    rows.Add(new
                    {
                        name = key == "mysql" ? "MySQL" : "MariaDB", tag = (string?)null, tagTone = (string?)null,
                        description = $"Base de datos global · datos en data{Path.DirectorySeparatorChar}{key}{Path.DirectorySeparatorChar}{Branch(status.Db?.Split(' ').LastOrDefault())}",
                        version = status.Db?.Split(' ').LastOrDefault() ?? "", listen, state = stateText, tone, restarts, service = items[0].Name, running = alive > 0,
                    });
                }
                else if (key == "dns")
                {
                    rows.Add(new { name = "DNS local", tag = (string?)null, tagTone = (string?)null, description = "Responde *.test con 127.0.0.1 y ::1 (dentro de roostd)", version = "", listen = $"127.53.0.1:{Core.Dns.LocalDns.Port}", state = stateText, tone, restarts, service = key, running = alive > 0 });
                }
                else
                {
                    rows.Add(new { name = key, tag = (string?)null, tagTone = (string?)null, description = key == "redis" ? "Caché, colas y sesiones" : key == "mailpit" ? "El correo de PHP se queda aquí" : "Lo añade una extensión", version = "", listen, state = stateText, tone, restarts, service = key, running = alive > 0 });
                }
            }
        }
        else
        {
            // Parado: lo que arrancaría «Iniciar todo».
            if (globalDb is not null)
                rows.Add(new { name = globalDb.Engine.DisplayName(), tag = (string?)null, tagTone = (string?)null, description = $"Base de datos global · datos en data{Path.DirectorySeparatorChar}{globalDb.Engine.Key()}{Path.DirectorySeparatorChar}{globalDb.Branch}", version = globalDb.Name, listen = $"127.0.0.1:{config.DbPort}", state = "Parado", tone = "off", restarts = 0, service = $"{globalDb.Engine.Key()}-{globalDb.Branch}", running = false });
            var branches = new List<(string Branch, InstalledPhp Php, bool Global, List<string> Sites)>();
            if (globalPhp is not null) branches.Add((Branch(globalPhp), globalPhp, true, []));
            foreach (var site in sites)
            {
                InstalledPhp php;
                try { php = globalPhp is null ? null! : planner.ResolveSitePhp(site, globalPhp, installedPhp); } catch (RoostException) { continue; }
                if (php is null) continue;
                var branch = Branch(php);
                var index = branches.FindIndex(b => b.Branch == branch);
                if (index < 0) branches.Add((branch, php, false, [site.Host]));
                else branches[index].Sites.Add(site.Host);
            }
            foreach (var (branch, php, isGlobal, usedBy) in branches)
            {
                var finding = php.Version is { } v ? app.Evaluate(ComponentKind.Php, v, php.Executable) : null;
                var uses = isGlobal ? (usedBy.Count == 0 ? "lo usa localhost" : $"lo usan localhost, {string.Join(", ", usedBy)}") : $"lo usa {string.Join(", ", usedBy)}";
                rows.Add(new { name = $"PHP {branch}", tag = isGlobal ? "global" : finding?.Verdict == AuditVerdict.Eol ? "EOL" : null, tagTone = isGlobal ? "accent" : "bad", description = $"{(OperatingSystem.IsWindows() ? $"{config.PhpPool} procesos php-cgi" : "php-fpm")} · {uses}", version = $"{php.Version} {(php.ThreadSafe ? "TS" : "NTS")}", listen = "", state = "Parado", tone = "off", restarts = 0, service = $"php-{branch}", running = false });
            }
            var (webName, webVersion) = config.UsesApache
                ? ("Apache", new ApacheInstaller(paths).Find(ApacheCatalog.Current)?.Version)
                : ("nginx", new NginxInstaller(paths).Find(NginxCatalog.Current)?.Version);
            rows.Add(new { name = webName, tag = (string?)null, tagTone = (string?)null, description = webVersion is null ? "Se instala solo al iniciar por primera vez" : sites.Count == 0 ? "localhost" : $"localhost y {sites.Count} {(sites.Count == 1 ? "sitio" : "sitios")} .test", version = webVersion ?? (config.UsesApache ? ApacheCatalog.Current.Version : NginxCatalog.Current.Version), listen = $":{config.HttpPort}" + (sites.Count > 0 ? $" · :{config.HttpsPort}" : ""), state = "Parado", tone = "off", restarts = 0, service = config.UsesApache ? "apache" : "nginx", running = false });
        }

        var siteRows = sites.Select(s =>
        {
            InstalledPhp? php = null;
            try { php = globalPhp is null ? null : planner.ResolveSitePhp(s, globalPhp, installedPhp); } catch (RoostException) { }
            var finding = php?.Version is { } v ? app.Evaluate(ComponentKind.Php, v, php.Executable) : null;
            return new
            {
                name = s.Name, host = s.Host, url = StackPlanner.SiteUrl(s, config.HttpsPort),
                php = php is null ? (s.Php ?? "global") : Branch(php), eol = finding?.Verdict == AuditVerdict.Eol,
                running = status is { Running: true },
            };
        }).ToList();

        var scan = app.LastScan;
        var audit = scan is null ? null : new
        {
            when = scan.When, red = scan.Findings.Count(f => f.Color == AuditColor.Red), amber = scan.Findings.Count(f => f.Color == AuditColor.Amber), green = scan.Findings.Count(f => f.Color == AuditColor.Green),
            reds = scan.Findings.Where(f => f.Color == AuditColor.Red).Select(f => new { kind = Kind(f.Component.Kind), version = f.Component.Version.ToString(), source = f.Component.Source, chip = Chip(f) }).ToList(),
        };
        var dataset = app.Dataset ?? app.LoadDataset();

        var url = status?.Url ?? (config.HttpPort == 80 ? "http://localhost/" : $"http://localhost:{config.HttpPort}/");
        var summary = status is { Running: true }
            ? $"Todo en marcha{(status.Since is { } since ? $" desde las {since.ToLocalTime():HH:mm}" : "")} · {sites.Count} {(sites.Count == 1 ? "sitio" : "sitios")}, PHP {Branch(status.Php)} global{(status.Db is null ? "" : $", {ShortDb(status.Db)}")}"
            : status is not null ? "El daemon está en marcha, pero sin stack. Iniciar todo lo arranca."
            : "Parado. Iniciar todo arranca la base de datos, los PHP en uso y el servidor web.";
        return new
        {
            summary, url,
            tiles = new
            {
                web = url,
                terminal = globalPhp is null ? "Sin PHP de Roost" : $"Con PHP {Branch(globalPhp)} en el PATH",
                www = paths.Www,
                db = globalDb is null ? "Ninguna instalada" : $"{globalDb.Engine.DisplayName()} {globalDb.Branch} · :{config.DbPort}",
                audit = scan is null ? "Sin escanear" : scan.Findings.Count(f => f.Color == AuditColor.Red) is var red and > 0 ? $"{red} en rojo" : "Todo al día",
            },
            services = rows, sites = siteRows, audit, datasetDate = dataset.Dataset.Generated.ToLocalTime().ToString("dd-MMM-yyyy").ToLowerInvariant(),
        };
    }

    public static string GroupOf(string service)
    {
        if (service.StartsWith("php-", StringComparison.Ordinal))
        {
            var rest = service[4..];
            var dash = rest.LastIndexOf('-');
            return dash > 0 ? "php-" + rest[..dash] : service;
        }
        if (service.StartsWith("mariadb-", StringComparison.Ordinal)) return "mariadb";
        if (service.StartsWith("mysql-", StringComparison.Ordinal)) return "mysql";
        return service;
    }

    public static string Branch(InstalledPhp php) => php.Version is { } v ? $"{v.Major}.{v.Minor}" : php.Name;

    public static string Branch(string? name) =>
        name is not null && ComponentVersion.TryParse(name, out var v) ? $"{v.Major}.{v.Minor}" : name ?? "";

    private static string ShortDb(string label)
    {
        var space = label.LastIndexOf(' ');
        return space > 0 && ComponentVersion.TryParse(label[(space + 1)..], out var v) ? $"{label[..space]} {v.Major}.{v.Minor}" : label;
    }

    public static string Kind(ComponentKind kind) => kind switch
    {
        ComponentKind.Php => "PHP",
        ComponentKind.MySql => "MySQL",
        ComponentKind.MariaDb => "MariaDB",
        ComponentKind.Nginx => "nginx",
        ComponentKind.Apache => "Apache",
        _ => kind.ToString(),
    };

    /// <summary>El texto del chip de un hallazgo, como en la app.</summary>
    public static string Chip(AuditFinding f) => f.Verdict switch
    {
        AuditVerdict.Vulnerable when f.Mitigated => "MITIGADA",
        AuditVerdict.Vulnerable => f.MatchedAdvisories.Any(a => a.Kev) ? "VULNERABLE · KEV" : "VULNERABLE",
        AuditVerdict.Eol => "EOL",
        AuditVerdict.Unknown => "sin verificar",
        _ when f.Uncertain => "conviene actualizar",
        AuditVerdict.Patched => "al día",
        AuditVerdict.Supported => "al día",
        _ => f.VerdictLabel,
    };

    public static string Tone(AuditColor color) => color switch
    {
        AuditColor.Green => "ok",
        AuditColor.Amber => "warn",
        AuditColor.Red => "bad",
        _ => "neutral",
    };
}
