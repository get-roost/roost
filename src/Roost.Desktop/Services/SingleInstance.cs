using System.Net.Sockets;
using Roost.Core.Platform;

namespace Roost.Desktop.Services;

/// <summary>
/// Una sola app por usuario, como roostd. Abrir Roost otra vez (el acceso directo, el menú
/// Inicio) no crea otra ventana: avisa a la que ya hay para que se enseñe.
/// <list type="bullet">
/// <item>Windows: un <c>Mutex</c> con nombre es la instancia y un <c>EventWaitHandle</c> con nombre, el aviso.</item>
/// <item>Linux y macOS (no hay objetos con nombre): un fichero de bloqueo (<c>flock</c>, que es lo que hace
/// .NET con <c>FileShare.None</c>) en <c>$XDG_RUNTIME_DIR</c> (o <c>/tmp/roost-desktop-&lt;uid&gt;</c>, 0700)
/// es la instancia, y un unix socket al lado es el aviso: la segunda app se conecta y la primera, al
/// aceptar, se enseña. El socket viejo de una app que murió se quita antes de escuchar: solo lo hace
/// quien tiene el bloqueo, así dos arranques a la vez no se pisan.</item>
/// </list>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Por usuario y raíz: una Roost con ROOST_HOME (de pruebas) no se cruza con la de siempre.
    private static string Name(string what) => $"roost-desktop-{what}-{Environment.UserName}{RoostPaths.InstanceTag()}";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _show;
    private readonly FileStream? _lock;
    private readonly Socket? _listener;
    private readonly string? _socketPath;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    private SingleInstance(FileStream @lock, Socket listener, string socketPath)
    {
        _lock = @lock;
        _listener = listener;
        _socketPath = socketPath;
    }

    /// <summary>La instancia de esta sesión, o null si ya hay otra (a la que se le ha pedido que se enseñe).</summary>
    public static SingleInstance? TryAcquire() =>
        OperatingSystem.IsWindows() ? TryAcquireWindows() : TryAcquirePosix(PosixDirectory());

    private static SingleInstance? TryAcquireWindows()
    {
        var mutex = new Mutex(initiallyOwned: true, Name("instance"), out var created);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name("show"));
        if (created) return new SingleInstance(mutex, show);

        show.Set();
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Dónde van el bloqueo y el socket en Linux y macOS: <c>$XDG_RUNTIME_DIR</c> (de este usuario, se
    /// vacía al cerrar la sesión) o, si no lo hay, <c>/tmp/roost-desktop-&lt;uid&gt;</c>.
    /// </summary>
    internal static string PosixDirectory(Func<string, string?>? env = null)
    {
        var runtime = (env ?? Environment.GetEnvironmentVariable)("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
            ? runtime
            : Path.Combine(Path.GetTempPath(), $"roost-desktop-{Posix.Uid}");
    }

    /// <summary>El fichero de bloqueo y el socket de una raíz en <paramref name="directory"/> (los tests usan una carpeta suya).</summary>
    internal static (string Lock, string Socket) PosixFiles(string directory, string? tag = null)
    {
        tag ??= RoostPaths.InstanceTag();
        return (Path.Combine(directory, $"roost-desktop{tag}.lock"), Path.Combine(directory, $"roost-desktop{tag}.sock"));
    }

    internal static SingleInstance? TryAcquirePosix(string directory, string? tag = null)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (lockPath, socketPath) = PosixFiles(directory, tag);

        FileStream @lock;
        try
        {
            @lock = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,   // flock(LOCK_EX | LOCK_NB): si otra app lo tiene, IOException
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException)
        {
            Knock(socketPath);
            return null;
        }

        try
        {
            // El bloqueo es nuestro: lo que quede de una app que murió sin recoger se quita.
            if (File.Exists(socketPath)) File.Delete(socketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new SingleInstance(@lock, listener, socketPath);
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            // Sin socket no hay aviso, pero sigue habiendo una sola app: la segunda no podrá llamar, solo salir.
            AppLog.Warn($"No se pudo abrir el socket de la instancia única ({socketPath}): {ex.Message}");
            return new SingleInstance(@lock, null!, socketPath);
        }
    }

    /// <summary>Le dice a la app que ya está que se enseñe (si no contesta nadie, no pasa nada).</summary>
    private static void Knock(string socketPath)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            client.Send("show\n"u8);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // La otra app aún arranca (o murió sin soltar el bloqueo): esta sale igual.
        }
    }

    /// <summary>Llama a <paramref name="onShow"/> (desde otro hilo) cada vez que se abre Roost otra vez.</summary>
    public void Listen(Action onShow)
    {
        var thread = new Thread(() =>
        {
            if (_show is not null)
            {
                var handles = new[] { _show, _stop.Token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0) onShow();
                return;
            }
            if (_listener is null) return;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = _listener.Accept();
                    onShow();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;   // se ha cerrado el socket (Dispose)
                }
            }
        })
        { IsBackground = true, Name = "roost-desktop-single-instance" };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* no era nuestro */ }
            _mutex.Dispose();
            _show?.Dispose();
            return;
        }
        _listener?.Dispose();
        if (_socketPath is not null)
        {
            try { File.Delete(_socketPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* se quita al siguiente arranque */ }
        }
        _lock?.Dispose();   // suelta el flock; el fichero vacío se queda (XDG_RUNTIME_DIR se vacía al cerrar la sesión)
    }
}
