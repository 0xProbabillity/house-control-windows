using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// A smart home platform the plan's lamps and ACs can be linked to, besides Smart Life:
/// Home Assistant and Philips Hue. Each one speaks the panel's own vocabulary — a light is
/// <c>on</c> / <c>brightness</c> 0…1 / <c>temp</c> in kelvin / <c>color</c> "#rrggbb", an AC is
/// <c>on</c> / <c>target</c> / <c>mode</c> / <c>fan</c> / … — so the hub and the UI never see what is underneath.
/// </summary>
/// <remarks>
/// Like the rest of the core, services are used from the UI thread (a single-threaded
/// synchronization context). <see cref="Changed"/> is always raised on the context the
/// service was created on.
/// </remarks>
public interface IDeviceService
{
    /// <summary>"homeassistant", "hue": what a plan's <c>link.service</c> says.</summary>
    string Id { get; }
    string Title { get; }
    bool IsConnected { get; }
    /// <summary>What the editor may show about the connection. Never a token or key.</summary>
    JsonObject Info { get; }
    /// <summary>
    /// Raised when the platform says something changed (a lamp switched from its own app),
    /// so the hub can read the linked devices again without waiting for the next poll.
    /// </summary>
    event Action? Changed;

    /// <summary>Every device the platform offers.</summary>
    Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default);
    /// <summary>The current state of the given devices. Devices the platform does not know are left out.</summary>
    Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default);
    /// <summary>
    /// Applies a change made in the panel. <paramref name="state"/> is the whole new state, for
    /// platforms that need more than the changed fields (a colour needs the brightness too).
    /// </summary>
    Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default);
    /// <summary>The devices linked in the plan, whose changes should raise <see cref="Changed"/>.</summary>
    void Watch(IReadOnlySet<string> ids) { }
    /// <summary>False for a catalog this computer cannot act on.</summary>
    bool ControlsDevices => true;
    /// <summary>Forgets the connection and its credentials.</summary>
    void Disconnect();
}

public enum DeviceKind { Light, Ac, Sensor }

public static class DeviceKinds
{
    public static string Name(this DeviceKind kind) => kind switch
    {
        DeviceKind.Light => "light",
        DeviceKind.Ac => "ac",
        _ => "sensor",
    };

    public static DeviceKind Parse(string? name) => name switch
    {
        "ac" => DeviceKind.Ac,
        "sensor" => DeviceKind.Sensor,
        _ => DeviceKind.Light,
    };

    /// <summary>
    /// A lamp the panel shows as on, given a new brightness or colour, is switched on as well:
    /// the real one may be off (switched off elsewhere since the last read), and some
    /// platforms ignore a colour sent to a lamp that is off.
    /// </summary>
    public static JsonObject WithPower(JsonObject patch, JsonObject state)
    {
        if (patch.ContainsKey("on") || state["on"]?.GetValueKind() != System.Text.Json.JsonValueKind.True) return patch;
        if (!new[] { "brightness", "temp", "color" }.Any(patch.ContainsKey)) return patch;
        var copy = (JsonObject)patch.DeepClone();
        copy["on"] = true;
        return copy;
    }
}

/// <summary>One device as the editor lists it.</summary>
public sealed class ServiceDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeviceKind Kind { get; init; }
    public string? Room { get; init; }
    public bool Online { get; init; } = true;
    /// <summary>For lights: which of "brightness", "temp", "color" it supports.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>For ACs: the set-point range it accepts.</summary>
    public (double Min, double Max)? Range { get; init; }
    /// <summary>A short technical description ("light · color_temp, hs").</summary>
    public string Detail { get; init; } = "";

    public JsonObject ToJson(string service)
    {
        var o = new JsonObject
        {
            ["service"] = service,
            ["id"] = Id,
            ["name"] = Name,
            ["kind"] = Kind.Name(),
            ["online"] = Online,
            ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode?)c).ToArray()),
            ["detail"] = Detail,
        };
        if (Room != null) o["room"] = Room;
        if (Range is { } r) { o["min"] = r.Min; o["max"] = r.Max; }
        return o;
    }
}

/// <summary>What one device reports, in the panel's terms.</summary>
public sealed class DeviceReading
{
    public JsonObject? Light { get; set; }
    public JsonObject? Ac { get; set; }
    /// <summary>What an AC can do: <c>modes</c>, <c>fans</c>, <c>swing</c>, <c>sleep</c>, <c>eco</c> (see the store's acFeatures).</summary>
    public JsonObject? Features { get; set; }
    /// <summary>A temperature sensor's reading in °C.</summary>
    public double? Temperature { get; set; }
    public bool Online { get; set; } = true;
}

/// <summary>A platform said no, or could not be reached; the message is shown to the user.</summary>
public class ServiceFailure(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Maps whatever a platform calls its fan speeds onto the panel's five.</summary>
public static class FanNames
{
    public static readonly string[] Order = ["auto", "low", "medium", "high", "turbo"];

    public static string? Panel(string name)
    {
        var n = name.ToLowerInvariant();
        if (new[] { "turbo", "max", "strong", "highest", "powerful", "boost", "very high" }.Any(n.Contains)) return "turbo";
        if (n.Contains("high")) return "high";
        if (n.Contains("med") || n.Contains("mid")) return "medium";
        if (n.Contains("low") || n.Contains("quiet") || n.Contains("silent")) return "low";
        if (n.Contains("auto")) return "auto";
        return null;
    }

    /// <summary>For each panel fan speed, the platform's own name for it (the first one that maps).</summary>
    public static Dictionary<string, string> Table(IEnumerable<string> names)
    {
        var table = new Dictionary<string, string>();
        foreach (var name in names)
        {
            if (name.Equals("off", StringComparison.OrdinalIgnoreCase)) continue;
            if (Panel(name) is { } p && !table.ContainsKey(p)) table[p] = name;
        }
        return table;
    }
}
