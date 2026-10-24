using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Share;

/// <summary>
/// La regla «Roost LAN &lt;puerto&gt;» del cortafuegos (<see cref="FirewallRule"/>): ponerla y quitarla
/// lo hace <c>roost-helper firewall-allow|firewall-remove &lt;puerto&gt;</c> con privilegios
/// (<see cref="Elevation"/>), y saber cuáles hay se lee sin privilegios: en Windows con
/// <see cref="LanProbe"/>; en Linux, donde <c>ufw status</c> pide root, con la lista de los puertos
/// que Roost abrió y aún no cerró (<c>state/lan-firewall.txt</c>). En Linux el helper solo toca ufw o
/// firewalld si están activos; si no hay cortafuegos, no hay nada que abrir. Lo usan
/// <c>roost share --lan</c>, la app y <c>roost uninstall-cleanup</c>.
/// </summary>
public static class LanFirewall
{
    /// <summary>El nombre que se ve en el cortafuegos: «Roost LAN 8443».</summary>
    public static string RuleName(int port) => FirewallRule.DisplayName(port);

    /// <summary>Si el helper acepta ese puerto (el 80, el 443 o de 1024 a 65535, sin los de administración remota ni los de bases de datos).</summary>
    public static bool IsAllowedPort(int port) => FirewallRule.IsAllowedPort(port);

    /// <summary>Pone la regla con <paramref name="helperExe"/> con privilegios. Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public static void Allow(string helperExe, int port, RoostPaths? paths = null, Elevation? elevation = null)
    {
        RunHelper(helperExe, "firewall-allow", port, elevation);
        if (!OperatingSystem.IsWindows()) Remember(paths ?? RoostPaths.Default(), port, opened: true);
    }

    /// <summary>Quita la regla con <paramref name="helperExe"/> con privilegios. Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public static void Remove(string helperExe, int port, RoostPaths? paths = null, Elevation? elevation = null)
    {
        RunHelper(helperExe, "firewall-remove", port, elevation);
        if (!OperatingSystem.IsWindows()) Remember(paths ?? RoostPaths.Default(), port, opened: false);
    }

    /// <summary>Los puertos con una regla de Roost puesta, leídos sin privilegios (nunca lanza salvo por cancelación).</summary>
    public static async Task<IReadOnlyList<int>> RulePortsAsync(ICommandRunner runner, CancellationToken cancellation = default, RoostPaths? paths = null) =>
        OperatingSystem.IsWindows()
            ? (await LanProbe.ReadAsync(runner, cancellation)).RulePorts
            : Remembered(paths ?? RoostPaths.Default());

    /// <summary>Linux: el fichero con los puertos que Roost ha abierto en el cortafuegos y aún no ha cerrado, uno por línea.</summary>
    public static string RecordFile(RoostPaths paths) => Path.Combine(paths.State, "lan-firewall.txt");

    /// <summary>Linux: los puertos apuntados en <see cref="RecordFile"/>.</summary>
    public static IReadOnlyList<int> Remembered(RoostPaths paths)
    {
        try
        {
            var file = RecordFile(paths);
            if (!File.Exists(file)) return [];
            return File.ReadAllLines(file)
                .Select(l => FirewallRule.TryParsePort(l.Trim(), out var port) ? port : 0)
                .Where(p => p > 0).Distinct().Order().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Remember(RoostPaths paths, int port, bool opened)
    {
        var ports = Remembered(paths).ToList();
        if (opened && !ports.Contains(port)) ports.Add(port);
        if (!opened) ports.Remove(port);
        Directory.CreateDirectory(paths.State);
        var file = RecordFile(paths);
        if (ports.Count == 0) File.Delete(file);
        else File.WriteAllLines(file, ports.Order().Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void RunHelper(string helperExe, string operation, int port, Elevation? elevation)
    {
        if (!FirewallRule.IsAllowedPort(port))
            throw new RoostException($"El puerto {port} no se abre en el cortafuegos: usa el 80, el 443 o uno de 1024 a 65535 (salvo los de escritorio remoto y bases de datos).");

        var run = (elevation ?? Elevation.Default).Run(helperExe, [operation, port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (run.Status == ElevationStatus.Cancelled)
        {
            throw new RoostException(operation == "firewall-allow"
                ? "Has cancelado el permiso de administrador: el cortafuegos no se ha tocado."
                : $"Has cancelado el permiso de administrador: la regla «{RuleName(port)}» sigue en el cortafuegos (solo deja entrar a ese puerto desde tu red local, y ya no escucha nada en el). Quitala con: roost share stop --lan");
        }
        if (run.ExitCode != 0)
            throw new RoostException($"roost-helper no pudo cambiar la regla del cortafuegos «{RuleName(port)}» (codigo {run.ExitCode}). Detalle en {HostsUpdater.HelperLog}.{(run.Output.Length == 0 ? "" : $" ({run.Output})")}");
    }
}
