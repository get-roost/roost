using System.Text.Json;
using Roost.Core.Platform;
using Roost.Core.Services;

namespace Roost.Daemon;

/// <summary>
/// El libro de hijos de roostd en Linux y macOS, lo que en Windows hace el Job Object
/// (<see cref="WindowsJob"/>): un fichero en <c>run/</c> con el pid y la hora de arranque (el campo 22 de
/// <c>/proc/&lt;pid&gt;/stat</c>) de cada proceso que lanza el supervisor. Si roostd muere sin poder
/// limpiar (un <c>kill -9</c>, un fallo), el siguiente roostd lee el libro al arrancar
/// (<see cref="Reconcile"/>) y para lo que siga vivo <b>con la misma hora de arranque</b>: un pid
/// reutilizado por otro proceso tiene otra, y no se toca nunca. Primero SIGTERM, al proceso y a los hijos
/// que tenga entonces (los workers de nginx), y a lo que no se vaya en unos segundos, SIGKILL.
/// </summary>
internal sealed class PosixPidLedger(string file, IProcessTable? table = null)
{
    public sealed record Entry(string Name, int Pid, long StartTime);

    private readonly IProcessTable _table = table ?? ProcProcessTable.Instance;
    private readonly object _lock = new();

    public static string FileFor(RoostPaths paths) => Path.Combine(paths.Run, "children.json");

    public static PosixPidLedger For(RoostPaths paths) => new(FileFor(paths));

    public string File => file;

    /// <summary>Lo apuntado; vacío si no hay libro o no se entiende.</summary>
    public IReadOnlyList<Entry> Load()
    {
        lock (_lock) return Read();
    }

    /// <summary>Apunta un hijo recién lanzado (con su hora de arranque, leída ahora; 0 si no se puede saber: entonces no se tocará al reconciliar).</summary>
    public void Add(string name, int pid)
    {
        lock (_lock)
        {
            var entries = Read().Where(e => e.Pid != pid).ToList();
            entries.Add(new Entry(name, pid, _table.StartTimeOf(pid) ?? 0));
            Write(entries);
        }
    }

