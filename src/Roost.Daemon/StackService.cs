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
/// quien ejecuta <c>nginx -t</c> (y <c>nginx -s reload</c>), las sondas de puertos, si la máquina
/// tiene IPv6 en loopback y, si se da, cómo se recarga en caliente el servidor web (si no,
/// <see cref="WebReload"/>: el evento de Apache no se puede probar sin un Apache de verdad).
/// </summary>
internal sealed record StackEnvironment(
    IProcessLauncher Launcher,
    IProcessRunner Runner,
    Func<IEnumerable<int>, IReadOnlyList<int>> BusyPorts,
    Func<int, CancellationToken, Task<bool>> AcceptsConnections,
    TimeSpan HealthTimeout,
    bool Ipv6,
    Func<ServiceDefinition, int?, string?>? ReloadWeb = null)
{
    // Solo se espera lo necesario; el margen es para el primer arranque de MySQL 8 tras crear el datadir.
    public static StackEnvironment Real => new(
        new OsProcessLauncher(), ProcessRunner.Default, StackPlanner.BusyPorts, StackPlanner.AcceptsConnectionsAsync,
        TimeSpan.FromSeconds(45), Loopback.HasIpv6);

    /// <summary>Recarga en caliente el servidor web <paramref name="web"/> (su proceso es <paramref name="pid"/>): null si ha ido bien, o por qué no.</summary>
    public string? Reload(ServiceDefinition web, int? pid) => ReloadWeb is { } reload ? reload(web, pid) : WebReload.Run(Runner, web, pid);
}

