using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Reads Tuya's JSON the way the Mac app's dictionaries do: a number is a number whether it
/// arrived as 24 or 24.0, and a value of the wrong type counts as missing.
/// </summary>
internal static class Loose
{
    public static double? AsDouble(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    public static bool? AsBool(this JsonNode? node) =>
        node is JsonValue v
            ? v.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static string? AsString(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A string, or a number written out ("ownerId" and error codes arrive as either).</summary>
    public static string? AsText(this JsonNode? node) =>
        node.AsString() ?? (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null);

    /// <summary>The objects of an array; null when it is not an array.</summary>
    public static List<JsonObject>? AsObjects(this JsonNode? node) =>
        node is JsonArray a ? a.OfType<JsonObject>().ToList() : null;

    /// <summary>A value as a log line or a transcript shows it: strings without quotes.</summary>
    public static string Plain(this JsonNode? node) => node.AsString() ?? node?.ToJsonString() ?? "";

    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    public static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());
}
