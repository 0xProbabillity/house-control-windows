using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core.HomeAssistant;

namespace HouseControl.Core.Tests;

/// <summary>Home Assistant's translation to and from the panel's vocabulary, without a server.</summary>
public class HomeAssistantMappingTests
{
    private static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void ListsLightsClimateAndSensors()
    {
        var light = HomeAssistantService.Device(O("""
            {"entity_id":"light.bed_light","state":"off","attributes":{"friendly_name":"Bed Light","supported_color_modes":["color_temp","hs"]}}
            """))!;
        Assert.Equal(DeviceKind.Light, light.Kind);
        Assert.Equal("Bed Light", light.Name);
        Assert.Equal(["brightness", "temp", "color"], light.Capabilities);
        var plug = HomeAssistantService.Device(O("""{"entity_id":"switch.decorative_lights","state":"unavailable","attributes":{}}"""))!;
        Assert.Empty(plug.Capabilities);
        Assert.False(plug.Online);
        var ac = HomeAssistantService.Device(O("""
            {"entity_id":"climate.hvac","state":"cool","attributes":{"min_temp":7,"max_temp":35,"hvac_modes":["off","cool"]}}
            """))!;
        Assert.Equal(DeviceKind.Ac, ac.Kind);
        Assert.Equal((10.0, 32.0), ac.Range);
        Assert.Equal(DeviceKind.Sensor, HomeAssistantService.Device(O("""
            {"entity_id":"sensor.outside","state":"15.6","attributes":{"unit_of_measurement":"°C"}}
            """))?.Kind);
        Assert.Null(HomeAssistantService.Device(O("""{"entity_id":"sensor.power","state":"3","attributes":{"unit_of_measurement":"W"}}""")));
        Assert.Null(HomeAssistantService.Device(O("""{"entity_id":"media_player.tv","state":"on","attributes":{}}""")));
    }

    [Fact]
    public void ReadsLightState()
    {
        var white = HomeAssistantService.Reading(O("""
            {"entity_id":"light.ceiling","state":"on","attributes":{"brightness":255,"color_mode":"color_temp","color_temp_kelvin":2631}}
            """)).Light!;
        Assert.True(white.Bool("on"));
        Assert.Equal(1, white.Num("brightness"));
        Assert.Equal(2631, white.Num("temp"));
        Assert.True(white.ContainsKey("color"));
        Assert.Null(white["color"]);
        var colour = HomeAssistantService.Reading(O("""
            {"entity_id":"light.kitchen","state":"on","attributes":{"brightness":180,"color_mode":"hs","rgb_color":[255,64,112]}}
            """)).Light!;
        Assert.Equal("#ff4070", colour.Str("color"));
        Assert.False(HomeAssistantService.Reading(O("""{"entity_id":"light.x","state":"unavailable","attributes":{}}""")).Online);
    }

    [Fact]
    public void ReadsClimate()
    {
        var reading = HomeAssistantService.Reading(O("""
            {"entity_id":"climate.hvac","state":"fan_only","attributes":{
              "hvac_modes":["off","heat","cool","auto","dry","fan_only"],"temperature":21,"current_temperature":22,
              "fan_modes":["on_low","on_high","auto_low","auto_high","off"],"fan_mode":"on_high",
              "swing_modes":["auto","1","off"],"swing_mode":"off","preset_modes":["home","eco"],"preset_mode":"eco"}}
            """));
        var ac = reading.Ac!;
        Assert.True(ac.Bool("on"));
        Assert.Equal("fan", ac.Str("mode"));
        Assert.Equal(21, ac.Num("target"));
        Assert.Equal(22, ac.Num("roomTemp"));
        Assert.Equal("high", ac.Str("fan"));
        Assert.Equal("off", ac.Str("swing"));
        Assert.True(ac.Bool("eco"));
        Assert.False(ac.Bool("idle"));
        var features = reading.Features!;
        Assert.Equal("""["cool","heat","dry","fan","auto"]""", features["modes"]!.ToJsonString());
        Assert.Equal("""["low","high"]""", features["fans"]!.ToJsonString());
        Assert.True(features.Bool("eco"));
        Assert.False(features.Bool("sleep"));
        Assert.True(features.Bool("swing"));

        // A thermostat that is on but whose room is already warm enough is idle, not heating.
        static bool? Idle(string state, string? action)
        {
            var attributes = new JsonObject { ["hvac_modes"] = new JsonArray("off", "heat"), ["temperature"] = 21 };
            if (action != null) attributes["hvac_action"] = action;
            return HomeAssistantService.Reading(new JsonObject
            {
                ["entity_id"] = "climate.heater", ["state"] = state, ["attributes"] = attributes,
            }).Ac.Bool("idle");
        }
        Assert.True(Idle("heat", "idle"));
        Assert.False(Idle("heat", "heating"));
        Assert.False(Idle("heat", null));
        Assert.False(Idle("off", "off"));

        // A heat_cool-only thermostat reports a range instead of one set-point.
        var range = HomeAssistantService.Reading(O("""
            {"entity_id":"climate.ecobee","state":"heat_cool","attributes":{"hvac_modes":["off","heat_cool"],"target_temp_low":21,"target_temp_high":24}}
            """)).Ac!;
        Assert.Equal("auto", range.Str("mode"));
        Assert.Equal(23, range.Num("target"));
    }