/// <summary>
/// El stack que mantiene vivo el daemon: un <see cref="Supervisor"/> con el plan actual. Las
/// operaciones se serializan. Un <c>up</c> con el stack en marcha prepara y valida la
/// configuración nueva ANTES de tocar nada: si no vale (un puerto ocupado, un vhost sin marcas,
/// un <c>nginx -t</c> que falla), lo de antes sigue en marcha tal cual (E1). Si vale, solo cambia
/// lo que cambia (E2, <see cref="ReloadPlan"/>): un sitio nuevo recarga el servidor web en
/// caliente, un PHP nuevo arranca su pool, un php.ini cambiado reinicia ese pool uno a uno, y la
/// base de datos, Redis o Mailpit solo se reinician si cambia lo suyo.
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
    // La huella de lo que leyó cada servicio al arrancar o recargarse (ServiceInputs): con la misma
    // definición, si cambia, hay que reiniciarlo o recargarlo.
    private Dictionary<string, string> _inputs = new(StringComparer.Ordinal);

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
    /// Arranca el stack o, si ya está en marcha, le aplica la configuración actual. No depende de
    /// que el cliente siga conectado: un Ctrl+C en la CLI no puede dejarlo a medias. Lo que se
    /// arranca y no llega a quedar como stack actual se para siempre, así que nunca quedan procesos
    /// sin dueño.
    ///
    /// Con el stack en marcha, primero se prepara y valida lo nuevo (<c>Prepare</c>, el
    /// <c>-t</c> del servidor web, los puertos que no sean ya nuestros, el DNS local si lo pide
    /// la configuración y no estaba). Como <c>Prepare</c> regenera en su sitio ficheros que lee el
    /// stack (<see cref="GeneratedConfig"/>), se hace una foto antes y se devuelve si el plan se
    /// rechaza. Si todo vale, solo se toca lo que cambia (<see cref="ApplyAsync"/>).
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
            ServiceDefinition? webStoppedFirst = null;
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
                // exclusiva: pasar de Apache a nginx no se puede validar con Apache en marcha. Esos
                // puertos son suyos, así que se para solo el servidor web de antes (la base de datos y
                // PHP siguen) y se valida otra vez; si no vale, vuelve a arrancar con sus ficheros.
                log($"el -t no puede abrir puertos que tiene {running.WebServer}: se para antes de validar");
                before.Restore();
                webStoppedFirst = running.Services.First(s => s.Name == running.WebServer);
                await _supervisor!.RemoveAsync(running.WebServer);
                try
                {
                    plan = Prepare();
                    var busy = _env.BusyPorts(plan.Ports.Except(running.Ports));
                    if (busy.Count > 0)
                        throw new RoostException(BusyPortsMessage(busy, config));
                    if (config.UsesLocalDns && _dns is null)
                    {
                        await SetDnsAsync(true);
                        dnsStartedHere = true;
                    }
                }
                catch (RoostException again)
                {
                    before.Restore();
                    var back = await RestoreWebAsync(webStoppedFirst, running);
                    throw new RoostException(back is null
                        ? $"No se ha aplicado el cambio: {again.Message}\nPara validarlo hubo que parar un momento {running.WebServer} (tenia sus puertos); ya vuelve a estar en marcha, con la configuracion anterior."
                        : $"No se ha aplicado el cambio: {again.Message}\nPara validarlo hubo que parar {running.WebServer} (tenia sus puertos), y no ha vuelto a arrancar: {back}");
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

            // Con el stack en marcha, solo lo que cambia (E2).
            if (wasRunning)
                return await ApplyAsync(plan, before, dnsStartedHere, webStoppedFirst, config);

            await SetDnsAsync(config.UsesLocalDns);
            _dnsWanted = config.UsesLocalDns;

            starting = new Supervisor(_env.Launcher, log);
            starting.Start(plan.Services);
            var problem = await WaitHealthyAsync(starting, plan);
            if (problem is not null)
                throw new RoostException(problem);

            _supervisor = starting;
            _plan = plan;
            _since = DateTimeOffset.Now;
            _inputs = ServiceInputs.Of(paths, plan);
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
    /// E2, la recarga selectiva: aplica <paramref name="plan"/> (ya validado) al stack en marcha
    /// tocando solo lo que cambia, en el orden de <see cref="ReloadPlan"/>:
    /// <list type="number">
    /// <item>los pools de PHP nuevos, antes de tocar nada: si no responden, se quitan, vuelven los
    /// ficheros de antes y el cambio se rechaza con lo de antes en marcha (como un plan que no valida);</item>
    /// <item>desde ahí no hay vuelta atrás: lo que estaba parado a mano y sigue igual se arranca (un
    /// <c>up</c> lo arranca todo), y la base de datos, Redis, Mailpit y los de las extensiones que
    /// cambian se paran con su definición de antes y arrancan con la nueva;</item>
    /// <item>los pools de PHP que cambian, uno a uno;</item>
    /// <item>el servidor web: recargado en caliente si solo cambian sus ficheros;</item>
    /// <item>los pools que sobran, cuando ya nadie les manda peticiones.</item>
    /// </list>
    /// Si algo de lo que va después del primer paso no arranca, el resto queda aplicado y en marcha,
    /// la respuesta lo dice, y el siguiente <c>up</c> lo vuelve a intentar.
    /// </summary>
    private async Task<CommandResponse> ApplyAsync(StackPlan plan, GeneratedConfig before, bool dnsStartedHere, ServiceDefinition? webStoppedFirst, RoostConfig config)
    {
        var supervisor = _supervisor!;
        var running = _plan!;
        // Lo que leerá cada servicio: los ficheros ya están como los deja este plan.
        var inputs = ServiceInputs.Of(paths, plan);
        // Se compara con lo que corre de verdad (las definiciones del supervisor), no con el plan de
        // antes: un php-cgi que un up a medias no llegó a sustituir sigue con la suya.
        var reload = ReloadPlan.Compute(supervisor.Definitions(), running.WebServer, _inputs, plan.Services, plan.WebServer, inputs);
        var done = new List<string>();
        var pending = new HashSet<string>(StringComparer.Ordinal);   // php-cgi que no se llegaron a reiniciar: guardan su huella vieja

        // 1. Lo que solo suma: los pools nuevos, antes de tocar nada.
        if (reload.AddPhp.Count > 0)
        {
            foreach (var service in reload.AddPhp) supervisor.Add(service);
            var added = reload.AddPhp.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            if (await WaitHealthyAsync(supervisor, plan, added.Contains) is { } problem)
            {
                foreach (var service in reload.AddPhp) await supervisor.RemoveAsync(service.Name);
                before.Restore();
                if (dnsStartedHere) await SetDnsAsync(false);
                var web = webStoppedFirst is null ? null : await RestoreWebAsync(webStoppedFirst, running);
                throw new RoostException($"No se ha aplicado el cambio: {problem}\nSigue en marcha lo de antes, con la configuracion anterior."
                    + (web is null ? "" : $"\nSalvo {running.WebServer}, que se paro para validar y no ha vuelto a arrancar: {web}"));
            }
        }

        // Punto sin retorno.
        await SetDnsAsync(config.UsesLocalDns);
        _dnsWanted = config.UsesLocalDns;

        // Lo que sigue igual pero estaba parado a mano (o dejó de relanzarse): up lo arranca todo.
        var stopped = supervisor.Status()
            .Where(s => reload.Unchanged.Contains(s.Name) && s.State is ServiceState.Stopped or ServiceState.Failed)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (stopped.Count > 0)
        {
            await supervisor.StartAsync(stopped.Contains);
            done.AddRange(stopped.Select(ReloadPlan.Pool).Distinct().Select(name => $"arrancado {name} (estaba parado)"));
        }

        // 2. La base de datos, Redis, Mailpit y los de las extensiones que se van o cambian: lo viejo se
        //    para con sus ficheros de antes (la parada limpia de la base de datos lee el puerto de su
        //    my.ini) y después se arranca lo nuevo, que puede querer el mismo puerto.
        if (reload.Stop.Count > 0)
        {
            var after = GeneratedConfig.Snapshot(paths);
            before.Restore();
            try
            {
                for (var i = reload.Stop.Count - 1; i >= 0; i--)
                    await supervisor.RemoveAsync(reload.Stop[i].Name);
            }
            finally
            {
                after.Restore();
            }
        }
        foreach (var service in reload.Start) supervisor.Add(service);
        done.AddRange(reload.DescribeOthers());

        // 3. Los pools que cambian (su php.ini, el conf.d, otra versión de la rama): uno a uno.
        string? phpProblem = null;
        foreach (var pool in reload.RestartPhp.GroupBy(r => ReloadPlan.Pool(r.Service.Name)))
        {
            var (problem, untouched) = await RestartOneByOneAsync(supervisor, plan, pool.ToList());
            if (problem is null)
            {
                done.Add($"reiniciado {pool.Key} (uno a uno)");
                continue;
            }
            phpProblem ??= problem;
            // Los que no se han tocado siguen con lo de antes: el próximo up los reinicia.
            pending.UnionWith(untouched);
        }

        // 4. El servidor web.
        switch (reload.Web)
        {
            case WebChange.Reload:
                done.Add(await ReloadWebAsync(supervisor, reload.NewWeb!));
                break;
            case WebChange.Restart:
                await supervisor.ReplaceAsync(reload.NewWeb!);
                done.Add(reload.DescribeWeb()!);
                break;
            case WebChange.Switch:
            case WebChange.Start:
                if (reload.OldWeb is { } oldWeb) await supervisor.RemoveAsync(oldWeb.Name);
                supervisor.Add(reload.NewWeb!);
                done.Add(webStoppedFirst is { } stoppedWeb && stoppedWeb.Name != reload.NewWeb!.Name
                    ? $"cambiado {stoppedWeb.Name} por {reload.NewWeb.Name}"
                    : reload.DescribeWeb()!);
                break;
        }

        // 5. Los pools que sobran: el servidor web ya no les manda nada.
        for (var i = reload.RemovePhp.Count - 1; i >= 0; i--)
            await supervisor.RemoveAsync(reload.RemovePhp[i].Name);
        done.AddRange(reload.DescribePhp(reload.AddPhp, "arrancado"));
        done.AddRange(reload.DescribePhp(reload.RemovePhp, "quitado"));

        supervisor.Arrange(plan.Services.Select(s => s.Name).ToList());
        _plan = plan;
        foreach (var name in pending) inputs[name] = _inputs.GetValueOrDefault(name, "");
        _inputs = inputs;

        log(done.Count == 0 ? "up: nada que cambiar en lo que esta en marcha" : "up: " + string.Join("; ", done));
        // Que todo responda, también el HTTPS de los sitios (lo abre una recarga si es el primero).
        var unhealthy = phpProblem ?? await WaitHealthyAsync(supervisor, plan, alsoPorts: plan.Sites.Count > 0 ? [plan.HttpsPort] : []);
        if (unhealthy is not null)
        {
            log("up a medias: " + unhealthy);
            return new CommandResponse(false, $"{unhealthy}\nEl resto del cambio se ha aplicado y sigue en marcha.", Status()) { Applied = done };
        }
        return new CommandResponse(true, null, Status()) { Applied = done };
    }

    /// <summary>
    /// Vuelve a arrancar el servidor web que se paró para poder validar (<c>FailsOnlyOnOwnPorts</c>),
    /// con sus ficheros de antes ya en su sitio. null si responde; si no, por qué.
    /// </summary>
    private async Task<string?> RestoreWebAsync(ServiceDefinition web, StackPlan running)
    {
        var supervisor = _supervisor!;
        if (supervisor.Status().Any(s => s.Name == web.Name)) return null;
        supervisor.Add(web);
        supervisor.Arrange(running.Services.Select(s => s.Name).ToList());
        return await WaitHealthyAsync(supervisor, running, name => name == web.Name);
    }

    /// <summary>
    /// Reinicia los php-cgi de un pool uno a uno (con su definición nueva si la hay): cada uno tiene
    /// que estar otra vez en marcha y aceptando conexiones antes de tocar el siguiente, así siempre
    /// queda alguno atendiendo. La petición que pilla a uno reiniciándose pasa a otro (nginx con
    /// <c>max_fails=0</c>, Apache con <c>retry=0</c>; probado en real con carga: ningún 502). Si uno
    /// no vuelve, se para ahí, y los que faltan siguen con lo de antes. null si ha ido bien.
    /// </summary>
    private async Task<(string? Problem, IReadOnlyList<string> Untouched)> RestartOneByOneAsync(
        Supervisor supervisor, StackPlan plan, IReadOnlyList<(ServiceDefinition Service, bool Replace)> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var (service, replace) = members[i];
            var oldPid = supervisor.Status().FirstOrDefault(s => s.Name == service.Name)?.ProcessId;
            if (replace) await supervisor.ReplaceAsync(service);
            else await supervisor.RestartAsync(name => name == service.Name);
            if (await WaitReadyAsync(supervisor, plan, service.Name, oldPid) is { } problem)
            {
                log($"{service.Name}: no vuelve tras reiniciarlo; los demas de {ReloadPlan.Pool(service.Name)} siguen como estaban");
                return (problem, members.Skip(i + 1).Select(m => m.Service.Name).ToList());
            }
        }
        return (null, []);
    }

    /// <summary>Espera a que <paramref name="name"/> esté en marcha con otro proceso que el de <paramref name="oldPid"/> y acepte conexiones.</summary>
    private async Task<string?> WaitReadyAsync(Supervisor supervisor, StackPlan plan, string name, int? oldPid)
    {
        var deadline = DateTime.UtcNow + _env.HealthTimeout;
        while (true)
        {
            var status = supervisor.Status().FirstOrDefault(s => s.Name == name);
            if (status is null) return $"{name} ya no esta en el stack.";
            if (status.State == ServiceState.Failed)
                return $"{name} no arranca ({status.LastError}).{LogTail(plan, name)}";
            if (status.State == ServiceState.Running && status.ProcessId is { } pid && pid != oldPid
                && (status.Port is not { } port || await _env.AcceptsConnections(port, CancellationToken.None)))
                return null;
            if (DateTime.UtcNow >= deadline)
                return $"{name} no responde en {_env.HealthTimeout.TotalSeconds:0} s ({status.LastError ?? status.State.ToString()}).{LogTail(plan, name)}";
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Recarga en caliente el servidor web en marcha (<see cref="StackEnvironment.Reload"/>); si no
    /// se puede, lo reinicia, que también lee lo nuevo. Si estaba parado, lo arranca. Devuelve lo
    /// hecho en palabras.
    /// </summary>
    private async Task<string> ReloadWebAsync(Supervisor supervisor, ServiceDefinition web)
    {
        var status = supervisor.Status().FirstOrDefault(s => s.Name == web.Name);
        if (status is not { State: ServiceState.Running, ProcessId: { } pid })
        {
            await supervisor.RestartAsync(name => name == web.Name);
            return $"arrancado {web.Name}";
        }

        if (_env.Reload(web, pid) is not { } error)
        {
            log($"{web.Name}: recargado en caliente (pid {pid})");
            return $"recargado {web.Name}";
        }
        log($"{web.Name}: no se pudo recargar en caliente ({error}); se reinicia");
        await supervisor.RestartAsync(name => name == web.Name);
        return $"reiniciado {web.Name} (no se pudo recargar: {error})";
    }

    /// <summary>
    /// Reinicia un servicio o un grupo sin tocar el resto: <c>nginx</c>, <c>php-8.4</c> (todo
    /// su pool) o <c>mariadb-11.4</c>. El pool de un PHP va uno a uno, sin dejar de atender (lo usan
    /// <c>roost php ext/ini/xdebug</c> y la pantalla PHP). Uno parado a mano se arranca. No regenera
    /// la configuración: para eso está <c>up</c>.
    /// </summary>
    public async Task<CommandResponse> RestartAsync(string service)
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor || _plan is not { } plan)
                return new CommandResponse(false, "El stack no esta en marcha.", Status());

            if (service == DnsService)
            {
                if (!_dnsWanted) return new CommandResponse(false, DnsNotWanted, Status());
                await SetDnsAsync(false);
                await SetDnsAsync(true);
                log("reinicio pedido: dns");
                return new CommandResponse(true, null, Status());
            }

            var members = supervisor.Definitions().Where(d => Matches(service)(d.Name)).ToList();
            if (members.Count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en el stack.", Status());

            if (members.All(m => ReloadPlan.IsPhpMember(m.Name)))
            {
                var (problem, _) = await RestartOneByOneAsync(supervisor, plan, members.Select(m => (m, false)).ToList());
                Remember(plan, members);
                log($"reinicio pedido: {service} ({members.Count} php-cgi, uno a uno)");
                return problem is null
                    ? new CommandResponse(true, null, Status()) { Applied = [$"reiniciado {service} (uno a uno)"] }
                    : new CommandResponse(false, problem, Status());
            }

            await supervisor.RestartAsync(Matches(service));
            Remember(plan, members);
            log($"reinicio pedido: {service} ({members.Count} proceso(s))");
            return new CommandResponse(true, null, Status()) { Applied = [$"reiniciado {service}"] };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Recarga un servicio sin cortar (el botón «Recargar» de Servidor web): el servidor web lee otra
    /// vez su configuración en caliente, después de validarla con su <c>-t</c> (si no vale, se queda
    /// con la que tenía y se dice por qué). El pool de un PHP se reinicia uno a uno, y el resto se
    /// reinicia, como <see cref="RestartAsync"/>. No regenera nada: lee los ficheros como estén.
    /// </summary>
    public async Task<CommandResponse> ReloadServiceAsync(string service)
    {
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is not { } supervisor || _plan is not { } plan)
                return new CommandResponse(false, "El stack no esta en marcha.", Status());
            if (service == plan.WebServer && supervisor.Definitions().FirstOrDefault(d => d.Name == service) is { } definition)
            {
                var apache = service == RoostConfig.WebApache;
                var test = _env.Runner.Run(definition.Executable, "-t " + definition.Arguments, TimeSpan.FromSeconds(15));
                if (test is null || !test.Contains(apache ? "Syntax OK" : "test is successful", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResponse(false,
                        $"{(apache ? "Apache" : "nginx")} no acepta la configuracion, asi que sigue con la que tenia:\n{test?.Trim() ?? "el -t no respondio"}", Status());
                }

                var done = await ReloadWebAsync(supervisor, definition);
                Remember(plan, [definition]);
                log($"recarga pedida: {done}");
                return new CommandResponse(true, null, Status()) { Applied = [done] };
            }
        }
        finally
        {
            _gate.Release();
        }

        // Lo que no es el servidor web no tiene recarga en caliente: se reinicia (un pool, uno a uno).
        return await RestartAsync(service);
    }

    /// <summary>Guarda la huella de lo que acaban de leer estos servicios (reiniciados o recargados).</summary>
    private void Remember(StackPlan plan, IEnumerable<ServiceDefinition> services)
    {
        foreach (var service in services)
            _inputs[service.Name] = ServiceInputs.Fingerprint(paths, plan, service);
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
            Remember(plan, supervisor.Definitions());
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

            // Lo que estaba parado lee los ficheros al arrancar: su huella pasa a ser la de ahora.
            var stopped = supervisor.Status().Where(s => Matches(service)(s.Name) && s.State is ServiceState.Stopped or ServiceState.Failed).Select(s => s.Name).ToHashSet();
            var count = await supervisor.StartAsync(Matches(service));
            if (count == 0)
                return new CommandResponse(false, $"No hay ningun servicio '{service}' en el stack.", Status());
            if (_plan is { } plan) Remember(plan, supervisor.Definitions().Where(d => stopped.Contains(d.Name)));

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
    /// (<see cref="LocalCa.EnsureSiteCert"/> decide) y, si ha cambiado alguno, recarga en caliente
    /// el servidor web para que lo cargue, sin cortar nada. Devuelve cuántos ha renovado. Nunca lanza.
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

            log($"certificados renovados: {string.Join(", ", renewed)}; se recarga {plan.WebServer} para que los cargue");
            if (supervisor.Definitions().FirstOrDefault(d => d.Name == plan.WebServer) is { } web)
            {
                await ReloadWebAsync(supervisor, web);
                Remember(plan, [web]);
            }
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
        _inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        log("stack parado");
    }

    /// <summary>
    /// Espera a que todos los servicios (o los que cumplan <paramref name="only"/>) estén vivos y
    /// aceptando conexiones en su puerto, y también <paramref name="alsoPorts"/> (el HTTPS de los
    /// sitios tras una recarga). Si alguno falla o no llega a tiempo, devuelve el motivo con el
    /// final de su log.
    /// </summary>
    private async Task<string?> WaitHealthyAsync(Supervisor supervisor, StackPlan plan, Func<string, bool>? only = null, IReadOnlyList<int>? alsoPorts = null)
    {
        var started = DateTime.UtcNow;
        var deadline = started + _env.HealthTimeout;
        var lastReport = started;
        while (true)
        {
            var statuses = supervisor.Status().Where(s => only?.Invoke(s.Name) ?? true).ToList();
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
            int? pendingPort = null;
            if (pending is null)
            {
                foreach (var port in alsoPorts ?? [])
                {
                    if (await _env.AcceptsConnections(port, CancellationToken.None)) continue;
                    pendingPort = port;
                    break;
                }
            }
            if (pending is null && pendingPort is null)
            {
                log($"salud: {(only is null ? "todo responde" : "responde")} en {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                return null;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return pending is null
                    ? $"{plan.WebServer} no responde en el puerto {pendingPort} en {_env.HealthTimeout.TotalSeconds:0} s.{LogTail(plan, plan.WebServer)}"
                    : $"{pending.Name} no responde en {_env.HealthTimeout.TotalSeconds:0} s "
                      + $"({pending.LastError ?? pending.State.ToString()}).{LogTail(plan, pending.Name)}";
            }

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(2))
            {
                log(pending is null
                    ? $"salud: esperando al puerto {pendingPort} de {plan.WebServer}"
                    : $"salud: esperando a {pending.Name} ({pending.State}, puerto {pending.Port})");
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
