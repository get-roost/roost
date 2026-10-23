namespace Roost.Desktop.Services;

/// <summary>
/// Una sola app por usuario, como roostd. Abrir Roost otra vez (el acceso directo, el menú
/// Inicio) no crea otra ventana: avisa a la que ya hay para que se enseñe.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Por usuario y raíz: una Roost con ROOST_HOME (de pruebas) no se cruza con la de siempre.
    private static string Name(string what) => $"roost-desktop-{what}-{Environment.UserName}{Roost.Core.Platform.RoostPaths.InstanceTag()}";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>La instancia de esta sesión, o null si ya hay otra (a la que se le ha pedido que se enseñe).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Name("instance"), out var created);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name("show"));
        if (created) return new SingleInstance(mutex, show);

        show.Set();
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Llama a <paramref name="onShow"/> (desde otro hilo) cada vez que se abre Roost otra vez.</summary>
    public void Listen(Action onShow)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { _show, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) onShow();
        })
        { IsBackground = true, Name = "roost-desktop-single-instance" };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* no era nuestro */ }
        _mutex.Dispose();
        _show.Dispose();
    }
}
