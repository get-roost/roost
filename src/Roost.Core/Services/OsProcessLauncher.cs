using System.Diagnostics;

namespace Roost.Core.Services;

/// <summary>
/// Lanzador real sobre <see cref="Process"/>: sin ventana, con el entorno de la definición y
/// con stdout/stderr añadidos al final de <see cref="ServiceDefinition.LogFile"/>.
/// </summary>
public sealed class OsProcessLauncher : IProcessLauncher
{
    public IServiceProcess Start(ServiceDefinition definition)
    {
        var psi = new ProcessStartInfo(definition.Executable, definition.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = definition.LogFile is not null,
            RedirectStandardError = definition.LogFile is not null,
            WorkingDirectory = definition.WorkingDirectory ?? Path.GetDirectoryName(definition.Executable) ?? "",
        };
        foreach (var (key, value) in definition.Environment)
            psi.Environment[key] = value;

        TextWriter? log = null;
        if (definition.LogFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(definition.LogFile)!);
            var stream = new FileStream(definition.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            log = TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
        }

        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"No se pudo arrancar {definition.Executable}.");

            if (log is not null)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            return new OsProcess(process, log);
        }
        catch
        {
            log?.Dispose();
            throw;
        }
    }

    private static void Write(TextWriter log, string line)
    {
        try { log.WriteLine(line); }
        catch { /* el log no debe tumbar el servicio */ }
    }

    private sealed class OsProcess(Process process, TextWriter? log) : IServiceProcess
    {
        public int Id => process.Id;

        public int? ExitCode
        {
            get
            {
                try { return process.HasExited ? process.ExitCode : null; }
                catch { return null; }
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);

        public void Kill()
        {
            // Fuera de Windows se apuntan antes todos los descendientes (por /proc) y, tras el Kill del árbol de
            // .NET, se rematan los que sigan vivos: se ha visto sobrevivir un worker de nginx que se estaba cerrando
            // tras el SIGQUIT, con el puerto cogido. Recién apuntados, no da tiempo a que otro proceso reuse el pid.
            var descendants = OperatingSystem.IsWindows() ? [] : Descendants(process.Id);
            try { process.Kill(entireProcessTree: true); }
            catch { /* ya había muerto */ }
            foreach (var pid in descendants)
                Platform.Posix.Kill(pid, Platform.Posix.SignalNumber(StopSignal.Kill));
        }

        /// <summary>Los descendientes de un proceso ahora mismo (hijos, nietos…), por el ppid de <c>/proc</c>.</summary>
        private static List<int> Descendants(int root)
        {
            var found = new List<int>();
            var pending = new Queue<int>([root]);
            while (pending.Count > 0 && found.Count < 1024)
            {
                foreach (var child in Platform.ProcFs.ChildrenOf(pending.Dequeue()))
                {
                    if (found.Contains(child)) continue;
                    found.Add(child);
                    pending.Enqueue(child);
                }
            }
            return found;
        }

        public void Terminate(StopSignal signal)
        {
            if (OperatingSystem.IsWindows() || signal == StopSignal.Kill)
            {
                Kill();
                return;
            }
            try
            {
                if (process.HasExited) return;
                // Solo al proceso: nginx y php-fpm pasan la señal a sus workers. Si no se le puede mandar, se mata.
                if (!Platform.Posix.Kill(process.Id, Platform.Posix.SignalNumber(signal))) Kill();
            }
            catch { /* ya había muerto */ }
        }

        public void Dispose()
        {
            process.Dispose();
            log?.Dispose();
        }
    }
}
