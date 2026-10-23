using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace HouseControl.Core.Discovery;

/// <summary>One resource record of a DNS answer, with the parts discovery cares about decoded.</summary>
internal sealed record DnsRecord(IReadOnlyList<string> Name, ushort Type, uint Ttl)
{
    /// <summary>PTR: the name pointed to. SRV: the host name of the service.</summary>
    public IReadOnlyList<string>? Target { get; init; }
    /// <summary>SRV: the service's port.</summary>
    public ushort Port { get; init; }
    /// <summary>TXT: the "key=value" strings, keys in lower case (they are case-insensitive).</summary>
    public IReadOnlyDictionary<string, string>? Txt { get; init; }
    /// <summary>A / AAAA: the address.</summary>
    public IPAddress? Address { get; init; }

    public string Key => MdnsPacket.Key(Name);
}

/// <summary>
/// Just enough of the DNS wire format (RFC 1035, as mDNS uses it in RFC 6762) to ask who
/// offers a service and to read the answers: names with compression, PTR, SRV, TXT, A, AAAA.
/// </summary>
internal static class MdnsPacket
{
    public const ushort A = 1, Ptr = 12, Txt = 16, Aaaa = 28, Srv = 33;

    /// <summary>A name compared the way DNS does: case-insensitively.</summary>
    public static string Key(IEnumerable<string> labels) => string.Join(".", labels).ToLowerInvariant();

    public static IReadOnlyList<string> Labels(string name) =>
        name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A query with one question per (name, type), class IN, no compression.</summary>
    public static byte[] Query(IEnumerable<(IReadOnlyList<string> Name, ushort Type)> questions)
    {
        var list = questions.ToList();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)list.Count);
        stream.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var (name, type) in list)
        {
            WriteName(stream, name);
            BinaryPrimitives.WriteUInt16BigEndian(tail, type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // IN
            stream.Write(tail);
        }
        return stream.ToArray();
    }

    private static void WriteName(Stream stream, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException($"Bad DNS label \"{label}\"");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    /// <summary>
    /// Every answer, authority and additional record of a response. Null for something that is
    /// not a well-formed DNS response (a query, a truncated or looping packet); never throws.
    /// </summary>
    public static List<DnsRecord>? Parse(ReadOnlySpan<byte> packet)
    {
        try
        {
            return ParseOrThrow(packet);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static List<DnsRecord>? ParseOrThrow(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) == 0) return null; // a question, not an answer
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
                      + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
                      + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref offset);
            offset += 4;
        }
        var list = new List<DnsRecord>();
        for (var i = 0; i < records; i++)
        {
            var name = ReadName(packet, ref offset);
            Need(packet, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            Need(packet, offset, length);
            var data = offset;
            offset += length;
            var record = new DnsRecord(name, type, ttl);
            switch (type)
            {
                case Ptr:
                    var at = data;
                    record = record with { Target = ReadName(packet, ref at) };
                    break;
                case Srv when length >= 7:
                    var target = data + 6;
                    record = record with
                    {
                        Port = BinaryPrimitives.ReadUInt16BigEndian(packet[(data + 4)..]),
                        Target = ReadName(packet, ref target),
                    };
                    break;
                case Txt:
                    record = record with { Txt = ReadTxt(packet.Slice(data, length)) };
                    break;
                case A when length == 4:
                    record = record with { Address = new IPAddress(packet.Slice(data, 4)) };
                    break;
                case Aaaa when length == 16:
                    record = record with { Address = new IPAddress(packet.Slice(data, 16)) };
                    break;
                default:
                    continue; // something discovery does not use
            }
            list.Add(record);
        }
        return list;
    }

    private static void Need(ReadOnlySpan<byte> packet, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > packet.Length) throw new FormatException("Truncated DNS packet");
    }

    /// <summary>A name at <paramref name="offset"/>, following compression pointers; moves past it.</summary>
    internal static List<string> ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            Need(packet, position, 1);
            int length = packet[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                Need(packet, position, 2);
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]) & 0x3FFF;
                if (!jumped) offset = position + 2;
                jumped = true;
                if (++jumps > 64 || pointer >= packet.Length) throw new FormatException("Looping DNS name");
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0) throw new FormatException("Unknown DNS label type");
            Need(packet, position + 1, length);
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
            if (labels.Count > 128) throw new FormatException("DNS name too long");
        }
        if (!jumped) offset = position;
        return labels;
    }

    private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> data)
    {
        var txt = new Dictionary<string, string>();
        var at = 0;
        while (at < data.Length)
        {
            int length = data[at++];
            if (at + length > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(at, length));
            at += length;
            if (entry.Length == 0) continue;
            var equals = entry.IndexOf('=');
            var key = (equals < 0 ? entry : entry[..equals]).ToLowerInvariant();
            if (key.Length > 0 && !txt.ContainsKey(key)) txt[key] = equals < 0 ? "" : entry[(equals + 1)..];
        }
        return txt;
    }
}
