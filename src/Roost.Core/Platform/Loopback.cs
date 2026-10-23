using System.Net;
using System.Net.Sockets;

namespace Roost.Core.Platform;

/// <summary>
/// Qué direcciones de loopback tiene la máquina. Roost escucha en <c>127.0.0.1</c> y, si existe,
/// también en <c>[::1]</c>: Windows resuelve <c>localhost</c> primero a <c>::1</c>, y si ahí no hay
/// nadie de Roost pero sí otro servidor en <c>[::]:80</c>, <c>http://localhost</c> acabaría en él.
/// En una máquina con IPv6 quitado (raro: <c>DisabledComponents</c> en el registro) no se intenta,
/// porque nginx y Apache no arrancarían con un <c>listen [::1]</c> que no se puede abrir.
/// </summary>
public static class Loopback
{
    private static readonly Lazy<bool> _ipv6 = new(Probe);

    /// <summary>true si se puede escuchar en <c>[::1]</c>. Se comprueba una vez por proceso.</summary>
    public static bool HasIpv6 => _ipv6.Value;

    private static bool Probe()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
