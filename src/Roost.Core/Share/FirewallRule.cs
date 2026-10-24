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
}
