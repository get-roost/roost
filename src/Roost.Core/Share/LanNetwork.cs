using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Roost.Core.Scanning;

namespace Roost.Core.Share;

/// <summary>Una dirección IPv4 de este equipo en una interfaz que está en marcha.</summary>
/// <param name="HasGateway">La interfaz tiene puerta de enlace (es la que sale a la red de verdad).</param>
public sealed record LanCandidate(IPAddress Address, int InterfaceIndex, string Name, string Description, NetworkInterfaceType Type, bool HasGateway)
{
    public override string ToString() => $"{Address} ({Name})";
}

/// <summary>Cómo tiene Windows marcada una red: el cortafuegos solo deja entrar en las privadas (la regla de Roost es solo para ese perfil).</summary>
public enum NetworkCategory
{
    Unknown,
    Public,
    Private,
    DomainAuthenticated,
}

/// <summary>
/// Qué IP de la red local se usa para <c>roost share --lan</c>: una IPv4 privada (10/8, 172.16/12,
/// 192.168/16) de una interfaz en marcha, preferida la que tiene puerta de enlace, y nunca la de
/// un adaptador virtual (Hyper-V, WSL, VirtualBox, VMware, VPN) salvo que se pida por su IP. Así
/// se escucha solo en esa dirección, no en <c>0.0.0.0</c>: ni en la VPN ni en las redes virtuales.
/// </summary>
public static class LanNetwork
{
    /// <summary>Las direcciones IPv4 de las interfaces en marcha de este equipo (nunca lanza).</summary>
    public static IReadOnlyList<LanCandidate> Candidates()
    {
        var found = new List<LanCandidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties properties;
                int index;
                try
                {
                    properties = nic.GetIPProperties();
                    index = properties.GetIPv4Properties()?.Index ?? -1;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }
                var gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    found.Add(new LanCandidate(unicast.Address, index, nic.Name, nic.Description, nic.NetworkInterfaceType, gateway));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Sin interfaces que leer: nada que ofrecer.
        }
        return found;
    }

    /// <summary>PURO: una IPv4 de las redes privadas (10/8, 172.16/12, 192.168/16).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static readonly string[] VirtualHints =
    [
        "hyper-v", "vethernet", "wsl", "virtualbox", "vmware", "virtual", "vpn", "tap-", "tap ", "wireguard", "tailscale",
        "zerotier", "openvpn", "fortinet", "cisco anyconnect", "docker", "loopback", "bluetooth",
    ];

    /// <summary>PURO: parece un adaptador virtual o de VPN, por su nombre o su descripción.</summary>
    public static bool LooksVirtual(LanCandidate candidate) =>
        candidate.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
        || VirtualHints.Any(h => candidate.Name.Contains(h, StringComparison.OrdinalIgnoreCase) || candidate.Description.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>PURO: las que valen para enseñar un sitio en la red local, la preferida primero.</summary>
    public static IReadOnlyList<LanCandidate> Usable(IEnumerable<LanCandidate> candidates) =>
        candidates
            .Where(c => IsPrivate(c.Address) && !LooksVirtual(c))
            .OrderByDescending(c => c.HasGateway)
            .ThenByDescending(c => c.Type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .ThenBy(c => c.InterfaceIndex)
            .ToList();

    /// <summary>
    /// PURO: la dirección que se usa. Con <paramref name="wanted"/> (<c>--ip</c>), tiene que ser una
    /// IPv4 privada de este equipo, aunque sea de un adaptador virtual; si no, la primera de
    /// <see cref="Usable"/>. Lanza <see cref="RoostException"/> si no hay ninguna que valga.
    /// </summary>
    public static LanCandidate Choose(IReadOnlyList<LanCandidate> candidates, string? wanted)
    {
        if (wanted is not null)
        {
            if (!IPAddress.TryParse(wanted, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new RoostException($"{wanted} no es una direccion IPv4.");
            if (!IsPrivate(address))
                throw new RoostException($"{wanted} no es una direccion de red local (10.x, 172.16-31.x o 192.168.x): Roost no comparte en una IP publica.");
            return candidates.FirstOrDefault(c => c.Address.Equals(address))
                ?? throw new RoostException($"{wanted} no es una direccion de este equipo. Las que tiene: {Describe(candidates.Where(c => IsPrivate(c.Address)))}.");
        }

        return Usable(candidates).FirstOrDefault()
            ?? throw new RoostException(
                "Este equipo no tiene ninguna direccion de red local (wifi o cable) en marcha"
                + (candidates.Any(c => IsPrivate(c.Address)) ? $"; solo de adaptadores virtuales o VPN ({Describe(candidates.Where(c => IsPrivate(c.Address)))}), que se pueden elegir con --ip." : "."));
    }

    /// <summary>
    /// PURO: el puerto de la escucha de la red local: el HTTPS de Roost si nadie lo usa ya en esa
    /// dirección (así la URL es la de siempre con la IP, <c>https://192.168.1.20:8443</c>), o el
    /// siguiente libre. <paramref name="taken"/> dice si un puerto ya está cogido en esa IP (por
    /// otra escucha de compartir o por otro programa).
    /// </summary>
    public static int ChoosePort(int httpsPort, Func<int, bool> taken)
    {
        for (var port = httpsPort; port < httpsPort + 50 && port <= 65535; port++)
        {
            if (FirewallRule.IsAllowedPort(port) && !taken(port)) return port;
        }
        throw new RoostException($"No hay ningun puerto libre para compartir en la red local entre el {httpsPort} y el {httpsPort + 49}.");
    }

    /// <summary>La URL de la escucha: <c>https://192.168.1.20:8443</c> (sin el puerto si es el 443).</summary>
    public static string Url(string address, int port) => port == 443 ? $"https://{address}/" : $"https://{address}:{port}/";

    /// <summary>true si se puede abrir <paramref name="port"/> en <paramref name="address"/> (solo bind, sin escuchar: no avisa al cortafuegos).</summary>
    public static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Un puerto libre de 127.0.0.1 (el sistema lo elige; solo bind, sin escuchar).</summary>
    public static int FreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static string Describe(IEnumerable<LanCandidate> candidates)
    {
        var list = candidates.Select(c => c.ToString()).ToList();
        return list.Count == 0 ? "ninguna" : string.Join(", ", list);
    }
}

/// <summary>Lo que dice Windows de las redes y de las reglas de Roost en el cortafuegos.</summary>
/// <param name="Categories">El tipo de red de cada interfaz (por su índice).</param>
/// <param name="RulePorts">Los puertos que ya tienen una regla «Roost LAN &lt;puerto&gt;».</param>
public sealed record LanProbeResult(IReadOnlyDictionary<int, NetworkCategory> Categories, IReadOnlyList<int> RulePorts)
{
    public static readonly LanProbeResult Empty = new(new Dictionary<int, NetworkCategory>(), []);

    public NetworkCategory CategoryOf(int interfaceIndex) => Categories.GetValueOrDefault(interfaceIndex, NetworkCategory.Unknown);
}

/// <summary>
/// Lee, SIN privilegios (con el PowerShell del sistema y sin cambiar nada), el tipo de cada red
/// (<c>Get-NetConnectionProfile</c>) y las reglas del grupo «Roost» del cortafuegos
/// (<c>Get-NetFirewallRule</c>). Poner y quitar reglas solo lo hace <c>roost-helper</c>. En Linux no
/// hay perfiles de red: el tipo es siempre «desconocido» (la CLI y la app lo avisan) y los puertos
/// abiertos los recuerda <see cref="LanFirewall"/>, porque leer ufw pide root.
/// </summary>
public static class LanProbe
{
    /// <summary>El script, fijo: solo lee.</summary>
    public const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        $profiles = @(Get-NetConnectionProfile | ForEach-Object { [pscustomobject]@{ i = [int]$_.InterfaceIndex; c = [string]$_.NetworkCategory } })
        $rules = @(Get-NetFirewallRule -Group 'Roost' | ForEach-Object { [string]$_.DisplayName })
        [pscustomobject]@{ profiles = $profiles; rules = $rules } | ConvertTo-Json -Compress -Depth 3
        """;

    /// <summary>El PowerShell del sistema por su ruta, con el script en <c>-EncodedCommand</c> (sin comillas que escapar).</summary>
    public static CommandSpec Command() => new(
        Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script))],
        Timeout: TimeSpan.FromSeconds(30));

    /// <summary>Lo pregunta; si algo falla, lo que se sepa (nunca lanza salvo por cancelación).</summary>
    public static async Task<LanProbeResult> ReadAsync(ICommandRunner runner, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows()) return LanProbeResult.Empty;
        try
        {
            var result = await runner.RunAsync(Command(), null, cancellation);
            return result.Succeeded ? Parse(result.Stdout) : LanProbeResult.Empty;
        }
        catch (RoostException)
        {
            return LanProbeResult.Empty;
        }
    }

    /// <summary>PURO: lee el JSON del script (una lista, o un objeto suelto si solo hay uno).</summary>
    public static LanProbeResult Parse(string json)
    {
        try
        {
            // Solo la línea del JSON: PowerShell puede escribir antes otras cosas (el «#< CLIXML» del progreso).
            var line = json.Split('\n').Select(l => l.Trim().TrimStart((char)0xFEFF)).FirstOrDefault(l => l.StartsWith('{'));
            if (line is null) return LanProbeResult.Empty;
            using var doc = JsonDocument.Parse(line);
            var categories = new Dictionary<int, NetworkCategory>();
            if (doc.RootElement.TryGetProperty("profiles", out var profiles))
            {
                foreach (var profile in Items(profiles))
                {
                    if (!profile.TryGetProperty("i", out var i) || !i.TryGetInt32(out var index)) continue;
                    categories[index] = profile.TryGetProperty("c", out var c) ? ParseCategory(c) : NetworkCategory.Unknown;
                }
            }
            var ports = new List<int>();
            if (doc.RootElement.TryGetProperty("rules", out var rules))
            {
                foreach (var rule in Items(rules))
                {
                    if (rule.ValueKind == JsonValueKind.String && FirewallRule.TryParseDisplayName(rule.GetString(), out var port) && !ports.Contains(port))
                        ports.Add(port);
                }
            }
            ports.Sort();
            return new LanProbeResult(categories, ports);
        }
        catch (JsonException)
        {
            return LanProbeResult.Empty;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().ToList(),
        JsonValueKind.Object or JsonValueKind.String => [element],
        _ => [],
    };

    private static NetworkCategory ParseCategory(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n switch { 0 => NetworkCategory.Public, 1 => NetworkCategory.Private, 2 => NetworkCategory.DomainAuthenticated, _ => NetworkCategory.Unknown },
            JsonValueKind.String => value.GetString() switch
            {
                "Public" => NetworkCategory.Public,
                "Private" => NetworkCategory.Private,
                "DomainAuthenticated" => NetworkCategory.DomainAuthenticated,
                _ => NetworkCategory.Unknown,
            },
            _ => NetworkCategory.Unknown,
        };
}
