namespace Roost.Core.Services;

/// <summary>
/// Supervisor propio, igual en todos los SO (decisión 4 de ROOST.md): arranca los servicios
/// en orden, relanza los que se caen y los para en orden inverso.
///
/// Distingue dos salidas. Un proceso que ha vivido al menos <c>healthyUptime</c> y sale es
/// normal (php-cgi se recicla solo tras PHP_FCGI_MAX_REQUESTS peticiones): se relanza en el
/// acto. Uno que sale antes es una caída: se relanza con espera creciente, y tras
/// <see cref="MaxQuickCrashes"/> caídas seguidas se marca <see cref="ServiceState.Failed"/>.
///
/// Con el supervisor en marcha se pueden añadir, quitar y sustituir servicios (<see cref="Add"/>,
/// <see cref="RemoveAsync"/>, <see cref="ReplaceAsync"/>) sin tocar el resto: es lo que usa la
/// recarga selectiva del daemon para aplicar un cambio sin pararlo todo.
/// </summary>
public sealed class Supervisor
{
    public const int MaxQuickCrashes = 5;

    private readonly IProcessLauncher _launcher;
    private readonly Action<string> _log;
    private readonly TimeSpan _restartDelay;
    private readonly TimeSpan _healthyUptime;
    private readonly List<Slot> _slots = [];
    private readonly object _gate = new();

