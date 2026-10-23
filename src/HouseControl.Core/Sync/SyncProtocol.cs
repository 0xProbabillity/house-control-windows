using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Sync;

/// <summary>
/// What an iPhone needs to find this PC and talk to it: shown as a QR code (or copied as a link)
/// once, kept in the phone's Keychain afterwards. The same format the Mac app uses, so the iPhone
/// app pairs with a PC exactly as it pairs with a Mac.
/// </summary>
public sealed record SyncPairing(string Id, string Name, byte[] Key)
{
    public const string Scheme = "housecontrol";

    /// <summary>A new identity and 32 random key bytes.</summary>
    public static SyncPairing Generate(string name) =>
        new(Guid.NewGuid().ToString().ToLowerInvariant(), name, RandomNumberGenerator.GetBytes(32));

    /// <summary><c>housecontrol://pair?v=1&amp;id=…&amp;name=…&amp;key=…</c>, the content of the pairing QR code.</summary>
    public string Link =>
        $"{Scheme}://pair?v=1&id={Uri.EscapeDataString(Id)}&name={Uri.EscapeDataString(Name)}&key={Base64Url(Key)}";

    /// <summary>Reads a pairing link; null for anything that is not one.</summary>
    public static SyncPairing? FromLink(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Scheme || uri.Host != "pair") return null;
        var items = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            if (!items.ContainsKey(k)) items[k] = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        if (items.GetValueOrDefault("v") != "1" || items.GetValueOrDefault("id") is not { Length: > 0 } id) return null;
        if (FromBase64Url(items.GetValueOrDefault("key") ?? "") is not { Length: 32 } key) return null;
        return new SyncPairing(id, items.GetValueOrDefault("name") ?? "PC", key);
    }

    /// <summary>The connection's pre-shared key: HMAC-SHA256 of a fixed label, keyed by the pairing key.</summary>
    public byte[] PreSharedKey => HMACSHA256.HashData(Key, "HouseControl sync v1"u8);

    public byte[] Encode() => Encoding.UTF8.GetBytes(Json.Serialize(new JsonObject
    {
        ["id"] = Id,
        ["name"] = Name,
        ["key"] = Convert.ToBase64String(Key),
    }));

    public static SyncPairing? Decode(byte[]? data) =>
        Json.ParseObject(data) is { } o && o.Str("id") is { } id && o.Str("name") is { } name
        && o.Str("key") is { } key && TryBase64(key) is { Length: 32 } bytes
            ? new SyncPairing(id, name, bytes)
            : null;

    private static byte[]? TryBase64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static byte[]? FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += "=";
        return TryBase64(s);
    }

    public bool Equals(SyncPairing? other) => other is not null && Id == other.Id && Name == other.Name && Key.AsSpan().SequenceEqual(other.Key);
    public override int GetHashCode() => HashCode.Combine(Id, Name);
}

/// <summary>What this PC tells a paired iPhone.</summary>
/// <param name="ComputerName">The name the phone shows for this PC.</param>
/// <param name="Revision">Fingerprint of this PC's own floor plan; empty when it only has the demo plans.</param>
/// <param name="Plan">The plan file byte for byte.</param>
/// <param name="UserCode">The Smart Life User Code entered here, to save typing it on the phone. It identifies the account; it is not a credential.</param>
/// <param name="SmartLifeConnected">Whether Smart Life is signed in here.</param>
/// <param name="Connections">The Home Assistant and Hue connections (address and token / bridge key), so the phone controls the same devices.</param>
public sealed record SyncState(string ComputerName, string Revision, byte[]? Plan, string UserCode, bool SmartLifeConnected, JsonObject? Connections)
{
    /// <summary>The <c>state</c> message; the plan only when the phone does not hold this revision yet.</summary>
    public JsonObject Message(bool includingPlan)
    {
        var message = new JsonObject
        {
            ["type"] = "state",
            ["mac"] = ComputerName, // the field keeps the name the iPhone app reads
            ["rev"] = Revision,
            ["userCode"] = UserCode,
            ["smartLife"] = SmartLifeConnected,
        };
        if (includingPlan && Plan != null) message["plan"] = Encoding.UTF8.GetString(Plan);
        if (Connections != null) message["connections"] = Connections.DeepClone();
        return message;
    }
}

/// <summary>JSON messages over one connection, each prefixed with its length (4 bytes, big endian).</summary>
public static class SyncFraming
{
    public const string ServiceType = "_housecontrol._tcp";
    public const int MaxFrame = 8 * 1024 * 1024;

    public static byte[] Frame(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(Json.Serialize(message));
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Reads the next message; null at the end of the stream or on a frame that is not a JSON object.</summary>
    public static JsonObject? Read(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header)) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxFrame) throw new InvalidDataException($"Frame of {length} bytes");
        var body = new byte[length];
        if (!ReadExactly(stream, body)) return null;
        return Json.ParseObject(body) ?? new JsonObject();
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
