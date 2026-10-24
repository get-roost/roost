using Roost.Core.Services;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el supervisor con un lanzador FALSO: los "procesos" son TaskCompletionSource que el
/// test hace terminar cuando quiere. Nada arranca procesos reales.
/// </summary>
public class SupervisorTests
{
    private sealed class FakeProcess(int id, Action<FakeProcess> onKill) : IServiceProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;
        public int? ExitCode { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);

        public void Exit(int code = 0)
        {
            ExitCode = code;
            _exit.TrySetResult();
        }

        public void Kill()
        {
            onKill(this);
            Exit(-1);
        }

        public void Dispose() { }
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        private readonly object _lock = new();
        private int _nextId = 100;
        private readonly List<(string Name, FakeProcess Process)> _started = [];
        private readonly List<string> _killed = [];
        private readonly List<ServiceDefinition> _definitions = [];

        /// <summary>Si se fija, los procesos de ese servicio salen en cuanto arrancan.</summary>
        public string? ExitsImmediately { get; set; }

        public string? Throws { get; init; }

        /// <summary>Si un comando "X-shutdown" hace que el servicio X salga solo (como mysqladmin shutdown).</summary>
        public bool ShutdownWorks { get; init; } = true;

        public IServiceProcess Start(ServiceDefinition definition)
        {
            if (definition.Name == Throws) throw new FileNotFoundException("no existe");
            lock (_lock)
            {
                FakeProcess? process = null;
                process = new FakeProcess(_nextId++, p => { lock (_lock) _killed.Add(definition.Name); });
                _started.Add((definition.Name, process));
                _definitions.Add(definition);
                if (definition.Name == ExitsImmediately) process.Exit(255);

                if (definition.Name.EndsWith("-shutdown", StringComparison.Ordinal))
                {
                    var service = definition.Name[..^"-shutdown".Length];
                    if (ShutdownWorks) _started.Last(s => s.Name == service).Process.Exit(0);
                    process.Exit(0); // el comando de parada termina enseguida
                }
                return process;
            }
        }

        public int StartsOf(string name) { lock (_lock) return _started.Count(s => s.Name == name); }
        public FakeProcess Last(string name) { lock (_lock) return _started.Last(s => s.Name == name).Process; }
        public List<string> StartOrder() { lock (_lock) return _started.Select(s => s.Name).ToList(); }
        public List<string> KillOrder() { lock (_lock) return [.. _killed]; }