    [Fact]
    public void ReadsTemperatureSensorsInCelsius()
    {
        Assert.Equal(15.6, HomeAssistantService.Reading(O("""
            {"entity_id":"sensor.outside","state":"15.6","attributes":{"unit_of_measurement":"°C"}}
            """)).Temperature);
        Assert.Equal(20, HomeAssistantService.Reading(O("""
            {"entity_id":"sensor.porch","state":"68","attributes":{"unit_of_measurement":"°F"}}
            """)).Temperature!.Value, 6);
    }

    [Fact]
    public void BuildsServiceCalls()
    {
        Dictionary<string, string> none = new();
        var off = HomeAssistantService.Calls("light.a", new JsonObject { ["on"] = false }, new JsonObject(), none, none);
        Assert.Equal(["turn_off"], off.Select(c => c.Service));
        var dim = HomeAssistantService.Calls("light.a", new JsonObject { ["brightness"] = 0.5, ["temp"] = 3000.0 }, new JsonObject(), none, none);
        Assert.Equal("turn_on", dim[0].Service);
        Assert.Equal("light", dim[0].Domain);
        Assert.Equal(50, dim[0].Data.Num("brightness_pct"));
        Assert.Equal(3000, dim[0].Data.Num("color_temp_kelvin"));
        Assert.Equal("light.a", dim[0].Data.Str("entity_id"));
        var colour = HomeAssistantService.Calls("light.a", new JsonObject { ["color"] = "#ff8000" }, new JsonObject(), none, none);
        Assert.Equal("[255,128,0]", colour[0].Data["rgb_color"]!.ToJsonString());
        Assert.Equal(["turn_on"], HomeAssistantService.Calls("switch.p", new JsonObject { ["on"] = true }, new JsonObject(), none, none).Select(c => c.Service));
        Assert.Empty(HomeAssistantService.Calls("switch.p", new JsonObject { ["brightness"] = 0.3 }, new JsonObject(), none, none));

        var modes = HomeAssistantService.ModeTable(["off", "cool", "heat_cool", "fan_only"]);
        Assert.Equal(new Dictionary<string, string> { ["cool"] = "cool", ["auto"] = "heat_cool", ["fan"] = "fan_only" }, modes);
        var fans = FanNames.Table(["on_low", "on_high", "auto_low", "off"]);
        Assert.Equal(new Dictionary<string, string> { ["low"] = "on_low", ["high"] = "on_high" }, fans);
        var on = HomeAssistantService.Calls("climate.x", new JsonObject { ["on"] = true }, new JsonObject { ["mode"] = "auto" }, fans, modes);
        Assert.Equal("heat_cool", on[0].Data.Str("hvac_mode"));
        var change = HomeAssistantService.Calls("climate.x", new JsonObject { ["target"] = 23.0, ["fan"] = "high", ["eco"] = true },
            new JsonObject { ["mode"] = "cool" }, fans, modes);
        Assert.Equal(new HashSet<string> { "set_temperature", "set_fan_mode", "set_preset_mode" }, change.Select(c => c.Service).ToHashSet());
        Assert.Equal("on_high", change.First(c => c.Service == "set_fan_mode").Data.Str("fan_mode"));
        Assert.Equal("eco", change.First(c => c.Service == "set_preset_mode").Data.Str("preset_mode"));
        Assert.Equal("off", HomeAssistantService.Calls("climate.x", new JsonObject { ["on"] = false }, new JsonObject(), fans, modes)[0].Data.Str("hvac_mode"));
    }

