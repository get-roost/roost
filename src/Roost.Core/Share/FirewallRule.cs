namespace Roost.Core.Share;

/// <summary>
/// La regla del cortafuegos de Windows de <c>roost share --lan</c>: lo que ejecuta <c>roost-helper</c>
/// (elevado) para dejar entrar a un puerto desde la red local, y para quitarlo. Como
/// <c>HostsBlock</c> y <c>NrptScripts</c>, se compila también dentro del helper (enlazado como
/// fuente), así que no puede depender de nada más de Roost. Lo único que llega de fuera es el
/// puerto, que se valida aquí: el nombre, el grupo, el perfil (solo el privado), el protocolo y el
/// origen (solo la subred local) son fijos.
/// </summary>
internal static class FirewallRule
{
    /// <summary>El grupo de las reglas de Roost (se ve en «Firewall de Windows con seguridad avanzada»).</summary>
    public const string Group = "Roost";

    /// <summary>Lo que dice la regla, para quien la vea en la consola del cortafuegos.</summary>
    public const string Description = "Roost: compartir un sitio en la red local (roost share --lan). Roost la quita al dejar de compartir.";

    private const string DisplayPrefix = "Roost LAN ";

    /// <summary>El nombre que se ve: «Roost LAN 8443».</summary>
    public static string DisplayName(int port) => DisplayPrefix + port;

    /// <summary>El identificador único de la regla (<c>-Name</c> de <c>New-NetFirewallRule</c>): «Roost-LAN-8443».</summary>
    public static string Name(int port) => "Roost-LAN-" + port;

    /// <summary>
    /// Puertos que nunca se abren, aunque estén en el rango: los de administrar el equipo a distancia
    /// (escritorio remoto, WinRM, VNC) y los de las bases de datos de siempre (otro MySQL o un Redis
    /// del usuario que escuchen en todas las interfaces quedarían a la vista de la red).
    /// </summary>
    public static readonly int[] Refused = [1433, 1521, 3306, 3389, 5432, 5900, 5985, 5986, 6379, 27017];

    /// <summary>Un puerto de servidor web: el 80, el 443 o uno de 1024 a 65535 que no sea de <see cref="Refused"/>.</summary>
    public static bool IsAllowedPort(int port) =>
        (port is 80 or 443 || port is >= 1024 and <= 65535) && Array.IndexOf(Refused, port) < 0;

    /// <summary>
    /// PURO: el puerto de un argumento, solo si son de 1 a 5 cifras ASCII sin cero delante (nada de
    /// signos, espacios, «0x» ni cifras de otros alfabetos) y es un puerto permitido.
    /// </summary>
    public static bool TryParsePort(string? text, out int port)
    {
        port = 0;
        if (text is null || text.Length is 0 or > 5 || text[0] == '0') return false;
        var value = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') return false;
            value = value * 10 + (c - '0');
        }
        if (!IsAllowedPort(value)) return false;
        port = value;
        return true;
    }

    /// <summary>PURO: el puerto de una regla de Roost por su nombre visible («Roost LAN 8443»), o false si no es una.</summary>
    public static bool TryParseDisplayName(string? name, out int port)
    {
        port = 0;
        return name is not null && name.StartsWith(DisplayPrefix, StringComparison.Ordinal)
               && TryParsePort(name[DisplayPrefix.Length..], out port);
    }

    /// <summary>
    /// Pone la regla de <paramref name="port"/> (quitando antes la que hubiera con ese nombre, para
    /// que repetirlo no la duplique): entrada TCP a ese puerto, solo en el perfil privado y solo desde
    /// la subred local.
    /// </summary>
    public static string AllowScript(int port)
    {
        Check(port);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            Get-NetFirewallRule -Name '{{Name(port)}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -Name '{{Name(port)}}' -DisplayName '{{DisplayName(port)}}' -Group '{{Group}}' -Description '{{Description}}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{port}} -Profile Private -RemoteAddress LocalSubnet | Out-Null
            exit 0
            """;
    }

    /// <summary>Quita la regla de <paramref name="port"/> si está (solo esa: la busca por su identificador).</summary>
    public static string RemoveScript(int port)
    {
        Check(port);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            Get-NetFirewallRule -Name '{{Name(port)}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            exit 0
            """;
    }

    private static void Check(int port)
    {
        if (!IsAllowedPort(port)) throw new ArgumentOutOfRangeException(nameof(port), port, "Puerto no permitido para la regla del cortafuegos.");
    }

    // Linux (L1, Privilegios): ufw (Ubuntu) y firewalld (Fedora), por el helper y solo si están
    // activos. Como no hay un «LocalSubnet» que valga para los dos, se deja entrar solo desde las
    // redes privadas de RFC 1918, que es lo que se comparte con roost share --lan. Todo por rutas
    // fijas y con los argumentos ya partidos: lo único que cambia es el puerto, ya validado.

    /// <summary>Las redes desde las que se deja entrar en Linux (RFC 1918): lo más parecido al perfil privado + subred local de Windows.</summary>
    public static readonly string[] PrivateNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    /// <summary>ufw, por su ruta: no se busca en el PATH.</summary>
    public const string UfwPath = "/usr/sbin/ufw";

    /// <summary>firewall-cmd (firewalld), por su ruta.</summary>
    public const string FirewalldPath = "/usr/bin/firewall-cmd";

    /// <summary>
    /// Los argumentos de <c>ufw</c> para una red: dejar entrar TCP a ese puerto desde ella (con el
    /// comentario «Roost LAN &lt;puerto&gt;», que es como se ve en <c>ufw status</c>), o quitar justo esa
    /// regla (<c>--force delete</c> con la misma especificación: ufw no pregunta).
    /// </summary>
    public static string[] UfwArguments(int port, string network, bool allow)
    {
        Check(port);
        string[] rule = ["allow", "in", "proto", "tcp", "from", network, "to", "any", "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "comment", DisplayName(port)];
        return allow ? rule : ["--force", "delete", .. rule];
    }

    /// <summary>La regla enriquecida de firewalld para una red: solo IPv4, solo ese puerto TCP, solo desde esa red.</summary>
    public static string FirewalldRichRule(int port, string network)
    {
        Check(port);
        return $"rule family=\"ipv4\" source address=\"{network}\" port port=\"{port}\" protocol=\"tcp\" accept";
    }

    /// <summary>Los argumentos de <c>firewall-cmd</c> para poner o quitar esa regla, en caliente o de forma permanente (hacen falta los dos).</summary>
    public static string[] FirewalldArguments(int port, string network, bool allow, bool permanent)
    {
        var action = (allow ? "--add-rich-rule=" : "--remove-rich-rule=") + FirewalldRichRule(port, network);
        return permanent ? ["--permanent", action] : [action];
    }
}
