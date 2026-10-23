using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// Everything between the UI and the real devices: which platforms are connected (Smart Life,
/// Home Assistant, Philips Hue), which device of the plan is linked to which real device,
/// sending the changes made in the panel and reading the devices' state back.
/// Used from the UI thread only.
/// </summary>
public sealed class DeviceHub
{
    public EventLog Log { get; }
    /// <summary>Smart Life; null in a build or test without it.</summary>
    public ILinkedCloud? Cloud { get; }
    /// <summary>The platforms other than Smart Life, by the id a plan's <c>link.service</c> uses.</summary>
    public IReadOnlyList<IDeviceService> Services { get; }

    private IDeviceService? Service(string id) => Services.FirstOrDefault(s => s.Id == id);
    /// <summary>Smart Life when it is part of this build and signed in.</summary>
    private ILinkedCloud? ConnectedCloud => Cloud is { IsConnected: true } ? Cloud : null;

    private sealed record Link(DeviceKind Kind, string Service, string Id, (string Service, string Id)? Sensor);

    /// <summary>App device id → its Smart Life link, for the devices of the active plan.</summary>
    private Dictionary<string, (string Kind, JsonObject Link)> _links = new();
    /// <summary>App device id → its link to another platform.</summary>
    private Dictionary<string, Link> _serviceLinks = new();
    /// <summary>Sensors from other platforms that report the room of a Smart Life AC.</summary>
    private Dictionary<string, (string Service, string Id)> _tuyaSensors = new();
    /// <summary>The command in flight for each real device; the next one waits for it.</summary>
    private readonly Dictionary<string, Task> _commandTails = new();
    private CancellationTokenSource? _refresh;

    /// <summary>
    /// Raised after a command failed in a way that leaves the panel out of step with the
    /// devices, or a platform reported a change, so the owner can read their state again.
    /// </summary>
    public event Action? Stale;

    public DeviceHub(string directory, IEnumerable<IDeviceService> services, ILinkedCloud? cloud = null)
    {
        Log = new EventLog(directory);
        Services = services.ToList();
        Cloud = cloud;
        foreach (var service in Services) service.Changed += ScheduleRefresh;
    }

    /// <summary>Changes reported by a platform arrive in bursts: one read for all of them.</summary>
    private async void ScheduleRefresh()
    {
        _refresh?.Cancel();
        var cts = _refresh = new CancellationTokenSource();
        try { await Task.Delay(300, cts.Token); }
        catch (OperationCanceledException) { return; }
        Stale?.Invoke();
    }

    // MARK: Links

    public bool IsConnected => ConnectedCloud != null || Services.Any(s => s.IsConnected && s.ControlsDevices);
    public bool HasLinks => _links.Count > 0 || _serviceLinks.Count > 0;

    /// <summary>True when there is something to read at all.</summary>
    public bool CanPoll =>
        (ConnectedCloud != null && _links.Count > 0)
        || _serviceLinks.Values.Any(l => Service(l.Service) is { IsConnected: true, ControlsDevices: true }
                                         || (l.Sensor is { } s && Service(s.Service) is { IsConnected: true }))
        || _tuyaSensors.Values.Any(s => Service(s.Service) is { IsConnected: true });

    public int LinkedDeviceCount => _links.Count + _serviceLinks.Values.Count(l => l.Id.Length > 0);

    /// <summary>Takes the device links from <paramref name="plan"/>; null (a bundled demo) means everything is simulated.</summary>
    public void SetLinks(JsonObject? plan)
    {
        _links = new();
        _serviceLinks = new();
        _tuyaSensors = new();
        try
        {
            if (plan is null) return;
            foreach (var (kind, key) in new[] { ("light", "lights"), ("ac", "acs") })
            {
                foreach (var device in plan.Arr(key)?.OfType<JsonObject>() ?? [])
                {
                    if (device.Str("id") is not { } id) continue;
                    var sensor = SensorLink(device["sensor"]);
                    if (device.Obj("link") is { } link && link.Str("service") is { } service && link.Str("id") is { Length: > 0 } target)
                    {
                        _serviceLinks[id] = new Link(DeviceKinds.Parse(kind), service, target, sensor);
                    }
                    else if (Cloud != null && device.Obj("tuya") is { } tuya && tuya.Str("deviceId") is { Length: > 0 })
                    {
                        _links[id] = (kind, tuya);
                        if (sensor is { } s) _tuyaSensors[id] = s;
                    }
                    else if (sensor is not null && kind == "ac")
                    {
                        // A simulated AC can still show a real room temperature.
                        _serviceLinks[id] = new Link(DeviceKind.Ac, "", "", sensor);
                    }
                }
            }
        }
        finally
        {
            WatchLinks();
        }
    }

