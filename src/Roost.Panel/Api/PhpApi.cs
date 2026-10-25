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
/// La pantalla PHP: las instaladas (con quién las usa y su estado de seguridad), las disponibles del
/// catálogo, instalar (una tarea larga con progreso), fijar la global, quitar (con el diálogo «Quitar
/// versión» si la usa un sitio) y el detalle de una instalación: php.ini, conf.d, Xdebug, extensiones y
/// ajustes rápidos, sobre <see cref="PhpConfigurator"/>. Cada cambio reinicia solo el pool de esa rama.
/// </summary>
public static class PhpApi
{
    public sealed record InstallRequest(string Spec, bool? Ts, string? Arch);
    public sealed record UseRequest(string Name);
    public sealed record RemoveRequest(string Name, string? SitesTo);
    public sealed record XdebugRequest(bool On);
    public sealed record ExtRequest(string Ext, bool On);
    public sealed record SettingsRequest(string MemoryLimit, string UploadMaxFilesize, string PostMaxSize, int MaxExecutionTime);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/php", (bool? catalog, CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await ListAsync(app, catalog == true, false, ct))));
        api.MapPost("/php/catalog", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await ListAsync(app, true, true, ct))));
        api.MapPost("/php/install", (InstallRequest request, CancellationToken ct) => PanelRoutes.Guard(() => Task.FromResult(Install(app, request))));
        api.MapPost("/php/use", (UseRequest request, CancellationToken ct) => PanelRoutes.Guard(() => UseAsync(app, request.Name, ct)));
        api.MapPost("/php/remove", (RemoveRequest request, CancellationToken ct) => PanelRoutes.Guard(() => RemoveAsync(app, request, ct)));
        api.MapGet("/php/{name}", (string name, CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await DetailAsync(app, name, ct))));
        api.MapGet("/php/{name}/text", (string name, string what) => PanelRoutes.Guard(() => PanelRoutes.Ok(Text(app, name, what))));
        api.MapPost("/php/{name}/xdebug", (string name, XdebugRequest request) => PanelRoutes.Guard(() => PanelRoutes.Started(Xdebug(app, name, request.On))));
        api.MapPost("/php/{name}/ext", (string name, ExtRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var php = Find(app, name);
            var check = await new PhpConfigurator(app.Paths, CommandRunner.Default).SetExtensionAsync(php, request.Ext, request.On, ct);
            var applied = await ApplyPoolAsync(app, php, ct);
            return PanelRoutes.Ok(new { ok = true, warnings = check.Warnings, applied });
        }));
        api.MapPost("/php/{name}/settings", (string name, SettingsRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var php = Find(app, name);
            var settings = new PhpSettings(request.MemoryLimit.Trim(), request.UploadMaxFilesize.Trim(), request.PostMaxSize.Trim(), request.MaxExecutionTime);
            var ini = new PhpConfigurator(app.Paths, CommandRunner.Default).SetSettings(php, settings);
            var applied = await ApplyPoolAsync(app, php, ct);
            return PanelRoutes.Ok(new { ok = true, ini, applied });
        }));
    }

    public static InstalledPhp Find(PanelApp app, string name) =>
        new PhpInstaller(app.Paths).ListInstalled().FirstOrDefault(p => p.Name == name)
        ?? throw new RoostException($"No hay ningun PHP '{name}' instalado por Roost.");

    private static (string Ts, string Arch, string? Compiler) Parts(InstalledPhp php)
    {
        var bits = php.Name.Split('-');
        return (php.ThreadSafe ? "TS" : "NTS", bits.Length >= 3 ? bits[2] : "x64", null);
    }

    public static async Task<object> ListAsync(PanelApp app, bool withCatalog, bool forceCatalog, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var installer = new PhpInstaller(paths);
        var installed = installer.ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledPhp? global = null;
        try { global = installed.Count == 0 ? null : planner.ResolvePhp(config, installed); } catch (RoostException) { }
        var sites = StackApi.SafeSites(paths);
        var status = await app.Daemon.TryStatusAsync(ct);
        var configurator = new PhpConfigurator(paths, CommandRunner.Default);

        var cards = installed.OrderByDescending(p => p.Version).Select(p =>
        {
            var finding = p.Version is { } v ? app.Evaluate(ComponentKind.Php, v, p.Executable) : null;
            var usedBy = new List<string>();
            if (global?.Name == p.Name) usedBy.Add("localhost");
            foreach (var s in sites)
            {
                try { if (global is not null && planner.ResolveSitePhp(s, global, installed).Name == p.Name) usedBy.Add(s.Host); }
                catch (RoostException) { }
            }
            var branch = StackApi.Branch(p);
            var running = status is { Running: true } && status.Services.Any(s => s.Name.StartsWith($"php-{branch}-", StringComparison.Ordinal) && s.State == "Running")
                          && (status.Php == p.Name || status.Sites.Any(x => x.Php == p.Name));
            var (ts, arch, _) = Parts(p);
            return new
            {
                name = p.Name, version = p.Version?.ToString() ?? p.Name, branch, ts, arch, compiler = Compiler(p),
                global = global?.Name == p.Name, usedBy, uses = usedBy.Count == 0 ? "Ningún sitio la usa" : string.Join(", ", usedBy),
                chip = finding is null ? "" : StackApi.Chip(finding), tone = finding is null ? "neutral" : StackApi.Tone(finding.Color), running,
                directory = p.Directory, xdebug = configurator.XdebugOn(p),
            };
        }).ToList();

        object? available = null;
        string? catalogProblem = null;
        DateTimeOffset? catalogWhen = app.PhpCatalogCached?.When;
        IReadOnlyList<PhpBuild>? builds = app.PhpCatalogCached?.Builds;
        if (withCatalog)
        {
            try { (builds, catalogWhen) = await app.PhpCatalogAsync(forceCatalog, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException) { catalogProblem = ex.Message; }
        }
        if (builds is not null)
        {
            var dataset = (app.Dataset ?? app.LoadDataset()).Dataset;
            dataset.Products.TryGetValue(AuditDataset.ProductKey(ComponentKind.Php) ?? "php", out var product);
            available = builds.Where(b => !b.ThreadSafe && b.Architecture == "x64").OrderByDescending(b => b.Version).Select(b =>
            {
                var finding = app.Evaluate(ComponentKind.Php, b.Version, b.FileName, PhpCatalog.SourceName);
                BranchInfo? info = null;
                product?.Branches.TryGetValue(b.Branch, out info);
                var support = finding.Verdict == AuditVerdict.Eol ? $"EOL desde {finding.Eol:dd-MMM-yyyy}".ToLowerInvariant().Replace("eol", "EOL")
                    : info?.SecurityUntil is { } sec && sec <= DateOnly.FromDateTime(DateTime.Today).AddYears(2) && info.Status.Contains("security", StringComparison.OrdinalIgnoreCase) ? $"Solo seguridad, hasta {sec:dd-MMM-yyyy}".ToLowerInvariant().Replace("solo", "Solo")
                    : info?.Eol is { } eol ? $"Soporte activo hasta {eol.Year}" : "Soporte activo";
                var isInstalled = installed.Any(p => p.Name == b.InstallName);
                return new { branch = b.Branch, version = b.Version.ToString(), ts = b.ThreadSafe, arch = b.Architecture, support, tone = StackApi.Tone(finding.Color), installed = isInstalled, size = b.SizeText, spec = b.Version.ToString(), windowsOnly = (string?)null };
            }).ToList();
        }
        var windowsOnly = OperatingSystem.IsWindows() ? [] : new[] { "7.4", "8.0" }.Select(b => new { branch = b, why = LinuxPhpCatalog.WhyWindowsOnly(b) }).ToList();

        return new
        {
            installed = cards, global = global?.Name, globalBranch = global is null ? null : StackApi.Branch(global),
            source = PhpCatalog.SourceName, catalogWhen, catalogProblem, available, windowsOnly,
            variants = OperatingSystem.IsWindows(),
            headline = OperatingSystem.IsWindows() ? "Builds oficiales de windows.php.net, cada versión en su carpeta. Actualizar nunca pisa la anterior." : "Builds de get-roost/roost-builds (8.1 a 8.5, con php-fpm y Xdebug), cada versión en su carpeta. Actualizar nunca pisa la anterior.",
            jobs = app.Jobs.Running().Where(j => j.Title.StartsWith("Instalando", StringComparison.Ordinal)).ToList(),
        };
    }

    private static string? Compiler(InstalledPhp php)
    {
        if (!OperatingSystem.IsWindows()) return php.ReadBuildInfo() is { } info ? "glibc 2.17" : null;
        // vs16, vs17, vc15: lo dice el nombre del zip, que no se guarda; se deduce de la rama como el catálogo.
        return php.Version is { } v ? (v.Major, v.Minor) switch { (7, 4) => "vc15", (8, 0) or (8, 1) or (8, 2) or (8, 3) => "vs16", _ => "vs17" } : null;
    }

    /// <summary>Instalar: una tarea larga con los pasos del diálogo «Instalando».</summary>
    private static IResult Install(PanelApp app, InstallRequest request)
    {
        var ts = request.Ts == true;
        var arch = string.IsNullOrWhiteSpace(request.Arch) ? "x64" : request.Arch;
        var job = app.Jobs.Start($"Instalando PHP {request.Spec}", $"{(ts ? "TS" : "NTS")} · {arch} · desde {PhpCatalog.SourceName}",
        [
            ("Resolver la versión en el catálogo", PhpCatalog.SourceName),
            ("Descargar", null),
            ("Verificar el SHA-256", "fijado en el catálogo, antes de usarlo"),
            ("Dejarla en su carpeta", Path.Combine(app.Paths.PhpBin, "…")),
        ], async ctx =>
        {
            ctx.Begin(0, "Consultando el catálogo…");
            var (builds, _) = await app.PhpCatalogAsync(false, ctx.Cancellation);
            var build = PhpCatalog.Resolve(builds, request.Spec, ts, arch);
            var installer = new PhpInstaller(app.Paths, app.Http);
            if (installer.IsInstalled(build)) throw new RoostException($"PHP {build.InstallName} ya esta instalado.");
            ctx.Step(0, "done", $"{build.Version} · {build.FileName}");
            ctx.Step(3, "pending", installer.InstallDirectory(build));
            ctx.Begin(1, $"Descargando {build.FileName}…");
            var progress = new Progress<long>(bytes =>
            {
                ctx.Message($"Descargando… {bytes / 1048576.0:0.0} MB{(build.Size is { } size ? $" de {size / 1048576.0:0.0} MB" : "")}");
                if (build.Size is { } total && total > 0) ctx.Progress(Math.Min(1, bytes / (double)total) * 0.8);
            });
            var installed = await installer.InstallAsync(build, progress, ctx.Cancellation);
            ctx.Finish($"PHP {installed.Name} instalado.");
            return new { name = installed.Name, version = build.Version.ToString() };
        });
        return PanelRoutes.Started(job);
    }

    /// <summary>«Usar como global»: la rama si esta instalación es la mejor de su rama (así entran solas las nuevas), o la exacta.</summary>
    private static async Task<IResult> UseAsync(PanelApp app, string name, CancellationToken ct)
    {
        var php = Find(app, name);
        var installed = new PhpInstaller(app.Paths).ListInstalled();
        var branch = StackApi.Branch(php);
        var best = PhpInstaller.Best(installed.Where(p => StackApi.Branch(p) == branch));
        RoostConfig.SetPhpVersion(app.Paths.ConfigFile, best.Name == php.Name ? branch : php.Name);
        var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
        return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied });
    }

    /// <summary>Quitar: si la usa un sitio, antes hay que decidir a qué PHP pasa («Quitar versión»); con <c>SitesTo</c> se pasan y se quita.</summary>
    private static async Task<IResult> RemoveAsync(PanelApp app, RemoveRequest request, CancellationToken ct)
    {
        var paths = app.Paths;
        var php = Find(app, request.Name);
        var installer = new PhpInstaller(paths);
        var installed = installer.ListInstalled();
        var config = RoostConfig.Load(paths.ConfigFile);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var sites = new SiteManager(paths);
        var all = sites.Registry.Load();
        InstalledPhp? global = null;
        try { global = planner.ResolvePhp(config, installed); } catch (RoostException) { }
        var users = all.Where(s => { try { return global is not null && planner.ResolveSitePhp(s, global, installed).Name == php.Name; } catch (RoostException) { return false; } }).ToList();
        var isGlobal = global?.Name == php.Name;

        if (request.SitesTo is null && (users.Count > 0 || isGlobal))
        {
            // El diálogo: quién la usa y a qué otras se puede pasar.
            var others = installed.Where(p => p.Name != php.Name).OrderByDescending(p => p.Version).Select(p => new { name = p.Name, version = p.Version?.ToString() ?? p.Name }).ToList();
            return PanelRoutes.Ok(new
            {
                inUse = true, isGlobal, name = php.Name, version = php.Version?.ToString(), directory = php.Directory,
                sites = users.Select(s => new { name = s.Name, host = s.Host, folder = s.Folder, pinned = s.Php is not null }).ToList(),
                global = global is null || isGlobal ? null : new { name = global.Name, version = global.Version?.ToString() }, others,
            });
        }

        if (request.SitesTo is { } to)
        {
            foreach (var s in users)
                sites.Add(s.Name, s.Folder, s.Root, to == "global" ? "global" : to);
            if (isGlobal)
            {
                var target = to == "global" ? null : installed.FirstOrDefault(p => p.Name == to);
                if (target is null) throw new RoostException("Elige a que PHP pasa el global antes de quitar este.");
                RoostConfig.SetPhpVersion(paths.ConfigFile, target.Name);
            }
        }
        if (planner.WhyNotRemove(php, RoostConfig.Load(paths.ConfigFile), installed, sites.Registry.Load()) is { } why)
            throw new RoostException(why);
        var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
        installer.Remove(php);
        return PanelRoutes.Ok(new { ok = true, removed = php.Name, applied = applied.Applied });
    }

    public static async Task<object> DetailAsync(PanelApp app, string name, CancellationToken ct)
    {
        var paths = app.Paths;
        var php = Find(app, name);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var configurator = new PhpConfigurator(paths, CommandRunner.Default);
        var config = RoostConfig.Load(paths.ConfigFile);
        var status = await app.Daemon.TryStatusAsync(ct);
        var branch = StackApi.Branch(php);
        var pool = status?.Services.Where(s => s.Name.StartsWith($"php-{branch}-", StringComparison.Ordinal)).ToList() ?? [];
        var ports = pool.Where(s => s.Port is not null).Select(s => s.Port!.Value).OrderBy(p => p).ToList();
        var serves = status is { Running: true } && (status.Php == php.Name || status.Sites.Any(x => x.Php == php.Name));
        var finding = php.Version is { } v ? app.Evaluate(ComponentKind.Php, v, php.Executable) : null;
        var xdebugOn = configurator.XdebugOn(php);
        var xdebugBuild = XdebugCatalog.For(php);
        var xdebugInstaller = new XdebugInstaller(paths, app.Http);
        var xdebugFile = xdebugInstaller.Locate(php);
        var xdebugText = XdebugCatalog.IsBundled(php)
            ? (xdebugOn ? $"Encendido. Xdebug {XdebugInstaller.BundledVersion(php) ?? ""} va dentro de este PHP; escucha al editor en el puerto 9003 cuando la petición lleva XDEBUG_TRIGGER." : $"Apagado. Xdebug {XdebugInstaller.BundledVersion(php) ?? ""} ya va dentro de este PHP: encenderlo no descarga nada.")
            : XdebugCatalog.WhyNot(php) is { } whyNot ? whyNot
            : xdebugOn ? $"Encendido. Xdebug {xdebugBuild?.Version} escucha al editor en el puerto 9003 cuando la petición lleva XDEBUG_TRIGGER."
            : xdebugFile is not null ? $"Apagado. La DLL de Xdebug {xdebugBuild?.Version} ya está descargada: encenderlo es inmediato."
            : $"Apagado. La primera vez se baja Xdebug {xdebugBuild?.Version} de xdebug.org ({(xdebugBuild?.Size ?? 0) / 1024} KB, con su SHA-256 comprobado).";
        var extensions = configurator.Extensions(php).Select(e => new { name = e.Name, on = e.On, byDefault = e.ByDefault, zend = e.Zend, builtIn = e.BuiltIn }).ToList();
        var settings = configurator.Settings(php);
        var iniPath = planner.PhpIniPath(php);
        var (ts, arch, _) = Parts(php);
        return new
        {
            name = php.Name, version = php.Version?.ToString() ?? php.Name, branch, ts, arch, compiler = Compiler(php),
            chip = finding is null ? "" : StackApi.Chip(finding), tone = finding is null ? "neutral" : StackApi.Tone(finding.Color),
            directory = php.Directory, executable = php.Executable, ini = iniPath, iniExists = File.Exists(iniPath),
            confdCommon = Path.Combine(planner.PhpConfD, "99-usuario.ini"), confdBranch = Path.Combine(planner.PhpBranchConfD(php), "99-usuario.ini"),
            pool = new { count = pool.Count, ports = ports.Count == 0 ? "" : ports.Count == 1 ? ports[0].ToString() : $"{ports[0]}–{ports[^1]}", running = pool.Any(s => s.State == "Running"), serves, kind = php.HasFpm ? "php-fpm" : "php-cgi", planned = config.PhpPool },
            global = planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled()).Name == php.Name,
            xdebug = new { on = xdebugOn, text = xdebugText, available = XdebugCatalog.IsBundled(php) || (xdebugBuild is not null && XdebugCatalog.WhyNot(php) is null), version = XdebugCatalog.IsBundled(php) ? XdebugInstaller.BundledVersion(php) : xdebugBuild?.Version },
            extensions, settings = new { memoryLimit = settings.MemoryLimit, uploadMaxFilesize = settings.UploadMaxFilesize, postMaxSize = settings.PostMaxSize, maxExecutionTime = settings.MaxExecutionTime },
            defaults = new { memoryLimit = PhpSettings.Default.MemoryLimit, uploadMaxFilesize = PhpSettings.Default.UploadMaxFilesize, postMaxSize = PhpSettings.Default.PostMaxSize, maxExecutionTime = PhpSettings.Default.MaxExecutionTime },
            launchJson = configurator.VsCodeLaunchJson(php),
        };
    }

    /// <summary>«Ver» el php.ini, phpinfo() y los módulos: texto de solo lectura.</summary>
    private static object Text(PanelApp app, string name, string what)
    {
        var php = Find(app, name);
        var planner = new StackPlanner(app.Paths, ProcessRunner.Default);
        var configurator = new PhpConfigurator(app.Paths, CommandRunner.Default);
        switch (what)
        {
            case "ini":
                var ini = planner.PhpIniPath(php);
                if (!File.Exists(ini)) planner.WritePhpIni(php);
                return new { title = Path.GetFileName(ini), subtitle = ini, text = File.ReadAllText(ini) };
            case "phpinfo":
            case "modules":
                var spec = configurator.Command(php, what == "phpinfo" ? "-i" : "-m");
                var result = CommandRunner.Default.RunAsync(spec, null, CancellationToken.None).GetAwaiter().GetResult();
                return new { title = what == "phpinfo" ? "phpinfo()" : "Módulos", subtitle = $"{php.Executable} {(what == "phpinfo" ? "-i" : "-m")}", text = result.Stdout + (result.Stderr.Length > 0 ? Environment.NewLine + result.Stderr : "") };
            default:
                throw new RoostException($"No se que es '{what}' (ini, phpinfo o modules).");
        }
    }

    /// <summary>Xdebug: en Windows la primera vez baja la DLL (una tarea larga); después, el php.ini y el pool.</summary>
    private static Job Xdebug(PanelApp app, string name, bool on)
    {
        var php = Find(app, name);
        var build = XdebugCatalog.For(php);
        var installer = new XdebugInstaller(app.Paths, app.Http);
        var needsDownload = on && !XdebugCatalog.IsBundled(php) && build is not null && installer.Find(build) is null;
        var steps = new List<(string, string?)>();
        if (needsDownload)
        {
            steps.Add(($"Xdebug {build!.Version} para PHP {build.PhpBranch}", build.FileName));
            steps.Add(("Descargando de xdebug.org", null));
            steps.Add(("Verificar el SHA-256", "fijado en Roost, antes de usarla"));
            steps.Add(("Dejarla en su carpeta", Path.Combine(app.Paths.Root, "bin", "xdebug", build.Version)));
        }
        steps.Add((on ? "Activar Xdebug en el php.ini" : "Quitar Xdebug del php.ini", null));
        steps.Add(("Reiniciar solo el pool de esta rama", "si está sirviendo algo"));
        var title = needsDownload ? $"Instalando Xdebug {build!.Version}" : on ? "Encendiendo Xdebug" : "Apagando Xdebug";
        return app.Jobs.Start(title, $"Para PHP {StackApi.Branch(php)} · {(php.ThreadSafe ? "TS" : "NTS")}", steps, async ctx =>
        {
            var step = 0;
            if (needsDownload)
            {
                if (XdebugCatalog.WhyNot(php) is { } why) throw new RoostException(why);
                ctx.Begin(1, "Descargando de xdebug.org…");
                await installer.InstallAsync(build!, new Progress<long>(b => ctx.Progress(build!.Size > 0 ? Math.Min(1, b / (double)build.Size) * 0.7 : null)), ctx.Cancellation);
                step = 4;
            }
            ctx.Begin(step, on ? "Activando en el php.ini…" : "Quitando del php.ini…");
            var check = await new PhpConfigurator(app.Paths, CommandRunner.Default).SetXdebugAsync(php, on, ctx.Cancellation);
            ctx.Begin(step + 1, "Reiniciando el pool…");
            var applied = await ApplyPoolAsync(app, php, ctx.Cancellation);
            ctx.Finish(on ? $"Xdebug {check.XdebugVersion ?? ""} encendido." : "Xdebug apagado.");
            return new { on, version = check.XdebugVersion, warnings = check.Warnings, applied };
        });
    }

    /// <summary>Como <c>PhpTarget.ApplyAsync</c> en la CLI: si el stack está en marcha y este PHP sirve algo, reinicia solo su pool.</summary>
    public static async Task<string?> ApplyPoolAsync(PanelApp app, InstalledPhp php, CancellationToken ct)
    {
        if (await app.Daemon.TryStatusAsync(ct) is not { Running: true } status) return null;
        var pool = $"php-{StackApi.Branch(php)}";
        var serves = status.Php == php.Name || status.Sites.Any(s => s.Php == php.Name);
        var running = status.Services.Any(s => s.Name.StartsWith(pool + "-", StringComparison.Ordinal) && s.State == "Running");
        if (!serves || !running) return null;
        var response = await app.Daemon.RestartAsync(pool, ct);
        if (!response.Ok) throw new RoostException($"No se pudo reiniciar {pool}: {response.Error}");
        return $"reiniciado solo el pool {pool}";
    }
}
