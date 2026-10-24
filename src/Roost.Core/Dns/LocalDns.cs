using System.Net;
using Microsoft.Win32;
using Roost.Core.Platform;

namespace Roost.Core.Dns;

/// <summary>
/// Una regla de resolución tal como está en el sistema: los espacios de nombres y sus servidores. En
/// Windows es una regla NRPT del registro; en Linux, lo que dice un fichero de systemd-resolved
/// (<c>Domains=~test</c> como <c>.test</c>, los <c>DNS=</c> separados por <c>;</c>).
/// </summary>
public sealed record NrptRule(IReadOnlyList<string> Namespaces, string Servers, string? Comment);

/// <summary>
/// El DNS local para los <c>.test</c> (ROOST.md §5.7, fase 2): en vez de editar el <c>hosts</c> en
/// cada alta o baja de un sitio (un permiso cada vez), una sola vez con permiso de administrador se
/// manda <c>.test</c> al servidor de Roost: en Windows con una regla NRPT, en Linux con un drop-in de
/// systemd-resolved (<see cref="ResolvedDropIn"/>). Lo pone y lo quita <c>roost-helper</c>; leerlo
/// no necesita privilegios. Sin systemd-resolved (Debian 12 de serie, un WSL sin systemd) no está
/// disponible, y en WSL los <c>.test</c> los resuelve Windows: <see cref="WhyNotAvailable"/>.
/// </summary>
public static class LocalDns
{
    /// <summary>
    /// Una dirección de loopback para Roost y no <c>127.0.0.1</c>: así no choca con otro DNS local
    /// (Acrylic, dnsmasq) ni con lo que tenga el 53 en todas las direcciones (en Windows, la conexión
    /// compartida, que usan Hyper-V y WSL).
    /// </summary>
    public static readonly IPAddress Address = IPAddress.Parse(NrptScripts.Address);

    /// <summary>53 en Windows (NRPT solo sabe preguntar al 53) y 5353 en Linux (resolved admite <c>ip:puerto</c>, y así el servidor de Roost escucha sin privilegios).</summary>
    public static int Port => OperatingSystem.IsWindows() ? 53 : ResolvedDropIn.Port;

    public const string Namespace = NrptScripts.Namespace;

    /// <summary>El comentario con el que se marca la regla de Roost (y por el que se reconoce).</summary>
    public const string RuleComment = NrptScripts.Comment;

    public static IPEndPoint EndPoint => new(Address, Port);

    /// <summary>Cómo se llama la regla en este sistema, para los mensajes.</summary>
    public static string RuleLabel =>
        OperatingSystem.IsWindows()
            ? $"Regla NRPT .test -> {Address}"
            : $"Drop-in de systemd-resolved {ResolvedDropIn.FilePath} (.test -> {ResolvedDropIn.Server})";

    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";

    /// <summary>
    /// null si el DNS local se puede usar en este sistema; si no, el porqué (sin tildes, para la CLI):
    /// en WSL los <c>.test</c> los resuelve Windows, y en Linux hace falta systemd-resolved en marcha.
    /// </summary>
    public static string? WhyNotAvailable()
    {
        if (OperatingSystem.IsWindows()) return null;
        if (Wsl.IsWsl) return "en WSL los .test los resuelve Windows con su hosts, asi que el DNS local de Roost no se usa aqui (sigue con el hosts)";
        if (!Directory.Exists("/run/systemd/system")) return "este sistema no arranca con systemd, y el DNS local va con systemd-resolved";
        if (!Directory.Exists("/run/systemd/resolve")) return "systemd-resolved no esta en marcha (Debian 12 de serie no lo trae): no disponible en esta distro";
        return null;
    }

    /// <summary>Las reglas que hay en el sistema: las NRPT locales del registro (Windows) o lo que dicen los ficheros de resolved (Linux).</summary>
    public static IReadOnlyList<NrptRule> ReadRules() =>
        OperatingSystem.IsWindows() ? ReadNrpt() : ReadResolved(ResolvedDropIn.Directory, ResolvedDropIn.MainFile);

