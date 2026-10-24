using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Roost.Core;
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
    /// <summary>
    /// Uno por usuario y raíz: una Roost con <c>ROOST_HOME</c>, o un cliente sobre otra raíz (los tests de la
    /// app), tiene su propia tubería y nunca habla con el daemon de la Roost de siempre.
    /// Endpoints: <c>GET /status</c>, <c>POST /up</c>, <c>/down</c>, <c>/restart</c> (todo),
    /// <c>/restart/{servicio}</c>, <c>/reload/{servicio}</c> (el servidor web, en caliente),
    /// <c>/stop/{servicio}</c> y <c>/start/{servicio}</c>; y para compartir (oleada 6), <c>GET /shares</c>,
    /// <c>POST /share/{sitio}/tunnel</c>, <c>/share/{sitio}/lan</c>, <c>/share/{sitio}/{tipo}/stop</c> y
    /// <c>/share/{sitio}/{tipo}/hold</c> (espera a que se deje de compartir; si el cliente se va, lo para).
    /// </summary>
    public static string PipeName(RoostPaths paths) => "roost-" + Environment.UserName + RoostPaths.InstanceTag(paths);

    /// <summary>
    /// El unix socket de roostd (Linux y macOS), el mismo para el host y para <see cref="DaemonClient"/>:
    /// <c>$XDG_RUNTIME_DIR/roost&lt;tag&gt;.sock</c> (una carpeta 0700 del usuario que el sistema limpia al
    /// cerrar sesión) o, sin esa variable (WSL sin systemd, un contenedor), <c>run/roost.sock</c> de la
    /// raíz, con <c>run/</c> a 0700. Si la ruta no cabe en los 108 bytes de <c>sun_path</c>, una corta en
    /// <c>/tmp/roost-&lt;uid&gt;&lt;tag&gt;/</c>. El socket queda a 0600 al escuchar.
    /// </summary>
    public static string SocketPath(RoostPaths paths) => ChooseSocket(paths, Environment.GetEnvironmentVariable, Posix.Uid).Path;

    /// <summary>sun_path son 108 bytes con el terminador: la ruta, en UTF-8, como mucho 107.</summary>
    internal const int MaxSocketPath = 107;

    /// <summary><see cref="SocketPath"/>, con el entorno y el uid que se le den (los tests). <c>OwnDirectory</c>: la carpeta es de Roost y se deja a 0700.</summary>
    internal static (string Path, bool OwnDirectory) ChooseSocket(RoostPaths paths, Func<string, string?> env, int uid)
    {
        var tag = RoostPaths.InstanceTag(paths);
        var runtime = env("XDG_RUNTIME_DIR");
        var (path, own) = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
            ? (Path.Combine(runtime, $"roost{tag}.sock"), false)
            : (Path.Combine(paths.Run, "roost.sock"), true);
        if (System.Text.Encoding.UTF8.GetByteCount(path) <= MaxSocketPath) return (path, own);
        return (Path.Combine(Path.GetTempPath(), $"roost-{uid}{tag}", "roost.sock"), true);
    }

    /// <summary>true si alguien acepta conexiones en ese unix socket (un roostd vivo); false si está huérfano o no existe.</summary>
    internal static bool IsSocketAlive(string socket)
    {
        if (!File.Exists(socket)) return false;
        try
        {
            using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            probe.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deja el sitio del socket listo para escuchar: su carpeta (0700 si es de Roost) y, si quedó un socket
    /// de un roostd anterior que murió, lo quita, pero solo tras comprobar que nadie contesta en él.
    /// </summary>
    internal static void PrepareSocket(string socket, bool ownDirectory)
    {
        var dir = Path.GetDirectoryName(socket)!;
        if (ownDirectory) PrivateFile.EnsureDirectory(dir);
        else Directory.CreateDirectory(dir);
        if (!File.Exists(socket)) return;
        if (IsSocketAlive(socket))
            throw new RoostException($"Ya hay un roostd escuchando en {socket}.");
        File.Delete(socket);
    }

    public static string LogFile(RoostPaths paths) => Path.Combine(paths.Logs, "roostd.log");

    // Mientras viva el proceso: la de SIGHUP (si se suelta, vuelve el comportamiento de serie).
    private static PosixSignalRegistration? _hangup;

    /// <param name="extensions">Las ya cargadas por quien lanza el daemon (la CLI); si no, se cargan aquí.</param>
    public static async Task<int> RunAsync(CancellationToken cancellation, ExtensionSet? extensions = null)
    {
        var paths = RoostPaths.Default();
        if (!OperatingSystem.IsWindows())
        {
            // El daemon se desliga él mismo de la terminal que lo lanzó (quien lo lanza no puede hacerlo por
            // él): su propia sesión (setsid: sin terminal de control, cerrarla no le manda SIGHUP ni a él ni a
            // nginx), SIGHUP ignorado por si llega igual, y stdout/stderr a roostd.log (así suelta la salida
            // que heredó de `roost up`, que un script podría estar capturando).
            Posix.SetSid();
            _hangup ??= PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
            Posix.RedirectStdioTo(LogFile(paths));
        }

        // Uno por usuario (y raíz, con ROOST_HOME): si ya hay otro, este sobra.
        using var single = new Mutex(true, $"roost-daemon-{Environment.UserName}{RoostPaths.InstanceTag(paths)}", out var first);
        if (!first) return 0;

        if (OperatingSystem.IsWindows())
            WindowsJob.TryKillChildrenOnExit();

        Directory.CreateDirectory(paths.Logs);
        var log = new FileLog(LogFile(paths));

        // Fuera de Windows no hay Job Object: lo que dejó vivo un roostd anterior que murió sin limpiar
        // (kill -9) se para ahora, antes de arrancar nada que quiera sus puertos.
        if (!OperatingSystem.IsWindows())
        {
            try { PosixPidLedger.For(paths).Reconcile(log.Write); }
            catch (Exception ex) { log.Write("libro de hijos: no se pudo reconciliar (" + ex.Message + ")"); }
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        string? unixSocket = null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Kestrel crea la pipe solo accesible para el usuario actual (CurrentUserOnly).
                kestrel.ListenNamedPipe(PipeName(paths));
            }
            else
            {
                var (socket, own) = ChooseSocket(paths, Environment.GetEnvironmentVariable, Posix.Uid);
                PrepareSocket(socket, own);
                unixSocket = socket;
                kestrel.ListenUnixSocket(socket);
            }
        });

        var app = builder.Build();
        extensions ??= ExtensionSet.Load(paths);
        var stack = new StackService(paths, log.Write, extensions);

        // Tras /down este daemon se va: lo que llegue mientras tanto (un `roost up` justo después)
        // no se atiende, para que el cliente lance otro en vez de montar un stack que este iba a
        // tirar al salir.
        var stopping = 0;
        app.Use(async (context, next) =>
        {
            if (Volatile.Read(ref stopping) == 1)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await next(context);
        });

        app.MapGet("/status", () => stack.Status());
        app.MapPost("/up", () => stack.UpAsync());
        app.MapPost("/restart", () => stack.RestartAllAsync());
        app.MapPost("/restart/{service}", (string service) => stack.RestartAsync(service));
        app.MapPost("/reload/{service}", (string service) => stack.ReloadServiceAsync(service));
        app.MapPost("/stop/{service}", (string service) => stack.StopServiceAsync(service));
        app.MapPost("/start/{service}", (string service) => stack.StartServiceAsync(service));
        // Compartir (oleada 6): un sitio por internet (túnel) o en la red local, mientras dure.
        app.MapGet("/shares", () => stack.Shares());
        app.MapPost("/share/{site}/tunnel", (string site) => stack.ShareTunnelAsync(site));
        app.MapPost("/share/{site}/lan", (string site, ShareLanRequest request) => stack.ShareLanAsync(site, request.Address, request.Port));
        app.MapPost("/share/{site}/{kind}/stop", (string site, string kind) =>
            Roost.Core.Share.ShareListener.TryParseKind(kind, out var parsed)
                ? stack.StopShareAsync(site, parsed)
                : Task.FromResult(new ShareResponse(false, $"No hay ningun tipo de compartir '{kind}' (tunnel o lan).", null)));
        app.MapPost("/share/{site}/{kind}/hold", (string site, string kind, HttpContext context) =>
            Roost.Core.Share.ShareListener.TryParseKind(kind, out var parsed)
                ? stack.HoldShareAsync(site, parsed, context.RequestAborted)
                : Task.FromResult(new ShareResponse(false, $"No hay ningun tipo de compartir '{kind}' (tunnel o lan).", null)));
        app.MapPost("/down", async () =>
        {
            var response = await stack.DownAsync();
            Volatile.Write(ref stopping, 1);
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
            // Solo este usuario habla con su daemon (ROOST.md §5.1): el socket a 0600 (su carpeta ya es 0700).
            if (unixSocket is not null && File.Exists(unixSocket))
                File.SetUnixFileMode(unixSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            log.Write($"roostd en marcha (pid {Environment.ProcessId}, raiz {paths.Root}{(unixSocket is null ? "" : $", socket {unixSocket}")})");
            foreach (var extension in extensions.Loaded)
                log.Write($"extension {extension.Name} {extension.Version} ({extension.Id})");
            foreach (var error in extensions.Errors)
                log.Write($"extension {error.Source}: {error.Message}");
            // El aviso de versión nueva para quien usa solo la CLI; acaba solo al pararse roostd.
            _ = UpdateRefresher.RunAsync(paths, log, app.Lifetime.ApplicationStopping);
            // Y los certificados de los sitios, que un daemon de semanas vería caducar.
            _ = CertRenewer.RunAsync(stack, log.Write, app.Lifetime.ApplicationStopping);
            // Sitios avanzados (C3): una subcarpeta nueva o borrada en una carpeta aparcada.
            _ = ParkWatch.RunAsync(stack, paths, log.Write, app.Lifetime.ApplicationStopping);
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
