using System.Text.Json.Nodes;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Smart Life for the device hub: the QR-linked account when there is one, otherwise a
/// Tuya Cloud project ("Advanced"). The app passes it to <c>new DeviceHub(…, cloud:)</c>.
/// Used from the UI thread only: every member, and the awaits inside, run on the caller's context.
/// </summary>
public sealed class SmartLifePlatform : ILinkedCloud
{
    private readonly TuyaService _tuya;
    private readonly SmartLifeService _smartLife;
    private readonly InfraredRemote _infrared = new();

    /// <summary>The folder the app keeps its files in (the plan's folder).</summary>
    public string Directory { get; }

    /// <summary>
    /// <c>vault("smartlife")</c> keeps the session, <c>vault("tuya")</c> the Tuya Cloud project's
    /// credentials. All requests share one HTTP client.
    /// </summary>
    public SmartLifePlatform(string directory, Func<string, ISecretStore> vault)
        : this(directory, vault, TuyaHttp.Shared)
    {
    }

    /// <summary>For tests: every request goes to <paramref name="handler"/>.</summary>
    internal SmartLifePlatform(string directory, Func<string, ISecretStore> vault, HttpMessageHandler handler)
        : this(directory, vault, TuyaHttp.Create(handler))
    {
    }

    private SmartLifePlatform(string directory, Func<string, ISecretStore> vault, HttpClient http)
    {
        Directory = directory;
        _tuya = new TuyaService(vault("tuya"), http);
        _smartLife = new SmartLifeService(vault("smartlife"), http);
    }

    internal SmartLifeService SmartLife => _smartLife;
    internal TuyaService Tuya => _tuya;
    internal InfraredRemote Infrared => _infrared;

    /// <summary>
    /// The backend real devices are reached through: the QR-linked Smart Life account
    /// when there is one, otherwise a Tuya Cloud project, otherwise nothing (simulation).
    /// </summary>
    private IDeviceCloud? Active => _smartLife.IsConfigured ? _smartLife : _tuya.IsConfigured ? _tuya : null;

    public bool IsConnected => Active != null;

    public JsonObject Info
    {
        get
        {
            var info = _smartLife.PublicInfo;
            info["connected"] = IsConnected;
            return info;
        }
    }

    // MARK: ILinkedCloud

    public async Task CommandAsync(string appId, string kind, JsonObject link, JsonObject patch, JsonObject state, EventLog log)
    {
        if (Active is not { } cloud || link["deviceId"].AsString() is not { } deviceId) return;
        // The first command to a device may arrive before anything is known about it.
        await cloud.PrepareAsync(deviceId);
        IReadOnlySet<string> codes = cloud.KnownCodes.TryGetValue(deviceId, out var known) ? known : new HashSet<string>();
        var ranges = cloud.KnownRanges.TryGetValue(deviceId, out var r) ? r : new();

        async Task Send(IReadOnlyList<JsonObject> commands)
        {
            if (commands.Count == 0) return;
            try
            {
                await cloud.SendAsync(commands, deviceId);
                log.Write($"{appId} {EventLog.Describe(commands)} → ok");
            }
            catch (Exception e)
            {
                log.Write($"{appId} {EventLog.Describe(commands)} → {e.Message}");
                throw;
            }
        }

        if (kind == "light")
        {
            await Send(TuyaMapper.LightCommands(patch, state, link, codes, ranges));
        }
        else if (TuyaMapper.IsInfraredAC(codes))
        {
            try
            {
                await _infrared.ApplyAsync(patch, state, deviceId, command => Send([command]), line => log.Write($"{appId} {line}"));
            }
            catch (InfraredRemote.Rejection rejected)
            {
                throw new CommandRejection(rejected.Message, rejected.Fields);
            }
        }
        else
        {
            await Send(TuyaMapper.AcCommands(patch, link, codes));
        }
    }

