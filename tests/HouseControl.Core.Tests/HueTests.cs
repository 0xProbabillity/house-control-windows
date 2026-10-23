using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core;

namespace HouseControl.Core.Tests;

/// <summary>Philips Hue: its translation to and from the panel's vocabulary.</summary>
public class HueMappingTests
{
    [Fact]
    public void ReadsAndWritesLights()
    {
        var white = Node("""
            {"id": "l1", "metadata": {"name": "Desk", "archetype": "classic_bulb"}, "on": {"on": true},
             "dimming": {"brightness": 50.0}, "color_temperature": {"mirek": 400, "mirek_valid": true},
             "color": {"xy": {"x": 0.5, "y": 0.4}}}
            """);
        var state = HueService.LightState(white);
        Assert.True(state.Bool("on"));
        Assert.Equal(0.5, state.Num("brightness"));
        Assert.Equal(2500, state.Num("temp"));
        Assert.True(state.ContainsKey("color") && state["color"] is null);

        var colour = HueService.LightState(Node("""
            {"on": {"on": true}, "color_temperature": {"mirek": null, "mirek_valid": false},
             "color": {"xy": {"x": 0.6915, "y": 0.3083}}}
            """));
        Assert.Equal("#ff0000", colour.Str("color"));
        Assert.Equal(new[] { "brightness", "temp", "color" }, HueService.DeviceFromLight(white)!.Capabilities);
        Assert.Equal("classic bulb · brightness, temp, color", HueService.DeviceFromLight(white)!.Detail);
        var plug = HueService.DeviceFromLight(Node("""{"id": "p", "on": {"on": false}}"""))!;
        Assert.Empty(plug.Capabilities);
        Assert.Equal("light · on/off", plug.Detail);

        var body = HueService.LightBody(new JsonObject { ["on"] = true, ["brightness"] = 0.42, ["temp"] = 2500.0, ["color"] = "#ff0000" });
        Assert.True(body.Obj("on").Bool("on"));
        Assert.Equal(42, body.Obj("dimming").Num("brightness"));
        Assert.Equal(400, body.Obj("color_temperature").Num("mirek"));
        Assert.Equal(0.64, body.Obj("color").Obj("xy").Num("x")!.Value, 2);
        Assert.Empty(HueService.LightBody(new JsonObject { ["color"] = "red" }));

        var rooms = HueService.RoomIndex([Node("""{"metadata": {"name": "Living"}, "children": [{"rid": "dev1", "rtype": "device"}]}""")]);
        Assert.Equal(new Dictionary<string, string> { ["dev1"] = "Living" }, rooms);
        Assert.Equal(21.5, HueService.Temperature(Node("""{"temperature": {"temperature_report": {"temperature": 21.5}}}""")));
        Assert.Equal(19, HueService.Temperature(Node("""{"temperature": {"temperature": 19}}""")));
    }

    [Fact]
    public void NamesTheHostTheWayTlsDoes()
    {
        Assert.Equal("192.168.1.2", BridgeTrust.Bare("192.168.1.2:8443"));
        Assert.Equal("fe80::1", BridgeTrust.Bare("[fe80::1]:443"));
        Assert.Equal("bridge.local", BridgeTrust.Bare("bridge.local"));
    }

    private static JsonObject Node(string json) => (JsonObject)JsonNode.Parse(json)!;
}

