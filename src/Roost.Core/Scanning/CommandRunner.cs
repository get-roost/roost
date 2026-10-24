using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Roost.Core.Scanning;

/// <summary>
/// <see cref="ICommandRunner"/> real sobre <see cref="Process"/>. Lee stdout y stderr en paralelo
/// (si solo se leyera uno, un proceso que llena el otro buffer se quedaría bloqueado), parte las
/// líneas en <c>\n</c>, <c>\r\n</c> y también en <c>\r</c> solo (git reescribe la línea de
/// progreso así), y mata el árbol entero de procesos al cancelar o al pasarse del tiempo, para
/// que nunca quede un hijo suelto con nuestras tuberías abiertas. Los dos flujos se leen en UTF-8,
/// que es lo que hablan git, Composer y Node.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    /// <summary>Instancia compartida sin estado.</summary>
    public static readonly CommandRunner Default = new();

    // Lo que se espera, tras matar el proceso o tras su salida normal, a que las tuberías se
    // cierren: un nieto que sobreviva (un servicio que la orden dejó lanzado) las mantendría
    // abiertas para siempre, y con lo leído hasta aquí basta.
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = spec.Exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.Stdin is not null,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in spec.Arguments) psi.ArgumentList.Add(argument);
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.Environment is not null)
        {
            foreach (var (name, value) in spec.Environment)
            {
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
        }
        if (spec.Stdin is not null) psi.StandardInputEncoding = Utf8;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new RoostException($"No se pudo ejecutar {spec.Exe}.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo ejecutar {spec.Exe}: {ex.Message}", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdout, lines),
            PumpAsync(process.StandardError, stderr, lines));

        if (spec.Stdin is not null)
        {
            // Si el proceso termina antes de leerlo todo (un `head`), escribir da IOException: no es un fallo nuestro.
            try
            {
                await process.StandardInput.WriteAsync(spec.Stdin.AsMemory(), cancellation);
                process.StandardInput.Close();
            }
            catch (IOException) { }
        }

        using var timeout = spec.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout?.Token ?? CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            // Que muera de verdad antes de volver: así quien cancela puede borrar lo que la orden tocaba.
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainGrace); } catch { /* ya no hay más que hacer */ }
            if (cancellation.IsCancellationRequested)
            {
                await DrainAsync(pumps);
                cancellation.ThrowIfCancellationRequested();
            }
            timedOut = true;
        }

        await DrainAsync(pumps);
        var exitCode = timedOut ? CommandResult.TimedOutExitCode : SafeExitCode(process);
        return new CommandResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return CommandResult.TimedOutExitCode; }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* ya murió, o no tenemos permiso: no hay más que hacer */ }
    }

    private static async Task DrainAsync(Task pumps)
    {
        try { await pumps.WaitAsync(DrainGrace); }
        catch (TimeoutException) { /* un nieto conserva la tubería: con lo leído basta */ }
    }

    /// <summary>
    /// Copia un flujo a <paramref name="sink"/> tal cual y da cada línea según llega. Una línea
    /// acaba en <c>\n</c>, <c>\r\n</c> o <c>\r</c> solo; lo que queda al cerrar el flujo sin
    /// terminador también se da.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, IProgress<string>? lines)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var pendingCr = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            lock (sink) sink.Append(buffer, 0, read);
            if (lines is null) continue;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (c == '\n') continue;   // era un \r\n: la línea ya se dio
                }
                if (c is '\n' or '\r')
                {
                    pendingCr = c == '\r';
                    lines.Report(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (lines is not null && line.Length > 0) lines.Report(line.ToString());
    }
}
