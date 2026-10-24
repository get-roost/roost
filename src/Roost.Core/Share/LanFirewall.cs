using System.ComponentModel;
using System.Diagnostics;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Share;

/// <summary>
/// La regla «Roost LAN &lt;puerto&gt;» del cortafuegos de Windows (<see cref="FirewallRule"/>): ponerla
/// y quitarla lo hace <c>roost-helper firewall-allow|firewall-remove &lt;puerto&gt;</c> elevado (UAC),
/// y saber cuáles hay se lee sin privilegios (<see cref="LanProbe"/>). Lo usan <c>roost share --lan</c>,
/// la app y <c>roost uninstall-cleanup</c>.
/// </summary>
public static class LanFirewall
{
    /// <summary>El nombre que se ve en el cortafuegos: «Roost LAN 8443».</summary>
    public static string RuleName(int port) => FirewallRule.DisplayName(port);

    /// <summary>Si el helper acepta ese puerto (el 80, el 443 o de 1024 a 65535, sin los de administración remota ni los de bases de datos).</summary>
    public static bool IsAllowedPort(int port) => FirewallRule.IsAllowedPort(port);

    /// <summary>Pone la regla con <paramref name="helperExe"/> elevado. Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public static void Allow(string helperExe, int port) => RunHelper(helperExe, "firewall-allow", port);

    /// <summary>Quita la regla con <paramref name="helperExe"/> elevado. Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public static void Remove(string helperExe, int port) => RunHelper(helperExe, "firewall-remove", port);

    /// <summary>Los puertos con una regla de Roost puesta, leídos sin privilegios (nunca lanza salvo por cancelación).</summary>
    public static async Task<IReadOnlyList<int>> RulePortsAsync(ICommandRunner runner, CancellationToken cancellation = default) =>
        (await LanProbe.ReadAsync(runner, cancellation)).RulePorts;

    private static void RunHelper(string helperExe, string operation, int port)
    {
        if (!OperatingSystem.IsWindows())
            throw new RoostException("Compartir en la red local solo esta hecho para Windows por ahora.");
        if (!FirewallRule.IsAllowedPort(port))
            throw new RoostException($"El puerto {port} no se abre en el cortafuegos: usa el 80, el 443 o uno de 1024 a 65535 (salvo los de escritorio remoto y bases de datos).");
        if (!File.Exists(helperExe))
            throw new RoostException($"No se encuentra {helperExe}, que es el que cambia el cortafuegos con permisos de administrador.");

        var start = new ProcessStartInfo(helperExe, $"{operation} {port}")
        {
            UseShellExecute = true,
            Verb = "runas", // UAC
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var helper = Process.Start(start) ?? throw new RoostException("No se pudo lanzar roost-helper.");
            helper.WaitForExit();
            if (helper.ExitCode != 0)
                throw new RoostException($"roost-helper no pudo cambiar la regla del cortafuegos «{RuleName(port)}» (codigo {helper.ExitCode}). Detalle en {HostsUpdater.HelperLog}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // «No» en el UAC
        {
            throw new RoostException(operation == "firewall-allow"
                ? "Has cancelado el permiso de administrador: el cortafuegos no se ha tocado."
                : $"Has cancelado el permiso de administrador: la regla «{RuleName(port)}» sigue en el cortafuegos (solo deja entrar a ese puerto desde tu red local, y ya no escucha nada en el). Quitala con: roost share stop --lan");
        }
    }
}