    /// <summary><c>{"service": "hue", "id": "…"}</c> in a plan.</summary>
    private static (string, string)? SensorLink(JsonNode? value) =>
        value is JsonObject o && o.Str("service") is { } service && o.Str("id") is { Length: > 0 } id ? (service, id) : null;

    private Dictionary<string, HashSet<string>> Wanted()
    {
        var ids = new Dictionary<string, HashSet<string>>();
        void Add(string service, string id)
        {
            if (!ids.TryGetValue(service, out var set)) ids[service] = set = new HashSet<string>();
            set.Add(id);
        }
        foreach (var link in _serviceLinks.Values)
        {
            if (link.Id.Length > 0) Add(link.Service, link.Id);
            if (link.Sensor is { } s) Add(s.Service, s.Id);
        }
        foreach (var s in _tuyaSensors.Values) Add(s.Service, s.Id);
        return ids;
    }

    /// <summary>Tells each platform which of its devices the plan uses, for its live updates.</summary>
    private void WatchLinks()
    {
        var ids = Wanted();
        foreach (var service in Services)
            service.Watch(ids.TryGetValue(service.Id, out var set) ? set : new HashSet<string>());
    }

    // MARK: Commands

    /// <summary>
    /// Sends a change made in the panel to the real device and says how it went, so a rejected
    /// change can be rolled back there. Commands to one device run strictly one after another,
    /// in the order this is called.
    /// </summary>
    public Task<JsonObject> CommandAsync(JsonObject message)
    {
        var id = message.Str("id");
        if (id != null && _serviceLinks.TryGetValue(id, out var link) && link.Id.Length > 0)
            return CommandServiceAsync(link, id, message);
        if (ConnectedCloud is not { } cloud || id == null || !_links.TryGetValue(id, out var entry)
            || entry.Link.Str("deviceId") is not { } deviceId)
        {
            return Task.FromResult(new JsonObject { ["ok"] = true }); // simulated device: nothing to send
        }
        var patch = message.Obj("patch") ?? new JsonObject();
        var state = message.Obj("state") ?? new JsonObject();
        return Enqueue(deviceId, async () =>
        {
            try
            {
                await cloud.CommandAsync(id, entry.Kind, entry.Link, patch, state, Log);
                return new JsonObject { ["ok"] = true };
            }
            catch (CommandRejection rejected)
            {
                return new JsonObject
                {
                    ["error"] = rejected.Message,
                    ["failed"] = new JsonArray(rejected.Fields.Select(f => (JsonNode?)f).ToArray()),
                };
            }
            catch (Exception e)
            {
                // The panel showed the change optimistically: pull the real state back in.
                Stale?.Invoke();
                return new JsonObject { ["error"] = e.Message };
            }
        });
    }

    private Task<JsonObject> CommandServiceAsync(Link link, string appId, JsonObject message)
    {
        if (Service(link.Service) is not { IsConnected: true, ControlsDevices: true } service)
            return Task.FromResult(new JsonObject { ["ok"] = true }); // not reachable from here: simulated
        var patch = message.Obj("patch") ?? new JsonObject();
        var state = message.Obj("state") ?? new JsonObject();
        var what = string.Join(" ", patch.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value?.ToJsonString().Trim('"')}"));
        return Enqueue($"{link.Service}:{link.Id}", async () =>
        {
            try
            {
                await service.SendAsync(link.Kind, link.Id, patch, state);
                Log.Write($"{appId} [{service.Title}] {what} → ok");
                return new JsonObject { ["ok"] = true };
            }
            catch (Exception e)
            {
                Log.Write($"{appId} [{service.Title}] {what} → {e.Message}");
                Stale?.Invoke();
                return new JsonObject { ["error"] = e.Message };
            }
        });
    }

    private Task<JsonObject> Enqueue(string key, Func<Task<JsonObject>> work)
    {
        _commandTails.TryGetValue(key, out var previous);
        var next = Run();
        _commandTails[key] = next;
        return next;

        async Task<JsonObject> Run()
        {
            if (previous != null)
            {
                try { await previous; } catch { /* the previous command already answered */ }
            }
            var result = await work();
            if (_commandTails.TryGetValue(key, out var tail) && tail.IsCompleted) _commandTails.Remove(key);
            return result;
        }
    }

    // MARK: State

