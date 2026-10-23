using System.Globalization;
using System.Text.Json.Nodes;
using Ranges = System.Collections.Generic.Dictionary<string, (double Min, double Max)>;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Translates between the app's device state and Tuya's "standard instruction set".
/// Devices differ in which data points they expose and in their value ranges, so every
/// mapping checks the codes the device actually reported and silently skips what it cannot do.
/// </summary>
internal static class TuyaMapper
{
    private static readonly Dictionary<string, string> AcModes = new()
    {
        ["cool"] = "cold", ["heat"] = "hot", ["dry"] = "wet", ["fan"] = "wind", ["auto"] = "auto",
    };
    private static readonly Dictionary<string, string> AcFans = new()
    {
        ["auto"] = "auto", ["low"] = "low", ["medium"] = "mid", ["high"] = "high", ["turbo"] = "strong",
    };
    // Infrared ACs (a remote learned by an IR blaster) number their modes and fan speeds.
    private static readonly Dictionary<string, int> IrModes = new()
    {
        ["cool"] = 0, ["heat"] = 1, ["auto"] = 2, ["fan"] = 3, ["dry"] = 4,
    };
    private static readonly Dictionary<string, int> IrFans = new()
    {
        ["auto"] = 0, ["low"] = 1, ["medium"] = 2, ["high"] = 3, ["turbo"] = 3,
    };

    private static string? Pick(string[] candidates, IReadOnlySet<string> codes, JsonNode? overrideCode)
    {
        if (overrideCode.AsString() is { Length: > 0 } o) return o;
        return candidates.FirstOrDefault(codes.Contains);
    }

    /// <summary>
    /// Range of a brightness / temperature point: the specification wins; otherwise the
    /// <c>_v2</c> points are 0…1000 and the legacy ones 0…255.
    /// </summary>
    private static (double Min, double Max) Range(string code, Ranges ranges, double floor = 0)
    {
        if (ranges.TryGetValue(code, out var r) && r.Max > r.Min) return r;
        return code.EndsWith("_v2", StringComparison.Ordinal) ? (floor > 0 ? 10 : 0, 1000) : (floor, 255);
    }

    /// <summary>Swift's <c>rounded()</c>: halves go away from zero.</summary>
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static JsonObject Command(string code, JsonNode? value) => new() { ["code"] = code, ["value"] = value };

