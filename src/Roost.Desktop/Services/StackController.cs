using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Daemon;

namespace Roost.Desktop.Services;

/// <summary>Un puerto que hace falta y tiene otro proceso, para el diálogo de arranque fallido.</summary>
public sealed record BusyPort(int Port, string Role, PortOwner? Owner, int? Suggestion, string ConfigKey);

/// <summary>Por qué no arrancó el stack: el mensaje del daemon y, si los hay, los puertos ocupados.</summary>
/// <param name="WasRunning">El stack estaba en marcha: se aplicaba un cambio.</param>
/// <param name="StillRunning">
/// Y sigue en marcha, con la configuración de antes: el daemon rechazó el cambio antes de parar
/// nada. false con <paramref name="WasRunning"/> = lo nuevo no arrancó y el stack ha quedado parado.
/// </param>
public sealed record StartFailure(string Title, string Message, IReadOnlyList<BusyPort> BusyPorts, bool WasRunning = false, bool StillRunning = false);

/// <summary>
/// Estado del stack tal como lo cuenta roostd, y las órdenes de arrancar, parar y aplicar
/// cambios. Hace lo mismo que <c>roost up</c> / <c>down</c>: las comprobaciones rápidas antes
/// de lanzar nada, instalar nginx la primera vez y pedirle el resto al daemon.
/// </summary>
public sealed partial class StackController(AppServices app) : ObservableObject
{
    private int _refreshing;

    /// <summary>Lo último que ha contado el daemon; null si no está en marcha.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDaemonUp))]
    public partial StatusResponse? Status { get; private set; }

    /// <summary>Arrancando, parando o aplicando: los botones se desactivan.</summary>
    [ObservableProperty]
    public partial bool Busy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>
    /// Lo que hizo el daemon en el último arranque o cambio aplicado con el stack en marcha
    /// («recargado nginx», «arrancado php-7.4»): solo toca lo que cambia. Vacío si no tocó nada.
    /// </summary>
    public IReadOnlyList<string> LastApplied { get; private set; } = [];

    public bool IsRunning => Status?.Running == true;

    public bool IsDaemonUp => Status is not null;

    /// <summary>Pregunta al daemon. Si ya hay una pregunta en curso, no lanza otra.</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            // Cada sondeo (3 s) trae un objeto nuevo aunque no cambie nada: solo se asigna si cambia
            // el contenido. Si no, la bandeja y las pantallas releerían el disco cada vez.
            var status = await app.Daemon.TryStatusAsync();
            if (!SameContent(Status, status)) Status = status;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Para los tests de vistas: un estado del daemon sin daemon.</summary>
    internal void Show(StatusResponse? status) => Status = status;

    private static bool SameContent(StatusResponse? a, StatusResponse? b) =>
        a is null || b is null ? a is null && b is null
            : System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    /// <summary>Arranca (o rearranca con la configuración actual) el stack. null si ha ido bien.</summary>
    public Task<StartFailure?> StartAsync(IProgress<string>? progress = null) =>
        RunBusyAsync("Arrancando...", () => StartCoreAsync(progress));

    /// <summary>
    /// Si el stack está en marcha, le aplica un cambio de configuración: el daemon solo toca lo que
    /// cambia (recarga el servidor web para un sitio, arranca o reinicia el pool de un PHP, la base de
    /// datos solo si cambia lo suyo) y lo cuenta en <see cref="LastApplied"/>.
    /// </summary>
    public async Task<StartFailure?> ApplyIfRunningAsync()
    {
        await RefreshAsync();
        if (!IsRunning) return null;
        var failure = await RunBusyAsync("Aplicando cambios...", () => StartCoreAsync(null));
        return failure is null ? null : failure with { WasRunning = true };
    }

    public Task StopAsync() => RunBusyAsync<object?>("Parando...", async () =>
    {
        if (await app.Daemon.TryStatusAsync() is not null)
            Status = (await app.Daemon.DownAsync()).Status;
        return null;
    });

    private async Task<T> RunBusyAsync<T>(string text, Func<Task<T>> action)
    {
        Busy = true;
        BusyText = text;
        try
        {
            return await action();
        }
        finally
        {
            Busy = false;
            BusyText = null;
            await RefreshAsync();
        }
    }

