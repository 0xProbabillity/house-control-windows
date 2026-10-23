using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseControl.Core.HomeAssistant;

/// <summary>
/// A Home Assistant server on the local network (or anywhere it is reachable), through its
/// REST API with a long-lived access token. Lights and switches become lamps, <c>climate</c>
/// entities become air conditioners and temperature sensors can stand in for an AC's room
/// reading. A WebSocket subscription tells the hub the moment something changes.
/// </summary>
/// <remarks>
/// Used from the UI thread, like the rest of the core. <see cref="Changed"/> is raised on the
/// synchronization context the service was created on (directly when there was none).
/// </remarks>
public sealed class HomeAssistantService : IDeviceService, ISharedConnection, IDisposable
{
    public string Id => "homeassistant";
    public string Title => "Home Assistant";
    public event Action? Changed;

    /// <summary>What is kept in the secret store: the same fields as the Mac app writes.</summary>
    internal sealed record Config(string Url, string Token, string? Name = null, string? Version = null)
    {
        public byte[] Encode() => Encoding.UTF8.GetBytes(Json.Serialize(new JsonObject
        {
            ["url"] = Url,
            ["token"] = Token,
            ["name"] = Name,
            ["version"] = Version,
        }));

        public static Config? Decode(byte[]? data) =>
            Json.ParseObject(data) is { } o && o.Str("url") is { } url && o.Str("token") is { } token
                ? new Config(url, token, o.Str("name"), o.Str("version"))
                : null;
    }

    /// <summary>One HTTP client for every Home Assistant request: short timeouts, no cookies.</summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        UseCookies = false,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long a lost WebSocket waits before it tries again; polling covers the gap.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    private readonly ISecretStore _vault;
    private readonly SynchronizationContext? _context;
    private Config? _config;
    /// <summary>Ends the current WebSocket and its retry loop.</summary>
    private CancellationTokenSource? _listening;
    /// <summary>Each climate entity's own names for the panel's fan speeds and modes, from the last read.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _fanNames = new();
    private readonly Dictionary<string, Dictionary<string, string>> _modeNames = new();
    /// <summary>The entities linked in the plan: only their changes are worth a read.</summary>
    private volatile IReadOnlySet<string> _watched = new HashSet<string>();
    private int _changePending;

    public HomeAssistantService(ISecretStore vault)
    {
        _vault = vault;
        _context = SynchronizationContext.Current;
        _config = Config.Decode(vault.Read());
        if (_config != null) Listen();
    }

    public bool IsConnected => _config != null;

    public JsonObject Info => _config is not { } config
        ? new JsonObject { ["connected"] = false }
        : new JsonObject
        {
            ["connected"] = true,
            ["url"] = config.Url,
            ["name"] = config.Name ?? "Home Assistant",
            ["version"] = config.Version ?? "",
        };

    // MARK: Connecting

    /// <summary>Checks the address and the token, then keeps them.</summary>
    public async Task ConnectAsync(string url, string token, CancellationToken cancel = default)
    {
        var address = Normalize(url);
        token = (token ?? "").Trim();
        if (token.Length == 0)
            throw new ServiceFailure("Paste a long-lived access token (your profile in Home Assistant → Security).");
        var candidate = new Config(address, token);
        var about = await RequestAsync(HttpMethod.Get, "/api/config", cancel: cancel, config: candidate) as JsonObject;
        candidate = candidate with { Name = about.Str("location_name"), Version = about.Str("version") };
        _config = candidate;
        _vault.Write(candidate.Encode());
        Listen();
    }

    /// <summary>
    /// The connection as a paired iPhone takes it over: address, token, name and version.
    /// Only ever sent over the sync channel, which nothing but a paired phone can open.
    /// </summary>
    public JsonObject? Shareable => _config is not { } config ? null : new JsonObject
    {
        ["url"] = config.Url,
        ["token"] = config.Token,
        ["name"] = config.Name ?? "",
        ["version"] = config.Version ?? "",
    };

    /// <summary>Takes over a connection a paired iPhone shared; null (or nothing usable) disconnects.</summary>
    public void Adopt(JsonObject? shared)
    {
        if (shared.Str("url") is not { } url || shared.Str("token") is not { } token)
        {
            if (_config != null) Disconnect();
            return;
        }
        var name = shared.Str("name") is { Length: > 0 } n ? n : null;
        if (_config is { } current && current.Url == url && current.Token == token && current.Name == name) return;
        _config = new Config(url, token, name, shared.Str("version"));
        _vault.Write(_config.Encode());
        Listen();
    }

