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
/// La recarga es asíncrona: se espera a que el servidor web cambie de hijo, así quien la pide (un
/// <c>site add</c>, un script que pide la página justo después) ya ve lo nuevo. Antes hay que validar
/// (<c>-t</c>): con la configuración mala, nginx se queda con la de antes y Apache se pararía.
/// </summary>
internal static class WebReload
{
    /// <summary>Recarga <paramref name="web"/> (<c>nginx</c> o <c>apache</c>), cuyo proceso es <paramref name="pid"/>; null si ha ido bien o por qué no.</summary>
    public static async Task<string?> RunAsync(IProcessRunner runner, ServiceDefinition web, int? pid)
    {
        if (pid is not { } parent) return $"{web.Name} no esta en marcha";
        var before = ChildProcesses.Of(parent);
        if (Signal(runner, web, parent) is { } error) return error;
        return await WaitForNewChildAsync(web.Name, parent, before);
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
    /// Espera a que el servidor web tenga un hijo que no estaba (el worker de nginx o el hijo de Apache
    /// con la configuración nueva; nginx tarda unos 400 ms). Con Apache se espera además a que se vaya el
    /// viejo, que sigue aceptando hasta que el nuevo está listo (un segundo, más lo que tarde en acabar
    /// lo que sirve), pero como mucho 5 s: si atiende una petición larga, sigue con ella.
    /// </summary>
    private static async Task<string?> WaitForNewChildAsync(string server, int pid, IReadOnlyList<int> before)
    {
        if (before.Count == 0)
        {
            // Sin la lista de hijos (no se ha podido leer) no hay en qué fijarse: lo que suele tardar.
            await Task.Delay(TimeSpan.FromSeconds(1));
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!ChildProcesses.Of(pid).Except(before).Any())
        {
            if (DateTime.UtcNow > deadline)
                return $"{server} no ha arrancado un proceso con la configuracion nueva en 10 s";
            await Task.Delay(25);
        }

        if (server == RoostConfig.WebApache)
        {
            var oldGone = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (ChildProcesses.Of(pid).Intersect(before).Any() && DateTime.UtcNow < oldGone)
                await Task.Delay(50);
        }
        else
        {
            await Task.Delay(50);   // el worker nuevo abre sus puertos al nacer
        }
        return null;
    }
}
