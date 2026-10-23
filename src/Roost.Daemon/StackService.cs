using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Dns;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Extensibility;

namespace Roost.Daemon;

/// <summary>
/// El stack que mantiene vivo el daemon: un <see cref="Supervisor"/> con el plan actual. Las
/// operaciones se serializan: un <c>up</c> con el stack en marcha lo para y lo vuelve a
/// arrancar con la configuración nueva (así se aplica un <c>roost php use</c>).
/// </summary>
internal sealed class StackService(RoostPaths paths, FileLog log, ExtensionSet extensions)
{
    // Solo se espera lo necesario; el margen es para el primer arranque de MySQL 8 tras crear el datadir.
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Supervisor? _supervisor;
    private StackPlan? _plan;
    private DateTimeOffset? _since;
    private LocalDnsServer? _dns;

    public StatusResponse Status()
    {
        var supervisor = _supervisor;
        var plan = _plan;
        var services = supervisor?.Status()
            .Select(s => new ServiceDto(s.Name, s.State.ToString(), s.ProcessId, s.Restarts, s.Port, s.LastError))
            .ToList() ?? [];
        // El DNS local va dentro del propio daemon: sale como un servicio más, con el pid de roostd.
        if (supervisor is not null && _dns is not null)
            services.Insert(0, new ServiceDto(DnsService, "Running", Environment.ProcessId, 0, _dns.EndPoint.Port, null));

        return new StatusResponse(
            Environment.ProcessId,
            supervisor is not null,
            plan?.Url,
            plan?.Php.Name,
            plan?.WebVersion,
            plan?.Db?.Label,
            plan?.Db is null ? null : plan.DbPort,
            plan?.Sites.Select(s => new SiteDto(s.Site.Name, s.Url, s.Php.Name)).ToList() ?? [],
            services,
            supervisor is null ? null : _since,
            plan?.WebServer);
    }

