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
            try { process.Kill(entireProcessTree: true); }
            catch { /* ya había muerto */ }
        }

        public void Dispose()
        {
            process.Dispose();
            log?.Dispose();
        }
    }
}
