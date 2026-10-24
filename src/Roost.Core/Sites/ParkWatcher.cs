namespace Roost.Core.Sites;

/// <summary>
/// Sitios avanzados (C3): vigila las carpetas aparcadas y avisa (<see cref="Changed"/>) cuando
/// aparece, desaparece o cambia de nombre una subcarpeta. Junta lo que pase seguido (un
/// <c>git clone</c> o descomprimir un zip son muchos eventos) en un solo aviso, pasado
/// <see cref="Debounce"/> sin más cambios: así, con el hosts, los sitios nuevos piden un solo permiso.
/// Lo usan la app y el daemon; quien recibe el aviso llama a <see cref="SiteManager.SyncParked"/>.
/// </summary>
public sealed class ParkWatcher : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly object _lock = new();
    private bool _disposed;

    public ParkWatcher(TimeSpan? debounce = null)
    {
        Debounce = debounce ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Lo que se espera sin cambios antes de avisar.</summary>
    public TimeSpan Debounce { get; }

    /// <summary>Algo ha cambiado en alguna carpeta aparcada (en un hilo del pool, no en el de la UI).</summary>
    public event Action? Changed;

    /// <summary>Las carpetas que se vigilan ahora.</summary>
    public IReadOnlyList<string> Folders
    {
        get { lock (_lock) return [.. _watchers.Keys]; }
    }

    /// <summary>
    /// Vigila exactamente <paramref name="folders"/>: deja las que ya vigilaba, quita las que sobran y
    /// empieza con las nuevas (las que no existen se saltan; se vuelven a probar en la siguiente llamada).
    /// </summary>
    public void Watch(IEnumerable<string> folders)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var wanted = folders.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _watchers[gone].Dispose();
                _watchers.Remove(gone);
            }
            foreach (var folder in wanted.Where(f => !_watchers.ContainsKey(f)))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        NotifyFilter = NotifyFilters.DirectoryName,
                        IncludeSubdirectories = false,
                    };
                    watcher.Created += (_, _) => Poke();
                    watcher.Deleted += (_, _) => Poke();
                    watcher.Renamed += (_, _) => Poke();
                    // Si se desborda el búfer o la carpeta deja de estar, se sincroniza todo igual.
                    watcher.Error += (_, _) => Poke();
                    watcher.EnableRaisingEvents = true;
                    _watchers[folder] = watcher;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // se vuelve a probar en la siguiente llamada
                }
            }
        }
    }

    /// <summary>Un cambio: el aviso sale cuando pase <see cref="Debounce"/> sin otro.</summary>
    public void Poke()
    {
        lock (_lock)
        {
            if (!_disposed) _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            _timer.Dispose();
        }
    }
}