        /// <summary>La definición con la que se lanzó por última vez algo con ese nombre.</summary>
        public ServiceDefinition LastDefinition(string name) { lock (_lock) return _definitions.Last(d => d.Name == name); }
    }

    private static ServiceDefinition Service(string name) => new() { Name = name, Executable = name + ".exe" };

    // Una base de datos: se para pidiendole shutdown, con un plazo corto para el test.
    private static ServiceDefinition Database(string name) => new()
    {
        Name = name,
        Executable = OsNames.Exe("mysqld"),
        StopCommand = new ServiceDefinition { Name = name + "-shutdown", Executable = OsNames.Exe("mysqladmin") },
        StopTimeout = TimeSpan.FromMilliseconds(300),
    };

    [Fact]
    public async Task Stop_ConStopCommand_PideShutdown_NoLaMata_NiLaRelanza()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Database("mysql-8.4"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        await supervisor.StopAsync();
        await Task.Delay(100); // margen para que un relanzamiento indebido se viera

        Assert.Equal(["nginx"], launcher.KillOrder());   // la base de datos salio sola
        Assert.Equal(1, launcher.StartsOf("mysql-8.4-shutdown"));
        Assert.Equal(1, launcher.StartsOf("mysql-8.4"));  // su salida no se tomo por reciclaje
        Assert.All(supervisor.Status(), s => Assert.Equal(ServiceState.Stopped, s.State));
    }

    [Fact]
    public async Task Stop_SiNoSaleTrasElShutdown_SeMataAlAcabarElPlazo()
    {
        var launcher = new FakeLauncher { ShutdownWorks = false };
        var supervisor = new Supervisor(launcher);
        supervisor.Start([Database("mysql-8.4"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        await supervisor.StopAsync();

        Assert.Equal(["nginx", "mysql-8.4"], launcher.KillOrder());
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("La condicion no se cumplio en 5 s.");
            await Task.Delay(10);
        }
    }

    private static ServiceStatus StatusOf(Supervisor supervisor, string name) =>
        supervisor.Status().Single(s => s.Name == name);

    [Fact]
    public async Task Start_ArrancaTodosEnElOrdenRecibido()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher);

        supervisor.Start([Service("db"), Service("php"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        Assert.Equal(["db", "php", "nginx"], launcher.StartOrder());
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ProcesoQueSaleTrasVivirUnRato_SeRelanza_SinContarComoCaida()
    {
        // healthyUptime cero: toda salida es "normal", como el reciclaje de php-cgi.
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Service("php")]);
        await Until(() => launcher.StartsOf("php") == 1 && StatusOf(supervisor, "php").State == ServiceState.Running);

        launcher.Last("php").Exit(0);
        await Until(() => launcher.StartsOf("php") == 2 && StatusOf(supervisor, "php").State == ServiceState.Running);

        var status = StatusOf(supervisor, "php");
        Assert.Equal(1, status.Restarts);
        Assert.Null(status.LastError);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task CaidasRapidasSeguidas_MarcanFailed_YDejanDeRelanzar()
    {
        var launcher = new FakeLauncher { ExitsImmediately = "nginx" };
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.FromHours(1));

        supervisor.Start([Service("nginx")]);
        await Until(() => StatusOf(supervisor, "nginx").State == ServiceState.Failed);

        Assert.Equal(Supervisor.MaxQuickCrashes, launcher.StartsOf("nginx"));
        Assert.Contains("codigo 255", StatusOf(supervisor, "nginx").LastError);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task EjecutableQueNoArranca_MarcaFailed()
    {
        var launcher = new FakeLauncher { Throws = "php" };
        var supervisor = new Supervisor(launcher);

        supervisor.Start([Service("php")]);
        await Until(() => StatusOf(supervisor, "php").State == ServiceState.Failed);

        Assert.Contains("no existe", StatusOf(supervisor, "php").LastError);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Stop_MataEnOrdenInverso_YNoRelanzaNada()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Service("db"), Service("php"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        await supervisor.StopAsync();
        await Task.Delay(100); // margen para que un relanzamiento indebido se viera

        Assert.Equal(["nginx", "php", "db"], launcher.KillOrder());
        Assert.All(supervisor.Status(), s => Assert.Equal(ServiceState.Stopped, s.State));
        Assert.Equal(3, launcher.StartOrder().Count);
    }

    [Fact]
    public async Task Restart_RelanzaElServicio_SinContarloComoCaida()
    {
        // healthyUptime de una hora: una salida normal contaria como caida; un reinicio pedido, no.
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.FromHours(1), healthyUptime: TimeSpan.FromHours(1));
        supervisor.Start([Service("php-8.4-0"), Service("php-8.4-1"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        var count = await supervisor.RestartAsync(name => name.StartsWith("php-8.4-", StringComparison.Ordinal));
        await Until(() => launcher.StartsOf("php-8.4-0") == 2 && launcher.StartsOf("php-8.4-1") == 2
                          && supervisor.Status().All(s => s.State == ServiceState.Running));

        Assert.Equal(2, count);
        Assert.Equal(1, launcher.StartsOf("nginx"));
        var status = StatusOf(supervisor, "php-8.4-0");
        Assert.Equal(1, status.Restarts);
        Assert.Null(status.LastError);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Restart_ConStopCommand_PideShutdown_YNoLaMata()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, healthyUptime: TimeSpan.FromHours(1));
        supervisor.Start([Database("mysql-8.4")]);
        await Until(() => StatusOf(supervisor, "mysql-8.4").State == ServiceState.Running);

        await supervisor.RestartAsync(name => name == "mysql-8.4");

        Assert.Equal(1, launcher.StartsOf("mysql-8.4-shutdown"));
        Assert.Equal(2, launcher.StartsOf("mysql-8.4"));
        Assert.Empty(launcher.KillOrder());
        Assert.Equal(ServiceState.Running, StatusOf(supervisor, "mysql-8.4").State);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Stop_DeUnGrupo_LoParaLimpio_NoLoRelanza_YStart_LoVuelveAArrancar()
    {
        // healthyUptime cero: si el supervisor tomara la salida por reciclaje, lo relanzaria en el acto.
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Database("mysql-8.4"), Service("php-8.4-0"), Service("php-8.4-1"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        Assert.Equal(1, await supervisor.StopAsync(name => name == "mysql-8.4"));
        await Task.Delay(100); // margen para que un relanzamiento indebido se viera

        Assert.Equal(ServiceState.Stopped, StatusOf(supervisor, "mysql-8.4").State);
        Assert.Null(StatusOf(supervisor, "mysql-8.4").ProcessId);
        Assert.Equal(1, launcher.StartsOf("mysql-8.4-shutdown"));
        Assert.Equal(1, launcher.StartsOf("mysql-8.4"));   // parada limpia, y nadie la relanza
        Assert.Empty(launcher.KillOrder());
        Assert.Equal(ServiceState.Running, StatusOf(supervisor, "nginx").State);   // el resto sigue

        // Arrancarla de nuevo la deja como al principio: con bucle, vigilada.
        Assert.Equal(1, await supervisor.StartAsync(name => name == "mysql-8.4"));
        await Until(() => StatusOf(supervisor, "mysql-8.4").State == ServiceState.Running);
        Assert.Equal(2, launcher.StartsOf("mysql-8.4"));
        launcher.Last("mysql-8.4").Exit(0);   // reciclaje: se relanza, luego sigue vigilada
        await Until(() => launcher.StartsOf("mysql-8.4") == 3);

        // Start sobre uno en marcha no hace nada; el pool entero se para y arranca por su prefijo.
        Assert.Equal(1, await supervisor.StartAsync(name => name == "nginx"));
        Assert.Equal(1, launcher.StartsOf("nginx"));
        Assert.Equal(2, await supervisor.StopAsync(name => name.StartsWith("php-8.4-", StringComparison.Ordinal)));
        Assert.Equal(["php-8.4-1", "php-8.4-0"], launcher.KillOrder());   // en orden inverso
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Restart_DeUnServicioParadoAMano_LoArranca()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, healthyUptime: TimeSpan.FromHours(1));
        supervisor.Start([Service("nginx")]);
        await Until(() => StatusOf(supervisor, "nginx").State == ServiceState.Running);
        await supervisor.StopAsync(name => name == "nginx");
        Assert.Equal(ServiceState.Stopped, StatusOf(supervisor, "nginx").State);

        await supervisor.RestartAsync(name => name == "nginx");

        await Until(() => StatusOf(supervisor, "nginx").State == ServiceState.Running);
        Assert.Equal(2, launcher.StartsOf("nginx"));
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Restart_DeUnServicioFailed_LoVuelveALanzar()
    {
        var launcher = new FakeLauncher { ExitsImmediately = "nginx" };
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.FromHours(1));
        supervisor.Start([Service("nginx")]);
        await Until(() => StatusOf(supervisor, "nginx").State == ServiceState.Failed);

        launcher.ExitsImmediately = null; // ya se ha arreglado lo que lo tiraba
        await supervisor.RestartAsync(name => name == "nginx");

        await Until(() => StatusOf(supervisor, "nginx").State == ServiceState.Running);
        Assert.Equal(Supervisor.MaxQuickCrashes + 1, launcher.StartsOf("nginx"));
        await supervisor.StopAsync();
    }

    // E2: añadir, quitar y sustituir servicios con el supervisor en marcha

    [Fact]
    public async Task Add_ConElSupervisorEnMarcha_LoArrancaYLoVigila_YArrange_LoPoneEnSuSitio()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Service("db"), Service("php-8.4-0"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        supervisor.Add(Service("php-7.4-0"));

        Assert.Equal(ServiceState.Running, StatusOf(supervisor, "php-7.4-0").State);
        Assert.Equal(["db", "php-8.4-0", "nginx", "php-7.4-0"], supervisor.Status().Select(s => s.Name));
        Assert.Throws<InvalidOperationException>(() => supervisor.Add(Service("nginx")));
        Assert.Equal(1, launcher.StartsOf("nginx"));   // el resto, sin tocar

        // Vigilado como los demás: una salida tras vivir un rato se relanza.
        launcher.Last("php-7.4-0").Exit(0);
        await Until(() => launcher.StartsOf("php-7.4-0") == 2 && StatusOf(supervisor, "php-7.4-0").State == ServiceState.Running);

        // En el orden del plan, que es también el de parada al revés.
        supervisor.Arrange(["db", "php-8.4-0", "php-7.4-0", "nginx"]);
        Assert.Equal(["db", "php-8.4-0", "php-7.4-0", "nginx"], supervisor.Status().Select(s => s.Name));
        await supervisor.StopAsync();
        Assert.Equal(["nginx", "php-7.4-0", "php-8.4-0", "db"], launcher.KillOrder());
    }

    [Fact]
    public async Task Remove_LoParaLimpio_YYaNoSaleEnElEstado_NiSeRelanza()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.Zero);
        supervisor.Start([Database("mysql-8.4"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));

        Assert.True(await supervisor.RemoveAsync("mysql-8.4"));
        await Task.Delay(100); // margen para que un relanzamiento indebido se viera

        Assert.Equal(1, launcher.StartsOf("mysql-8.4-shutdown"));   // limpia, con su comando de parada
        Assert.Equal(1, launcher.StartsOf("mysql-8.4"));
        Assert.Empty(launcher.KillOrder());
        Assert.Equal(["nginx"], supervisor.Status().Select(s => s.Name));
        Assert.False(await supervisor.RemoveAsync("redis"));
        Assert.Equal(1, launcher.StartsOf("nginx"));
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Replace_ParaConLaDefinicionVieja_YArrancaLaNueva_EnElMismoSitio()
    {
        var launcher = new FakeLauncher();
        var supervisor = new Supervisor(launcher, restartDelay: TimeSpan.Zero, healthyUptime: TimeSpan.FromHours(1));
        var old = Database("mysql-8.4") with
        {
            StopCommand = new ServiceDefinition { Name = "mysql-8.4-shutdown", Executable = OsNames.Exe("mysqladmin"), Arguments = "--port=3306 shutdown" },
        };
        supervisor.Start([old, Service("php-8.4-0"), Service("nginx")]);
        await Until(() => supervisor.Status().All(s => s.State == ServiceState.Running));
        var replacement = old with
        {
            Arguments = "--port=3307",
            StopCommand = new ServiceDefinition { Name = "mysql-8.4-shutdown", Executable = OsNames.Exe("mysqladmin"), Arguments = "--port=3307 shutdown" },
        };

        Assert.True(await supervisor.ReplaceAsync(replacement));

        // La vieja se para con SU comando (el puerto de la que corre) y la nueva arranca en su sitio.
        Assert.Equal("--port=3306 shutdown", launcher.LastDefinition("mysql-8.4-shutdown").Arguments);
        Assert.Equal("--port=3307", launcher.LastDefinition("mysql-8.4").Arguments);
        Assert.Equal(["mysql-8.4", "php-8.4-0", "nginx"], supervisor.Status().Select(s => s.Name));
        Assert.Equal(ServiceState.Running, StatusOf(supervisor, "mysql-8.4").State);
        Assert.Equal(0, StatusOf(supervisor, "mysql-8.4").Restarts);
        Assert.Equal(1, launcher.StartsOf("php-8.4-0"));
        Assert.Equal(1, launcher.StartsOf("nginx"));
        Assert.False(await supervisor.ReplaceAsync(Service("redis")));

        // Y la nueva se para con el suyo.
        await supervisor.StopAsync();
        Assert.Equal("--port=3307 shutdown", launcher.LastDefinition("mysql-8.4-shutdown").Arguments);
    }

    [Fact]
    public void ServiceDefinition_EsIgualPorContenido_TambienElEntornoYElComandoDeParada()
    {
        static ServiceDefinition Php(string scanDir) => new()
        {
            Name = "php-8.4-0",
            Executable = OsNames.Exe("php-cgi"),
            Arguments = "-b 127.0.0.1:9840",
            Environment = new Dictionary<string, string> { ["PHP_FCGI_MAX_REQUESTS"] = "500", ["PHP_INI_SCAN_DIR"] = scanDir },
            Port = 9840,
            StopCommand = new ServiceDefinition { Name = "x", Executable = "x.exe", Environment = new Dictionary<string, string> { ["A"] = scanDir } },
        };

        // Cada up crea diccionarios nuevos: con lo mismo dentro, es el mismo servicio.
        Assert.Equal(Php("conf.d"), Php("conf.d"));
        Assert.True(Php("conf.d") == Php("conf.d"));
        Assert.Equal(Php("conf.d").GetHashCode(), Php("conf.d").GetHashCode());
        Assert.NotEqual(Php("conf.d"), Php("otra"));
        Assert.NotEqual(Php("conf.d"), Php("conf.d") with { Port = 9841 });
        Assert.NotEqual(Php("conf.d"), Php("conf.d") with { StopCommand = null });
    }
}
