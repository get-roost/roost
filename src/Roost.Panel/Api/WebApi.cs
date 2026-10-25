using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.WebPanel.Api;

/// <summary>
/// Servidor web: cuál arranca (nginx o Apache), los puertos HTTP/HTTPS comprobados en vivo (libre,
/// ocupado y por quién, en uso por Roost), lo instalado y sus ficheros, «Validar» (el mismo <c>-t</c>
/// que hace <c>up</c>) y «Recargar» (solo el servidor web). Cambiar de servidor lo instala si falta.
/// </summary>
public static class WebApi
{
    public sealed record PortsRequest(int Http, int Https);
    public sealed record ServerRequest(string Server);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/web", (CancellationToken ct) => PanelRoutes.Guard(async () => PanelRoutes.Ok(await GetAsync(app, ct))));
        api.MapPost("/web/ports", (PortsRequest request, CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            if (request.Http is < 1 or > 65535 || request.Https is < 1 or > 65535 || request.Http == request.Https)
                throw new RoostException("Los puertos tienen que estar entre 1 y 65535 y ser distintos.");
            RoostConfig.SetWebPorts(app.Paths.ConfigFile, request.Http, request.Https);
            var applied = await SitesApi.ApplyIfRunningAsync(app, ct);
            return PanelRoutes.Ok(new { ok = applied.Ok, error = applied.Error, applied = applied.Applied, status = applied.Status });
        }));
        api.MapPost("/web/server", (ServerRequest request) => PanelRoutes.Guard(() => PanelRoutes.Started(Switch(app, request.Server))));
        api.MapPost("/web/validate", () => PanelRoutes.Guard(() => PanelRoutes.Ok(Validate(app))));
        api.MapPost("/web/reload", (CancellationToken ct) => PanelRoutes.Guard(async () =>
        {
            var status = await app.Daemon.TryStatusAsync(ct);
            if (status is not { Running: true }) throw new RoostException("Roost esta parado: no hay nada que recargar.");
            var response = await app.Daemon.ReloadAsync(RoostConfig.Load(app.Paths.ConfigFile).UsesApache ? "apache" : "nginx", ct);
            return PanelRoutes.Ok(new { ok = response.Ok, error = response.Error, applied = response.Applied });
        }));
        api.MapGet("/web/file", (string name) => PanelRoutes.Guard(() =>
        {
            var file = name switch
            {
                "nginx.conf" => Path.Combine(app.Paths.Etc, "nginx", "nginx.conf"),
                "httpd.conf" => Path.Combine(app.Paths.Etc, "apache", "httpd.conf"),
                _ => throw new RoostException("Solo nginx.conf o httpd.conf."),
            };
            return PanelRoutes.Ok(new { file, text = File.Exists(file) ? File.ReadAllText(file) : null });
        }));
    }

    public static async Task<object> GetAsync(PanelApp app, CancellationToken ct)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var status = await app.Daemon.TryStatusAsync(ct);
        var ours = status is { Running: true } ? status.Ports ?? status.Services.Where(s => s.Port is not null).Select(s => s.Port!.Value).ToList() : [];
        var nginx = new NginxInstaller(paths).Find(NginxCatalog.Current);
        var apache = new ApacheInstaller(paths).Find(ApacheCatalog.Current);
        var distro = ApacheCatalog.Current.IsDistro;
        var sites = StackApi.SafeSites(paths).Count;
        var webRunning = status is { Running: true } && status.Services.Any(s => s.Name is "nginx" or "apache" && s.State == "Running");
        return new
        {
            server = config.WebServer, running = webRunning, webVersion = status?.WebVersion,
            http = Port(config.HttpPort, ours), https = Port(config.HttpsPort, ours),
            nginx = new
            {
                installed = nginx is not null, version = nginx?.Version ?? NginxCatalog.Current.Version, directory = nginx?.Directory,
                listen = $"127.0.0.1:{config.HttpPort}" + (sites > 0 ? $" · 127.0.0.1:{config.HttpsPort}" : "") + " (solo este equipo)",
                conf = Path.Combine(paths.Etc, "nginx", "nginx.conf"), sitesDir = Path.Combine(paths.NginxSites, "*.conf"), errorLog = Path.Combine(paths.Logs, "nginx-error.log"),
                catalog = NginxCatalog.Current.Version, source = OperatingSystem.IsWindows() ? "nginx.org" : "get-roost/roost-builds",
                verified = OperatingSystem.IsWindows() ? "SHA-256 y firma PGP verificados" : "SHA-256 fijado en Roost y attestation",
            },
            apache = new
            {
                available = !distro || apache is not null, distro, installed = apache is not null, version = apache?.Version ?? ApacheCatalog.Current.Version, directory = apache?.Directory,
                conf = Path.Combine(paths.Etc, "apache", "httpd.conf"), sitesDir = Path.Combine(paths.ApacheSites, "*.conf"), errorLog = Path.Combine(paths.Logs, "apache-error.log"),
                why = distro && apache is null ? $"No está en el sistema: en Linux, Roost usa el apache2 de la distro y no instala paquetes del sistema. Instálalo con: {LinuxApache.InstallHint}." : null,
                note = distro ? DistroNote(apache, config) : null,
                redistributable = distro ? null : ApacheCatalog.Current.Redistributable,
            },
            jobs = app.Jobs.Running().Where(j => j.Title.Contains("nginx", StringComparison.Ordinal) || j.Title.Contains("Apache", StringComparison.Ordinal)).ToList(),
        };
    }

    /// <summary>
    /// Linux: lo que hay que saber del Apache de la distro (lo mismo que avisa <c>roost web use apache</c>): cuál es,
    /// que va con tus permisos y la configuración de Roost, el apache2.service del sistema si coge los mismos puertos,
    /// y que como usuario solo valen puertos de 1024 en adelante (a un binario del sistema no se le hace setcap).
    /// </summary>
    private static string DistroNote(InstalledApache? apache, RoostConfig config)
    {
        var text = (apache is null ? "Roost no descarga Apache: usa el apache2 de la distro" : $"El Apache {apache.Version} de la distro ({apache.Executable})")
            + ", lanzado por Roost con tus permisos y su propia configuración (no la de /etc/apache2). Como usuario, solo puertos de 1024 en adelante.";
        if (apache is not null && LinuxApache.ServiceWarning(SystemServices.Current.Find(ProcessRunner.Default), config.HttpPort, config.HttpsPort) is { } service)
            text += " " + service;
        return text;
    }

    /// <summary>Un puerto, comprobado en vivo: libre, en uso por Roost, ocupado por otro (y quién) o denegado (Linux, &lt; 1024).</summary>
    private static object Port(int port, IReadOnlyList<int> ours)
    {
        if (ours.Contains(port)) return new { port, state = "roost", text = "en uso por Roost", tone = "ok" };
        var probe = StackPlanner.Probe(port);
        if (probe == PortState.Free) return new { port, state = "free", text = "libre", tone = "ok" };
        if (probe == PortState.Denied) return new { port, state = "denied", text = "necesita permiso (setcap al iniciar)", tone = "warn" };
        var owner = PortOwners.Find(port);
        return new { port, state = "busy", text = owner is null ? "ocupado" : $"ocupado: {PortOwners.Describe(owner)}", tone = "bad" };
    }

    private static Job Switch(PanelApp app, string server)
    {
        if (server is not (RoostConfig.WebNginx or RoostConfig.WebApache)) throw new RoostException("El servidor web es nginx o apache.");
        var apache = server == RoostConfig.WebApache ? new ApacheInstaller(app.Paths).Find(ApacheCatalog.Current) : null;
        // Linux: el de la distro no se descarga; si falta, se dice cómo ponerlo en vez de lanzar una tarea que no puede hacer nada.
        if (server == RoostConfig.WebApache && apache is null && ApacheCatalog.Current.IsDistro)
            throw new RoostException($"Apache no esta en el sistema, y en Linux Roost usa el de la distro (no instala paquetes del sistema): {LinuxApache.InstallHint}. Despues vuelve a elegirlo.");
        var label = server == RoostConfig.WebApache ? $"Apache {apache?.Version ?? ApacheCatalog.Current.Version}" : $"nginx {NginxCatalog.Current.Version}";
        var installed = server == RoostConfig.WebApache ? apache is not null : new NginxInstaller(app.Paths).Find(NginxCatalog.Current) is not null;
        var steps = new List<(string, string?)>();
        if (!installed) { steps.Add(($"Descargar {label}", null)); steps.Add(("Verificar el SHA-256", "fijado en Roost")); steps.Add(("Dejarlo en su carpeta", null)); }
        steps.Add(("Guardar [web] server en roost.toml", null));
        steps.Add(("Aplicar al stack si está en marcha", "solo cambia el servidor web"));
        return app.Jobs.Start(installed ? $"Cambiando a {label}" : $"Instalando {label}", "Los PHP y la base de datos no cambian", steps, async ctx =>
        {
            var step = 0;
            if (!installed)
            {
                ctx.Begin(0, $"Descargando {label}…");
                if (server == RoostConfig.WebApache) await new ApacheInstaller(app.Paths, app.Http).InstallAsync(ApacheCatalog.Current, new Progress<long>(b => ctx.Message($"Descargando… {b / 1048576.0:0.0} MB")), ctx.Cancellation);
                else await new NginxInstaller(app.Paths, app.Http).InstallAsync(NginxCatalog.Current, new Progress<long>(b => ctx.Message($"Descargando… {b / 1048576.0:0.0} MB")), ctx.Cancellation);
                step = 3;
            }
            ctx.Begin(step, "Guardando…");
            RoostConfig.SetWebServer(app.Paths.ConfigFile, server);
            ctx.Begin(step + 1, "Aplicando…");
            var applied = await SitesApi.ApplyIfRunningAsync(app, ctx.Cancellation);
            if (!applied.Ok) throw new RoostException(applied.Error ?? "No se pudo aplicar.");
            ctx.Finish($"{label} es el servidor activo.");
            return new { server, applied = applied.Applied };
        }, canCancel: !installed);
    }

    private static object Validate(PanelApp app)
    {
        var paths = app.Paths;
        var config = RoostConfig.Load(paths.ConfigFile);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        string? output;
        if (config.UsesApache)
        {
            var apache = new ApacheInstaller(paths).Find(ApacheCatalog.Current) ?? throw new RoostException("Apache no esta instalado.");
            if (!File.Exists(Path.Combine(paths.Etc, "apache", "httpd.conf"))) throw new RoostException("Aun no hay httpd.conf generado: lo escribe el primer Iniciar todo.");
            output = planner.TestApache(apache);
            return new { ok = output?.Contains("Syntax OK", StringComparison.Ordinal) == true, output = output ?? "(sin salida)" };
        }
        var nginx = new NginxInstaller(paths).Find(NginxCatalog.Current) ?? throw new RoostException("nginx no esta instalado.");
        if (!File.Exists(Path.Combine(paths.Etc, "nginx", "nginx.conf"))) throw new RoostException("Aun no hay nginx.conf generado: lo escribe el primer Iniciar todo.");
        output = planner.TestNginx(nginx);
        return new { ok = output?.Contains("test is successful", StringComparison.Ordinal) == true, output = output ?? "(sin salida)" };
    }
}