    [Fact]
    public void NormalizesTheAddress()
    {
        Assert.Equal("http://homeassistant.local:8123", HomeAssistantService.Normalize("homeassistant.local"));
        Assert.Equal("http://homeassistant:8123", HomeAssistantService.Normalize("homeassistant"));
        Assert.Equal("http://10.0.0.5:8123", HomeAssistantService.Normalize(" http://10.0.0.5:8123/ "));
        Assert.Equal("https://ha.example.com", HomeAssistantService.Normalize("https://ha.example.com"));
        Assert.Equal("http://box.local:80", HomeAssistantService.Normalize("box.local:80"));
        Assert.Throws<ServiceFailure>(() => HomeAssistantService.Normalize(""));
        Assert.Throws<ServiceFailure>(() => HomeAssistantService.Normalize("ftp://ha.example.com"));
    }

    [Fact]
    public void ReadsAreasFromTheTemplate()
    {
        var areas = HomeAssistantService.ParseAreas("light.a\tKitchen\nlight.b\t\nsensor.c\tNone\nclimate.d\tBedroom\n");
        Assert.Equal(new Dictionary<string, string> { ["light.a"] = "Kitchen", ["climate.d"] = "Bedroom" }, areas);
        Assert.Equal("ws://h.local:8123/api/websocket", HomeAssistantService.SocketUrl("http://h.local:8123").ToString());
        Assert.Equal("wss://ha.example.com/api/websocket", HomeAssistantService.SocketUrl("https://ha.example.com").ToString());
    }
}

/// <summary>The client against a small Home Assistant stand-in: REST and the WebSocket.</summary>
public class HomeAssistantServerTests : IDisposable
{
    private readonly FakeHomeAssistant _server = new();

    public void Dispose() => _server.Dispose();