    public void Watch(IReadOnlySet<string> ids) => _watched = ids.ToHashSet();

    public void Disconnect()
    {
        _config = null;
        _vault.Write(null);
        StopListening();
    }

    public void Dispose() => StopListening();

    /// <summary>"homeassistant.local:8123" → "http://homeassistant.local:8123".</summary>
    internal static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            throw new ServiceFailure("Enter the address of your Home Assistant, for example http://homeassistant.local:8123");
        if (!s.Contains("://")) s = "http://" + s;
        s = s.TrimEnd('/');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Host.Length == 0
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ServiceFailure($"\"{raw}\" is not a web address");
        }
        var host = uri.Host.ToLowerInvariant();
        if (!HasExplicitPort(s) && uri.Scheme == Uri.UriSchemeHttp && (host.EndsWith(".local") || host == "homeassistant"))
            s += ":8123"; // Home Assistant's default port, which people leave out
        return s;
    }

    /// <summary>Whether the address says a port itself ("http://x:80" does, "http://x" does not).</summary>
    private static bool HasExplicitPort(string url)
    {
        var authority = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        if (end >= 0) authority = authority[..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close >= 0 && authority[(close + 1)..].StartsWith(':');
        }
        return authority.Contains(':');
    }

    // MARK: Devices

    public async Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default)
    {
        var states = (await RequestAsync(HttpMethod.Get, "/api/states", cancel: cancel) as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        Dictionary<string, string> areas;
        try { areas = await AreaNamesAsync(cancel); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception) { areas = new(); } // rooms are a nicety: list the devices without them
        return states
            .Select(state => Device(state, state.Str("entity_id") is { } id && areas.TryGetValue(id, out var room) ? room : null))
            .OfType<ServiceDevice>()
            .OrderBy(d => d.Kind.Name(), StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Area of every entity, in one template request (the REST API has no area endpoint).</summary>
    private async Task<Dictionary<string, string>> AreaNamesAsync(CancellationToken cancel)
    {
        const string template = "{% for e in states %}{{ e.entity_id }}\t{{ area_name(e.entity_id) or '' }}\n{% endfor %}";
        var text = await RequestAsync(HttpMethod.Post, "/api/template", new JsonObject { ["template"] = template }, raw: true, cancel: cancel) as JsonValue;
        return ParseAreas(text?.GetValue<string>() ?? "");
    }

    internal static Dictionary<string, string> ParseAreas(string text)
    {
        var areas = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 2);
            if (parts.Length == 2 && parts[1].Length > 0 && parts[1] != "None") areas[parts[0]] = parts[1];
        }
        return areas;
    }

    public async Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default)
    {
        var readings = new Dictionary<string, DeviceReading>();
        if (ids.Count == 0) return readings;
        var states = await RequestAsync(HttpMethod.Get, "/api/states", cancel: cancel) as JsonArray;
        foreach (var state in states?.OfType<JsonObject>() ?? [])
        {
            if (state.Str("entity_id") is not { } id || !ids.Contains(id)) continue;
            var attributes = state.Obj("attributes");
            if (id.StartsWith("climate.", StringComparison.Ordinal))
            {
                _fanNames[id] = FanNames.Table(Strings(attributes, "fan_modes"));
                _modeNames[id] = ModeTable(Strings(attributes, "hvac_modes"));
            }
            readings[id] = Reading(state);
        }
        return readings;
    }

    public async Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default)
    {
        var calls = Calls(id, patch, state,
            _fanNames.GetValueOrDefault(id) ?? new(), _modeNames.GetValueOrDefault(id) ?? new());
        foreach (var call in calls)
            await RequestAsync(HttpMethod.Post, $"/api/services/{call.Domain}/{call.Service}", call.Data, cancel: cancel);
    }

    // MARK: Mapping (static, so the tests can run it without a server)

    private static readonly string[] Offline = ["unavailable", "unknown"];

    internal static List<string> Strings(JsonNode? node, string key) =>
        node.Arr(key)?.Select(v => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [];

    internal static ServiceDevice? Device(JsonObject state, string? room = null)
    {
        if (state.Str("entity_id") is not { } id) return null;
        var attributes = state.Obj("attributes");
        var name = attributes.Str("friendly_name") ?? id;
        var domain = id.Split('.')[0];
        var online = !Offline.Contains(state.Str("state") ?? "");
        switch (domain)
        {
            case "light":
                var modes = Strings(attributes, "supported_color_modes");
                return new ServiceDevice
                {
                    Id = id, Name = name, Kind = DeviceKind.Light, Room = room, Online = online,
                    Capabilities = LightCapabilities(modes),
                    Detail = "light · " + (modes.Count == 0 ? "on/off" : string.Join(", ", modes)),
                };
            case "switch":
                return new ServiceDevice { Id = id, Name = name, Kind = DeviceKind.Light, Room = room, Online = online, Detail = "switch · on/off" };
            case "climate":
                (double, double)? range = attributes.Num("min_temp") is { } lo && attributes.Num("max_temp") is { } hi
                    ? (Math.Max(lo, 10), Math.Min(hi, 32))
                    : null;
                return new ServiceDevice
                {
                    Id = id, Name = name, Kind = DeviceKind.Ac, Room = room, Online = online, Range = range,
                    Detail = "climate · " + string.Join(", ", Strings(attributes, "hvac_modes")),
                };
            case "sensor" when IsTemperature(attributes):
                return new ServiceDevice { Id = id, Name = name, Kind = DeviceKind.Sensor, Room = room, Online = online, Detail = "temperature sensor" };
            default:
                return null;
        }
    }

    internal static List<string> LightCapabilities(IReadOnlyCollection<string> modes)
    {
        var caps = new List<string>();
        if (modes.Any(m => m != "onoff")) caps.Add("brightness");
        if (modes.Contains("color_temp")) caps.Add("temp");
        if (modes.Any(m => m is "hs" or "xy" or "rgb" or "rgbw" or "rgbww")) caps.Add("color");
        return caps;
    }

    internal static bool IsTemperature(JsonObject? attributes) =>
        attributes.Str("device_class") == "temperature" || attributes.Str("unit_of_measurement") is "°C" or "°F";

    /// <summary>The panel's AC modes and the climate entity's names for them, in the panel's order.</summary>
    internal static Dictionary<string, string> ModeTable(IReadOnlyCollection<string> hvacModes)
    {
        var table = new Dictionary<string, string>();
        foreach (var (panel, names) in new (string, string[])[]
                 { ("cool", ["cool"]), ("heat", ["heat"]), ("dry", ["dry"]), ("fan", ["fan_only"]), ("auto", ["auto", "heat_cool"]) })
        {
            if (names.FirstOrDefault(hvacModes.Contains) is { } name) table[panel] = name;
        }
        return table;
    }

    internal static DeviceReading Reading(JsonObject state)
    {
        var id = state.Str("entity_id") ?? "";
        var value = state.Str("state") ?? "";
        var attributes = state.Obj("attributes");
        var reading = new DeviceReading { Online = !Offline.Contains(value) };
        if (!reading.Online) return reading;

        if (id.StartsWith("light.", StringComparison.Ordinal) || id.StartsWith("switch.", StringComparison.Ordinal))
        {
            var light = new JsonObject { ["on"] = value == "on" };
            if (value == "on" && attributes.Num("brightness") is { } b) light["brightness"] = Math.Clamp(b / 255, 0.05, 1);
            switch (attributes.Str("color_mode"))
            {
                case "color_temp":
                    if (attributes.Num("color_temp_kelvin") is { } k)
                    {
                        light["temp"] = Math.Clamp(k, 2200, 6500);
                        light["color"] = null;
                    }
                    break;
                case "hs" or "xy" or "rgb" or "rgbw" or "rgbww":
                    if (attributes.Arr("rgb_color") is { Count: 3 } rgb)
                    {
                        var c = rgb.Select(v => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number ? jv.GetValue<double>() : (double?)null).ToList();
                        if (c.All(x => x != null)) light["color"] = ColorMath.Hex(c[0]!.Value / 255, c[1]!.Value / 255, c[2]!.Value / 255);
                    }
                    break;
            }
            reading.Light = light;
        }
        else if (id.StartsWith("climate.", StringComparison.Ordinal))
        {
            var modes = ModeTable(Strings(attributes, "hvac_modes"));
            var ac = new JsonObject { ["on"] = value != "off" };
            // On, but not running right now: a thermostat whose room is already warm (or cool) enough.
            var action = attributes.Str("hvac_action");
            ac["idle"] = value != "off" && action is "idle" or "off";
            if (modes.FirstOrDefault(m => m.Value == value).Key is { } mode) ac["mode"] = mode;
            if (attributes.Num("temperature") is { } t) ac["target"] = t;
            else if (attributes.Num("target_temp_low") is { } lo && attributes.Num("target_temp_high") is { } hi)
                ac["target"] = Math.Round((lo + hi) / 2, MidpointRounding.AwayFromZero);
            if (attributes.Num("current_temperature") is { } room) ac["roomTemp"] = room;
            var fans = FanNames.Table(Strings(attributes, "fan_modes"));
            if (attributes.Str("fan_mode") is { } current && FanNames.Panel(current) is { } fan && fans.ContainsKey(fan)) ac["fan"] = fan;
            var swings = Strings(attributes, "swing_modes");
            if (attributes.Str("swing_mode") is { } swing && swings.Count > 0) ac["swing"] = swing == "off" ? "off" : "swing";
            var presets = Strings(attributes, "preset_modes");
            var preset = attributes.Str("preset_mode") ?? "";
            if (presets.Contains("sleep")) ac["sleep"] = preset == "sleep";
            if (presets.Contains("eco")) ac["eco"] = preset == "eco";
            reading.Ac = ac;
            reading.Features = new JsonObject
            {
                ["modes"] = new JsonArray(new[] { "cool", "heat", "dry", "fan", "auto" }.Where(modes.ContainsKey).Select(m => (JsonNode?)m).ToArray()),
                ["fans"] = new JsonArray(FanNames.Order.Where(fans.ContainsKey).Select(f => (JsonNode?)f).ToArray()),
                ["swing"] = swings.Contains("off") && swings.Count > 1,
                ["sleep"] = presets.Contains("sleep"),
                ["eco"] = presets.Contains("eco"),
            };
        }
        else if (id.StartsWith("sensor.", StringComparison.Ordinal)
                 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t))
        {
            reading.Temperature = attributes.Str("unit_of_measurement") == "°F" ? (t - 32) * 5 / 9 : t;
        }
        return reading;
    }

    /// <summary>One Home Assistant service call: <c>POST /api/services/{Domain}/{Service}</c> with <see cref="Data"/>.</summary>
    internal sealed record Call(string Domain, string Service, JsonObject Data);

    internal static List<Call> Calls(string id, JsonObject patch, JsonObject state,
                                     IReadOnlyDictionary<string, string> fans, IReadOnlyDictionary<string, string> modes)
    {
        var domain = id.Split('.')[0];
        Call Make(string service, JsonObject? data = null)
        {
            var body = new JsonObject { ["entity_id"] = id };
            foreach (var (k, v) in data ?? new JsonObject()) body[k] = v?.DeepClone();
            return new Call(domain, service, body);
        }

        var on = patch.Bool("on");
        if (domain == "switch")
            return on is { } power ? [Make(power ? "turn_on" : "turn_off")] : [];
        if (domain == "light")
        {
            if (on == false) return [Make("turn_off")];
            var data = new JsonObject();
            if (patch.Num("brightness") is { } b) data["brightness_pct"] = (int)Math.Round(b * 100, MidpointRounding.AwayFromZero);
            if (patch.Num("temp") is { } k) data["color_temp_kelvin"] = (int)k;
            if (ColorMath.Rgb(patch.Str("color")) is { } color)
            {
                var (r, g, bl) = color;
                data["rgb_color"] = new JsonArray(
                    (int)Math.Round(r * 255, MidpointRounding.AwayFromZero),
                    (int)Math.Round(g * 255, MidpointRounding.AwayFromZero),
                    (int)Math.Round(bl * 255, MidpointRounding.AwayFromZero));
            }
            if (data.Count == 0 && on != true) return [];
            return [Make("turn_on", data)];
        }
        if (domain != "climate") return [];

        if (on == false) return [Make("set_hvac_mode", new JsonObject { ["hvac_mode"] = "off" })];
        var calls = new List<Call>();
        // Picking a mode is what switches a climate entity on.
        if (on == true || patch.ContainsKey("mode"))
        {
            var mode = patch.Str("mode") ?? state.Str("mode") ?? "cool";
            var name = modes.TryGetValue(mode, out var n) ? n : modes.Values.FirstOrDefault();
            if (name != null) calls.Add(Make("set_hvac_mode", new JsonObject { ["hvac_mode"] = name }));
        }
        if (patch.Num("target") is { } target)
            calls.Add(Make("set_temperature", new JsonObject { ["temperature"] = target }));
        if (patch.Str("fan") is { } fan && fans.TryGetValue(fan, out var fanName))
            calls.Add(Make("set_fan_mode", new JsonObject { ["fan_mode"] = fanName }));
        if (patch.Str("swing") is { } swing)
            calls.Add(Make("set_swing_mode", new JsonObject { ["swing_mode"] = swing == "off" ? "off" : "on" }));
        foreach (var preset in new[] { "sleep", "eco" })
        {
            if (patch.Bool(preset) is { } enabled)
                calls.Add(Make("set_preset_mode", new JsonObject { ["preset_mode"] = enabled ? preset : "none" }));
        }
        return calls;
    }

    // MARK: HTTP

    /// <summary>
    /// One REST request. Answers the parsed JSON, or the text itself as a string value when
    /// <paramref name="raw"/>; failures become a <see cref="ServiceFailure"/> worth showing.
    /// </summary>
    private async Task<JsonNode?> RequestAsync(HttpMethod method, string path, JsonObject? body = null,
                                               bool raw = false, CancellationToken cancel = default, Config? config = null)
    {
        config ??= _config;
        if (config is null || !Uri.TryCreate(config.Url + path, UriKind.Absolute, out var url))
            throw new ServiceFailure("Connect Home Assistant first");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        if (body != null) request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");

        HttpStatusCode code;
        string text;
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            code = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            var why = e is TaskCanceledException ? "timed out" : e.Message;
            throw new ServiceFailure($"Home Assistant at {config.Url} did not answer: {why}", e);
        }

        var status = (int)code;
        switch (status)
        {
            case >= 200 and < 300:
                break;
            case 401 or 403:
                throw new ServiceFailure("Home Assistant refused the token. Create a new long-lived access token and connect again.");
            case 404:
                throw new ServiceFailure($"Home Assistant does not know {path}. Is the API enabled?");
            default:
                throw new ServiceFailure($"Home Assistant answered {status} {(text.Length > 200 ? text[..200] : text)}".TrimEnd());
        }
        if (raw) return JsonValue.Create(text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    // MARK: Live updates

    /// <summary>
    /// Subscribes to state changes of the entities the hub cares about, and says so through
    /// <see cref="Changed"/>. Reconnects by itself; polling covers the gaps.
    /// </summary>
    private void Listen()
    {
        StopListening();
        if (_config is not { } config) return;
        var cts = _listening = new CancellationTokenSource();
        _ = KeepListeningAsync(config, cts.Token);
    }

    private void StopListening()
    {
        _listening?.Cancel();
        _listening?.Dispose();
        _listening = null;
    }

    private async Task KeepListeningAsync(Config config, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunSocketAsync(config, stop);
            }
            catch (Exception)
            {
                // Lost (or never had) the connection: try again in a while.
            }
            try { await Task.Delay(RetryDelay, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static Uri SocketUrl(string url)
    {
        var builder = new UriBuilder(url.TrimEnd('/') + "/api/websocket");
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    private async Task RunSocketAsync(Config config, CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(SocketUrl(config.Url), connecting.Token);
            _ = await ReceiveAsync(socket, connecting.Token); // auth_required
            await SendTextAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = config.Token }, connecting.Token);
            var auth = await ReceiveAsync(socket, connecting.Token);
            if (auth.Str("type") != "auth_ok") return; // a token that no longer works: the REST calls say why
        }
        await SendTextAsync(socket, new JsonObject { ["id"] = 1, ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, stop);

        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, stop);
            if (message.Obj("event").Obj("data").Str("entity_id") is not { } entity || !_watched.Contains(entity)) continue;
            // A burst of changes (a scene) becomes one read.
            if (Interlocked.Exchange(ref _changePending, 1) == 1) continue;
            _ = AnnounceChangeAsync(stop);
        }
        if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (Exception) { }
        }
    }

    private async Task AnnounceChangeAsync(CancellationToken stop)
    {
        try { await Task.Delay(400, stop); }
        catch (OperationCanceledException) { }
        Interlocked.Exchange(ref _changePending, 0);
        if (stop.IsCancellationRequested) return;
        if (_context is { } context) context.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }

    /// <summary>One whole text message as JSON (null for anything else); throws when the socket closes.</summary>
    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Home Assistant closed the connection");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 * 1024 * 1024) throw new WebSocketException("Message too large");
            if (result.EndOfMessage) break;
        }
        return Json.ParseObject(message.ToArray());
    }

    private static Task SendTextAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancel) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(Json.Serialize(message)), WebSocketMessageType.Text, true, cancel);
}
