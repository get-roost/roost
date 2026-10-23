using System.Buffers.Binary;
using System.Text;

namespace Roost.Core.Dns;

/// <summary>Una pregunta DNS ya leída: lo justo para contestarla.</summary>
public sealed record DnsQuery(ushort Id, bool RecursionDesired, string Name, ushort Type, ushort Class, byte[] Question);

/// <summary>
/// PURO: leer una pregunta DNS (RFC 1035) y contestarla como lo hace el DNS local de Roost
/// (ROOST.md §5.7). Cualquier <c>*.test</c> (también los subdominios, como el certificado) es
/// <c>127.0.0.1</c>; a AAAA se contesta sin respuesta, porque nginx solo escucha en IPv4 y un
/// <c>::1</c> haría que el navegador probara primero una dirección que no responde. Lo que no es
/// <c>.test</c> se rechaza (REFUSED): Roost no es el DNS de nada más.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort ClassIn = 1;

    private const int HeaderSize = 12;
    private const byte RcodeNoError = 0, RcodeFormErr = 1, RcodeRefused = 5;

    /// <summary>Lee la primera (y única) pregunta de un mensaje; false si no es una pregunta DNS válida.</summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> message, out DnsQuery? query)
    {
        query = null;
        if (message.Length < HeaderSize) return false;
        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        if ((flags & 0x8000) != 0 || ((flags >> 11) & 0xF) != 0 || questions != 1) return false; // respuesta, no QUERY o varias

        var name = new StringBuilder();
        var pos = HeaderSize;
        while (true)
        {
            if (pos >= message.Length) return false;
            var length = message[pos++];
            if (length == 0) break;
            if (length > 63 || pos + length > message.Length) return false; // sin compresión en una pregunta
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(message.Slice(pos, length)));
            pos += length;
            if (name.Length > 253) return false;
        }
        if (pos + 4 > message.Length) return false;
        var type = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 2)..]);
        pos += 4;

        query = new DnsQuery(id, (flags & 0x0100) != 0, name.ToString(), type, cls, message[HeaderSize..pos].ToArray());
        return true;
    }

    /// <summary>true si Roost responde por este nombre: <c>test</c> o cualquier cosa terminada en <c>.test</c>.</summary>
    public static bool IsOurs(string name)
    {
        name = name.TrimEnd('.');
        return name.Equals("test", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>La respuesta a <paramref name="query"/>.</summary>
    public static byte[] Answer(DnsQuery query)
    {
        var ours = IsOurs(query.Name);
        var withAddress = ours && query.Type == TypeA && query.Class == ClassIn;
        var rcode = ours ? RcodeNoError : RcodeRefused;

        var response = new List<byte>(HeaderSize + query.Question.Length + 16);
        Header(response, query.Id, query.RecursionDesired, rcode, answers: withAddress ? (ushort)1 : (ushort)0, authoritative: ours);
        response.AddRange(query.Question);
        if (withAddress)
        {
            response.AddRange([0xC0, 0x0C]);               // el nombre: puntero a la pregunta
            Add16(response, TypeA);
            Add16(response, ClassIn);
            response.AddRange([0, 0, 0, 60]);              // TTL: 60 s
            Add16(response, 4);
            response.AddRange([127, 0, 0, 1]);
        }
        return [.. response];
    }

    /// <summary>La respuesta a algo que no se entiende: FORMERR con el mismo id, si se puede leer.</summary>
    public static byte[]? FormatError(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2) return null;
        var response = new List<byte>(HeaderSize);
        Header(response, BinaryPrimitives.ReadUInt16BigEndian(message), recursionDesired: false, RcodeFormErr, answers: 0, authoritative: false);
        response[5] = 0; // sin pregunta
        return [.. response];
    }

    private static void Header(List<byte> to, ushort id, bool recursionDesired, byte rcode, ushort answers, bool authoritative)
    {
        Add16(to, id);
        var flags = (ushort)(0x8000 | (authoritative ? 0x0400 : 0) | (recursionDesired ? 0x0100 : 0) | rcode); // QR, AA, RD; sin RA
        Add16(to, flags);
        Add16(to, 1);        // QDCOUNT
        Add16(to, answers);  // ANCOUNT
        Add16(to, 0);        // NSCOUNT
        Add16(to, 0);        // ARCOUNT
    }

    private static void Add16(List<byte> to, ushort value)
    {
        to.Add((byte)(value >> 8));
        to.Add((byte)value);
    }
}
