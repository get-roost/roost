using Roost.Core.Config;
using Roost.Core.Scanning;
using Roost.Core.Services;

namespace Roost.Daemon;

/// <summary>
/// Recarga en caliente del servidor web en marcha: lee otra vez su configuración (los vhosts, los
/// certificados, los upstreams, un puerto nuevo) sin cortar las conexiones. Probado en real en
/// Windows con peticiones en bucle, sin un solo fallo:
/// <list type="bullet">
/// <item>nginx: <c>nginx -s reload</c> con el mismo <c>-p</c> y <c>-c</c> con que se arrancó (lee el pid de <c>run/nginx.pid</c>). El maestro sigue (el supervisor no ve nada) y cambia de worker; abre y cierra puertos;</item>
/// <item>Apache: <c>httpd -k graceful</c> y <c>-k restart</c> solo valen para el servicio de Windows (AH00436), así que se le da el aviso que usa el propio mpm_winnt: el evento con nombre <c>ap&lt;pid&gt;_restart</c> del proceso padre. Relee la configuración y cambia de proceso hijo; el viejo acaba lo que estaba sirviendo.</item>
/// </list>
/// Antes hay que validar (<c>-t</c>): con la configuración mala, nginx se queda con la de antes y
/// Apache se pararía.
/// </summary>
internal static class WebReload
{
    /// <summary>Recarga <paramref name="web"/> (<c>nginx</c> o <c>apache</c>), cuyo proceso es <paramref name="pid"/>; null si ha ido bien o por qué no.</summary>
    public static string? Run(IProcessRunner runner, ServiceDefinition web, int? pid)
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

    private static string? SignalApache(int? pid)
    {
        if (pid is null) return "Apache no esta en marcha";
        if (!OperatingSystem.IsWindows()) return "la recarga en caliente de Apache solo esta hecha para Windows";
        try
        {
            using var restart = EventWaitHandle.OpenExisting(ApacheRestartEvent(pid.Value));
            restart.Set();
            return null;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return $"no se pudo avisar a Apache ({ex.Message})";
        }
    }
}