    public async Task<JsonObject?> PollAsync(IReadOnlyDictionary<string, (string Kind, JsonObject Link)> links)
    {
        if (Active is not { } cloud || links.Count == 0) return null;
        // Devices plus the temperature sensors that stand in for an AC's room reading.
        var ids = links.Values
            .SelectMany(e => new[] { e.Link["deviceId"].AsString(), e.Link["sensorId"].AsString() })
            .OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
        Dictionary<string, List<JsonObject>> status;
        try { status = await cloud.StatusAsync(ids); }
        catch (Exception) { return null; }

        JsonObject lights = new(), acs = new(), features = new();
        foreach (var (id, entry) in links)
        {
            if (entry.Link["deviceId"].AsString() is not { } deviceId) continue;
            IReadOnlySet<string> codes = cloud.KnownCodes.TryGetValue(deviceId, out var known) ? known : new HashSet<string>();
            var ranges = cloud.KnownRanges.TryGetValue(deviceId, out var r) ? r : new();
            if (entry.Kind == "ac" && TuyaMapper.AcFeatures(codes, ranges) is { } offered) features[id] = offered;
            if (entry.Kind == "light")
            {
                if (status.TryGetValue(deviceId, out var dps)) lights[id] = TuyaMapper.LightState(dps, ranges);
                continue;
            }
            var acState = status.TryGetValue(deviceId, out var acDps) ? TuyaMapper.AcState(acDps) : new JsonObject();
            if (entry.Link["sensorId"].AsString() is { } sensorId && status.TryGetValue(sensorId, out var sensorDps)
                && TuyaMapper.SensorTemperature(sensorDps) is { } temperature)
            {
                acState["roomTemp"] = temperature;
            }
            if (acState.Count > 0) acs[id] = acState;
        }
        return new JsonObject { ["lights"] = lights, ["acs"] = acs, ["features"] = features };
    }

    public async Task<IReadOnlyList<JsonObject>> DevicesAsync() =>
        (await ListDevicesAsync()).Select(Device).ToList();

    /// <summary>The account's devices as Tuya reports them (with their data point codes).</summary>
    public async Task<IReadOnlyList<JsonObject>> ListDevicesAsync()
    {
        if (Active is not { } cloud) throw new TuyaFailure("Connect Smart Life first");
        return await cloud.ListDevicesAsync();
    }

    /// <summary>A Smart Life device in the same shape as the other platforms' (plus its Tuya codes).</summary>
    internal static JsonObject Device(JsonObject d)
    {
        var codes = (d["codes"] as JsonArray)?.Select(c => c.AsString()).OfType<string>().ToHashSet() ?? [];
        var category = d["category"].AsString() ?? "";
        DeviceKind kind;
        if (category is "kt" or "ktkzq" or "infrared_ac" || TuyaMapper.IsInfraredAC(codes) || codes.Contains("temp_set"))
            kind = DeviceKind.Ac;
        else if (category is "wsdcg" or "wk" or "wnykq" || (codes.Contains("temp_current") && !codes.Contains("switch_led")))
            kind = DeviceKind.Sensor;
        else
            kind = DeviceKind.Light;
        var caps = new List<string>();
        if (codes.Contains("bright_value") || codes.Contains("bright_value_v2")) caps.Add("brightness");
        if (codes.Contains("temp_value") || codes.Contains("temp_value_v2")) caps.Add("temp");
        if (codes.Contains("colour_data") || codes.Contains("colour_data_v2")) caps.Add("color");
        var output = (JsonObject)d.DeepClone();
        output["service"] = "smartlife";
        output["kind"] = kind.Name();
        output["capabilities"] = Loose.Array(caps);
        output.Remove("room");
        output["detail"] = string.Join(" · ", new[] { d["product"].AsString(), category }.Where(s => !string.IsNullOrEmpty(s)));
        return output;
    }

    /// <summary>Read-only snapshot of what Smart Life reports for the given devices.</summary>
    public Task<JsonObject> DiagnosticsAsync(IReadOnlyList<string> ids) => _smartLife.DiagnosticsAsync(ids);

    // MARK: Smart Life account

    /// <summary>What the UI may know about the session: never the tokens.</summary>
    public JsonObject SmartLifeInfo => _smartLife.PublicInfo;
    public bool IsSmartLifeConnected => _smartLife.IsConfigured;
    public string UserCode => _smartLife.Current?.UserCode ?? "";
    public string Account => _smartLife.Current?.Username ?? "";

    /// <summary>Asks Tuya for a login ticket; <c>QrContent</c> is what the QR code must contain.</summary>
    public Task<(string Token, string QrContent)> BeginLoginAsync(string userCode) => _smartLife.BeginLoginAsync(userCode);

    /// <summary>True once the user has confirmed the login in the Smart Life app.</summary>
    public async Task<bool> FinishLoginAsync(string token, string userCode)
    {
        try { return await _smartLife.FinishLoginAsync(token, userCode); }
        catch (Exception) { return false; }
    }

    public void SignOut() => _smartLife.SignOut();

    // MARK: Tuya Cloud project ("Advanced")

    public JsonObject TuyaInfo => _tuya.PublicInfo;

    /// <summary>
    /// Stores the project credentials. An empty secret keeps the one already stored;
    /// an empty access id removes them.
    /// </summary>
    public void SaveTuya(string accessId, string accessSecret, string region)
    {
        var id = accessId.Trim();
        var secret = accessSecret.Length == 0 ? _tuya.Current?.AccessSecret ?? "" : accessSecret.Trim();
        _tuya.Save(id.Length == 0 ? null : new TuyaService.Credentials { AccessId = id, AccessSecret = secret, Region = region });
    }
}
