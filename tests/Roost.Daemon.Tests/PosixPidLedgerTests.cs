using System.Diagnostics;
using Roost.Core.Platform;
using Roost.Core.Services;

namespace Roost.Daemon.Tests;

/// <summary>
/// El libro de hijos de roostd fuera de Windows: lo que apunta, y la reconciliación de un daemon anterior
/// muerto sin limpiar, con una tabla de procesos de pega (vale en cualquier sistema) y, en Linux, con
/// procesos de verdad.
/// </summary>
public class PosixPidLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-ledger-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "run", "children.json");

    private sealed class FakeTable : IProcessTable
    {
        public Dictionary<int, long> Alive { get; } = new();
        public Dictionary<int, List<int>> Children { get; } = new();
        public List<(int Pid, int Signal)> Kills { get; } = [];
        public bool DiesOnTerm { get; set; } = true;

        public long? StartTimeOf(int pid) => Alive.TryGetValue(pid, out var start) ? start : null;

        public IReadOnlyList<int> ChildrenOf(int pid) => Children.GetValueOrDefault(pid) ?? [];

        public bool Kill(int pid, int signal)
        {
            Kills.Add((pid, signal));
            if (signal == Posix.SIGKILL || DiesOnTerm) Alive.Remove(pid);
            return true;
        }

        public void Sleep(TimeSpan time) { }
    }

    [Fact]
    public void Add_Remove_YLoad_ConLaHoraDeArranqueDeLaTabla()
    {
        var table = new FakeTable { Alive = { [100] = 5000, [101] = 5001 } };
        var ledger = new PosixPidLedger(File, table);

        ledger.Add("php-8.4-0", 100);
        ledger.Add("nginx", 101);
        Assert.Equal([("php-8.4-0", 100, 5000L), ("nginx", 101, 5001L)], ledger.Load().Select(e => (e.Name, e.Pid, e.StartTime)));

        ledger.Remove(100);
        Assert.Equal(["nginx"], ledger.Load().Select(e => e.Name));

        ledger.Add("nginx", 101);   // repetir un pid no duplica
        Assert.Single(ledger.Load());
    }

    [Fact]
    public void Reconcile_ParaLoQueSigueVivoConLaMismaHora_YSusHijos_YNoTocaUnPidReutilizado()
    {
        var before = new FakeTable { Alive = { [100] = 5000, [101] = 5001, [102] = 5002 } };
        var ledger = new PosixPidLedger(File, before);
        ledger.Add("php-8.4-0", 100);
        ledger.Add("nginx", 101);
        ledger.Add("php-8.4-1", 102);

        // El roostd anterior ha muerto: nginx sigue (con un worker, pid 201), php-8.4-0 tambien, y el pid 102
        // lo tiene ahora otro proceso (otra hora de arranque).
        var now = new FakeTable { Alive = { [100] = 5000, [101] = 5001, [201] = 6000, [102] = 9999 } };
        now.Children[101] = [201];
        var log = new List<string>();

        var stopped = new PosixPidLedger(File, now).Reconcile(log.Add);

        Assert.Equal(2, stopped);
        Assert.Equal([(100, Posix.SIGTERM), (101, Posix.SIGTERM), (201, Posix.SIGTERM)], now.Kills);
        Assert.Equal([102], now.Alive.Keys);   // el reutilizado sigue vivo
        Assert.Contains(log, l => l.Contains("murio sin limpiar") && l.Contains("nginx (pid 101)") && l.Contains("php-8.4-0 (pid 100)"));
        Assert.False(System.IO.File.Exists(File));   // el libro se vacia
    }

    [Fact]
    public void Reconcile_LoQueNoSeVaConSigterm_SeMataConSigkill()
    {
        var table = new FakeTable { Alive = { [100] = 5000 }, DiesOnTerm = false };
        new PosixPidLedger(File, table).Add("nginx", 100);
        var log = new List<string>();

        new PosixPidLedger(File, table).Reconcile(log.Add);

        Assert.Equal([(100, Posix.SIGTERM), (100, Posix.SIGKILL)], table.Kills);
        Assert.Contains(log, l => l.Contains("SIGKILL"));
    }

    [Fact]
    public void Reconcile_SinLibro_OSinNadaVivo_NoMataNada()
    {
        var table = new FakeTable();
        Assert.Equal(0, new PosixPidLedger(File, table).Reconcile(_ => { }));

        table.Alive[100] = 5000;
        new PosixPidLedger(File, table).Add("nginx", 100);
        table.Alive.Clear();   // ya no esta
        var log = new List<string>();
        Assert.Equal(0, new PosixPidLedger(File, table).Reconcile(log.Add));
        Assert.Empty(table.Kills);
        Assert.Contains(log, l => l.Contains("ninguno sigue vivo"));
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void Reconcile_UnHijoSinHoraDeArranque_NoSeToca()
    {
        var table = new FakeTable();   // no sabe la hora de nadie
        new PosixPidLedger(File, table).Add("nginx", 100);
        table.Alive[100] = 5000;
        var log = new List<string>();

        Assert.Equal(0, new PosixPidLedger(File, table).Reconcile(log.Add));

        Assert.Empty(table.Kills);
        Assert.Contains(log, l => l.Contains("sin hora de arranque"));
    }

    [Fact]
    public void LedgerLauncher_ApuntaAlArrancar_YQuitaAlSoltarElProceso()
    {
        var table = new FakeTable { Alive = { [300] = 7000 } };
        var ledger = new PosixPidLedger(File, table);
        var launcher = new LedgerLauncher(new FakeInner(300), ledger);

        var process = launcher.Start(new ServiceDefinition { Name = "nginx", Executable = "nginx", StopSignal = StopSignal.Quit });
        Assert.Equal([("nginx", 300)], ledger.Load().Select(e => (e.Name, e.Pid)));

        process.Terminate(StopSignal.Quit);
        process.Dispose();
        Assert.Empty(ledger.Load());
    }

    private sealed class FakeInner(int pid) : IProcessLauncher
    {
        public IServiceProcess Start(ServiceDefinition definition) => new Process(pid);

        private sealed class Process(int pid) : IServiceProcess
        {
            public int Id => pid;
            public int? ExitCode => 0;
            public Task WaitForExitAsync(CancellationToken cancellation) => Task.CompletedTask;
            public void Kill() { }
            public void Dispose() { }
        }
    }

    [LinuxFact]
    public void Linux_Reconcile_MataUnSleepApuntado_YNoUnoConOtraHoraDeArranque()
    {
        // Dos sleep: uno apuntado como lo haria el supervisor, otro apuntado con una hora de arranque falsa
        // (el pid reutilizado por otro proceso): el primero muere, el segundo no.
        using var tracked = Process.Start(new ProcessStartInfo("sleep", "300") { UseShellExecute = false })!;
        using var impostor = Process.Start(new ProcessStartInfo("sleep", "300") { UseShellExecute = false })!;
        try
        {
            var ledger = new PosixPidLedger(File);
            ledger.Add("nginx", tracked.Id);
            var entries = ledger.Load().ToList();
            Assert.True(entries[0].StartTime > 0);
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File,
                $"[{{\"Name\":\"nginx\",\"Pid\":{tracked.Id},\"StartTime\":{entries[0].StartTime}}},{{\"Name\":\"viejo\",\"Pid\":{impostor.Id},\"StartTime\":1}}]");
            var log = new List<string>();

            var stopped = new PosixPidLedger(File).Reconcile(log.Add);

            Assert.Equal(1, stopped);
            Assert.True(tracked.WaitForExit(3000), "el sleep apuntado tenia que morir");
            Assert.False(impostor.HasExited);
            Assert.Contains(log, l => l.Contains($"nginx (pid {tracked.Id})"));
        }
        finally
        {
            try { impostor.Kill(); } catch { }
            try { tracked.Kill(); } catch { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