    private static async Task<T> Within<T>(Task<T> task, int seconds = 5)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(done == task, "timed out");
        return await task;
    }

    [Fact]
    public async Task ConnectsListsReadsAndSends()
    {
        var vault = new MemorySecretStore();
        using var service = new HomeAssistantService(vault);
        Assert.False(service.IsConnected);
        Assert.False(service.Info.Bool("connected"));
        await Assert.ThrowsAsync<ServiceFailure>(() => service.DevicesAsync());

        var refused = await Assert.ThrowsAsync<ServiceFailure>(() => service.ConnectAsync(_server.Url, "wrong-token"));
        Assert.Contains("refused the token", refused.Message);
        Assert.False(service.IsConnected);
        Assert.Null(vault.Read());
        await Assert.ThrowsAsync<ServiceFailure>(() => service.ConnectAsync(_server.Url, "  "));

        await service.ConnectAsync(_server.Url + "/", " " + FakeHomeAssistant.Token + "\n");
        Assert.True(service.IsConnected);
        Assert.Equal("Test Home", service.Info.Str("name"));
        Assert.Equal("2026.10.0", service.Info.Str("version"));
        Assert.Equal(_server.Url, service.Info.Str("url"));
        Assert.DoesNotContain(FakeHomeAssistant.Token, service.Info.ToJsonString());
        var stored = Json.ParseObject(vault.Read())!;
        Assert.Equal(FakeHomeAssistant.Token, stored.Str("token"));
        Assert.Equal(_server.Url, stored.Str("url"));

        var devices = await service.DevicesAsync();
        Assert.Equal(["climate.hvac", "light.kitchen", "switch.plug", "sensor.outside"], devices.Select(d => d.Id));
        Assert.Equal("Kitchen", devices.First(d => d.Id == "light.kitchen").Room);
        Assert.Null(devices.First(d => d.Id == "switch.plug").Room);
        var json = devices.First(d => d.Id == "climate.hvac").ToJson(service.Id);
        Assert.Equal("homeassistant", json.Str("service"));
        Assert.Equal(16, json.Num("min"));

        var readings = await service.ReadAsync(new HashSet<string> { "light.kitchen", "climate.hvac", "sensor.outside", "light.missing" });
        Assert.Equal(3, readings.Count);
        Assert.Equal("#ff4070", readings["light.kitchen"].Light.Str("color"));
        Assert.Equal(19.5, readings["sensor.outside"].Temperature);
        Assert.Equal("cool", readings["climate.hvac"].Ac.Str("mode"));

        // The read taught it the climate entity's own fan and mode names.
        await service.SendAsync(DeviceKind.Ac, "climate.hvac",
            new JsonObject { ["mode"] = "heat", ["fan"] = "high" }, new JsonObject { ["mode"] = "heat" });
        await service.SendAsync(DeviceKind.Light, "light.kitchen", new JsonObject { ["on"] = false }, new JsonObject());
        Assert.Equal([
            "/api/services/climate/set_hvac_mode {\"entity_id\":\"climate.hvac\",\"hvac_mode\":\"heat\"}",
            "/api/services/climate/set_fan_mode {\"entity_id\":\"climate.hvac\",\"fan_mode\":\"High\"}",
            "/api/services/light/turn_off {\"entity_id\":\"light.kitchen\"}",
        ], _server.ServiceCalls);
        Assert.Equal("Bearer wrong-token", _server.Authorizations[0]);
        Assert.All(_server.Authorizations.Skip(1), a =>Assert.Equal("Bearer " + FakeHomeAssistant.Token, a));

        // A second service started from the same vault is connected straight away.
        using (var again = new HomeAssistantService(vault)) Assert.True(again.IsConnected);

        service.Disconnect();
        Assert.False(service.IsConnected);
        Assert.Null(vault.Read());
    }

    [Fact]
    public async Task ReportsAnUnreachableServer()
    {
        var port = FakeHomeAssistant.FreePort();
        using var service = new HomeAssistantService(new MemorySecretStore());
        var failure = await Assert.ThrowsAsync<ServiceFailure>(() => service.ConnectAsync($"http://127.0.0.1:{port}", "abc"));
        Assert.Contains("did not answer", failure.Message);
        Assert.DoesNotContain("abc", failure.Message);
    }

    [Fact]
    public async Task SaysWhenAWatchedEntityChanges()
    {
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        HomeAssistantService service;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            service = new HomeAssistantService(new MemorySecretStore());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using (service)
        {
            var changes = 0;
            var onContext = true;
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += () =>
            {
                onContext &= SynchronizationContext.Current == context;
                Interlocked.Increment(ref changes);
                first.TrySetResult(true);
            };
            service.Watch(new HashSet<string> { "light.kitchen" });
            await service.ConnectAsync(_server.Url, FakeHomeAssistant.Token);
            await Within(_server.Subscribed.Task);
            Assert.True(_server.SocketAuthenticated);

            await _server.PushAsync("light.elsewhere");
            await Task.Delay(700);
            Assert.Equal(0, changes);

            // A burst of changes (a scene) is one report.
            await _server.PushAsync("light.kitchen");
            await _server.PushAsync("light.kitchen");
            await _server.PushAsync("light.kitchen");
            await Within(first.Task);
            await Task.Delay(700);
            Assert.Equal(1, changes);
            Assert.True(onContext);
            Assert.True(context.Posts > 0);

            service.Disconnect();
            await _server.PushAsync("light.kitchen");
            await Task.Delay(700);
            Assert.Equal(1, changes);
        }
    }

    /// <summary>Runs posted work on the thread pool with itself as the current context, and counts it.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                callback(state);
            });
        }
    }
}