/// <summary>The Hue service against a pretend bridge on 127.0.0.1, over TLS with a self-signed certificate.</summary>
public class HueBridgeTests : IDisposable
{
    private readonly FakeBridge _bridge = new();
    private readonly List<HueService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        _bridge.Dispose();
    }

    private HueService Service(ISecretStore? vault = null)
    {
        var service = new HueService(vault ?? new MemorySecretStore()) { ReconnectDelay = TimeSpan.FromMilliseconds(200) };
        _services.Add(service);
        return service;
    }

    private async Task<(HueService Service, MemorySecretStore Vault)> PairedAsync()
    {
        var vault = new MemorySecretStore();
        var service = Service(vault);
        _bridge.ButtonPressed = true;
        await service.PairAsync(_bridge.Host);
        return (service, vault);
    }

    [Fact]
    public async Task PairsOnceTheButtonIsPressed()
    {
        var vault = new MemorySecretStore();
        var service = Service(vault);
        Assert.False(service.IsConnected);
        Assert.False(service.Info.Bool("connected"));

        await Assert.ThrowsAsync<HueButtonNotPressedException>(() => service.PairAsync(" https://" + _bridge.Host + "/ "));
        Assert.False(service.IsConnected);
        Assert.Null(vault.Read());

        _bridge.ButtonPressed = true;
        await service.PairAsync(_bridge.Host, "fallback-id", "Fallback");
        Assert.True(service.IsConnected);
        Assert.StartsWith("house_control#", _bridge.LastDeviceType);
        var info = service.Info;
        Assert.Equal(_bridge.Host, info.Str("host"));
        Assert.Equal("Test Bridge", info.Str("name"));
        Assert.Equal(FakeBridge.BridgeId, info.Str("bridgeId"));
        Assert.False(info.ContainsKey("key"));

        // The key and the certificate it was paired over are kept, and come back on the next start.
        var stored = Json.ParseObject(vault.Read())!;
        Assert.Equal(FakeBridge.Key, stored.Str("key"));
        Assert.Equal(_bridge.Fingerprint, stored.Str("certificate"));
        var again = Service(vault);
        Assert.True(again.IsConnected);
        Assert.NotEmpty(await again.DevicesAsync());

        service.Disconnect();
        Assert.False(service.IsConnected);
        Assert.Null(vault.Read());
        await Assert.ThrowsAsync<ServiceFailure>(() => service.DevicesAsync());
    }

    [Fact]
    public async Task RefusesABridgeWhoseCertificateChanged()
    {
        var vault = new MemorySecretStore(Encoding.UTF8.GetBytes(Json.Serialize(new JsonObject
        {
            ["host"] = _bridge.Host,
            ["key"] = FakeBridge.Key,
            ["certificate"] = new string('0', 64),
        })));
        var service = Service(vault);
        await Assert.ThrowsAsync<ServiceFailure>(() => service.DevicesAsync());
        Assert.Equal(0, _bridge.ResourceRequests);
    }

    [Fact]
    public async Task RefusesAnUnknownKey()
    {
        var (service, _) = await PairedAsync();
        _bridge.Revoke();
        var failure = await Assert.ThrowsAsync<ServiceFailure>(() => service.DevicesAsync());
        Assert.Contains("Pair it again", failure.Message);
    }

    [Fact]
    public async Task ListsLampsAndSensorsWithTheirRooms()
    {
        var (service, _) = await PairedAsync();
        var devices = await service.DevicesAsync();
        Assert.Equal(new[] { "Colour lamp", "Smart plug", "Motion sensor" }, devices.Select(d => d.Name));

        var lamp = devices[0];
        Assert.Equal(DeviceKind.Light, lamp.Kind);
        Assert.Equal(new[] { "brightness", "temp", "color" }, lamp.Capabilities);
        Assert.Equal("Living room", lamp.Room);
        Assert.True(lamp.Online);
        Assert.Equal("sultan bulb · brightness, temp, color", lamp.Detail);

        var plug = devices[1];
        Assert.Empty(plug.Capabilities);
        Assert.False(plug.Online);
        Assert.Null(plug.Room);

        var sensor = devices[2];
        Assert.Equal(DeviceKind.Sensor, sensor.Kind);
        Assert.Equal("Living room", sensor.Room);
        Assert.Equal("temperature sensor", sensor.Detail);
        Assert.Equal("hue", sensor.ToJson(service.Id).Str("service"));
    }

    [Fact]
    public async Task ReadsAndSendsLightStates()
    {
        var (service, _) = await PairedAsync();
        var lamp = FakeBridge.LampId;
        var sensor = FakeBridge.SensorId;

        var readings = await service.ReadAsync(new HashSet<string> { lamp, sensor, "unknown" });
        Assert.Equal(2, readings.Count);
        Assert.False(readings[lamp].Light.Bool("on"));
        Assert.Equal(0.8, readings[lamp].Light.Num("brightness")!.Value, 3);
        Assert.Equal(22.4, readings[sensor].Temperature);
        Assert.Empty(await service.ReadAsync(new HashSet<string>()));

        // A colour for a lamp the panel shows as on switches the real one on as well.
        await service.SendAsync(DeviceKind.Light, lamp, new JsonObject { ["brightness"] = 0.3, ["color"] = "#0000ff" },
            new JsonObject { ["on"] = true });
        var put = _bridge.LastPut!;
        Assert.True(put.Obj("on").Bool("on"));
        Assert.Equal(30, put.Obj("dimming").Num("brightness"));
        var light = (await service.ReadAsync(new HashSet<string> { lamp }))[lamp].Light!;
        Assert.True(light.Bool("on"));
        Assert.Equal(0.3, light.Num("brightness")!.Value, 3);
        Assert.True(ColorMath.Rgb(light.Str("color"))!.Value.B > 0.9);

        await service.SendAsync(DeviceKind.Light, lamp, new JsonObject { ["temp"] = 2700.0 }, new JsonObject());
        Assert.Equal(370, _bridge.LastPut!.Obj("color_temperature").Num("mirek"));
        light = (await service.ReadAsync(new HashSet<string> { lamp }))[lamp].Light!;
        Assert.Equal(2700, light.Num("temp")!.Value, 20.0);
        Assert.True(light.ContainsKey("color") && light["color"] is null);

        // The bridge's own refusal reaches the panel.
        var refused = await Assert.ThrowsAsync<ServiceFailure>(() =>
            service.SendAsync(DeviceKind.Light, FakeBridge.PlugId, new JsonObject { ["brightness"] = 0.5 }, new JsonObject()));
        Assert.Contains("dimming", refused.Message);
        await Assert.ThrowsAsync<ServiceFailure>(() =>
            service.SendAsync(DeviceKind.Ac, lamp, new JsonObject { ["on"] = true }, new JsonObject()));
    }

    [Fact]
    public async Task TheEventStreamReportsChangesToWatchedLamps()
    {
        var context = new RecordingContext();
        HueService service;
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { service = Service(); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }

        _bridge.ButtonPressed = true;
        await service.PairAsync(_bridge.Host);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();
        service.Watch(new HashSet<string> { FakeBridge.LampId });
        await _bridge.WaitForListenerAsync(TimeSpan.FromSeconds(5));

        // A change to a lamp nobody watches says nothing.
        _bridge.Emit(FakeBridge.PlugId);
        await Task.Delay(300);
        Assert.False(changed.Task.IsCompleted);

        _bridge.Emit(FakeBridge.LampId);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(context.Posts > 0);

        // The stream comes back after the bridge drops it.
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridge.DropListeners();
        await _bridge.WaitForListenerAsync(TimeSpan.FromSeconds(5));
        await service.SendAsync(DeviceKind.Light, FakeBridge.LampId, new JsonObject { ["on"] = true }, new JsonObject());
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SharesTheConnectionWithAnotherDevice()
    {
        var (service, _) = await PairedAsync();
        var vault = new MemorySecretStore();
        var other = Service(vault);
        other.Adopt(service.Shareable);
        Assert.True(other.IsConnected);
        Assert.Equal(_bridge.Fingerprint, Json.ParseObject(vault.Read())!.Str("certificate"));
        Assert.Equal(3, (await other.DevicesAsync()).Count);
        other.Adopt(null);
        Assert.False(other.IsConnected);
    }

    /// <summary>Runs posted callbacks on the thread pool and counts them.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => _posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}

/// <summary>
/// Just enough of a Hue Bridge: link-button pairing, a few CLIP v2 resources, updates to
/// lights and the event stream, over HTTPS with a fresh self-signed certificate.
/// </summary>
internal sealed class FakeBridge : IDisposable
{
    public const string Key = "test-application-key";
    public const string BridgeId = "001788fffe000001";
    public const string LampId = "11111111-0000-0000-0000-000000000001";
    public const string PlugId = "11111111-0000-0000-0000-000000000002";
    public const string SensorId = "11111111-0000-0000-0000-000000000003";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly List<JsonObject> _resources;
    private readonly List<Stream> _listeners = [];
    private bool _revoked;

    public bool ButtonPressed { get; set; }
    public string Host { get; }
    public string Fingerprint { get; }
    public string? LastDeviceType { get; private set; }
    public JsonObject? LastPut { get; private set; }
    public int ResourceRequests;

    public FakeBridge()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={BridgeId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel only serves a certificate whose key it can find again: round-trip through PFX.
        _certificate = new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
        Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
        _resources = Resources();
        _listener.Start();
        Host = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = AcceptAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        DropListeners();
        _certificate.Dispose();
    }

    public void Revoke() => _revoked = true;

    private static List<JsonObject> Resources()
    {
        static JsonObject N(string json) => (JsonObject)JsonNode.Parse(json)!;
        return
        [
            N("""{"id": "b0", "type": "bridge", "bridge_id": "001788fffe000001", "owner": {"rid": "d0", "rtype": "device"}}"""),
            N("""{"id": "d0", "type": "device", "metadata": {"name": "Test Bridge", "archetype": "bridge_v2"}, "services": [{"rid": "b0", "rtype": "bridge"}]}"""),
            N("""{"id": "d1", "type": "device", "metadata": {"name": "Colour lamp", "archetype": "sultan_bulb"}, "services": []}"""),
            N("""{"id": "d2", "type": "device", "metadata": {"name": "Smart plug", "archetype": "plug"}, "services": []}"""),
            N("""{"id": "d3", "type": "device", "metadata": {"name": "Motion sensor", "archetype": "unknown_archetype"}, "services": []}"""),
            N("""{"id": "r1", "type": "room", "metadata": {"name": "Living room"}, "children": [{"rid": "d1", "rtype": "device"}, {"rid": "d3", "rtype": "device"}]}"""),
            N($$$"""
              {"id": "{{{LampId}}}", "type": "light", "owner": {"rid": "d1", "rtype": "device"},
               "metadata": {"name": "Colour lamp", "archetype": "sultan_bulb"}, "on": {"on": false},
               "dimming": {"brightness": 80}, "color_temperature": {"mirek": 366, "mirek_valid": true},
               "color": {"xy": {"x": 0.4573, "y": 0.41} } }
              """),
            N($$$"""
              {"id": "{{{PlugId}}}", "type": "light", "owner": {"rid": "d2", "rtype": "device"},
               "metadata": {"name": "Smart plug", "archetype": "plug"}, "on": {"on": false}}
              """),
            N($$$"""
              {"id": "{{{SensorId}}}", "type": "temperature", "owner": {"rid": "d3", "rtype": "device"},
               "temperature": {"temperature": 22.4, "temperature_report": {"temperature": 22.4} } }
              """),
            N("""{"id": "z1", "type": "zigbee_connectivity", "owner": {"rid": "d1", "rtype": "device"}, "status": "connected"}"""),
            N("""{"id": "z2", "type": "zigbee_connectivity", "owner": {"rid": "d2", "rtype": "device"}, "status": "connectivity_issue"}"""),
        ];
    }

    public async Task WaitForListenerAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_lock) if (_listeners.Count > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("the event stream never connected");
    }

    public void DropListeners()
    {
        lock (_lock)
        {
            foreach (var l in _listeners) l.Dispose();
            _listeners.Clear();
        }
    }

    /// <summary>Tells every open event stream that <paramref name="id"/> changed.</summary>
    public void Emit(string id)
    {
        var data = new JsonArray(new JsonObject
        {
            ["type"] = "update",
            ["data"] = new JsonArray(new JsonObject { ["id"] = id, ["type"] = "light" }),
        });
        var bytes = Encoding.UTF8.GetBytes($"id: 1:0\ndata: {Json.Serialize(data)}\n\n");
        lock (_lock)
        {
            foreach (var l in _listeners.ToList())
            {
                try { l.Write(bytes); l.Flush(); }
                catch (Exception) { _listeners.Remove(l); }
            }
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        var keep = false;
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate });
            var head = await ReadHeadAsync(ssl);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            var (method, path) = (parts[0], parts[1]);
            var headers = lines.Skip(1).Where(l => l.Contains(':'))
                .ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            var body = "";
            if (headers.TryGetValue("content-length", out var length) && int.Parse(length) is var n and > 0)
            {
                var buffer = new byte[n];
                await ssl.ReadExactlyAsync(buffer);
                body = Encoding.UTF8.GetString(buffer);
            }
            keep = await AnswerAsync(ssl, method, path, headers.GetValueOrDefault("hue-application-key"), body);
        }
        catch (Exception)
        {
            // A client that went away, or a handshake the client refused (a pin that does not match).
        }
        finally
        {
            if (!keep) { ssl.Dispose(); client.Dispose(); }
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r')
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 4);
        }
    }

    /// <summary>Answers one request; true when the connection stays open as an event stream.</summary>
    private async Task<bool> AnswerAsync(Stream stream, string method, string path, string? key, string body)
    {
        if (method == "POST" && path == "/api")
        {
            LastDeviceType = Json.ParseObject(body)?.Str("devicetype");
            if (!ButtonPressed)
            {
                await RespondAsync(stream, 200, """[{"error": {"type": 101, "address": "", "description": "link button not pressed"}}]""");
                return false;
            }
            await RespondAsync(stream, 200, $$$"""[{"success": {"username": "{{{Key}}}", "clientkey": "00"}}]""");
            return false;
        }
        if (key != Key || _revoked)
        {
            await RespondAsync(stream, 403, """{"errors": [{"description": "unauthorized user"}], "data": []}""");
            return false;
        }
        if (path == "/eventstream/clip/v2")
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n: hi\n\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.FlushAsync();
            lock (_lock) _listeners.Add(stream);
            return true;
        }
        const string prefix = "/clip/v2/resource/";
        if (!path.StartsWith(prefix))
        {
            await RespondAsync(stream, 404, """{"errors": [{"description": "unknown resource"}], "data": []}""");
            return false;
        }
        Interlocked.Increment(ref ResourceRequests);
        var segments = path[prefix.Length..].Split('/');
        var (type, rid) = (segments[0], segments.Length > 1 ? segments[1] : null);
        string answer;
        var status = 200;
        string? changed = null;
        lock (_lock)
        {
            var list = _resources.Where(r => r.Str("type") == type && (rid is null || r.Str("id") == rid)).ToList();
            if (method == "GET")
            {
                answer = Json.Serialize(new JsonObject
                {
                    ["errors"] = new JsonArray(),
                    ["data"] = new JsonArray(list.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
                });
            }
            else if (method == "PUT" && rid != null && list.Count == 1)
            {
                var target = list[0];
                var patch = Json.ParseObject(body) ?? new JsonObject();
                LastPut = (JsonObject)patch.DeepClone();
                if (patch.ContainsKey("dimming") && !target.ContainsKey("dimming"))
                {
                    status = 400;
                    answer = """{"errors": [{"description": "device (dimming) is not available"}], "data": []}""";
                }
                else
                {
                    if (patch.Obj("color_temperature") is { } ct) ct["mirek_valid"] = true;
                    if (patch.ContainsKey("color") && target.Obj("color_temperature") is { } own)
                    {
                        own["mirek"] = null;
                        own["mirek_valid"] = false;
                    }
                    Merge(target, patch);
                    changed = rid;
                    answer = $$$"""{"errors": [], "data": [{"rid": "{{{rid}}}", "rtype": "{{{type}}}"}]}""";
                }
            }
            else
            {
                status = 405;
                answer = """{"errors": [{"description": "method not available"}], "data": []}""";
            }
        }
        await RespondAsync(stream, status, answer);
        if (changed != null) Emit(changed);
        return false;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (k, v) in patch.ToList())
        {
            if (v is JsonObject inner && target[k] is JsonObject existing) Merge(existing, inner);
            else target[k] = v?.DeepClone();
        }
    }

    private static async Task RespondAsync(Stream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
