using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roost.Core.Platform;
using Roost.Extensibility;

namespace Roost.Daemon;

/// <summary>
/// roostd, el daemon de USUARIO (ROOST.md §5.1): padre de nginx y php-cgi, los vigila y sigue
/// ahí al cerrar la terminal. Habla HTTP+JSON por una named pipe (Windows) o un unix socket,
/// nunca por TCP, y solo con el usuario que lo arrancó. Arranca bajo demanda: lo lanza la CLI
/// (<c>roost daemon</c>) si no responde, y termina con <c>roost down</c>.
/// </summary>
public static class DaemonHost
{
    public static string PipeName => "roost-" + Environment.UserName;

    public static string SocketPath(RoostPaths paths) => Path.Combine(paths.Run, "roost.sock");

    public static string LogFile(RoostPaths paths) => Path.Combine(paths.Logs, "roostd.log");

    /// <param name="extensions">Las ya cargadas por quien lanza el daemon (la CLI); si no, se cargan aquí.</param>
    public static async Task<int> RunAsync(CancellationToken cancellation, ExtensionSet? extensions = null)
    {
        // Uno por usuario: si ya hay otro, este sobra.
        using var single = new Mutex(true, $"roost-daemon-{Environment.UserName}", out var first);
        if (!first) return 0;

        if (OperatingSystem.IsWindows())
            WindowsJob.TryKillChildrenOnExit();

        var paths = RoostPaths.Default();
        Directory.CreateDirectory(paths.Logs);
        var log = new FileLog(LogFile(paths));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Kestrel crea la pipe solo accesible para el usuario actual (CurrentUserOnly).
                kestrel.ListenNamedPipe(PipeName);
            }
            else
            {
                Directory.CreateDirectory(paths.Run);
                var socket = SocketPath(paths);
                if (File.Exists(socket)) File.Delete(socket);
                kestrel.ListenUnixSocket(socket);
            }
        });

        var app = builder.Build();
        extensions ??= ExtensionSet.Load(paths);
        var stack = new StackService(paths, log, extensions);

        app.MapGet("/status", () => stack.Status());
        app.MapPost("/up", () => stack.UpAsync());
        app.MapPost("/restart/{service}", (string service) => stack.RestartAsync(service));
        app.MapPost("/down", async () =>
        {
            var response = await stack.DownAsync();
            // Se para el daemon después de contestar, no durante.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                app.Lifetime.StopApplication();
            });
            return response;
        });

        try
        {
            await app.StartAsync(cancellation);
            log.Write($"roostd en marcha (pid {Environment.ProcessId}, raiz {paths.Root})");
            foreach (var extension in extensions.Loaded)
                log.Write($"extension {extension.Name} {extension.Version} ({extension.Id})");
            foreach (var error in extensions.Errors)
                log.Write($"extension {error.Source}: {error.Message}");
            // El aviso de versión nueva para quien usa solo la CLI; acaba solo al pararse roostd.
            _ = UpdateRefresher.RunAsync(paths, log, app.Lifetime.ApplicationStopping);
            await app.WaitForShutdownAsync(cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write("roostd no pudo arrancar: " + ex.Message);
            return 1;
        }
        finally
        {
            await stack.DownAsync();
            log.Write("roostd parado");
        }

        return 0;
    }
}

/// <summary>Log del daemon: una línea por evento, con hora, añadida al final del fichero.</summary>
internal sealed class FileLog(string path)
{
    private readonly object _lock = new();

    public void Write(string message)
    {
        lock (_lock)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch { /* el log no debe tumbar el daemon */ }
        }
    }
}
