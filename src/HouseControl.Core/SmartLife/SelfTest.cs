using System.Text.Json.Nodes;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Offline self-checks and the read-only diagnostic behind the app's command line flags
/// (<c>--selftest-mapper</c>, <c>--selftest-infrared</c>, <c>--selftest-transport</c>, <c>--diagnose</c>).
/// </summary>
public static class SelfTest
{
    /// <summary>
    /// Runs the check named in <paramref name="args"/>, writing to <paramref name="output"/>, and returns
    /// the process exit code; null when no check was asked for. Call it before the UI starts.
    /// </summary>
    public static int? Run(string[] args, TextWriter output)
    {
        // Prints the Smart Life request envelope for fixed inputs, so the port can be
        // compared against the reference Python implementation and the Mac app.
        if (args.Contains("--selftest-transport"))
        {
            foreach (var line in TransportLines()) output.WriteLine(line);
            return 0;
        }
        // Prints the commands built for typical actions.
        if (args.Contains("--selftest-mapper"))
        {
            foreach (var line in MapperLines()) output.WriteLine(line);
            return 0;
        }
        // Plays typical changes against a simulated cloud remote whose temperature is fixed
        // in Auto mode, and prints the key presses.
        if (args.Contains("--selftest-infrared"))
        {
            foreach (var line in InfraredTranscriptAsync().GetAwaiter().GetResult()) output.WriteLine(line);
            return 0;
        }
        // `--diagnose <id> [<id>…]` prints what Smart Life reports for those devices
        // (status + specification). It only reads; nothing is sent to the devices.
        var flag = Array.IndexOf(args, "--diagnose");
        if (flag >= 0)
        {
            var ids = args[(flag + 1)..];
            var directory = new PlanStore().Directory;
            var cloud = new SmartLifePlatform(directory, name => new ProtectedFileSecretStore(Path.Combine(directory, $"{name}.json")));
            var dump = cloud.DiagnosticsAsync(ids).GetAwaiter().GetResult();
            output.WriteLine(Json.Serialize(Json.Sorted(dump), pretty: true));
            return 0;
        }
        return null;
    }

    /// <summary>Hash key, secret, envelope, the envelope decrypted again, and the signature, one per line.</summary>
    internal static string[] TransportLines()
    {
        const string rid = "11111111-2222-3333-4444-555555555555";
        var hashKey = SmartLifeService.Transport.Md5Hex(rid + "refresh-token");
        var secret = SmartLifeService.Transport.Secret(rid, hashKey);
        var enc = SmartLifeService.Transport.Encrypt("{\"homeId\":\"42\"}", secret, nonce: "ABCDEFGHJKMN");
        (string, string)[] headers = [("X-appKey", "app"), ("X-requestId", rid), ("X-sid", ""), ("X-time", "1700000000000"), ("X-token", "tok")];
        return
        [
            hashKey, secret, enc, SmartLifeService.Transport.Decrypt(enc, secret),
            SmartLifeService.Transport.Sign(hashKey, headers, enc, ""),
        ];
    }

    /// <summary>"name → commands" for typical actions, the commands with sorted keys.</summary>
    internal static string[] MapperLines()
    {
        var ir = new HashSet<string> { "PowerOn", "PowerOff", "T", "M", "F" };
        var bulb = new HashSet<string> { "switch_led", "work_mode", "bright_value_v2", "temp_value_v2", "colour_data_v2" };
        var ranges = new Dictionary<string, (double Min, double Max)> { ["bright_value_v2"] = (10, 1000), ["temp_value_v2"] = (0, 1000) };
        (string, List<JsonObject>)[] cases =
        [
            ("ir on+heat+24", TuyaMapper.AcCommands(new() { ["on"] = true, ["mode"] = "heat", ["target"] = 24.0 }, new(), ir)),
            ("ir off", TuyaMapper.AcCommands(new() { ["on"] = false }, new(), ir)),
            ("ir fan high", TuyaMapper.AcCommands(new() { ["fan"] = "high" }, new(), ir)),
            ("bulb on 50%", TuyaMapper.LightCommands(new() { ["on"] = true, ["brightness"] = 0.5 }, new(), new(), bulb, ranges)),
            ("bulb 2700K", TuyaMapper.LightCommands(new() { ["temp"] = 2700.0 }, new(), new(), bulb, ranges)),
            ("bulb colour", TuyaMapper.LightCommands(new() { ["color"] = "#ff5a4f" }, new() { ["brightness"] = 0.8 }, new(), bulb, ranges)),
            ("plug on (nothing known)", TuyaMapper.LightCommands(new() { ["on"] = true }, new(), new(), new HashSet<string>())),
        ];
        return cases.Select(c => $"{c.Item1} → {SortedJson(c.Item2)}").ToArray();
    }

    /// <summary>Commands as JSON with sorted keys, the way the Mac app prints them.</summary>
    internal static string SortedJson(IEnumerable<JsonObject> commands) =>
        Json.Serialize(Json.Sorted(Loose.Array(commands.Select(c => (JsonNode?)c.DeepClone()))));

    /// <summary>One line per case: the key presses an infrared AC received and how the change ended.</summary>
    internal static async Task<List<string>> InfraredTranscriptAsync()
    {
        var remote = new InfraredRemote { Spacing = TimeSpan.Zero };
        var cloud = new Dictionary<string, JsonNode?> { ["M"] = 2, ["T"] = 24, ["F"] = 0, ["power"] = false }; // what Tuya remembers
        var presses = new List<string>();
        Task Send(JsonObject command)
        {
            var code = command["code"].AsString() ?? "";
            var value = command["value"];
            if (code == "T" && (cloud["M"].AsDouble() == 2 || value.AsDouble() == 31))
            {
                presses.Add($"{code}={value.Plain()} ✗");
                throw new TuyaFailure("command or value not support (code 2008)", "2008");
            }
            presses.Add($"{code}={value.Plain()}");
            if (code is "PowerOn" or "PowerOff") cloud["power"] = code == "PowerOn";
            else cloud[code] = value?.DeepClone();
            return Task.CompletedTask;
        }
        static JsonObject State(bool on, string mode, double target, string fan) =>
            new() { ["on"] = on, ["mode"] = mode, ["target"] = target, ["fan"] = fan };
        (string, JsonObject, JsonObject)[] cases =
        [
            ("fan while off (kept)", new() { ["fan"] = "high" }, State(false, "cool", 24, "high")),
            ("switch on (sends the kept fan)", new() { ["on"] = true }, State(true, "cool", 24, "high")),
            ("25° while the cloud is in Auto", new() { ["target"] = 25.0 }, State(true, "cool", 25, "high")),
            ("26° afterwards", new() { ["target"] = 26.0 }, State(true, "cool", 26, "high")),
            ("a setting the remote lacks", new() { ["target"] = 31.0 }, State(true, "cool", 31, "high")),
            ("switch off", new() { ["on"] = false }, State(false, "cool", 26, "high")),
        ];
        var lines = new List<string>();
        foreach (var (name, patch, state) in cases)
        {
            presses.Clear();
            var outcome = "ok";
            try
            {
                await remote.ApplyAsync(patch, state, "remote", Send, note => presses.Add($"({note})"));
            }
            catch (InfraredRemote.Rejection rejected)
            {
                outcome = $"rejected [{string.Join(", ", rejected.Fields.Select(f => $"\"{f}\""))}]: {rejected.Message}";
            }
            catch (Exception e)
            {
                outcome = e.Message;
            }
            lines.Add($"{name} → {string.Join("  ", presses)} → {outcome}");
        }
        return lines;
    }
}