    /// <summary>
    /// (Re)arranca el stack. No depende de que el cliente siga conectado: un Ctrl+C en la CLI
    /// no puede dejarlo a medias. Lo que se arranca y no llega a quedar como stack actual se
    /// para siempre, así que nunca quedan procesos sin dueño.
    /// </summary>
    public async Task<CommandResponse> UpAsync()
    {
        await _gate.WaitAsync();
        Supervisor? starting = null;
        try
        {
            await StopCurrentAsync();

            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var installedPhp = new PhpInstaller(paths).ListInstalled();
            var php = planner.ResolvePhp(config, installedPhp);
            // El servidor web de [web] server; si falta, Prepare lo dice (roost up lo instala antes).
            var nginx = new NginxInstaller(paths).Find(NginxCatalog.Windows);
            var apache = new ApacheInstaller(paths).Find(ApacheCatalog.Windows);
            var db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());
            var sites = new SiteRegistry(paths).Load();
            var extra = extensions.StackServices(new StackContext(paths, config));
            var plan = planner.Prepare(config, php, nginx, db, sites, installedPhp, new LocalCa(paths), extra, apache);

            // El DNS local, antes que nada: sin él, con [sites] resolver = "dns", los .test no resuelven.
            await SetDnsAsync(config.UsesLocalDns);

            var busy = StackPlanner.BusyPorts(plan.Ports);
            if (busy.Count > 0)
                throw new RoostException(BusyPortsMessage(busy, config));

            starting = new Supervisor(new OsProcessLauncher(), log.Write);
            starting.Start(plan.Services);
            var problem = await WaitHealthyAsync(starting, plan);
            if (problem is not null)
                throw new RoostException(problem);

            _supervisor = starting;
            _plan = plan;
            _since = DateTimeOffset.Now;
            starting = null;
            log.Write($"stack en marcha en {plan.Url} con PHP {php.Name}, {plan.WebServer} {plan.WebVersion}"
                + (db is null ? ", sin base de datos" : $", {db.Label} en el puerto {plan.DbPort}")
                + $" y {plan.Sites.Count} sitio(s)");
            return new CommandResponse(true, null, Status());
        }
        catch (RoostException ex)
        {
            log.Write("up fallido: " + ex.Message);
            return new CommandResponse(false, ex.Message, Status());
        }
        catch (Exception ex)
        {
            log.Write("up fallido por un error inesperado: " + ex);
            return new CommandResponse(false, $"Error inesperado en el daemon: {ex.Message} (detalle en {DaemonHost.LogFile(paths)})", Status());
        }
        finally
        {
            if (starting is not null) await starting.StopAsync();
            // Un up fallido no deja nada a medias: tampoco el DNS local.
            if (_supervisor is null) await SetDnsAsync(false);
            _gate.Release();
        }
    }

    /// <summary>
    /// Reinicia un servicio o un grupo sin tocar el resto: <c>nginx</c>, <c>php-8.4</c> (todo
    /// su pool) o <c>mariadb-11.4</c>. No regenera la configuración: para eso está <c>up</c>.
    /// </summary>
    public async Task<CommandResponse> RestartAsync(string service)
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor)
                return new CommandResponse(false, "El stack no esta en marcha.", Status());

            if (service == DnsService)
            {
                if (_dns is null) return new CommandResponse(false, "El DNS local no esta en marcha ([sites] resolver no es \"dns\").", Status());
                await SetDnsAsync(false);
                await SetDnsAsync(true);
                log.Write("reinicio pedido: dns");
                return new CommandResponse(true, null, Status());
            }

            var count = await supervisor.RestartAsync(name => name == service || name.StartsWith(service + "-", StringComparison.Ordinal));
            if (count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en marcha.", Status());

            log.Write($"reinicio pedido: {service} ({count} proceso(s))");
            return new CommandResponse(true, null, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResponse> DownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCurrentAsync();
            return new CommandResponse(true, null, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    public const string DnsService = "dns";

    /// <summary>Arranca o para el DNS local (dentro de este proceso). Lanza si su puerto está cogido.</summary>
    private async Task SetDnsAsync(bool enabled)
    {
        if (enabled && _dns is null)
        {
            _dns = LocalDnsServer.Start(LocalDns.EndPoint, log.Write);
            log.Write($"dns: en marcha en {LocalDns.EndPoint} (*.test -> 127.0.0.1)");
            if (!LocalDns.IsRuleInstalled())
                log.Write("dns: aviso: no hay regla NRPT para .test; Windows no le preguntara (roost dns install)");
        }
        else if (!enabled && _dns is not null)
        {
            await _dns.DisposeAsync();
            _dns = null;
            log.Write("dns: parado");
        }
    }

    private async Task StopCurrentAsync()
    {
        await SetDnsAsync(false);
        if (_supervisor is null) return;
        await _supervisor.StopAsync();
        _supervisor = null;
        _plan = null;
        log.Write("stack parado");
    }

    /// <summary>
    /// Espera a que todos los servicios estén vivos y aceptando conexiones en su puerto. Si
    /// alguno falla o no llega a tiempo, devuelve el motivo con el final de su log.
    /// </summary>
    private async Task<string?> WaitHealthyAsync(Supervisor supervisor, StackPlan plan)
    {
        var started = DateTime.UtcNow;
        var deadline = started + HealthTimeout;
        var lastReport = started;
        while (true)
        {
            var statuses = supervisor.Status();
            if (statuses.FirstOrDefault(s => s.State == ServiceState.Failed) is { } failed)
                return $"{failed.Name} no arranca ({failed.LastError}).{LogTail(plan, failed.Name)}";

            ServiceStatus? pending = null;
            foreach (var s in statuses)
            {
                if (s.State != ServiceState.Running
                    || (s.Port is { } port && !await StackPlanner.AcceptsConnectionsAsync(port)))
                {
                    pending = s;
                    break;
                }
            }
            if (pending is null)
            {
                log.Write($"salud: todo responde en {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                return null;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return $"{pending.Name} no responde en {HealthTimeout.TotalSeconds:0} s "
                    + $"({pending.LastError ?? pending.State.ToString()}).{LogTail(plan, pending.Name)}";
            }

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(2))
            {
                log.Write($"salud: esperando a {pending.Name} ({pending.State}, puerto {pending.Port})");
                lastReport = DateTime.UtcNow;
            }

            await Task.Delay(250);
        }
    }

    private string LogTail(StackPlan plan, string service)
    {
        var files = new List<string>();
        if (plan.Services.FirstOrDefault(s => s.Name == service)?.LogFile is { } own) files.Add(own);
        if (service == "nginx") files.Add(Path.Combine(paths.Logs, "nginx-error.log"));
        if (service == "apache") files.Add(Path.Combine(paths.Logs, "apache-error.log"));

        var lines = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var tail = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(6).ToList();
                if (tail.Count > 0) lines.Add($"\n{file}:\n  " + string.Join("\n  ", tail));
            }
            catch
            {
                // log que no existe todavía: nada que enseñar
            }
        }
        return string.Concat(lines);
    }

    private string BusyPortsMessage(IReadOnlyList<int> busy, RoostConfig config)
    {
        if (busy.Contains(config.HttpPort))
        {
            return $"El puerto {config.HttpPort} ya esta en uso en 127.0.0.1{OwnedBy(config.HttpPort)} (Laragon, IIS u otro servidor web?). "
                + $"Paralo, o cambia [web] port en {paths.ConfigFile}.";
        }
        if (busy.Contains(config.HttpsPort))
        {
            return $"El puerto {config.HttpsPort} (HTTPS de los sitios .test) ya esta en uso en 127.0.0.1{OwnedBy(config.HttpsPort)} (IIS, VMware, Skype u otro servidor?). "
                + $"Paralo, o cambia [web] https_port en {paths.ConfigFile}.";
        }
        if (busy.Contains(config.DbPort))
        {
            return $"El puerto {config.DbPort} ya esta en uso en 127.0.0.1{OwnedBy(config.DbPort)} (otro MySQL o MariaDB, quiza un servicio de Windows?). "
                + $"Paralo, cambia [db] port en {paths.ConfigFile}, o arranca sin base de datos con: roost db use none";
        }
        return $"Los puertos {string.Join(", ", busy)} del pool de PHP ya estan en uso por otro proceso. "
            + $"Paralo, o baja [php] pool en {paths.ConfigFile}.";
    }

    private static string OwnedBy(int port) =>
        PortOwners.Find(port) is { } owner ? $" por {owner.ProcessName ?? "otro proceso"} (PID {owner.Pid})" : "";
}
