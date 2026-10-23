using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Roost.Core.Dns;

/// <summary>
/// Preguntar al DNS local directamente (sin pasar por la regla NRPT): para <c>roost dns status</c>
/// y la app. No lanza: si no contesta en el plazo, es false.
/// </summary>
public static class LocalDnsProbe
{
    /// <summary>true si el servidor de <paramref name="endPoint"/> (por defecto el de Roost) resuelve <paramref name="name"/> a 127.0.0.1.</summary>
    public static async Task<bool> AsksAsync(string name, CancellationToken cancellation = default, IPEndPoint? endPoint = null)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await udp.SendAsync(Query(0x524F, name, DnsMessage.TypeA), endPoint ?? LocalDns.EndPoint, timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token);
            return Equals(FirstAddress(reply.Buffer), IPAddress.Loopback);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>PURO: una pregunta DNS estándar, con recursión pedida.</summary>
    public static byte[] Query(ushort id, string name, ushort type)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return [.. bytes];
    }

    /// <summary>PURO: la primera dirección IPv4 de la sección de respuestas, o null.</summary>
    public static IPAddress? FirstAddress(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12) return null;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            pos = SkipName(response, pos);
            if (pos < 0) return null;
            pos += 4;
        }
        for (var i = 0; i < answers; i++)
        {
            pos = SkipName(response, pos);
            if (pos < 0 || pos + 10 > response.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[pos..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(response[(pos + 8)..]);
            pos += 10;
            if (pos + length > response.Length) return null;
            if (type == DnsMessage.TypeA && length == 4) return new IPAddress(response.Slice(pos, 4));
            pos += length;
        }
        return null;
    }

    private static int SkipName(ReadOnlySpan<byte> message, int pos)
    {
        while (pos < message.Length)
        {
            var length = message[pos];
            if (length == 0) return pos + 1;
            if ((length & 0xC0) == 0xC0) return pos + 2;   // puntero de compresión
            pos += 1 + length;
        }
        return -1;
    }
}
