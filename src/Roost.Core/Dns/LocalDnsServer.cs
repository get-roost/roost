using System.Net;
using System.Net.Sockets;

namespace Roost.Core.Dns;

/// <summary>
/// El servidor DNS de Roost: UDP, en una dirección de loopback propia (<see cref="LocalDns.Address"/>),
/// contestando con <see cref="DnsMessage"/>. Lo arranca roostd con el stack cuando
/// <c>[sites] resolver = "dns"</c>. Una pregunta rara no lo tumba: se contesta FORMERR o se ignora.
/// </summary>
public sealed class LocalDnsServer : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string> _log;
    private readonly bool _ipv6;
    private readonly Task _loop;

    private LocalDnsServer(UdpClient udp, Action<string> log, bool ipv6)
    {
        _udp = udp;
        _log = log;
        _ipv6 = ipv6;
        _loop = Task.Run(LoopAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>Preguntas contestadas desde que arrancó (para el estado).</summary>
    public long Answered => Interlocked.Read(ref _answered);

    private long _answered;

    /// <summary>Escucha en <paramref name="endPoint"/>. Lanza <see cref="RoostException"/> si el puerto está cogido.</summary>
    /// <param name="ipv6">Si se contesta <c>::1</c> a las AAAA (el servidor web escucha en [::1]).</param>
    public static LocalDnsServer Start(IPEndPoint endPoint, Action<string>? log = null, bool ipv6 = true)
    {
        var udp = new UdpClient(endPoint.AddressFamily);
        try
        {
            // Sin reutilizar la dirección: si otro tiene justo esta, que falle aquí y no a medias.
            udp.ExclusiveAddressUse = OperatingSystem.IsWindows();
            udp.Client.Bind(endPoint);
        }
        catch (SocketException ex)
        {
            udp.Dispose();
            throw new RoostException($"El DNS local no puede escuchar en {endPoint} (UDP): {ex.Message}. "
                + "Vuelve al fichero hosts con: roost dns remove");
        }
        return new LocalDnsServer(udp, log ?? (_ => { }), ipv6);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // En Windows, un ICMP «puerto inalcanzable» de un cliente que ya se fue llega como error al leer.
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                var reply = DnsMessage.TryParseQuery(received.Buffer, out var query)
                    ? DnsMessage.Answer(query!, _ipv6)
                    : DnsMessage.FormatError(received.Buffer);
                if (reply is null) continue;
                await _udp.SendAsync(reply, received.RemoteEndPoint, _stop.Token);
                Interlocked.Increment(ref _answered);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // El cliente ya no está o estamos parando: nada que contestar.
            }
            catch (Exception ex)
            {
                _log($"dns: pregunta que no se pudo contestar ({ex.Message})");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        try { await _loop; } catch { /* ya estaba parando */ }
        _stop.Dispose();
    }
}