    /// <param name="restartDelay">Espera base tras una caída (se dobla en cada caída seguida, hasta 30 s).</param>
    /// <param name="healthyUptime">Lo que tiene que vivir un proceso para que su salida no cuente como caída.</param>
    public Supervisor(
        IProcessLauncher launcher,
        Action<string>? log = null,
        TimeSpan? restartDelay = null,
        TimeSpan? healthyUptime = null)
    {
        _launcher = launcher;
        _log = log ?? (_ => { });
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(1);
        _healthyUptime = healthyUptime ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Si se usa <see cref="ServiceDefinition.StopSignal"/> para pedir la parada (Linux y macOS: <c>kill(2)</c>
    /// y esperar <see cref="ServiceDefinition.StopTimeout"/> antes de matar). En Windows no hay señales y se
    /// mata, como siempre. Los tests lo activan para probar el flujo en cualquier sistema.
    /// </summary>
    public bool UseSignals { get; init; } = !OperatingSystem.IsWindows();

    private bool UsesSignal(ServiceDefinition definition) => UseSignals && definition.StopSignal != StopSignal.Kill;

    private static string SignalName(StopSignal signal) => "SIG" + signal.ToString().ToUpperInvariant();

    /// <summary>
    /// Arranca los servicios en el orden recibido (ROOST.md §5.5: DB → PHP → nginx). El primer
    /// arranque de cada uno es síncrono, para que el orden sea de verdad ese; la vigilancia y
    /// los relanzamientos van en segundo plano. Solo se puede llamar una vez: después, los servicios
    /// se añaden con <see cref="Add"/>.
    /// </summary>
    public void Start(IEnumerable<ServiceDefinition> services)
    {
        lock (_gate)
        {
            if (_slots.Count > 0) throw new InvalidOperationException("El supervisor ya tiene servicios en marcha.");
            foreach (var definition in services)
            {
                var slot = new Slot(definition);
                _slots.Add(slot);
                var first = Launch(slot);
                slot.Loop = first is null ? Task.CompletedTask : Task.Run(() => SuperviseAsync(slot, first.Value));
            }
        }
    }

    public IReadOnlyList<ServiceStatus> Status()
    {
        lock (_gate)
            return _slots.Select(s => s.Snapshot()).ToList();
    }

    // Cambiar el stack en marcha (E2, la recarga selectiva): añadir, quitar y sustituir servicios
    // sin parar el resto. Quien llama decide el orden (Arrange) y espera a que lo nuevo responda.

    /// <summary>Las definiciones de los servicios que tiene ahora, en su orden (también los parados).</summary>
    public IReadOnlyList<ServiceDefinition> Definitions()
    {
        lock (_gate)
            return _slots.Select(s => s.Definition).ToList();
    }

    /// <summary>
    /// Añade un servicio con el supervisor en marcha (un pool de PHP nuevo, Redis al activarlo) y
    /// lo arranca como <see cref="Start"/>: el primer arranque, síncrono, y la vigilancia en segundo
    /// plano. Va al final del orden; <see cref="Arrange"/> lo pone en su sitio. Lanza si ya hay uno
    /// con ese nombre.
    /// </summary>
    public void Add(ServiceDefinition definition)
    {
        lock (_gate)
        {
            if (_slots.Any(s => s.Definition.Name == definition.Name))
                throw new InvalidOperationException($"El supervisor ya tiene un servicio '{definition.Name}'.");
            var slot = new Slot(definition);
            _slots.Add(slot);
            var first = Launch(slot);
            slot.Loop = first is null ? Task.CompletedTask : Task.Run(() => SuperviseAsync(slot, first.Value));
        }
    }

    /// <summary>
    /// Para un servicio como <see cref="StopAsync()"/> (limpio, si tiene comando de parada) y lo
    /// quita del supervisor: ya no sale en <see cref="Status"/>. false si no estaba.
    /// </summary>
    public async Task<bool> RemoveAsync(string name)
    {
        Slot? slot;
        lock (_gate) slot = _slots.FirstOrDefault(s => s.Definition.Name == name);
        if (slot is null) return false;

        await StopSlotsAsync([slot]);
        lock (_gate) _slots.Remove(slot);
        return true;
    }

    /// <summary>
    /// Sustituye un servicio por otra definición con el mismo nombre (otra versión de PHP, otro
    /// puerto): para el proceso de la definición vieja, que es la que sabe pararlo (limpio, si tiene
    /// comando de parada), y arranca la nueva en el mismo sitio del orden. Las cuentas de reinicios
    /// empiezan de cero. false si no estaba.
    /// </summary>
    public async Task<bool> ReplaceAsync(ServiceDefinition definition)
    {
        Slot? old;
        lock (_gate) old = _slots.FirstOrDefault(s => s.Definition.Name == definition.Name);
        if (old is null) return false;

        await StopSlotsAsync([old]);
        lock (_gate)
        {
            var slot = new Slot(definition);
            var index = _slots.IndexOf(old);
            if (index >= 0) _slots[index] = slot;
            else _slots.Add(slot);
            var first = Launch(slot);
            slot.Loop = first is null ? Task.CompletedTask : Task.Run(() => SuperviseAsync(slot, first.Value));
        }
        return true;
    }

    /// <summary>
    /// Ordena los servicios como <paramref name="order"/> (el del plan: DB → … → PHP → servidor
    /// web), que es el de <see cref="Status"/> y, al revés, el de parada. Los que no salen en la
    /// lista se quedan al final, en el orden que tenían.
    /// </summary>
    public void Arrange(IReadOnlyList<string> order)
    {
        var position = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++) position.TryAdd(order[i], i);
        lock (_gate)
        {
            var sorted = _slots
                .Select((slot, i) => (Slot: slot, Position: position.GetValueOrDefault(slot.Definition.Name, int.MaxValue), Original: i))
                .OrderBy(s => s.Position)
                .ThenBy(s => s.Original)
                .Select(s => s.Slot)
                .ToList();
            _slots.Clear();
            _slots.AddRange(sorted);
        }
    }

    /// <summary>
    /// Para los servicios en orden inverso al de arranque y espera a que terminen. Los que
    /// tienen <see cref="ServiceDefinition.StopCommand"/> se paran limpios: se lanza el comando
    /// y se espera a que salgan solos; si no salen en <see cref="ServiceDefinition.StopTimeout"/>, se matan.
    /// </summary>
    public async Task StopAsync()
    {
        List<Slot> slots;
        lock (_gate) slots = [.. _slots];
        await StopSlotsAsync(slots);
    }

    /// <summary>
    /// Para solo los servicios cuyo nombre cumple <paramref name="match"/> (un grupo: la base de
    /// datos, el pool php-8.4-*, nginx), como <see cref="StopAsync()"/>: limpios si tienen comando
    /// de parada, en orden inverso, y quedan <see cref="ServiceState.Stopped"/> sin que nadie los
    /// relance hasta <see cref="StartAsync"/> o <see cref="RestartAsync"/>. Devuelve cuántos casaban.
    /// </summary>
    public async Task<int> StopAsync(Func<string, bool> match)
    {
        List<Slot> slots;
        lock (_gate) slots = _slots.Where(s => match(s.Definition.Name)).ToList();
        await StopSlotsAsync(slots);
        return slots.Count;
    }

    /// <summary>
    /// Vuelve a arrancar, en el orden de arranque, los servicios parados o <see cref="ServiceState.Failed"/>
    /// cuyo nombre cumple <paramref name="match"/>; los que ya están en marcha se dejan como están.
    /// Devuelve cuántos casaban.
    /// </summary>
    public Task<int> StartAsync(Func<string, bool> match)
    {
        List<Slot> slots;
        lock (_gate) slots = _slots.Where(s => match(s.Definition.Name)).ToList();

        foreach (var slot in slots)
        {
            // Sin bucle vivo no hay nadie que lo relance: se arranca otro, como la primera vez.
            if (slot.Loop is { IsCompleted: true }) Relaunch(slot);
        }
        return Task.FromResult(slots.Count);
    }

    private async Task StopSlotsAsync(List<Slot> slots)
    {
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            if (slot.Definition.StopCommand is { } command && slot.BeginGracefulStop())
                await StopGracefullyAsync(slot, command);
            else if (UsesSignal(slot.Definition) && slot.BeginGracefulStop())
                await StopBySignalAsync(slot);

            slot.Stop();
            _log($"{slot.Definition.Name}: parado");
        }

        await Task.WhenAny(Task.WhenAll(slots.Select(s => s.Loop ?? Task.CompletedTask)), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Un servicio sin bucle (parado a mano o Failed): se deja como nuevo y se lanza con otro bucle.</summary>
    private void Relaunch(Slot slot)
    {
        slot.Reset();
        var first = Launch(slot);
        if (first is null) return;
        _log($"{slot.Definition.Name}: relanzado a peticion");
        lock (_gate) slot.Loop = Task.Run(() => SuperviseAsync(slot, first.Value));
    }

    /// <summary>
    /// Reinicia los servicios cuyo nombre cumple <paramref name="match"/> (la UI reinicia un
    /// grupo: nginx, el pool php-8.4-*, la base de datos). Se paran como en <see cref="StopAsync()"/>
    /// (limpios si tienen comando de parada) y el supervisor los relanza en el acto, sin
    /// contarlo como caída. Uno marcado <see cref="ServiceState.Failed"/>, o parado a mano con
    /// <see cref="StopAsync(Func{string, bool})"/>, se vuelve a lanzar. Devuelve cuántos servicios casaban.
    /// </summary>
    public async Task<int> RestartAsync(Func<string, bool> match)
    {
        List<Slot> slots;
        lock (_gate) slots = _slots.Where(s => match(s.Definition.Name)).ToList();

        foreach (var slot in slots)
        {
            // Uno sin bucle (parado a mano, o Failed porque dejó de relanzarse) se arranca otra vez.
            if (slot.Loop is { IsCompleted: true })
            {
                Relaunch(slot);
                continue;
            }
            if (slot.IsStopping) continue;

            var generation = slot.Generation;
            if (!slot.BeginRestart()) continue;
            if (slot.Definition.StopCommand is { } command && await RestartGracefullyAsync(slot, command, generation))
                continue;
            if (UsesSignal(slot.Definition) && await RestartBySignalAsync(slot, generation))
                continue;
            slot.KillForRestart(generation);
        }
        return slots.Count;
    }

    /// <summary>
    /// Le manda su señal de parada y espera a que salga solo (su bucle termina sin relanzarlo: ya está
    /// parando); si no sale en <see cref="ServiceDefinition.StopTimeout"/>, quien llama lo mata después.
    /// </summary>
    private async Task StopBySignalAsync(Slot slot)
    {
        if (!slot.Signal()) return;
        using var timeout = new CancellationTokenSource(slot.Definition.StopTimeout);
        try
        {
            await (slot.Loop ?? Task.CompletedTask).WaitAsync(timeout.Token);
            _log($"{slot.Definition.Name}: se ha parado limpio ({SignalName(slot.Definition.StopSignal)})");
        }
        catch (OperationCanceledException)
        {
            _log($"{slot.Definition.Name}: no se paro en {slot.Definition.StopTimeout.TotalSeconds:0} s tras {SignalName(slot.Definition.StopSignal)}; se mata");
        }
    }

    /// <summary>Le manda su señal de parada y espera a que el bucle lo relance; false si no llega a tiempo (se matará).</summary>
    private async Task<bool> RestartBySignalAsync(Slot slot, int generation)
    {
        if (!slot.Signal(generation)) return false;
        using var timeout = new CancellationTokenSource(slot.Definition.StopTimeout);
        try
        {
            while (slot.Generation == generation && !slot.IsStopping)
                await Task.Delay(50, timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log($"{slot.Definition.Name}: no se paro en {slot.Definition.StopTimeout.TotalSeconds:0} s tras {SignalName(slot.Definition.StopSignal)} para reiniciarlo; se mata");
            return false;
        }
    }

    /// <summary>Pide la parada limpia y espera a que el bucle lo relance; false si no llega a tiempo.</summary>
    private async Task<bool> RestartGracefullyAsync(Slot slot, ServiceDefinition command, int generation)
    {
        using var timeout = new CancellationTokenSource(slot.Definition.StopTimeout);
        try
        {
            using (var stopper = _launcher.Start(command))
                await stopper.WaitForExitAsync(timeout.Token);
            while (slot.Generation == generation && !slot.IsStopping)
                await Task.Delay(50, timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log($"{slot.Definition.Name}: no se paro en {slot.Definition.StopTimeout.TotalSeconds:0} s para reiniciarlo; se mata");
        }
        catch (Exception ex)
        {
            _log($"{slot.Definition.Name}: no se le pudo pedir que parase ({ex.Message}); se mata para reiniciarlo");
        }
        return false;
    }

    private async Task StopGracefullyAsync(Slot slot, ServiceDefinition command)
    {
        using var timeout = new CancellationTokenSource(slot.Definition.StopTimeout);
        try
        {
            using (var stopper = _launcher.Start(command))
                await stopper.WaitForExitAsync(timeout.Token);

            // El bucle del servicio termina cuando su proceso sale (ya no lo relanza: está parando).
            await (slot.Loop ?? Task.CompletedTask).WaitAsync(timeout.Token);
            _log($"{slot.Definition.Name}: se ha parado limpio");
        }
        catch (OperationCanceledException)
        {
            _log($"{slot.Definition.Name}: no se paro en {slot.Definition.StopTimeout.TotalSeconds:0} s; se mata");
        }
        catch (Exception ex)
        {
            _log($"{slot.Definition.Name}: no se le pudo pedir que parase ({ex.Message}); se mata");
        }
    }

    /// <summary>Lanza el proceso de <paramref name="slot"/>; null si no arranca o si ya lo estaban parando.</summary>
    private (IServiceProcess Process, DateTime Started)? Launch(Slot slot)
    {
        IServiceProcess process;
        try
        {
            process = _launcher.Start(slot.Definition);
        }
        catch (Exception ex)
        {
            slot.Fail($"no arranca: {ex.Message}");
            _log($"{slot.Definition.Name}: no arranca: {ex.Message}");
            return null;
        }

        // Si nos han parado mientras arrancaba, no lo dejamos huérfano.
        if (!slot.Attach(process))
            return null;

        _log($"{slot.Definition.Name}: en marcha (pid {process.Id})");
        return (process, DateTime.UtcNow);
    }

    private async Task SuperviseAsync(Slot slot, (IServiceProcess Process, DateTime Started) first)
    {
        var quickCrashes = 0;
        var (process, started) = first;
        while (true)
        {
            try
            {
                await process.WaitForExitAsync(slot.Stopping.Token);
            }
            catch (OperationCanceledException)
            {
                // Stop() ya lo ha matado.
                slot.Detach();
                process.Dispose();
                return;
            }

            var exitCode = process.ExitCode;
            var uptime = DateTime.UtcNow - started;
            slot.Detach();
            process.Dispose();
            // Parando (también a medias, tras pedirle shutdown): su salida no es una caída.
            if (slot.IsStopping) return;

            if (slot.TakeRestart())
            {
                // Reinicio pedido desde fuera: ni es caída ni hace falta esperar.
                quickCrashes = 0;
                slot.CountRestart(null);
                _log($"{slot.Definition.Name}: reiniciado a peticion");
            }
            else if (uptime >= _healthyUptime)
            {
                // Salida normal tras un rato vivo (reciclaje de php-cgi): se relanza ya.
                quickCrashes = 0;
                slot.CountRestart(null);
            }
            else
            {
                quickCrashes++;
                var error = $"se cayo a los {uptime.TotalSeconds:0.0} s (codigo {exitCode?.ToString() ?? "?"})";
                if (quickCrashes >= MaxQuickCrashes)
                {
                    slot.Fail($"{error}; {MaxQuickCrashes} caidas seguidas, se deja de relanzar");
                    _log($"{slot.Definition.Name}: {error}; se deja de relanzar");
                    return;
                }

                slot.CountRestart(error);
                _log($"{slot.Definition.Name}: {error}; se relanza");
                var delay = TimeSpan.FromTicks(Math.Min(
                    _restartDelay.Ticks * (1L << (quickCrashes - 1)),
                    TimeSpan.FromSeconds(30).Ticks));
                try
                {
                    await Task.Delay(delay, slot.Stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (Launch(slot) is not { } next) return;
            (process, started) = next;
        }
    }

    /// <summary>Estado mutable de un servicio; todo acceso pasa por su propio lock.</summary>
    private sealed class Slot(ServiceDefinition definition)
    {
        private readonly object _lock = new();
        private IServiceProcess? _process;
        private ServiceState _state = ServiceState.Restarting;
        private int _restarts;
        private string? _lastError;
        private bool _stopRequested;
        private bool _restartRequested;
        private int _generation;

        public ServiceDefinition Definition { get; } = definition;
        public CancellationTokenSource Stopping { get; private set; } = new();
        public Task? Loop { get; set; }

        /// <summary>
        /// Lo deja como recién creado para arrancarlo de nuevo tras una parada a mano o un Failed:
        /// otra cancelación (la de antes ya está disparada) y sin parada ni reinicio pendientes.
        /// Solo con el bucle anterior terminado.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                Stopping = new CancellationTokenSource();
                _stopRequested = false;
                _restartRequested = false;
                _state = ServiceState.Restarting;
            }
        }

        /// <summary>true desde que se empieza a parar (limpio o no): ya no se relanza.</summary>
        public bool IsStopping
        {
            get { lock (_lock) return _stopRequested || Stopping.IsCancellationRequested; }
        }

        /// <summary>Marca el servicio como parándose; true si tiene un proceso vivo al que pedírselo.</summary>
        public bool BeginGracefulStop()
        {
            lock (_lock)
            {
                _stopRequested = true;
                return _process is not null;
            }
        }

        /// <summary>Cambia con cada proceso nuevo: sirve para saber si un reinicio ya se ha hecho.</summary>
        public int Generation
        {
            get { lock (_lock) return _generation; }
        }

        public bool Attach(IServiceProcess process)
        {
            lock (_lock)
            {
                if (_stopRequested || Stopping.IsCancellationRequested)
                {
                    process.Kill();
                    process.Dispose();
                    return false;
                }
                _process = process;
                _generation++;
                // Un reinicio pedido mientras esperaba a relanzarse ya está hecho con este proceso.
                _restartRequested = false;
                _state = ServiceState.Running;
                return true;
            }
        }

        /// <summary>Marca que la próxima salida es un reinicio pedido; false si se está parando.</summary>
        public bool BeginRestart()
        {
            lock (_lock)
            {
                if (_stopRequested || Stopping.IsCancellationRequested) return false;
                _restartRequested = true;
                return true;
            }
        }

        public bool TakeRestart()
        {
            lock (_lock)
            {
                var requested = _restartRequested;
                _restartRequested = false;
                return requested;
            }
        }

        /// <summary>Mata el proceso para reiniciarlo, si sigue siendo el de <paramref name="generation"/>.</summary>
        public void KillForRestart(int generation)
        {
            lock (_lock)
            {
                if (_generation == generation) _process?.Kill();
            }
        }

        /// <summary>
        /// Le manda la señal de parada de su definición (POSIX). false si no hay proceso vivo o, con
        /// <paramref name="generation"/>, si ya es otro proceso.
        /// </summary>
        public bool Signal(int? generation = null)
        {
            lock (_lock)
            {
                if (_process is null || (generation is { } g && _generation != g)) return false;
                _process.Terminate(Definition.StopSignal);
                return true;
            }
        }

        public void Detach()
        {
            lock (_lock) _process = null;
        }

        public void CountRestart(string? error)
        {
            lock (_lock)
            {
                _restarts++;
                _state = ServiceState.Restarting;
                if (error is not null) _lastError = error;
            }
        }

        public void Fail(string error)
        {
            lock (_lock)
            {
                _state = ServiceState.Failed;
                _lastError = error;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                // Primero matar y luego cancelar: la cancelación puede ejecutar en línea el
                // bucle, que libera el proceso. Con el lock, el bucle no puede ver la salida
                // del proceso como una caída hasta que la cancelación esté hecha.
                _stopRequested = true;
                _process?.Kill();
                Stopping.Cancel();
                if (_state != ServiceState.Failed) _state = ServiceState.Stopped;
            }
        }

        public ServiceStatus Snapshot()
        {
            lock (_lock)
            {
                int? pid = null;
                try { pid = _process?.Id; } catch { /* proceso ya cerrado */ }
                return new ServiceStatus(Definition.Name, _state, pid, _restarts, Definition.Port, _lastError);
            }
        }
    }
}
