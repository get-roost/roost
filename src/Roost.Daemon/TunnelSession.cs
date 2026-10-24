using Roost.Core;
using Roost.Core.Scanning;
using Roost.Core.Share;

namespace Roost.Daemon;

/// <summary>
/// Un cloudflared en marcha para un sitio: lo lanza sobre el <see cref="ICommandRunner"/> (hijo del
/// daemon, así que muere con él por el Job Object), lee sus líneas con <see cref="QuickTunnelOutput"/>
/// y las deja todas en <c>logs/tunel-&lt;sitio&gt;.log</c>. No es un servicio del supervisor a
/// propósito: relanzarlo daría otra URL, y quien la tiene no se enteraría. Si se cae, se cae, y el
/// daemon deja de compartir y lo dice.
/// </summary>
internal sealed class TunnelSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly QuickTunnelOutput _output = new();
    private readonly object _lock = new();
    private readonly StreamWriter? _log;
    private string? _failure;

    private TunnelSession(string logFile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            // Compartido para leer: la pantalla Registros lo abre mientras escribe.
            _log = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log = null;   // sin log se comparte igual
        }
    }

    /// <summary>Termina cuando cloudflared sale (o se para): su resultado, o null si se paró o no se pudo lanzar.</summary>
    public Task<CommandResult?> Exited { get; private set; } = Task.FromResult<CommandResult?>(null);

    /// <summary>true desde que se pide parar: entonces su salida no es una caída.</summary>
    public bool Stopping => _stop.IsCancellationRequested;

    public bool Connected
    {
        get { lock (_lock) return _output.Connected; }
    }

    /// <summary>Por qué no ha salido la URL o por qué se ha cerrado, en palabras (sin tildes).</summary>
    public string Failure
    {
        get
        {
            lock (_lock)
            {
                var exit = Exited.IsCompletedSuccessfully ? Exited.Result?.ExitCode : null;
                return _failure ?? QuickTunnelOutput.Explain(_output.LastError, exit);
            }
        }
    }

    /// <summary>Por qué se ha cerrado solo, ya compartiendo: su último error o su código de salida.</summary>
    public string ClosedReason
    {
        get
        {
            lock (_lock)
            {
                if (_failure is not null) return _failure;
                if (_output.LastError is { } error) return error;
                return Exited.IsCompletedSuccessfully && Exited.Result is { } result
                    ? $"cloudflared ha terminado (codigo {result.ExitCode})"
                    : "cloudflared ha terminado";
            }
        }
    }

    public static TunnelSession Start(ICommandRunner runner, CommandSpec spec, string logFile, string site)
    {
        var session = new TunnelSession(logFile);
        session.Write($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} roost share {site}: {spec.Display}");
        session.Exited = session.RunAsync(runner, spec);
        return session;
    }

    private async Task<CommandResult?> RunAsync(ICommandRunner runner, CommandSpec spec)
    {
        await Task.Yield();   // que quien lanza no espere a un runner que empieza en su mismo hilo
        try
        {
            var result = await runner.RunAsync(spec, new Lines(OnLine), _stop.Token);
            Write($"=== cloudflared ha terminado (codigo {result.ExitCode})");
            return result;
        }
        catch (OperationCanceledException)
        {
            Write("=== parado por Roost");
            return null;
        }
        catch (RoostException ex)
        {
            lock (_lock) _failure = ex.Message;
            Write("=== " + ex.Message);
            return null;
        }
        finally
        {
            _url.TrySetCanceled();
            _connected.TrySetResult();
            lock (_lock) _log?.Dispose();
        }
    }

    private void OnLine(string line)
    {
        Write(line);
        lock (_lock)
        {
            if (!_output.Feed(line)) return;
            if (_output.Url is { } url) _url.TrySetResult(url);
            if (_output.Connected) _connected.TrySetResult();
        }
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try { _log?.WriteLine(line); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* el log no tumba el túnel */ }
        }
    }

    /// <summary>La URL pública en cuanto cloudflared la diga; null si sale antes o pasa <paramref name="timeout"/>.</summary>
    public async Task<string?> WaitForUrlAsync(TimeSpan timeout)
    {
        await Task.WhenAny(_url.Task, Task.Delay(timeout));
        return _url.Task.IsCompletedSuccessfully ? _url.Task.Result : null;
    }

    /// <summary>Espera, como mucho <paramref name="timeout"/>, a la primera conexión registrada (o a que salga).</summary>
    public Task WaitConnectedAsync(TimeSpan timeout) => Task.WhenAny(_connected.Task, Task.Delay(timeout));

    /// <summary>Para cloudflared (mata su árbol) y espera a que salga.</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        try { await Exited; }
        catch (Exception) { /* ya está parado */ }
    }

    /// <summary>Las líneas en el mismo hilo y en orden (un <c>Progress&lt;T&gt;</c> sin contexto las mandaría al pool, desordenadas).</summary>
    private sealed class Lines(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }
}