    private async Task<StartFailure?> StartCoreAsync(IProgress<string>? progress)
    {
        var paths = app.Paths;
        try
        {
            // Lo mismo que comprueba roost up antes de lanzar nada.
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var php = planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled());
            planner.ResolveDb(config, new DbInstaller(paths).ListInstalled());

            await EnsureWebServerAsync(config.WebServer, progress);

            progress?.Report($"Arrancando PHP {php.Name} y {(config.UsesApache ? "Apache" : "nginx")}...");
            await app.Daemon.EnsureRunningAsync(DaemonLauncher.FromDirectory(AppServices.AppDirectory));
            var response = await app.Daemon.UpAsync();
            Status = response.Status;
            LastApplied = response.Applied;
            return response.Ok ? null : Failure(response.Error ?? "El daemon no ha dicho por qué.", config, response.Status, response.Applied.Count > 0);
        }
        catch (RoostException ex)
        {
            return new StartFailure("No se pudo arrancar Roost", ex.Message, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new StartFailure("No se pudo arrancar Roost", ex.Message, []);
        }
    }

    /// <summary>El servidor web de <c>[web] server</c>, instalado la primera vez que hace falta.</summary>
    private async Task EnsureWebServerAsync(string server, IProgress<string>? progress)
    {
        if (server == RoostConfig.WebApache)
        {
            var installer = new ApacheInstaller(app.Paths, app.Http);
            var build = ApacheCatalog.Windows;
            if (installer.Find(build) is not null) return;
            progress?.Report($"Instalando Apache {build.Version} (la primera vez)...");
            await installer.InstallAsync(build, new Progress<long>(b => progress?.Report($"Instalando Apache {build.Version}... {b / 1048576.0:0.0} MB")));
        }
        else
        {
            var installer = new NginxInstaller(app.Paths, app.Http);
            var build = NginxCatalog.Windows;
            if (installer.Find(build) is not null) return;
            progress?.Report($"Instalando nginx {build.Version} (la primera vez)...");
            await installer.InstallAsync(build, new Progress<long>(b => progress?.Report($"Instalando nginx {build.Version}... {b / 1048576.0:0.0} MB")));
        }
    }

    /// <param name="partial">El daemon aplicó parte del cambio (lo demás sigue en marcha) y algo de lo nuevo no arrancó.</param>
    private StartFailure Failure(string error, RoostConfig config, StatusResponse status, bool partial = false)
    {
        // Si el daemon rechazó el cambio, lo de antes sigue en marcha con sus puertos: esos son
        // nuestros, no de otro. Tras un up que no arrancó no queda nada nuestro en marcha.
        var ours = status.Running ? status.Ports ?? status.Services.Where(s => s.Port is not null).Select(s => s.Port!.Value).ToList() : [];
        var wanted = new List<(int Port, string Role, string Key, int[] Alternatives)>
        {
            (config.HttpPort, config.UsesApache ? "HTTP de Apache" : "HTTP de nginx", "[web] port", [8080, 8000, 8081, 8888, 8088]),
        };
        if (new SiteRegistry(app.Paths).Load().Count > 0)
            wanted.Add((config.HttpsPort, "HTTPS de los sitios .test", "[web] https_port", [8443, 4443, 9443, 10443]));
        if (config.DbEngine != RoostConfig.NoDb && new DbInstaller(app.Paths).ListInstalled().Count > 0)
            wanted.Add((config.DbPort, "la base de datos", "[db] port", [3307, 3308, 3309, 3310, 3406]));

        var busy = new List<BusyPort>();
        foreach (var (port, role, key, alternatives) in wanted)
        {
            if (ours.Contains(port) || StackPlanner.BusyPorts([port]).Count == 0) continue;
            int? suggestion = alternatives.Where(p => p != config.HttpPort && p != config.HttpsPort && p != config.DbPort)
                .FirstOrDefault(p => StackPlanner.BusyPorts([p]).Count == 0) is var free and > 0 ? free : null;
            busy.Add(new BusyPort(port, role, PortOwners.Find(port), suggestion, key));
        }

        var first = error.Split(' ', 2)[0];
        var service = first.StartsWith("php-", StringComparison.Ordinal) ? "PHP"
            : first is "nginx" or "mariadb" or "mysql" ? first
            : first is "apache" ? "Apache"
            : busy.Count > 0 ? busy[0].Role
            : null;
        var title = status.Running && partial ? "El cambio se ha aplicado a medias"
            : status.Running ? "No se ha aplicado el cambio"
            : service is null ? "No se pudo arrancar Roost" : $"No se pudo arrancar {service}";
        return new StartFailure(title, error, busy, StillRunning: status.Running);
    }
}
