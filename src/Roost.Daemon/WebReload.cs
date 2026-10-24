using Roost.Core.Config;
using Roost.Core.Scanning;
using Roost.Core.Services;

namespace Roost.Daemon;

/// <summary>
/// Recarga en caliente del servidor web en marcha: lee otra vez su configuración (los vhosts, los
/// certificados, los upstreams, un puerto nuevo) sin pararse. Probado en real en Windows:
/// <list type="bullet">
/// <item>nginx: <c>nginx -s reload</c> con el mismo <c>-p</c> y <c>-c</c> con que se arrancó (lee el pid
/// de <c>run/nginx.pid</c>). El maestro sigue (el supervisor no ve nada), arranca un worker con lo nuevo y
/// al viejo le dice que acabe; abre y cierra puertos. Con un cliente que pide en bucle, ningún fallo.
/// Con varios a la vez, alguna conexión que estaba en la cola del worker viejo se corta al cerrarla
/// (nginx para Windows abre un socket de escucha por worker): una o dos por recarga con cuatro
/// clientes sin pausa. Sigue siendo mucho menos que pararlo;</item>
/// <item>Apache: <c>httpd -k graceful</c> y <c>-k restart</c> solo valen para el servicio de Windows
/// (AH00436), así que se le da el aviso que usa el propio mpm_winnt: el evento con nombre
/// <c>ap&lt;pid&gt;_restart</c> del proceso padre. Relee la configuración y arranca otro hijo, que hereda
/// los sockets del padre; el viejo deja de aceptar cuando el nuevo está listo y acaba lo que estaba
/// sirviendo. Sin un solo fallo, también con varios clientes.</item>
/// </list>
/// La recarga es asíncrona: se espera a que el servidor web tenga su hijo nuevo listo, así quien la
/// pide (un <c>site add</c>, un script que pide la página justo después) ya ve lo nuevo. Antes hay
/// que validar (<c>-t</c>): con la configuración mala, nginx se queda con la de antes y Apache se pararía.
/// </summary>
internal static class WebReload
{
    /// <summary>Recarga <paramref name="web"/> (<c>nginx</c> o <c>apache</c>), cuyo proceso es <paramref name="pid"/>; null si ha ido bien o por qué no.</summary>
    public static async Task<string?> RunAsync(IProcessRunner runner, ServiceDefinition web, int? pid)
    {
        if (pid is not { } parent) return $"{web.Name} no esta en marcha";
        var before = ChildProcesses.Of(parent);
        if (Signal(runner, web, parent) is { } error) return error;
        return await WaitForNewChildAsync(web, parent, before);
    }

    /// <summary>Le pide la recarga (sin esperar a que acabe); null si se la ha podido pedir o por qué no.</summary>
    public static string? Signal(IProcessRunner runner, ServiceDefinition web, int pid)
    {
        if (web.Name == RoostConfig.WebApache) return SignalApache(pid);
        if (web.Name != RoostConfig.WebNginx) return $"{web.Name} no se sabe recargar en caliente";

        // Sin salida si va bien; si no, «nginx: [error] OpenEvent(...) failed» o «[emerg] ...», con código 1.
        var output = runner.Run(web.Executable, "-s reload " + web.Arguments, TimeSpan.FromSeconds(15));
        if (output is null) return "nginx -s reload no respondio";
        return output.Contains("[emerg]", StringComparison.Ordinal) || output.Contains("[alert]", StringComparison.Ordinal)
               || output.Contains("[crit]", StringComparison.Ordinal) || output.Contains("[error]", StringComparison.Ordinal)
            ? output.Trim()
            : null;
    }

    /// <summary>El nombre del evento con el que el padre de Apache (mpm_winnt) recibe la orden de reiniciar en caliente.</summary>
    public static string ApacheRestartEvent(int pid) => $"ap{pid}_restart";

    private static string? SignalApache(int pid)
    {
        if (!OperatingSystem.IsWindows()) return "la recarga en caliente de Apache solo esta hecha para Windows";
        try
        {
            using var restart = EventWaitHandle.OpenExisting(ApacheRestartEvent(pid));
            restart.Set();
            return null;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return $"no se pudo avisar a Apache ({ex.Message})";
        }
    }

    /// <summary>
    /// Espera a que el servidor web tenga un hijo que no estaba, con la configuración nueva. El worker
    /// de nginx atiende en cuanto nace (unos 400 ms). El hijo de Apache tarda un segundo en estar listo
    /// (y hasta entonces sigue aceptando el viejo): se espera a que apunte en su log que arranca sus
    /// hilos (AH00354, con su pid) o, si no hay log que leer, a que se vaya el viejo, como mucho 5 s.
    /// </summary>
    private static async Task<string?> WaitForNewChildAsync(ServiceDefinition web, int pid, IReadOnlyList<int> before)
    {
        if (before.Count == 0)
        {
            // Sin la lista de hijos (no se ha podido leer) no hay en qué fijarse: lo que suele tardar.
            await Task.Delay(TimeSpan.FromSeconds(1));
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        int? child;
        while ((child = ChildProcesses.Of(pid).Except(before).Cast<int?>().FirstOrDefault()) is null)
        {
            if (DateTime.UtcNow > deadline)
                return $"{web.Name} no ha arrancado un proceso con la configuracion nueva en 10 s";
            await Task.Delay(25);
        }

        if (web.Name != RoostConfig.WebApache)
        {
            await Task.Delay(50);   // el worker nuevo abre sus puertos al nacer
            return null;
        }

        var errorLog = web.LogFile is { } stdout ? Path.Combine(Path.GetDirectoryName(stdout)!, "apache-error.log") : null;
        var ready = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < ready)
        {
            if (errorLog is not null && ApacheChildStarted(errorLog, child.Value))
            {
                await Task.Delay(100);   // el viejo deja de aceptar en cuanto el nuevo está listo
                return null;
            }
            if (!ChildProcesses.Of(pid).Intersect(before).Any()) return null;   // el viejo ya se ha ido
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>true si el log de errores de Apache dice que el hijo <paramref name="child"/> ya arranca sus hilos (AH00354).</summary>
    internal static bool ApacheChildStarted(string errorLog, int child)
    {
        try
        {
            using var stream = new FileStream(errorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var tail = reader.ReadToEnd();
            var marker = $"[pid {child}:";
            return tail.Split('\n').Any(line => line.Contains(marker, StringComparison.Ordinal) && line.Contains("AH00354", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