    /// <summary>Lo quita del libro (ha salido).</summary>
    public void Remove(int pid)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.All(e => e.Pid != pid)) return;
            Write(entries.Where(e => e.Pid != pid).ToList());
        }
    }

    /// <summary>
    /// Lo que hace el roostd que arranca con lo que dejó el anterior: para los apuntados que siguen vivos
    /// con la misma hora de arranque (y sus hijos), y vacía el libro. Devuelve cuántos había que parar.
    /// </summary>
    public int Reconcile(Action<string> log)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.Count == 0)
            {
                Clear();
                return 0;
            }

            var alive = entries.Where(e => e.StartTime > 0 && _table.StartTimeOf(e.Pid) == e.StartTime).ToList();
            var unknown = entries.Where(e => e.StartTime <= 0).ToList();
            if (unknown.Count > 0)
                log($"libro de hijos: {unknown.Count} sin hora de arranque, no se tocan: {string.Join(", ", unknown.Select(e => $"{e.Name} (pid {e.Pid})"))}");
            if (alive.Count == 0)
            {
                log($"libro de hijos: el roostd anterior dejo {entries.Count} apuntado(s), ninguno sigue vivo");
                Clear();
                return 0;
            }

            // Lo que hay que parar: cada hijo apuntado y lo que cuelgue de él ahora mismo (sus workers), con la
            // hora de arranque de cada uno para no confundirlo con otro proceso después.
            var targets = new List<(string Name, int Pid, long StartTime)>();
            foreach (var entry in alive)
            {
                targets.Add((entry.Name, entry.Pid, entry.StartTime));
                foreach (var child in Descendants(entry.Pid))
                {
                    if (_table.StartTimeOf(child) is { } start) targets.Add(($"{entry.Name} (hijo)", child, start));
                }
            }

            log($"el roostd anterior murio sin limpiar: siguen vivos {string.Join(", ", alive.Select(e => $"{e.Name} (pid {e.Pid})"))}; se paran");
            foreach (var target in targets) _table.Kill(target.Pid, Posix.SIGTERM);

            var deadline = DateTime.UtcNow + GraceAfterTerm;
            while (DateTime.UtcNow < deadline && targets.Any(t => _table.StartTimeOf(t.Pid) == t.StartTime))
                _table.Sleep(TimeSpan.FromMilliseconds(100));

            var stubborn = targets.Where(t => _table.StartTimeOf(t.Pid) == t.StartTime).ToList();
            foreach (var target in stubborn)
            {
                _table.Kill(target.Pid, Posix.SIGKILL);
                log($"{target.Name} (pid {target.Pid}): no se fue con SIGTERM; SIGKILL");
            }

            Clear();
            return alive.Count;
        }
    }

    /// <summary>Lo que se espera tras SIGTERM antes de matar.</summary>
    public static readonly TimeSpan GraceAfterTerm = TimeSpan.FromSeconds(3);

    private IReadOnlyList<int> Descendants(int pid)
    {
        var all = new List<int>();
        var pending = new Queue<int>([pid]);
        while (pending.Count > 0 && all.Count < 10_000)
        {
            foreach (var child in _table.ChildrenOf(pending.Dequeue()))
            {
                if (all.Contains(child)) continue;
                all.Add(child);
                pending.Enqueue(child);
            }
        }
        return all;
    }

    private List<Entry> Read()
    {
        try
        {
            if (!System.IO.File.Exists(file)) return [];
            return JsonSerializer.Deserialize(System.IO.File.ReadAllText(file), LedgerJson.Default.ListEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Write(List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(entries, LedgerJson.Default.ListEntry));
            System.IO.File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El libro no debe tumbar el stack: sin él, el siguiente roostd no reconcilia, nada más.
        }
    }

    private void Clear()
    {
        try { System.IO.File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Lo que el libro necesita del sistema; los tests ponen una tabla de pega.</summary>
internal interface IProcessTable
{
    /// <summary>La hora de arranque de un proceso, o null si no existe.</summary>
    long? StartTimeOf(int pid);

    IReadOnlyList<int> ChildrenOf(int pid);

    bool Kill(int pid, int signal);

    void Sleep(TimeSpan time);
}

/// <summary>La de verdad: <c>/proc</c> y <c>kill(2)</c>.</summary>
internal sealed class ProcProcessTable : IProcessTable
{
    public static readonly ProcProcessTable Instance = new();

    public long? StartTimeOf(int pid) => ProcFs.StartTimeOf(pid);

    public IReadOnlyList<int> ChildrenOf(int pid) => ProcFs.ChildrenOf(pid);

    public bool Kill(int pid, int signal) => Posix.Kill(pid, signal);

    public void Sleep(TimeSpan time) => Thread.Sleep(time);
}

/// <summary>
/// El lanzador del daemon fuera de Windows: el de verdad, apuntando cada hijo en el libro al nacer y
/// quitándolo cuando el supervisor lo suelta (ya ha salido).
/// </summary>
internal sealed class LedgerLauncher(IProcessLauncher inner, PosixPidLedger ledger) : IProcessLauncher
{
    public IServiceProcess Start(ServiceDefinition definition)
    {
        var process = inner.Start(definition);
        ledger.Add(definition.Name, process.Id);
        return new Tracked(process, ledger);
    }

    private sealed class Tracked(IServiceProcess inner, PosixPidLedger ledger) : IServiceProcess
    {
        public int Id => inner.Id;

        public int? ExitCode => inner.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellation) => inner.WaitForExitAsync(cancellation);

        public void Kill() => inner.Kill();

        public void Terminate(StopSignal signal) => inner.Terminate(signal);

        public void Dispose()
        {
            try { ledger.Remove(inner.Id); } catch { /* el proceso ya no sabe su pid */ }
            inner.Dispose();
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<PosixPidLedger.Entry>))]
internal sealed partial class LedgerJson : System.Text.Json.Serialization.JsonSerializerContext;
