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
/// Lo que el stack necesita del sistema, y que los tests sustituyen: quien lanza los procesos,
/// quien ejecuta <c>nginx -t</c>, las sondas de puertos y si la máquina tiene IPv6 en loopback.
/// </summary>
internal sealed record StackEnvironment(
    IProcessLauncher Launcher,
    IProcessRunner Runner,
    Func<IEnumerable<int>, IReadOnlyList<int>> BusyPorts,
    Func<int, CancellationToken, Task<bool>> AcceptsConnections,
    TimeSpan HealthTimeout,
    bool Ipv6)
{
    // Solo se espera lo necesario; el margen es para el primer arranque de MySQL 8 tras crear el datadir.
    public static StackEnvironment Real => new(
        new OsProcessLauncher(), ProcessRunner.Default, StackPlanner.BusyPorts, StackPlanner.AcceptsConnectionsAsync,
        TimeSpan.FromSeconds(45), Loopback.HasIpv6);
}

/// <summary>
/// El stack que mantiene vivo el daemon: un <see cref="Supervisor"/> con el plan actual. Las
/// operaciones se serializan. Un <c>up</c> con el stack en marcha prepara y valida la
/// configuración nueva ANTES de parar nada: si no vale (un puerto ocupado, un vhost sin marcas,
/// un <c>nginx -t</c> que falla), lo de antes sigue en marcha tal cual; si vale, para lo de antes
/// y arranca lo nuevo (así se aplica un <c>roost php use</c>).
/// </summary>
internal sealed class StackService(RoostPaths paths, Action<string> log, ExtensionSet extensions, StackEnvironment? environment = null)
{
    private readonly StackEnvironment _env = environment ?? StackEnvironment.Real;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Supervisor? _supervisor;
    private StackPlan? _plan;
    private DateTimeOffset? _since;
    private LocalDnsServer? _dns;
    private bool _dnsWanted;   // el plan actual lleva DNS local (aunque esté parado a mano)

