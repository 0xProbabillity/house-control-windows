using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>The loose, dictionary-like JSON the web UI and the platforms speak.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static JsonObject? ParseObject(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try { return JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string Serialize(JsonNode? node, bool pretty = false) =>
        node?.ToJsonString(pretty ? Pretty : Compact) ?? "null";

    /// <summary>A copy with the keys of every object sorted, so the same plan always writes the same bytes.</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    public static string? Str(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    public static double? Num(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        if (jv.GetValueKind() == JsonValueKind.Number) return jv.GetValue<double>();
        return null;
    }

    public static bool? Bool(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
            ? jv.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static JsonObject? Obj(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;

    public static JsonArray? Arr(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;
}