/// <summary>
/// Just enough of Home Assistant on 127.0.0.1: <c>/api/config</c>, <c>/api/states</c>,
/// <c>/api/template</c>, <c>/api/services/…</c> and the <c>/api/websocket</c> handshake.
/// </summary>
internal sealed class FakeHomeAssistant : IDisposable
{
    public const string Token = "test-token-not-real";
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private WebSocket? _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);

    public string Url { get; }
    public List<string> ServiceCalls { get; } = new();
    public List<string> Authorizations { get; } = new();
    public TaskCompletionSource<bool> Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool SocketAuthenticated { get; private set; }

    private const string States = """
        [
          {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen","supported_color_modes":["hs"],
             "brightness":180,"color_mode":"hs","rgb_color":[255,64,112]}},
          {"entity_id":"switch.plug","state":"off","attributes":{"friendly_name":"Plug"}},
          {"entity_id":"climate.hvac","state":"cool","attributes":{"friendly_name":"AC","min_temp":16,"max_temp":30,
             "hvac_modes":["off","cool","heat"],"fan_modes":["Low","High"],"fan_mode":"Low","temperature":22}},
          {"entity_id":"sensor.outside","state":"19.5","attributes":{"friendly_name":"Outside","device_class":"temperature","unit_of_measurement":"°C"}},
          {"entity_id":"sensor.power","state":"3","attributes":{"unit_of_measurement":"W"}},
          {"entity_id":"media_player.tv","state":"on","attributes":{}}
        ]
        """;

    public FakeHomeAssistant()
    {
        var port = FreePort();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = ServeAsync();
    }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        try
        {
            if (path == "/api/websocket" && request.IsWebSocketRequest)
            {
                await SocketAsync(context);
                return;
            }
            var authorization = request.Headers["Authorization"] ?? "";
            lock (Authorizations) Authorizations.Add(authorization);
            var body = await new StreamReader(request.InputStream, Encoding.UTF8).ReadToEndAsync();
            if (authorization != "Bearer " + Token)
            {
                await Answer(context, 401, "401: Unauthorized", "text/plain");
                return;
            }
            switch (path)
            {
                case "/api/config":
                    await Answer(context, 200, """{"location_name":"Test Home","version":"2026.10.0"}""");
                    break;
                case "/api/states":
                    await Answer(context, 200, States);
                    break;
                case "/api/template":
                    Assert.Contains("area_name", Json.ParseObject(body).Str("template"));
                    await Answer(context, 200, "light.kitchen\tKitchen\nswitch.plug\t\nclimate.hvac\tNone\n", "text/plain");
                    break;
                case var p when p.StartsWith("/api/services/"):
                    lock (ServiceCalls) ServiceCalls.Add($"{p} {Json.Serialize(Json.ParseObject(body))}");
                    await Answer(context, 200, "[]");
                    break;
                default:
                    await Answer(context, 404, "404: Not Found", "text/plain");
                    break;
            }
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    private static async Task Answer(HttpListenerContext context, int status, string text, string type = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task SocketAsync(HttpListenerContext context)
    {
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await SendAsync(socket, """{"type":"auth_required","ha_version":"2026.10.0"}""");
        var auth = await ReceiveAsync(socket);
        if (auth.Str("type") != "auth" || auth.Str("access_token") != Token)
        {
            await SendAsync(socket, """{"type":"auth_invalid","message":"Invalid access token"}""");
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, CancellationToken.None);
            return;
        }
        SocketAuthenticated = true;
        await SendAsync(socket, """{"type":"auth_ok","ha_version":"2026.10.0"}""");
        var subscribe = await ReceiveAsync(socket);
        if (subscribe.Str("type") != "subscribe_events" || subscribe.Str("event_type") != "state_changed") return;
        await SendAsync(socket, $$"""{"id":{{subscribe.Num("id")}},"type":"result","success":true,"result":null}""");
        _socket = socket;
        Subscribed.TrySetResult(true);
        // Keep reading so a close from the client is noticed.
        try
        {
            while (socket.State == WebSocketState.Open) await ReceiveAsync(socket);
        }
        catch (Exception) { }
    }

    /// <summary>A <c>state_changed</c> event for <paramref name="entity"/> on the open subscription.</summary>
    public async Task PushAsync(string entity)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        var message = Json.Serialize(new JsonObject
        {
            ["id"] = 1,
            ["type"] = "event",
            ["event"] = new JsonObject
            {
                ["event_type"] = "state_changed",
                ["data"] = new JsonObject
                {
                    ["entity_id"] = entity,
                    ["old_state"] = null,
                    ["new_state"] = new JsonObject { ["entity_id"] = entity, ["state"] = "on", ["attributes"] = new JsonObject() },
                },
            },
        });
        try { await SendAsync(socket, message); }
        catch (Exception) { }
    }

    private async Task SendAsync(WebSocket socket, string text)
    {
        await _sending.WaitAsync();
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _stop.Token); }
        finally { _sending.Release(); }
    }

    private async Task<JsonObject?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Json.ParseObject(message.ToArray());
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _socket?.Abort(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
    }
}