    private static IReadOnlyList<NrptRule> ReadNrpt()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (key is null) return [];
            var rules = new List<NrptRule>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var rule = key.OpenSubKey(name);
                if (rule is null) continue;
                var namespaces = rule.GetValue("Name") switch
                {
                    string[] many => many,
                    string one => [one],
                    _ => [],
                };
                rules.Add(new NrptRule(namespaces, rule.GetValue("GenericDNSServers") as string ?? "", rule.GetValue("Comment") as string));
            }
            return rules;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Los ficheros de systemd-resolved como reglas: cada <c>Domains=</c> es un espacio de nombres
    /// (<c>~test</c> → <c>.test</c>), los <c>DNS=</c> van separados por <c>;</c> y el drop-in de Roost
    /// lleva su comentario. Lo que no enruta nada no cuenta.
    /// </summary>
    public static IReadOnlyList<NrptRule> ReadResolved(string directory, string mainFile) =>
        ResolvedDropIn.ReadFiles(directory, mainFile)
            .Where(e => e.Domains.Count > 0)
            .Select(e => new NrptRule(e.Domains.Select(AsNamespace).ToList(), string.Join(';', e.Servers), e.IsRoost ? RuleComment : null))
            .ToList();

    private static string AsNamespace(string domain) => "." + domain.TrimStart('~').TrimStart('.').TrimEnd('.');

    /// <summary>PURO: true si alguna regla manda <c>.test</c> a <see cref="Address"/> (al 53 o, en Linux, a <c>127.53.0.1:5353</c>).</summary>
    public static bool HasRoostRule(IEnumerable<NrptRule> rules) =>
        rules.Any(r => r.Namespaces.Any(n => n.Equals(Namespace, StringComparison.OrdinalIgnoreCase))
                       && r.Servers.Split(';', StringSplitOptions.TrimEntries).Any(s => s == Address.ToString() || s == ResolvedDropIn.Server));

    /// <summary>PURO: una regla de <c>.test</c> que NO es de Roost (otra herramienta ya los resuelve).</summary>
    public static NrptRule? ForeignRule(IEnumerable<NrptRule> rules) =>
        rules.FirstOrDefault(r => r.Namespaces.Any(n => n.Equals(Namespace, StringComparison.OrdinalIgnoreCase)) && r.Comment != RuleComment);

    public static bool IsRuleInstalled() => HasRoostRule(ReadRules());

    /// <summary>Pone la regla con <paramref name="helperExe"/> con privilegios. Lanza si se cancela o falla.</summary>
    public static void InstallRule(string helperExe, Elevation? elevation = null) => RunHelper(helperExe, "dns-enable", elevation);

    /// <summary>Quita la regla de Roost con <paramref name="helperExe"/> con privilegios.</summary>
    public static void RemoveRule(string helperExe, Elevation? elevation = null) => RunHelper(helperExe, "dns-disable", elevation);

    private static void RunHelper(string helperExe, string operation, Elevation? elevation)
    {
        var run = (elevation ?? Elevation.Default).Run(helperExe, [operation]);
        if (run.Status == ElevationStatus.Cancelled)
            throw new RoostException("Has cancelado el permiso de administrador: la regla de DNS no se ha tocado.");
        switch (run.ExitCode)
        {
            case HelperExit.Ok:
                return;
            case HelperExit.ForeignRule:
                throw new RoostException(
                    "Ya hay una regla de DNS para .test que no es de Roost (otra herramienta resuelve esos nombres). "
                    + "Quitala o sigue con el fichero hosts.");
            case HelperExit.NoResolved:
                throw new RoostException("El DNS local no esta disponible en esta distro: no hay systemd-resolved. Sigue con el fichero hosts.");
            default:
                throw new RoostException($"roost-helper no pudo cambiar la regla de DNS (codigo {run.ExitCode}). Detalle en {Sites.HostsUpdater.HelperLog}.{(run.Output.Length == 0 ? "" : $" ({run.Output})")}");
        }
    }
}
