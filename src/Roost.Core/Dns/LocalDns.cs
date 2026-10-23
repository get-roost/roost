using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace Roost.Core.Dns;

/// <summary>Una regla NRPT tal como está en el registro: los espacios de nombres y sus servidores.</summary>
public sealed record NrptRule(IReadOnlyList<string> Namespaces, string Servers, string? Comment);

/// <summary>
/// El DNS local para los <c>.test</c> (ROOST.md §5.7, fase 2): en vez de editar el <c>hosts</c> en
/// cada alta o baja de un sitio (un UAC cada vez), una regla NRPT de Windows manda las preguntas de
/// <c>.test</c> al servidor de Roost, una sola vez con permiso de administrador. La regla la pone y
/// la quita <c>roost-helper</c>; leerla no necesita privilegios.
/// </summary>
public static class LocalDns
{
    /// <summary>
    /// Una dirección de loopback para Roost y no <c>127.0.0.1</c>: así no choca con otro DNS local
    /// (Acrylic, dnsmasq) ni con lo que tenga el 53 en todas las direcciones (en Windows, la conexión
    /// compartida, que usan Hyper-V y WSL). NRPT solo sabe preguntar al puerto 53.
    /// </summary>
    public static readonly IPAddress Address = IPAddress.Parse(NrptScripts.Address);

    public const int Port = 53;

    public const string Namespace = NrptScripts.Namespace;

    /// <summary>El comentario con el que se marca la regla de Roost (y por el que se reconoce).</summary>
    public const string RuleComment = NrptScripts.Comment;

    public static IPEndPoint EndPoint => new(Address, Port);

    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";

    /// <summary>Las reglas NRPT locales (las que hay en el registro; las de directiva de grupo van aparte).</summary>
    public static IReadOnlyList<NrptRule> ReadRules()
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

    /// <summary>PURO: true si alguna regla manda <c>.test</c> a <see cref="Address"/>.</summary>
    public static bool HasRoostRule(IEnumerable<NrptRule> rules) =>
        rules.Any(r => r.Namespaces.Any(n => n.Equals(Namespace, StringComparison.OrdinalIgnoreCase))
                       && r.Servers.Split(';', StringSplitOptions.TrimEntries).Contains(Address.ToString()));

    /// <summary>PURO: una regla de <c>.test</c> que NO es de Roost (otra herramienta ya los resuelve).</summary>
    public static NrptRule? ForeignRule(IEnumerable<NrptRule> rules) =>
        rules.FirstOrDefault(r => r.Namespaces.Any(n => n.Equals(Namespace, StringComparison.OrdinalIgnoreCase)) && r.Comment != RuleComment);

    public static bool IsRuleInstalled() => HasRoostRule(ReadRules());

    /// <summary>Pone la regla NRPT con <paramref name="helperExe"/> elevado (UAC). Lanza si se cancela o falla.</summary>
    public static void InstallRule(string helperExe) => RunHelper(helperExe, "dns-enable");

    /// <summary>Quita la regla NRPT de Roost con <paramref name="helperExe"/> elevado (UAC).</summary>
    public static void RemoveRule(string helperExe) => RunHelper(helperExe, "dns-disable");

    private static void RunHelper(string helperExe, string operation)
    {
        if (!OperatingSystem.IsWindows())
            throw new RoostException("El DNS local solo esta hecho para Windows por ahora.");
        if (!File.Exists(helperExe))
            throw new RoostException($"No se encuentra {helperExe}, que es el que pone la regla de DNS con permisos de administrador.");

        var start = new ProcessStartInfo(helperExe, operation)
        {
            UseShellExecute = true,
            Verb = "runas", // UAC
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var helper = Process.Start(start) ?? throw new RoostException("No se pudo lanzar roost-helper.");
            helper.WaitForExit();
            switch (helper.ExitCode)
            {
                case 0: return;
                case 69:
                    throw new RoostException(
                        "Ya hay una regla de DNS para .test que no es de Roost (otra herramienta resuelve esos nombres). "
                        + "Quitala o sigue con el fichero hosts.");
                default:
                    throw new RoostException($"roost-helper no pudo cambiar la regla de DNS (codigo {helper.ExitCode}). Detalle en {Sites.HostsUpdater.HelperLog}.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // «No» en el UAC
        {
            throw new RoostException("Has cancelado el permiso de administrador: la regla de DNS no se ha tocado.");
        }
    }
}
