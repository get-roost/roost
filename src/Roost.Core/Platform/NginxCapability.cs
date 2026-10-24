using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Platform;

/// <summary>
/// En Linux, que el nginx de Roost pueda escuchar en el 80 y el 443 sin ser root:
/// <c>setcap cap_net_bind_service=+ep</c> sobre su binario, que lo hace <c>roost-helper setcap-nginx
/// &lt;ruta&gt;</c> con privilegios (ROOST.md §5.6). El helper solo lo acepta si es un fichero normal
/// llamado <c>nginx</c>, ELF, del usuario que lo pide y dentro de su home, sin enlaces en el camino.
/// La capability se pierde al reescribir o copiar el binario (al actualizar nginx hay que volver a
/// pedirla) y no vale en sistemas de ficheros sin xattr (<c>/mnt/c</c> en WSL) ni montados con
/// <c>nosuid</c>. Quién la pide desde <c>roost up</c> lo engancha el coordinador al integrar L1.
/// </summary>
public static class NginxCapability
{
    public const string Capability = "cap_net_bind_service";

    /// <summary>Pide al helper la capability para ese binario. Lanza <see cref="RoostException"/> si se cancela, si el helper lo rechaza o si setcap falla.</summary>
    public static void Request(string helperExe, string nginxExe, Elevation? elevation = null)
    {
        var run = (elevation ?? Elevation.Default).Run(helperExe, ["setcap-nginx", nginxExe]);
        if (run.Status == ElevationStatus.Cancelled)
            throw new RoostException("Has cancelado el permiso de administrador: nginx seguira sin poder escuchar en el 80 y el 443 (usa puertos a partir del 1024, o repite y acepta el permiso).");
        switch (run.ExitCode)
        {
            case HelperExit.Ok:
                return;
            case HelperExit.BadInput:
                throw new RoostException(
                    $"roost-helper no acepta {nginxExe}: tiene que ser el binario nginx de Roost (un fichero normal llamado nginx, ELF), tuyo y dentro de tu carpeta personal, sin enlaces en el camino. Detalle en {HostsUpdater.HelperLog}.");
            default:
                throw new RoostException($"roost-helper no pudo dar a nginx el permiso para los puertos bajos (codigo {run.ExitCode}). Detalle en {HostsUpdater.HelperLog}.{(run.Output.Length == 0 ? "" : $" ({run.Output})")}");
        }
    }

    /// <summary>La orden para hacerlo a mano.</summary>
    public static string ManualCommand(string nginxExe) => $"sudo setcap {Capability}=+ep {Elevation.ShellQuote(nginxExe)}";

    /// <summary>true si el binario ya tiene la capability (<c>getcap</c>, sin privilegios). Nunca lanza.</summary>
    public static async Task<bool> IsGrantedAsync(ICommandRunner runner, string nginxExe, CancellationToken cancellation = default)
    {
        try
        {
            foreach (var getcap in new[] { "/usr/sbin/getcap", "/sbin/getcap" })
            {
                if (!File.Exists(getcap)) continue;
                var result = await runner.RunAsync(new CommandSpec(getcap, [nginxExe], Timeout: TimeSpan.FromSeconds(10)), null, cancellation);
                return result.Succeeded && result.Stdout.Contains(Capability, StringComparison.Ordinal);
            }
            return false;
        }
        catch (RoostException)
        {
            return false;
        }
    }
}
