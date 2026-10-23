using System.Diagnostics;

namespace Roost.Core.Scanning;

/// <summary>
/// Implementación real de <see cref="IProcessRunner"/> sobre <see cref="Process"/>.
/// Corre el binario con un timeout, captura stdout y stderr sin bloquearse por
/// buffers llenos, y mata el proceso si se pasa de tiempo. Diseñada para no lanzar
/// nunca: un binario colgado o inexistente devuelve <c>null</c>, no una excepción.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Instancia compartida sin estado; sirve para todos los scanners.</summary>
    public static readonly ProcessRunner Default = new();

    public string? Run(string executablePath, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;

            // Leemos ambos flujos en paralelo: si solo esperásemos a uno, un binario
            // que llena el buffer del otro se quedaría bloqueado para siempre.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            var millis = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            if (!process.WaitForExit(millis))
            {
                // Se pasó del timeout: lo matamos (y a sus hijos) y damos por fallido.
                try { process.Kill(entireProcessTree: true); } catch { /* ya murió o sin permiso */ }
                return null;
            }

            // El proceso terminó; las lecturas ya deberían estar completas.
            try { Task.WaitAll([stdout, stderr], 3000); } catch { /* da igual: validamos abajo */ }

            var outText = stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : string.Empty;
            var errText = stderr.Status == TaskStatus.RanToCompletion ? stderr.Result : string.Empty;
            return outText + errText;
        }
        catch
        {
            // Ruta inexistente, binario corrupto, permisos… nada de esto debe tumbar el escaneo.
            return null;
        }
    }
}