    /// <summary>
    /// What the linked devices report right now, in the shape the panel's <c>applyRemote</c>
    /// takes. Null when nothing is connected or nothing could be reached.
    /// </summary>
    public async Task<JsonObject?> PollAsync()
    {
        var tuyaTask = PollCloudAsync();
        var serviceTask = PollServicesAsync();
        var fromTuya = await tuyaTask;
        var fromServices = await serviceTask;
        if (fromTuya is null && fromServices is null) return null;
        var merged = new JsonObject();
        foreach (var key in new[] { "lights", "acs", "features" })
        {
            var section = (fromTuya.Obj(key)?.DeepClone() as JsonObject) ?? new JsonObject();
            foreach (var (id, value) in fromServices.Obj(key) ?? new JsonObject())
            {
                if (section[id] is JsonObject existing && value is JsonObject more)
                {
                    foreach (var (k, v) in more) existing[k] = v?.DeepClone();
                }
                else
                {
                    section[id] = value?.DeepClone();
                }
            }
            merged[key] = section;
        }
        return merged;
    }

    /// <summary>Home Assistant and Hue, each asked once for all of its linked devices.</summary>
    private async Task<JsonObject?> PollServicesAsync()
    {
        var wanted = Wanted();
        var readings = new Dictionary<string, IReadOnlyDictionary<string, DeviceReading>>();
        var asks = wanted
            .Select(w => (w, service: Service(w.Key)))
            .Where(x => x.service is { IsConnected: true, ControlsDevices: true })
            .Select(async x =>
            {
                try { return (x.w.Key, result: await x.service!.ReadAsync(x.w.Value)); }
                catch { return (x.w.Key, result: (IReadOnlyDictionary<string, DeviceReading>?)null); }
            })
            .ToList();
        foreach (var (serviceId, result) in await Task.WhenAll(asks))
            if (result != null) readings[serviceId] = result;
        if (readings.Count == 0) return null;

        DeviceReading? Reading(string service, string id) =>
            readings.TryGetValue(service, out var r) && r.TryGetValue(id, out var reading) ? reading : null;

        JsonObject lights = new(), acs = new(), features = new();
        foreach (var (id, link) in _serviceLinks)
        {
            var reading = Reading(link.Service, link.Id);
            if (link.Kind == DeviceKind.Light && reading?.Light is { } light && reading.Online)
                lights[id] = light.DeepClone();
            if (link.Kind == DeviceKind.Ac)
            {
                var ac = reading is { Online: false } ? new JsonObject() : (reading?.Ac?.DeepClone() as JsonObject ?? new JsonObject());
                if (link.Sensor is { } s && Reading(s.Service, s.Id)?.Temperature is { } t) ac["roomTemp"] = t;
                if (ac.Count > 0) acs[id] = ac;
                if (reading?.Features is { } known) features[id] = known.DeepClone();
            }
        }
        foreach (var (id, s) in _tuyaSensors)
            if (Reading(s.Service, s.Id)?.Temperature is { } t) acs[id] = new JsonObject { ["roomTemp"] = t };
        return new JsonObject { ["lights"] = lights, ["acs"] = acs, ["features"] = features };
    }

    private async Task<JsonObject?> PollCloudAsync()
    {
        if (ConnectedCloud is not { } cloud || _links.Count == 0) return null;
        try { return await cloud.PollAsync(_links); }
        catch { return null; }
    }

    /// <summary>
    /// Every device of every connected platform, in the editor's terms, plus what went wrong
    /// with the platforms that could not be asked.
    /// </summary>
    public async Task<(JsonArray Devices, JsonObject Errors)> AllDevicesAsync()
    {
        var devices = new JsonArray();
        var errors = new JsonObject();
        if (ConnectedCloud is { } cloud)
        {
            try
            {
                foreach (var d in await cloud.DevicesAsync()) devices.Add(d.DeepClone());
            }
            catch (Exception e)
            {
                errors["smartlife"] = e.Message;
            }
        }
        foreach (var service in Services.Where(s => s.IsConnected))
        {
            try
            {
                foreach (var d in await service.DevicesAsync()) devices.Add(d.ToJson(service.Id));
            }
            catch (Exception e)
            {
                errors[service.Id] = e.Message;
            }
        }
        return (devices, errors);
    }

    /// <summary>What the editor and the settings show for each platform.</summary>
    public JsonObject ServicesInfo
    {
        get
        {
            var info = new JsonObject();
            if (Cloud != null) info["smartlife"] = Cloud.Info.DeepClone();
            foreach (var service in Services) info[service.Id] = service.Info.DeepClone();
            return info;
        }
    }

    /// <summary>Disconnects a platform and forgets its credentials.</summary>
    public void Disconnect(string service) => Service(service)?.Disconnect();
}
