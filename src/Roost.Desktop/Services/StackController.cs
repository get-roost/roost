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
public sealed record StartFailure(string Title, string Message, IReadOnlyList<BusyPort> BusyPorts);

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

    public bool IsRunning => Status?.Running == true;

    public bool IsDaemonUp => Status is not null;

    /// <summary>Pregunta al daemon. Si ya hay una pregunta en curso, no lanza otra.</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            Status = await app.Daemon.TryStatusAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Para los tests de vistas: un estado del daemon sin daemon.</summary>
    internal void Show(StatusResponse? status) => Status = status;

    /// <summary>Arranca (o rearranca con la configuración actual) el stack. null si ha ido bien.</summary>
    public Task<StartFailure?> StartAsync(IProgress<string>? progress = null) =>
        RunBusyAsync("Arrancando...", () => StartCoreAsync(progress));

    /// <summary>Si el stack está en marcha, lo rearranca para aplicar un cambio de configuración.</summary>
    public async Task<StartFailure?> ApplyIfRunningAsync()
    {
        await RefreshAsync();
        return IsRunning ? await RunBusyAsync("Aplicando cambios...", () => StartCoreAsync(null)) : null;
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

            await EnsureNginxAsync(progress);

            progress?.Report($"Arrancando PHP {php.Name} y nginx...");
            await app.Daemon.EnsureRunningAsync(DaemonLauncher.FromDirectory(AppServices.AppDirectory));
            var response = await app.Daemon.UpAsync();
            Status = response.Status;
            return response.Ok ? null : Failure(response.Error ?? "El daemon no ha dicho por qué.", config);
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

    private async Task EnsureNginxAsync(IProgress<string>? progress)
    {
        var installer = new NginxInstaller(app.Paths, app.Http);
        var build = NginxCatalog.Windows;
        if (installer.Find(build) is not null) return;

        progress?.Report($"Instalando nginx {build.Version} (la primera vez)...");
        var bytes = new Progress<long>(b => progress?.Report($"Instalando nginx {build.Version}... {b / 1048576.0:0.0} MB"));
        await installer.InstallAsync(build, bytes);
    }

    private StartFailure Failure(string error, RoostConfig config)
    {
        // Tras un up fallido no queda nada nuestro en marcha: lo que esté ocupado es de otro.
        var wanted = new List<(int Port, string Role, string Key, int[] Alternatives)>
        {
            (config.HttpPort, "HTTP de nginx", "[web] port", [8080, 8000, 8081, 8888, 8088]),
        };
        if (new SiteRegistry(app.Paths).Load().Count > 0)
            wanted.Add((config.HttpsPort, "HTTPS de los sitios .test", "[web] https_port", [8443, 4443, 9443, 10443]));
        if (config.DbEngine != RoostConfig.NoDb && new DbInstaller(app.Paths).ListInstalled().Count > 0)
            wanted.Add((config.DbPort, "la base de datos", "[db] port", [3307, 3308, 3309, 3310, 3406]));

        var busy = new List<BusyPort>();
        foreach (var (port, role, key, alternatives) in wanted)
        {
            if (StackPlanner.BusyPorts([port]).Count == 0) continue;
            int? suggestion = alternatives.Where(p => p != config.HttpPort && p != config.HttpsPort && p != config.DbPort)
                .FirstOrDefault(p => StackPlanner.BusyPorts([p]).Count == 0) is var free and > 0 ? free : null;
            busy.Add(new BusyPort(port, role, PortOwners.Find(port), suggestion, key));
        }

        var first = error.Split(' ', 2)[0];
        var service = first.StartsWith("php-", StringComparison.Ordinal) ? "PHP"
            : first is "nginx" or "mariadb" or "mysql" ? first
            : busy.Count > 0 ? busy[0].Role
            : null;
        return new StartFailure(service is null ? "No se pudo arrancar Roost" : $"No se pudo arrancar {service}", error, busy);
    }
}
