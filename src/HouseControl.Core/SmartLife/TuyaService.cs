using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ranges = System.Collections.Generic.Dictionary<string, (double Min, double Max)>;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Minimal client for the Tuya Cloud OpenAPI ("Smart Home" project with the Smart Life
/// app account linked to it). Covers exactly what the app needs: list devices, read
/// their status, send commands. Used from the UI thread only.
/// </summary>
internal sealed class TuyaService : IDeviceCloud
{
    /// <summary>The project credentials, stored as JSON with the same keys as the Mac app's <c>tuya.json</c>.</summary>
    internal sealed record Credentials
    {
        public required string AccessId { get; init; }
        public required string AccessSecret { get; init; }
        public required string Region { get; init; }
    }

    public static readonly IReadOnlyDictionary<string, string> Hosts = new Dictionary<string, string>
    {
        ["eu"] = "openapi.tuyaeu.com",        // Central Europe
        ["eu-w"] = "openapi-weaz.tuyaeu.com", // Western Europe
        ["us"] = "openapi.tuyaus.com",        // Western America
        ["us-e"] = "openapi-ueaz.tuyaus.com", // Eastern America
        ["cn"] = "openapi.tuyacn.com",
        ["in"] = "openapi.tuyain.com",
        ["sg"] = "openapi-sg.iotbing.com",
    };

    private static readonly JsonSerializerOptions CredentialsJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISecretStore _vault;
    private readonly HttpClient _http;
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    /// <summary>Data-point codes each device reported, used to pick the right command dialect.</summary>
    private readonly Dictionary<string, HashSet<string>> _knownCodes = new();
    private readonly Dictionary<string, Ranges> _knownRanges = new();

    public Credentials? Current { get; private set; }
    public IReadOnlyDictionary<string, HashSet<string>> KnownCodes => _knownCodes;
    public IReadOnlyDictionary<string, Ranges> KnownRanges => _knownRanges;
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The credentials live in <paramref name="vault"/> (the app's "tuya" secret).</summary>
    public TuyaService(ISecretStore vault, HttpClient http)
    {
        _vault = vault;
        _http = http;
        if (vault.Read() is { } data)
        {
            try { Current = JsonSerializer.Deserialize<Credentials>(data, CredentialsJson); }
            catch (JsonException) { }
        }
    }

    public bool IsConfigured =>
        Current is { } c && c.AccessId.Length > 0 && c.AccessSecret.Length > 0 && Hosts.ContainsKey(c.Region);

    /// <summary>What the UI may know: never the secret itself.</summary>
    public JsonObject PublicInfo => new()
    {
        ["configured"] = IsConfigured,
        ["accessId"] = Current?.AccessId ?? "",
        ["region"] = Current?.Region ?? "eu",
        ["hasSecret"] = !string.IsNullOrEmpty(Current?.AccessSecret),
    };

    public void Save(Credentials? next)
    {
        Current = next;
        _token = null;
        _tokenExpiry = DateTimeOffset.MinValue;
        _vault.Write(next is null ? null : JsonSerializer.SerializeToUtf8Bytes(next, CredentialsJson));
    }

    public Task PrepareAsync(string deviceId) => Task.CompletedTask;

    // MARK: API

    /// <summary>Every device of the linked Smart Life account, with the DP codes it supports.</summary>
    public async Task<List<JsonObject>> ListDevicesAsync()
    {
        var devices = new List<JsonObject>();
        string? lastKey = null;
        do
        {
            var query = new Dictionary<string, string> { ["size"] = "50" };
            if (lastKey != null) query["last_row_key"] = lastKey;
            if (await RequestAsync("GET", "/v1.0/iot-01/associated-users/devices", query) is not JsonObject page) break;
            foreach (var d in page["devices"].AsObjects() ?? [])
            {
                if (d["id"].AsString() is not { } id) continue;
                var codes = SmartLifeService.Codes(d["status"].AsObjects() ?? []);
                _knownCodes[id] = codes.ToHashSet();
                devices.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = d["name"].AsString() ?? id,
                    ["category"] = d["category"].AsString() ?? "",
                    ["product"] = d["product_name"].AsString() ?? "",
                    ["online"] = d["online"].AsBool() ?? false,
                    ["codes"] = Loose.Array(codes),
                });
            }
            lastKey = page["has_more"].AsBool() ?? false ? page["last_row_key"].AsString() : null;
        } while (lastKey != null);
        return devices;
    }

    /// <summary>Current data points for the given devices, keyed by device id.</summary>
    public async Task<Dictionary<string, List<JsonObject>>> StatusAsync(IReadOnlyList<string> ids)
    {
        var output = new Dictionary<string, List<JsonObject>>();
        foreach (var chunk in ids.Chunk(20))
        {
            var result = await RequestAsync("GET", "/v1.0/iot-03/devices/status",
                new Dictionary<string, string> { ["device_ids"] = string.Join(",", chunk) });
            foreach (var entry in result.AsObjects() ?? [])
            {
                if (entry["id"].AsString() is not { } id || entry["status"].AsObjects() is not { } status) continue;
                output[id] = status;
                _knownCodes[id] = SmartLifeService.Codes(status).ToHashSet();
            }
        }
        return output;
    }

    public async Task SendAsync(IReadOnlyList<JsonObject> commands, string deviceId)
    {
        if (commands.Count == 0) return;
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["commands"] = Loose.Array(commands.Select(c => c.DeepClone())) }
            .ToJsonString(Json.Compact));
        await RequestAsync("POST", $"/v1.0/iot-03/devices/{deviceId}/commands", body: body);
    }

    // MARK: Signing

    private async Task<string> AccessTokenAsync()
    {
        if (_token != null && _tokenExpiry > Now().AddSeconds(60)) return _token;
        var result = await RequestAsync("GET", "/v1.0/token", new Dictionary<string, string> { ["grant_type"] = "1" }, authorized: false);
        if (result is not JsonObject dict || dict["access_token"].AsString() is not { } value)
            throw new TuyaFailure("Tuya did not return an access token");
        _token = value;
        _tokenExpiry = Now().AddSeconds(dict["expire_time"].AsDouble() is { } seconds ? (int)seconds : 3600);
        return value;
    }

    /// <summary>
    /// Tuya's "new" signature: HMAC-SHA256 (key: access secret, upper-case hex) over
    /// client id + access token + t + method, SHA-256 of the body, no signed headers, and the
    /// path with its query sorted by name.
    /// </summary>
    internal static string Sign(string accessId, string accessSecret, string token, string t, string method, byte[]? body, string signedUrl)
    {
        var bodyHash = TuyaHttp.Hex(SHA256.HashData(body ?? []));
        var payload = accessId + token + t + $"{method}\n{bodyHash}\n\n{signedUrl}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(accessSecret), Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<JsonNode?> RequestAsync(string method, string path, IReadOnlyDictionary<string, string>? query = null,
                                               byte[]? body = null, bool authorized = true, bool retry = true)
    {
        if (Current is not { } c || !IsConfigured || !Hosts.TryGetValue(c.Region, out var host))
            throw new TuyaFailure("Tuya credentials are not set");
        var queryString = string.Join("&", (query ?? new Dictionary<string, string>())
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        var signedUrl = queryString.Length == 0 ? path : $"{path}?{queryString}";
        if (!Uri.TryCreate($"https://{host}{signedUrl}", UriKind.Absolute, out var uri)) throw new TuyaFailure("Bad request URL");

        var t = TuyaHttp.NowMs(Now).ToString(CultureInfo.InvariantCulture);
        var token = authorized ? await AccessTokenAsync() : "";
        var sign = Sign(c.AccessId, c.AccessSecret, token, t, method, body, signedUrl);

        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.TryAddWithoutValidation("client_id", c.AccessId);
        request.Headers.TryAddWithoutValidation("sign", sign);
        request.Headers.TryAddWithoutValidation("t", t);
        request.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        if (authorized) request.Headers.TryAddWithoutValidation("access_token", token);
        if (body != null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        var json = await TuyaHttp.JsonAsync(_http, request);
        if (json["success"].AsBool() == true) return json["result"]?.DeepClone();
        var code = json["code"].AsDouble() is { } n ? (int)n : 0;
        // 1010 / 1011: token expired or invalid. Fetch a fresh one once.
        if (authorized && retry && code is 1010 or 1011)
        {
            _token = null;
            return await RequestAsync(method, path, query, body, authorized: true, retry: false);
        }
        throw new TuyaFailure($"{json["msg"].AsString() ?? "Tuya request failed"} (code {code})", code.ToString(CultureInfo.InvariantCulture));
    }
}