    public static List<JsonObject> LightCommands(JsonObject patch, JsonObject state, JsonObject link,
                                                 IReadOnlySet<string> codes, Ranges? ranges = null)
    {
        ranges ??= new Ranges();
        var custom = link.Obj("codes") ?? new JsonObject();
        var output = new List<JsonObject>();
        if (patch["on"].AsBool() is { } on)
        {
            // A plain plug only has a switch; assume switch_1 when the device has not reported yet.
            var code = Pick(["switch_led", "switch_1", "switch"], codes, custom["switch"]) ?? "switch_1";
            output.Add(Command(code, on));
        }
        if (patch["brightness"].AsDouble() is { } b && Pick(["bright_value_v2", "bright_value"], codes, null) is { } brightCode)
        {
            var (lo, hi) = Range(brightCode, ranges, floor: 25);
            output.Add(Command(brightCode, Round(lo + b * (hi - lo))));
        }
        if (patch["temp"].AsDouble() is { } k && Pick(["temp_value_v2", "temp_value"], codes, null) is { } tempCode)
        {
            var t = Math.Max(0, Math.Min(1, (k - 2200) / (6500 - 2200)));
            var (lo, hi) = Range(tempCode, ranges);
            if (codes.Contains("work_mode")) output.Add(Command("work_mode", "white"));
            output.Add(Command(tempCode, Round(lo + t * (hi - lo))));
        }
        if (patch["color"].AsString() is { } hex && Hsv(hex) is { } hsv
            && Pick(["colour_data_v2", "colour_data"], codes, null) is { } colourCode)
        {
            var (h, s, _) = hsv;
            // Saturation and value share the brightness scale of the same generation.
            var scale = Range(colourCode == "colour_data_v2" ? "bright_value_v2" : "bright_value", ranges).Max > 255 ? 1000.0 : 255.0;
            var v = Math.Max(0.01, state["brightness"].AsDouble() ?? 1);
            if (codes.Contains("work_mode")) output.Add(Command("work_mode", "colour"));
            // Sent as a JSON string, which is also how the device reports it.
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{{\"h\":{(int)h},\"s\":{(int)(s * scale)},\"v\":{(int)(v * scale)}}}");
            output.Add(Command(colourCode, text));
        }
        return output;
    }

    public static JsonObject LightState(IEnumerable<JsonObject> status, Ranges? ranges = null)
    {
        ranges ??= new Ranges();
        var output = new JsonObject();
        foreach (var dp in status)
        {
            if (dp["code"].AsString() is not { } code || dp["value"] is not { } value) continue;
            switch (code)
            {
                case "switch_led" or "switch_1" or "switch":
                    if (!output.ContainsKey("on") && value.AsBool() is { } on) output["on"] = on;
                    break;
                case "bright_value_v2" or "bright_value":
                    if (value.AsDouble() is { } bv)
                    {
                        var (lo, hi) = Range(code, ranges, floor: 25);
                        output["brightness"] = Math.Max(0.05, Math.Min(1, (bv - lo) / (hi - lo)));
                    }
                    break;
                case "temp_value_v2" or "temp_value":
                    if (value.AsDouble() is { } tv)
                    {
                        var (lo, hi) = Range(code, ranges);
                        output["temp"] = 2200 + Math.Max(0, Math.Min(1, (tv - lo) / (hi - lo))) * 4300;
                    }
                    break;
            }
        }
        return output;
    }

    /// <summary>
    /// True for an AC that is a remote learned by an infrared blaster. Those take the
    /// "PowerOn / PowerOff / T / M / F" key set and never report their state back.
    /// </summary>
    public static bool IsInfraredAC(IReadOnlySet<string> codes) => codes.Contains("PowerOn") || codes.Contains("PowerOff");

    /// <summary>One key press of an infrared remote, and the piece of app state it carries.</summary>
    public sealed record InfraredKey(string Field, JsonObject Command);

    /// <summary>
    /// The key presses that bring an infrared AC to <paramref name="patch"/>. Order matters for a
    /// remote: power first, then mode, then the set-point and fan.
    /// </summary>
    public static List<InfraredKey> InfraredKeys(JsonObject patch)
    {
        var output = new List<InfraredKey>();
        if (patch["on"].AsBool() is { } on)
        {
            var key = on ? "PowerOn" : "PowerOff";
            output.Add(new InfraredKey("on", Command(key, key)));
        }
        if (patch["mode"].AsString() is { } m && IrModes.TryGetValue(m, out var mv))
            output.Add(new InfraredKey("mode", Command("M", mv)));
        if (patch["target"].AsDouble() is { } t)
            output.Add(new InfraredKey("target", Command("T", (int)t)));
        if (patch["fan"].AsString() is { } f && IrFans.TryGetValue(f, out var fv))
            output.Add(new InfraredKey("fan", Command("F", fv)));
        return output;
    }

    /// <summary>
    /// What the panel may offer for a linked AC. Controls the device has no function for
    /// are hidden instead of pretending to work. Null until the functions are known.
    /// </summary>
    public static JsonObject? AcFeatures(IReadOnlySet<string> codes, Ranges ranges)
    {
        if (codes.Count == 0) return null;
        string[] allModes = ["cool", "heat", "dry", "fan", "auto"];
        if (IsInfraredAC(codes))
        {
            var m = ranges.TryGetValue("M", out var mr) ? mr : (0, 4);
            var f = ranges.TryGetValue("F", out var fr) ? fr : (0, 3);
            static double Number(Dictionary<string, int> table, string key) => table.TryGetValue(key, out var n) ? n : 99;
            return new JsonObject
            {
                ["infrared"] = true,
                ["modes"] = Loose.Array(allModes.Where(x => Number(IrModes, x) >= m.Item1 && Number(IrModes, x) <= m.Item2)),
                // Turbo shares the top speed with High on a remote, so it is not offered twice.
                ["fans"] = Loose.Array(new[] { "auto", "low", "medium", "high" }
                    .Where(x => Number(IrFans, x) >= f.Item1 && Number(IrFans, x) <= f.Item2)),
                ["sleep"] = false, ["eco"] = false, ["swing"] = false,
            };
        }
        return new JsonObject
        {
            ["infrared"] = false,
            ["modes"] = Loose.Array(codes.Contains("mode") ? allModes : []),
            ["fans"] = Loose.Array(codes.Contains("fan_speed_enum") || codes.Contains("windspeed")
                ? ["auto", "low", "medium", "high", "turbo"] : Array.Empty<string>()),
            ["sleep"] = codes.Contains("sleep"), ["eco"] = codes.Contains("eco"), ["swing"] = false,
        };
    }

    public static List<JsonObject> AcCommands(JsonObject patch, JsonObject link, IReadOnlySet<string> codes)
    {
        var custom = link.Obj("codes") ?? new JsonObject();
        var output = new List<JsonObject>();

        if (IsInfraredAC(codes)) return InfraredKeys(patch).Select(k => k.Command).ToList();

        if (patch["on"].AsBool() is { } on)
            output.Add(Command(Pick(["switch", "power", "switch_1"], codes, custom["switch"]) ?? "switch", on));
        if (patch["target"].AsDouble() is { } t)
            output.Add(Command(Pick(["temp_set"], codes, custom["target"]) ?? "temp_set", (int)t));
        if (patch["mode"].AsString() is { } m && AcModes.TryGetValue(m, out var mv))
            output.Add(Command(Pick(["mode"], codes, custom["mode"]) ?? "mode", mv));
        if (patch["fan"].AsString() is { } f && AcFans.TryGetValue(f, out var fv)
            && Pick(["fan_speed_enum", "windspeed"], codes, custom["fan"]) is { } fanCode)
            output.Add(Command(fanCode, fv));
        if (patch["sleep"].AsBool() is { } sleep && codes.Contains("sleep")) output.Add(Command("sleep", sleep));
        if (patch["eco"].AsBool() is { } eco && codes.Contains("eco")) output.Add(Command("eco", eco));
        return output;
    }

    public static JsonObject AcState(IEnumerable<JsonObject> status)
    {
        var output = new JsonObject();
        foreach (var dp in status)
        {
            if (dp["code"].AsString() is not { } code || dp["value"] is not { } value) continue;
            switch (code)
            {
                case "switch" or "power":
                    if (value.AsBool() is { } on) output["on"] = on;
                    break;
                case "temp_set":
                    if (value.AsDouble() is { } target) output["target"] = target;
                    break;
                case "temp_current":
                    if (value.AsDouble() is { } room) output["roomTemp"] = room;
                    break;
                case "mode":
                    if (value.AsString() is { } mv && AcModes.FirstOrDefault(p => p.Value == mv).Key is { } mode) output["mode"] = mode;
                    break;
                case "fan_speed_enum" or "windspeed":
                    if (value.AsString() is { } fv && AcFans.FirstOrDefault(p => p.Value == fv).Key is { } fan) output["fan"] = fan;
                    break;
            }
        }
        return output;
    }

    /// <summary>Reading of a temperature sensor in °C (Tuya reports tenths of a degree).</summary>
    public static double? SensorTemperature(IEnumerable<JsonObject> status)
    {
        foreach (var dp in status)
        {
            if (dp["code"].AsString() is not ("temp_current" or "va_temperature") || dp["value"].AsDouble() is not { } v) continue;
            return Math.Abs(v) > 60 ? v / 10 : v;
        }
        return null;
    }

    private static (double H, double S, double V)? Hsv(string hex)
    {
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n)) return null;
        double r = ((n >> 16) & 255) / 255.0, g = ((n >> 8) & 255) / 255.0, b = (n & 255) / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        var h = 0.0;
        if (d > 0)
        {
            if (mx == r) h = ((g - b) / d) % 6;
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, mx == 0 ? 0 : d / mx, mx);
    }
}