    public StatusResponse Status()
    {
        var supervisor = _supervisor;
        var plan = _plan;
        var services = supervisor?.Status()
            .Select(s => new ServiceDto(s.Name, s.State.ToString(), s.ProcessId, s.Restarts, s.Port, s.LastError))
            .ToList() ?? [];
        // El DNS local va dentro del propio daemon: sale como un servicio más, con el pid de roostd.
        if (supervisor is not null && _dnsWanted)
        {
            services.Insert(0, _dns is { } dns
                ? new ServiceDto(DnsService, "Running", Environment.ProcessId, 0, dns.EndPoint.Port, null)
                : new ServiceDto(DnsService, "Stopped", null, 0, LocalDns.EndPoint.Port, null));
        }

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
            plan?.WebServer,
            plan?.Ports);
    }

    /// <summary>
    /// (Re)arranca el stack. No depende de que el cliente siga conectado: un Ctrl+C en la CLI
    /// no puede dejarlo a medias. Lo que se arranca y no llega a quedar como stack actual se
    /// para siempre, así que nunca quedan procesos sin dueño.
    ///
    /// Con el stack en marcha, primero se prepara y valida lo nuevo (<c>Prepare</c>, el
    /// <c>-t</c> del servidor web, los puertos que no sean ya nuestros, el DNS local si lo pide
    /// la configuración y no estaba). Solo si todo vale se para lo de antes y se arranca. Como
    /// <c>Prepare</c> regenera en su sitio ficheros que lee el stack (<see cref="GeneratedConfig"/>),
    /// se hace una foto antes y se devuelve si el plan se rechaza. Lo único que no se puede
    /// saber antes de parar es si lo nuevo arranca: si no, el stack queda parado y se dice.
    /// </summary>
    public async Task<CommandResponse> UpAsync()
    {
        await _gate.WaitAsync();
        Supervisor? starting = null;
        var wasRunning = _supervisor is not null;
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, _env.Runner);
            var installedPhp = new PhpInstaller(paths).ListInstalled();
            var php = planner.ResolvePhp(config, installedPhp);
            // El servidor web de [web] server; si falta, Prepare lo dice (roost up lo instala antes).
            var nginx = new NginxInstaller(paths).Find(NginxCatalog.Windows);
            var apache = new ApacheInstaller(paths).Find(ApacheCatalog.Windows);
            var db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());
            var sites = new SiteRegistry(paths).Load();
            var extra = extensions.StackServices(new StackContext(paths, config));

            StackPlan Prepare() => planner.Prepare(config, php, nginx, db, sites, installedPhp, new LocalCa(paths), extra, apache);

            var before = GeneratedConfig.Snapshot(paths);
            StackPlan plan;
            var dnsStartedHere = false;
            var stoppedFirst = false;
            try
            {
                plan = Prepare();

                // Los puertos del stack en marcha son nuestros: se liberan al pararlo.
                var busy = _env.BusyPorts(plan.Ports.Except(_plan?.Ports ?? []));
                if (busy.Count > 0)
                    throw new RoostException(BusyPortsMessage(busy, config));

                // El DNS local no depende del plan: si hace falta y no está, se arranca ya, y si su
                // puerto está cogido se rechaza el cambio sin haber parado nada.
                if (config.UsesLocalDns && _dns is null)
                {
                    await SetDnsAsync(true);
                    dnsStartedHere = true;
                }
            }
            catch (RoostException ex) when (wasRunning && _plan is { } running && FailsOnlyOnOwnPorts(ex.Message, running.Ports))
            {
                // En Windows, nginx -t abre los puertos de verdad, y el Apache en marcha los tiene en
                // exclusiva: pasar de Apache a nginx no se puede validar con lo de antes en marcha. Esos
                // puertos son nuestros, así que se para lo de antes y se valida otra vez.
                log("el -t no puede abrir puertos que tiene el stack en marcha: se para antes de validar");
                before.Restore();
                if (dnsStartedHere) await SetDnsAsync(false);
                await StopCurrentAsync();
                stoppedFirst = true;
                try
                {
                    plan = Prepare();
                    var busy = _env.BusyPorts(plan.Ports);
                    if (busy.Count > 0)
                        throw new RoostException(BusyPortsMessage(busy, config));
                }
                catch (RoostException again)
                {
                    throw new RoostException($"{again.Message}\nPara validarlo habia que parar lo que estaba en marcha (el servidor web de antes tenia sus puertos), y lo nuevo no vale: el stack ha quedado parado.");
                }
            }
            catch (Exception ex) when (wasRunning)
            {
                // Rechazado con lo de antes en marcha: se le devuelven sus ficheros tal como estaban.
                before.Restore();
                if (dnsStartedHere) await SetDnsAsync(false);
                if (ex is not RoostException) throw;
                throw new RoostException($"No se ha aplicado el cambio: {ex.Message}\nSigue en marcha lo de antes, con la configuracion anterior.");
            }

            // Punto sin retorno: lo nuevo vale. Se para lo de antes con sus ficheros de siempre (la
            // parada limpia de la base de datos lee el puerto de su my.ini) y luego quedan los nuevos.
            if (wasRunning && !stoppedFirst)
            {
                var after = GeneratedConfig.Snapshot(paths);
                before.Restore();
                await StopCurrentAsync();
                after.Restore();
            }
            await SetDnsAsync(config.UsesLocalDns);
            _dnsWanted = config.UsesLocalDns;

            starting = new Supervisor(_env.Launcher, log);
            starting.Start(plan.Services);
            var problem = await WaitHealthyAsync(starting, plan);
            if (problem is not null)
            {
                throw new RoostException(wasRunning
                    ? $"{problem}\nPara aplicar el cambio se habia parado lo que estaba en marcha, y lo nuevo no arranca: el stack ha quedado parado."
                    : problem);
            }

            _supervisor = starting;
            _plan = plan;
            _since = DateTimeOffset.Now;
            starting = null;
            log($"stack en marcha en {plan.Url} con PHP {php.Name}, {plan.WebServer} {plan.WebVersion}"
                + (db is null ? ", sin base de datos" : $", {db.Label} en el puerto {plan.DbPort}")
                + (plan.Redis is { } redis ? $", {redis.Label} en el {config.RedisPort}" : "")
                + (plan.Mailpit is { } mailpit ? $", {mailpit.Label} (SMTP {config.MailSmtpPort}, bandeja {config.MailWebPort})" : "")
                + $" y {plan.Sites.Count} sitio(s)");
            return new CommandResponse(true, null, Status());
        }
        catch (RoostException ex)
        {
            log("up fallido: " + ex.Message);
            return new CommandResponse(false, ex.Message, Status());
        }
        catch (Exception ex)
        {
            log("up fallido por un error inesperado: " + ex);
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
    /// su pool) o <c>mariadb-11.4</c>. Uno parado a mano se arranca. No regenera la
    /// configuración: para eso está <c>up</c>.
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
                if (!_dnsWanted) return new CommandResponse(false, DnsNotWanted, Status());
                await SetDnsAsync(false);
                await SetDnsAsync(true);
                log("reinicio pedido: dns");
                return new CommandResponse(true, null, Status());
            }

            var count = await supervisor.RestartAsync(Matches(service));
            if (count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en el stack.", Status());

            log($"reinicio pedido: {service} ({count} proceso(s))");
            return new CommandResponse(true, null, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reinicia todo el stack con el mismo plan: para en orden inverso (limpio lo que tiene
    /// comando de parada) y arranca en orden, y espera a que todo responda. No regenera nada.
    /// </summary>
    public async Task<CommandResponse> RestartAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor || _plan is not { } plan)
                return new CommandResponse(false, "El stack no esta en marcha.", Status());

            log("reinicio pedido: todo");
            await supervisor.StopAsync();
            if (_dnsWanted)
            {
                await SetDnsAsync(false);
                await SetDnsAsync(true);
            }
            await supervisor.StartAsync(_ => true);
            _since = DateTimeOffset.Now;
            var problem = await WaitHealthyAsync(supervisor, plan);
            return problem is null ? new CommandResponse(true, null, Status()) : new CommandResponse(false, problem, Status());
        }
        catch (RoostException ex)
        {
            return new CommandResponse(false, ex.Message, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Para un servicio o grupo sin tocar el resto (la base de datos, limpia). Queda
    /// <c>Stopped</c> y nadie lo relanza hasta <see cref="StartServiceAsync"/>, un
    /// <see cref="RestartAsync"/> o el siguiente <c>up</c>.
    /// </summary>
    public async Task<CommandResponse> StopServiceAsync(string service)
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor)
                return new CommandResponse(false, "El stack no esta en marcha.", Status());

            if (service == DnsService)
            {
                if (!_dnsWanted) return new CommandResponse(false, DnsNotWanted, Status());
                await SetDnsAsync(false);
                log("parada pedida: dns");
                return new CommandResponse(true, null, Status());
            }

            var count = await supervisor.StopAsync(Matches(service));
            if (count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en el stack.", Status());

            log($"parada pedida: {service} ({count} proceso(s))");
            return new CommandResponse(true, null, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Vuelve a arrancar un servicio o grupo parado a mano (o que dejó de relanzarse); uno en marcha se deja.</summary>
    public async Task<CommandResponse> StartServiceAsync(string service)
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor)
                return new CommandResponse(false, "El stack no esta en marcha. Arrancalo con: roost up", Status());

            if (service == DnsService)
            {
                if (!_dnsWanted) return new CommandResponse(false, DnsNotWanted, Status());
                await SetDnsAsync(true);
                log("arranque pedido: dns");
                return new CommandResponse(true, null, Status());
            }

            var count = await supervisor.StartAsync(Matches(service));
            if (count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en el stack.", Status());

            log($"arranque pedido: {service} ({count} proceso(s))");
            return new CommandResponse(true, null, Status());
        }
        catch (RoostException ex)
        {
            return new CommandResponse(false, ex.Message, Status());
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
            await SetDnsAsync(false);
            _dnsWanted = false;
            await StopCurrentAsync();
            return new CommandResponse(true, null, Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renueva los certificados de los sitios del stack en marcha que caducan pronto
    /// (<see cref="LocalCa.EnsureSiteCert"/> decide) y, si ha cambiado alguno, reinicia solo el
    /// servidor web para que lo cargue. Devuelve cuántos ha renovado. Nunca lanza.
    /// </summary>
    public async Task<int> RenewCertificatesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor || _plan is not { } plan || plan.Sites.Count == 0) return 0;

            var ca = new LocalCa(paths);
            var renewed = new List<string>();
            foreach (var site in plan.Sites)
            {
                var certFile = ca.SiteCertFile(site.Site.Name);
                var old = File.Exists(certFile) ? File.ReadAllText(certFile) : null;
                ca.EnsureSiteCert(site.Site);
                if (File.ReadAllText(certFile) != old) renewed.Add(site.Site.Host);
            }
            if (renewed.Count == 0) return 0;

            log($"certificados renovados: {string.Join(", ", renewed)}; se reinicia {plan.WebServer} para que los cargue");
            await supervisor.RestartAsync(name => name == plan.WebServer);
            return renewed.Count;
        }
        catch (Exception ex)
        {
            log("certificados: no se pudieron renovar (" + ex.Message + ")");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public const string DnsService = "dns";

    private const string DnsNotWanted = "El DNS local no va con este stack ([sites] resolver no es \"dns\").";

    /// <summary>El servicio con ese nombre, o todos los de ese grupo (<c>php-8.4</c> → <c>php-8.4-0</c>…).</summary>
    private static Func<string, bool> Matches(string service) =>
        name => name == service || name.StartsWith(service + "-", StringComparison.Ordinal);

    /// <summary>Arranca o para el DNS local (dentro de este proceso). Lanza si su puerto está cogido.</summary>
    private async Task SetDnsAsync(bool enabled)
    {
        if (enabled && _dns is null)
        {
            _dns = LocalDnsServer.Start(LocalDns.EndPoint, log, _env.Ipv6);
            log($"dns: en marcha en {LocalDns.EndPoint} (*.test -> 127.0.0.1{(_env.Ipv6 ? " y ::1" : "")})");
            if (!LocalDns.IsRuleInstalled())
                log("dns: aviso: no hay regla NRPT para .test; Windows no le preguntara (roost dns install)");
        }
        else if (!enabled && _dns is not null)
        {
            await _dns.DisposeAsync();
            _dns = null;
            log("dns: parado");
        }
    }

    /// <summary>
    /// PURO: true si el <c>-t</c> del servidor web solo ha fallado por no poder abrir puertos que
    /// tiene el stack en marcha (<c>bind() to …:puerto failed (10013</c> o <c>10048</c>). Pasa al
    /// cambiar de Apache a nginx: nginx -t abre los puertos y Apache los tiene en exclusiva.
    /// </summary>
    internal static bool FailsOnlyOnOwnPorts(string message, IEnumerable<int> ownPorts)
    {
        var own = ownPorts.ToHashSet();
        var binds = BindFailure.Matches(message);
        return binds.Count > 0 && binds.All(m =>
            own.Contains(int.Parse(m.Groups[1].Value)) && m.Groups[2].Value is "10013" or "10048");
    }

    private static readonly System.Text.RegularExpressions.Regex BindFailure =
        new(@"bind\(\) to \S*?:(\d+) failed \((\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task StopCurrentAsync()
    {
        if (_supervisor is null) return;
        await _supervisor.StopAsync();
        _supervisor = null;
        _plan = null;
        log("stack parado");
    }

    /// <summary>
    /// Espera a que todos los servicios estén vivos y aceptando conexiones en su puerto. Si
    /// alguno falla o no llega a tiempo, devuelve el motivo con el final de su log.
    /// </summary>
    private async Task<string?> WaitHealthyAsync(Supervisor supervisor, StackPlan plan)
    {
        var started = DateTime.UtcNow;
        var deadline = started + _env.HealthTimeout;
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
                    || (s.Port is { } port && !await _env.AcceptsConnections(port, CancellationToken.None)))
                {
                    pending = s;
                    break;
                }
            }
            if (pending is null)
            {
                log($"salud: todo responde en {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                return null;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return $"{pending.Name} no responde en {_env.HealthTimeout.TotalSeconds:0} s "
                    + $"({pending.LastError ?? pending.State.ToString()}).{LogTail(plan, pending.Name)}";
            }

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(2))
            {
                log($"salud: esperando a {pending.Name} ({pending.State}, puerto {pending.Port})");
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

    private string BusyPortsMessage(IReadOnlyList<int> busy, RoostConfig config) =>
        BusyPortsText(busy, config) + ServiceHint(busy);

    private string BusyPortsText(IReadOnlyList<int> busy, RoostConfig config)
    {
        if (busy.Contains(config.HttpPort))
        {
            return $"El puerto {config.HttpPort} ya esta en uso en esta maquina{OwnedBy(config.HttpPort)} (Laragon, IIS u otro servidor web?). "
                + $"Paralo, o cambia [web] port en {paths.ConfigFile}.";
        }
        if (busy.Contains(config.HttpsPort))
        {
            return $"El puerto {config.HttpsPort} (HTTPS de los sitios .test) ya esta en uso en esta maquina{OwnedBy(config.HttpsPort)} (IIS, VMware, Skype u otro servidor?). "
                + $"Paralo, o cambia [web] https_port en {paths.ConfigFile}.";
        }
        if (busy.Contains(config.DbPort))
        {
            return $"El puerto {config.DbPort} ya esta en uso en esta maquina{OwnedBy(config.DbPort)} (otro MySQL o MariaDB, quiza un servicio de Windows?). "
                + $"Paralo, cambia [db] port en {paths.ConfigFile}, o arranca sin base de datos con: roost db use none";
        }
        if (config.RedisEnabled && busy.Contains(config.RedisPort))
        {
            return $"El puerto {config.RedisPort} (Redis) ya esta en uso en esta maquina{OwnedBy(config.RedisPort)} (otro Redis, Memurai?). "
                + $"Paralo, cambia [redis] port en {paths.ConfigFile} (roost redis port <puerto>), o quita Redis del stack con: roost redis disable";
        }
        if (config.MailEnabled && (busy.Contains(config.MailSmtpPort) || busy.Contains(config.MailWebPort)))
        {
            var (port, key) = busy.Contains(config.MailSmtpPort) ? (config.MailSmtpPort, "smtp_port") : (config.MailWebPort, "web_port");
            return $"El puerto {port} (Mailpit) ya esta en uso en esta maquina{OwnedBy(port)} (otro Mailpit o MailHog?). "
                + $"Paralo, cambia [mail] {key} en {paths.ConfigFile}, o quita Mailpit del stack con: roost mail disable";
        }
        return $"Los puertos {string.Join(", ", busy)} del pool de PHP ya estan en uso por otro proceso. "
            + $"Paralo, o baja [php] pool en {paths.ConfigFile}.";
    }

    private static string OwnedBy(int port) =>
        PortOwners.Find(port) is { } owner ? $" por {owner.ProcessName ?? "otro proceso"} (PID {owner.Pid})" : "";

    /// <summary>
    /// Si el que tiene el puerto es un servicio de Windows (el MySQL80 de su MSI, una MariaDB
    /// instalada aparte, IIS en el 80), cuál es y cómo dejarlo en Manual: es lo que suele pasar
    /// (E7). Roost no toca servicios: solo lo dice. Vacío si no es un servicio.
    /// </summary>
    private string ServiceHint(IReadOnlyList<int> busy)
    {
        var owners = busy.Select(PortOwners.Find).Where(o => o is not null).ToList();
        if (owners.Count == 0) return "";
        var services = WindowsServices.Find(_env.Runner);
        foreach (var owner in owners)
        {
            if (WindowsServices.OwnerOf(owner, services) is not { } service) continue;
            return $"\nEl puerto {owner!.Port} lo tiene el servicio de Windows {WindowsServices.Label(service)}, que {WindowsServices.StateText(service)}. "
                + $"Para que no se lo quede, ponlo en Manual en services.msc (o, en una consola de administrador: {WindowsServices.ManualCommand(service)}) y paralo.";
        }
        return "";
    }
}
