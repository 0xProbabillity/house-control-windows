using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// Thrown by <see cref="HueService.PairAsync"/> until someone presses the round button on the
/// bridge (it then accepts pairing for 30 seconds). The app answers it with <c>{waiting: true}</c>
/// and asks again.
/// </summary>
public sealed class HueButtonNotPressedException() : ServiceFailure("Press the round button on the Hue Bridge.");

/// <summary>
/// A Philips Hue Bridge on the local network, through its own API (CLIP v2). No cloud and no
/// account: the bridge hands out a key when its round button is pressed. Hue lamps become the
/// plan's lamps; the temperature reported by Hue motion sensors can stand in for an AC's room
/// reading. The bridge's event stream tells the hub at once when a lamp changes elsewhere.
/// </summary>
/// <remarks>
/// Used from the UI thread, like every service. The event stream runs on the thread pool and
/// only reads the watched ids (swapped whole, never changed in place); <see cref="Changed"/> is
/// raised on the synchronization context the service was created on.
/// </remarks>
public sealed class HueService : IDeviceService, ISharedConnection, IDisposable
{
    public string Id => "hue";
    public string Title => "Philips Hue";
    public event Action? Changed;

    /// <summary>What is kept in the secret store, and what travels to another device of the household.</summary>
    /// <param name="Host">The bridge's address, with a port when it is not 443.</param>
    /// <param name="Key">The application key the bridge handed out when its button was pressed.</param>
    /// <param name="BridgeId">The bridge's own id, for the editor.</param>
    /// <param name="Name">The bridge's name, for the editor.</param>
    /// <param name="Certificate">
    /// SHA-256 of the bridge's certificate, remembered when it was paired: the bridge's
    /// certificate is self-signed, so it is trusted on first use and checked ever after.
    /// </param>
    internal sealed record Config(string Host, string Key, string? BridgeId, string? Name, string? Certificate)
    {
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["host"] = Host, ["key"] = Key };
            if (BridgeId != null) o["bridgeId"] = BridgeId;
            if (Name != null) o["name"] = Name;
            if (Certificate != null) o["certificate"] = Certificate;
            return o;
        }

        public static Config? From(JsonObject? o) =>
            o?.Str("host") is { } host && o.Str("key") is { } key
                ? new Config(host, key, o.Str("bridgeId"), o.Str("name"), o.Str("certificate"))
                : null;
    }

    private readonly ISecretStore _vault;
    private readonly SynchronizationContext? _context;
    private readonly BridgeTrust _trust = new();
    private readonly HttpClient _client;
    /// <summary>The event stream stays open for hours: it gets a client without the short timeout.</summary>
    private readonly HttpClient _streamClient;
    private Config? _config;
    private CancellationTokenSource? _stream;
    private volatile IReadOnlySet<string> _watched = new HashSet<string>();

    /// <summary>How long to wait before opening the event stream again after it dropped.</summary>
    internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>The bridge sends a keep-alive well within this; a stream silent for longer is dead.</summary>
    internal TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(600);

    public HueService(ISecretStore vault)
    {
        _vault = vault;
        _context = SynchronizationContext.Current;
        _client = new HttpClient(Handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8) };
        _streamClient = new HttpClient(Handler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _config = Config.From(Json.ParseObject(vault.Read()));
        if (_config is { } config)
        {
            _trust.Pin(config.Host, config.Certificate);
            Listen();
        }
    }

    private SocketsHttpHandler Handler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = _trust.Validate },
    };

    public bool IsConnected => _config != null;

    public JsonObject Info => _config is not { } config
        ? new JsonObject { ["connected"] = false }
        : new JsonObject
        {
            ["connected"] = true,
            ["host"] = config.Host,
            ["name"] = config.Name ?? "Hue Bridge",
            ["bridgeId"] = config.BridgeId ?? "",
        };

    /// <summary>The connection with its key and certificate, for the other devices of the household. Null when not paired.</summary>
    public JsonObject? Shareable => _config?.ToJson();

    /// <summary>Takes over a connection shared by another device of the household; null disconnects.</summary>
    public void Adopt(JsonObject? shared)
    {
        if (Config.From(shared) is not { } incoming)
        {
            if (_config != null) Disconnect();
            return;
        }
        if (incoming.Host == _config?.Host && incoming.Key == _config?.Key) return;
        _config = incoming;
        Save();
        _trust.Pin(incoming.Host, incoming.Certificate);
        Listen();
    }

    public void Disconnect()
    {
        _config = null;
        _vault.Write(null);
        StopListening();
    }

    public void Watch(IReadOnlySet<string> ids) => _watched = new HashSet<string>(ids);

    public void Dispose()
    {
        StopListening();
        _client.Dispose();
        _streamClient.Dispose();
    }

    private void Save() =>
        _vault.Write(_config is { } config ? Encoding.UTF8.GetBytes(Json.Serialize(config.ToJson())) : null);

    // MARK: Pairing

    /// <summary>
    /// Asks the bridge at <paramref name="host"/> for a key. Throws
    /// <see cref="HueButtonNotPressedException"/> until someone presses the round button on the
    /// bridge (it then accepts pairing for 30 seconds).
    /// </summary>
    public async Task PairAsync(string host, string? bridgeId = null, string? name = null, CancellationToken cancel = default)
    {
        host = host.Trim().Replace("https://", "").Replace("http://", "").Trim('/');
        if (host.Length == 0 || !Uri.TryCreate($"https://{host}/api", UriKind.Absolute, out var url))
            throw new ServiceFailure("Enter the bridge’s address");
        _trust.Pin(host, null); // trust on first use, remember below
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(new JsonObject { ["devicetype"] = "house_control#Windows", ["generateclientkey"] = true }),
        };
        string text;
        try
        {
            using var response = await _client.SendAsync(request, cancel);
            text = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            throw new ServiceFailure($"No Hue Bridge answered at {host}: {Reason(e)}", e);
        }
        JsonObject answer;
        try { answer = (JsonNode.Parse(text) as JsonArray)?.FirstOrDefault() as JsonObject ?? new JsonObject(); }
        catch (JsonException) { answer = new JsonObject(); }
        if (answer.Obj("error") is { } error)
        {
            if (error.Num("type") == 101) throw new HueButtonNotPressedException();
            throw new ServiceFailure($"The bridge said: {error.Str("description") ?? "error"}");
        }
        if (answer.Obj("success").Str("username") is not { } key)
            throw new ServiceFailure($"The bridge at {host} did not answer like a Hue Bridge");

        var paired = new Config(host, key, bridgeId, name, _trust.Fingerprint(host));
        _config = paired;
        // Fill in the bridge's own name for the editor.
        try
        {
            var bridge = (await GetAsync("/clip/v2/resource/bridge", cancel)).FirstOrDefault();
            var bridgeService = bridge?.Str("id");
            var device = (await GetAsync("/clip/v2/resource/device", cancel)).FirstOrDefault(d =>
                d.Arr("services")?.OfType<JsonObject>().Any(s => s.Str("rid") == bridgeService) == true);
            if (bridge != null && device != null)
            {
                paired = paired with
                {
                    BridgeId = bridge.Str("bridge_id") ?? bridgeId,
                    Name = device.Obj("metadata").Str("name") ?? name,
                };
            }
        }
        catch (ServiceFailure) { }
        _config = paired;
        Save();
        Listen();
    }

    // MARK: Devices

    public async Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default)
    {
        var lights = GetAsync("/clip/v2/resource/light", cancel);
        var rooms = GetAsync("/clip/v2/resource/room", cancel);
        var sensors = GetAsync("/clip/v2/resource/temperature", cancel);
        var hardware = GetAsync("/clip/v2/resource/device", cancel);
        var links = GetAsync("/clip/v2/resource/zigbee_connectivity", cancel);
        await Task.WhenAll(lights, rooms, sensors, hardware, links).ConfigureAwait(false);

        var roomOf = RoomIndex(rooms.Result);
        var deviceName = new Dictionary<string, string>();
        foreach (var d in hardware.Result)
            if (d.Str("id") is { } id && d.Obj("metadata").Str("name") is { } n) deviceName.TryAdd(id, n);
        var offline = links.Result
            .Where(l => l.Str("status") != "connected")
            .Select(l => l.Obj("owner").Str("rid"))
            .OfType<string>()
            .ToHashSet();

        var output = new List<ServiceDevice>();
        foreach (var light in lights.Result)
        {
            var owner = light.Obj("owner").Str("rid") ?? "";
            if (DeviceFromLight(light, roomOf.GetValueOrDefault(owner), !offline.Contains(owner)) is { } device)
                output.Add(device);
        }
        foreach (var sensor in sensors.Result)
        {
            if (sensor.Str("id") is not { } id) continue;
            var owner = sensor.Obj("owner").Str("rid") ?? "";
            output.Add(new ServiceDevice
            {
                Id = id,
                Name = deviceName.GetValueOrDefault(owner) ?? "Hue sensor",
                Kind = DeviceKind.Sensor,
                Room = roomOf.GetValueOrDefault(owner),
                Detail = "temperature sensor",
                Online = !offline.Contains(owner),
            });
        }
        return output
            .OrderBy(d => d.Kind.Name(), StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default)
    {
        var output = new Dictionary<string, DeviceReading>();
        if (ids.Count == 0) return output;
        var lights = GetAsync("/clip/v2/resource/light", cancel);
        var sensors = GetAsync("/clip/v2/resource/temperature", cancel);
        await Task.WhenAll(lights, sensors).ConfigureAwait(false);
        foreach (var light in lights.Result)
            if (light.Str("id") is { } id && ids.Contains(id)) output[id] = new DeviceReading { Light = LightState(light) };
        foreach (var sensor in sensors.Result)
            if (sensor.Str("id") is { } id && ids.Contains(id)) output[id] = new DeviceReading { Temperature = Temperature(sensor) };
        return output;
    }

    public async Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default)
    {
        if (kind != DeviceKind.Light) throw new ServiceFailure("Hue has no air conditioners");
        var body = LightBody(DeviceKinds.WithPower(patch, state));
        if (body.Count == 0) return;
        var answer = await CallAsync(HttpMethod.Put, $"/clip/v2/resource/light/{Uri.EscapeDataString(id)}", body, cancel).ConfigureAwait(false);
        if (answer.Arr("errors")?.FirstOrDefault() is { } first)
            throw new ServiceFailure($"Hue: {first.Str("description") ?? "the bridge refused the change"}");
    }

    // MARK: Mapping

    /// <summary>Device id → the name of the room it is in.</summary>
    internal static Dictionary<string, string> RoomIndex(IEnumerable<JsonObject> rooms)
    {
        var output = new Dictionary<string, string>();
        foreach (var room in rooms)
        {
            if (room.Obj("metadata").Str("name") is not { } name) continue;
            foreach (var child in room.Arr("children")?.OfType<JsonObject>() ?? [])
                if (child.Str("rid") is { } rid) output[rid] = name;
        }
        return output;
    }

    internal static ServiceDevice? DeviceFromLight(JsonObject light, string? room = null, bool online = true)
    {
        if (light.Str("id") is not { } id) return null;
        var caps = new List<string>();
        if (light.ContainsKey("dimming")) caps.Add("brightness");
        if (light.ContainsKey("color_temperature")) caps.Add("temp");
        if (light.ContainsKey("color")) caps.Add("color");
        var archetype = light.Obj("metadata").Str("archetype") ?? "light";
        return new ServiceDevice
        {
            Id = id,
            Name = light.Obj("metadata").Str("name") ?? "Hue light",
            Kind = DeviceKind.Light,
            Room = room,
            Online = online,
            Capabilities = caps,
            Detail = $"{archetype.Replace('_', ' ')} · " + (caps.Count == 0 ? "on/off" : string.Join(", ", caps)),
        };
    }

    /// <summary>A light resource in the panel's terms: <c>on</c>, <c>brightness</c> 0…1, <c>temp</c> in kelvin or <c>color</c>.</summary>
    internal static JsonObject LightState(JsonObject light)
    {
        var output = new JsonObject();
        if (light.Obj("on").Bool("on") is { } on) output["on"] = on;
        if (light.Obj("dimming").Num("brightness") is { } b) output["brightness"] = Math.Max(0.05, Math.Min(1, b / 100));
        var ct = light.Obj("color_temperature");
        if (ct.Bool("mirek_valid") == true && ct.Num("mirek") is { } mirek)
        {
            output["temp"] = Math.Max(2200, Math.Min(6500, ColorMath.Kelvin(mirek)));
            output["color"] = null;
        }
        else if (light.Obj("color").Obj("xy") is { } xy && xy.Num("x") is { } x && xy.Num("y") is { } y)
        {
            output["color"] = ColorMath.HexFromXy(x, y);
        }
        return output;
    }

    /// <summary>A temperature resource's reading in °C.</summary>
    internal static double? Temperature(JsonObject sensor)
    {
        var t = sensor.Obj("temperature");
        return t.Obj("temperature_report").Num("temperature") ?? t.Num("temperature");
    }

    /// <summary>The body of a light update for a change in the panel's terms.</summary>
    internal static JsonObject LightBody(JsonObject patch)
    {
        var body = new JsonObject();
        if (patch.Bool("on") is { } on) body["on"] = new JsonObject { ["on"] = on };
        if (patch.Num("brightness") is { } b)
            body["dimming"] = new JsonObject { ["brightness"] = Math.Round(b * 1000, MidpointRounding.AwayFromZero) / 10 };
        if (patch.Num("temp") is { } k)
            body["color_temperature"] = new JsonObject { ["mirek"] = (int)Math.Round(ColorMath.Mired(k), MidpointRounding.AwayFromZero) };
        if (patch.Str("color") is { } hex && ColorMath.Xy(hex) is var (x, y))
            body["color"] = new JsonObject { ["xy"] = new JsonObject { ["x"] = x, ["y"] = y } };
        return body;
    }

    // MARK: HTTP

    private async Task<List<JsonObject>> GetAsync(string path, CancellationToken cancel)
    {
        var answer = await CallAsync(HttpMethod.Get, path, null, cancel).ConfigureAwait(false);
        return answer.Arr("data")?.OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<JsonObject> CallAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancel)
    {
        if (_config is not { } config || !Uri.TryCreate($"https://{config.Host}{path}", UriKind.Absolute, out var url))
            throw new ServiceFailure("Pair a Hue Bridge first");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("hue-application-key", config.Key);
        if (body != null) request.Content = JsonContent(body);
        HttpStatusCode status;
        string text;
        try
        {
            using var response = await _client.SendAsync(request, cancel).ConfigureAwait(false);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            throw new ServiceFailure($"The Hue Bridge at {config.Host} did not answer: {Reason(e)}", e);
        }
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new ServiceFailure("The Hue Bridge no longer accepts this PC’s key. Pair it again.");
        var json = Json.ParseObject(text) ?? new JsonObject();
        if ((int)status is < 200 or >= 300)
        {
            var reason = json.Arr("errors")?.FirstOrDefault().Str("description") ?? $"status {(int)status}";
            throw new ServiceFailure($"Hue: {reason}");
        }
        return json;
    }

    private static StringContent JsonContent(JsonObject body)
    {
        var content = new StringContent(Json.Serialize(body), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string Reason(Exception e) => e switch
    {
        OperationCanceledException => "it took too long",
        HttpRequestException { InnerException: { } inner } h => $"{h.Message} {inner.Message}",
        _ => e.Message,
    };

    // MARK: Event stream

    private void Listen()
    {
        StopListening();
        if (_config is not { } config || !Uri.TryCreate($"https://{config.Host}/eventstream/clip/v2", UriKind.Absolute, out var url))
            return;
        var stop = _stream = new CancellationTokenSource();
        _ = Task.Run(() => StreamAsync(url, config.Key, stop.Token));
    }

    private void StopListening()
    {
        _stream?.Cancel();
        _stream?.Dispose();
        _stream = null;
    }

    private async Task StreamAsync(Uri url, string key, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(StreamIdleTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("hue-application-key", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                using var reader = new StreamReader(body, Encoding.UTF8);
                while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
                {
                    idle.CancelAfter(StreamIdleTimeout);
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    if (_watched.Any(id => line.Contains(id, StringComparison.Ordinal))) RaiseChanged();
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return; // disconnected, paired again elsewhere, or disposed
            }
            catch (Exception)
            {
                // The bridge restarted or the Wi-Fi dropped: try again below.
            }
            try { await Task.Delay(ReconnectDelay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void RaiseChanged()
    {
        if (_context is { } context) context.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }
}
