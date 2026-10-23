using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ranges = System.Collections.Generic.Dictionary<string, (double Min, double Max)>;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Smart Life account access without a Tuya developer project: the user approves a QR
/// login in the Smart Life app and the app talks to Tuya's "device sharing" gateway.
/// This is a C# port of the open-source <c>tuya-device-sharing-sdk</c> that the Home
/// Assistant Tuya integration is built on, and it identifies itself the same way.
/// Used from the UI thread only.
/// </summary>
internal sealed class SmartLifeService : IDeviceCloud
{
    private const string ClientId = "HA_3y9q4ak7g4ephrvke";
    private const string Schema = "haauthorize";
    internal const string LoginHost = "https://apigw.iotbing.com";

    /// <summary>The signed-in session, stored as JSON with the same keys as the Mac app's.</summary>
    internal sealed record Session
    {
        public required string UserCode { get; init; }
        public required string AccessToken { get; init; }
        public required string RefreshToken { get; init; }
        /// <summary>Milliseconds since 1970.</summary>
        public required double ExpiresAt { get; init; }
        public required string Uid { get; init; }
        public required string Endpoint { get; init; }
        public required string Username { get; init; }
    }

    private static readonly JsonSerializerOptions SessionJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISecretStore _vault;
    private readonly HttpClient _http;
    private DateTime? _sessionStamp;
    private bool _loaded;
    private readonly Dictionary<string, HashSet<string>> _knownCodes = new();
    private readonly Dictionary<string, Ranges> _knownRanges = new();
    private bool _refreshing;

    public Session? Current { get; private set; }
    public IReadOnlyDictionary<string, HashSet<string>> KnownCodes => _knownCodes;
    public IReadOnlyDictionary<string, Ranges> KnownRanges => _knownRanges;
    /// <summary>The clock; tests move it to check the token refresh.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public SmartLifeService(ISecretStore vault, HttpClient http)
    {
        _vault = vault;
        _http = http;
        ReloadSession();
    }

    /// <summary>
    /// Refreshing rotates the refresh token, so a second process (<c>--diagnose</c> next to the
    /// running app) must pick up what the other one stored instead of carrying on with a
    /// token that no longer works.
    /// </summary>
    private void ReloadSession()
    {
        var stamp = _vault.Stamp;
        if (_loaded && stamp == _sessionStamp) return;
        _loaded = true;
        _sessionStamp = stamp;
        Current = Decode(_vault.Read());
    }

    private static Session? Decode(byte[]? data)
    {
        if (data is null) return null;
        try { return JsonSerializer.Deserialize<Session>(data, SessionJson); }
        catch (JsonException) { return null; }
    }

    public bool IsConfigured => Current != null;

    public JsonObject PublicInfo => new()
    {
        ["connected"] = IsConfigured,
        ["userCode"] = Current?.UserCode ?? "",
        ["account"] = Current?.Username ?? "",
    };

    private void Store(Session? next)
    {
        Current = next;
        _vault.Write(next is null ? null : JsonSerializer.SerializeToUtf8Bytes(next, SessionJson));
        _sessionStamp = _vault.Stamp;
    }

    public void SignOut()
    {
        Store(null);
        _knownCodes.Clear();
        _knownRanges.Clear();
    }

    // MARK: QR login

    /// <summary>
    /// Percent-encodes everything but letters and digits, like Foundation's
    /// <c>addingPercentEncoding(withAllowedCharacters: .alphanumerics)</c> for an ASCII code.
    /// </summary>
    private static string EncodeUserCode(string code)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(code))
        {
            if (b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }

    /// <summary>Asks Tuya for a login ticket; the returned string is what the QR code must contain.</summary>
    public async Task<(string Token, string QrContent)> BeginLoginAsync(string userCode)
    {
        var code = userCode.Trim();
        if (code.Length == 0) throw new TuyaFailure("Enter the User Code from the Smart Life app first.");
        var url = $"{LoginHost}/v1.0/m/life/home-assistant/qrcode/tokens?clientid={ClientId}&usercode={EncodeUserCode(code)}&schema={Schema}";
        var json = await TuyaHttp.JsonAsync(_http, new HttpRequestMessage(HttpMethod.Post, url));
        if (json["success"].AsBool() != true || json["result"] is not JsonObject result || result["qrcode"].AsString() is not { } token)
            throw new TuyaFailure(Explain(json, "Tuya did not accept this User Code."));
        return (token, $"tuyaSmart--qrLogin?token={token}");
    }

    /// <summary>True once the user has confirmed the login in the Smart Life app.</summary>
    public async Task<bool> FinishLoginAsync(string token, string userCode)
    {
        var code = userCode.Trim();
        var url = $"{LoginHost}/v1.0/m/life/home-assistant/qrcode/tokens/{token}?clientid={ClientId}&usercode={EncodeUserCode(code)}";
        var json = await TuyaHttp.JsonAsync(_http, new HttpRequestMessage(HttpMethod.Get, url));
        if (json["success"].AsBool() != true || json["result"] is not JsonObject r
            || r["access_token"].AsString() is not { } access || r["refresh_token"].AsString() is not { } refresh
            || r["endpoint"].AsString() is not { } endpoint) return false;
        var now = json["t"].AsDouble() ?? TuyaHttp.NowMs(Now);
        Store(new Session
        {
            UserCode = code,
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = now + (r["expire_time"].AsDouble() ?? 7200) * 1000,
            Uid = r["uid"].AsString() ?? "",
            Endpoint = endpoint,
            Username = r["username"].AsString() ?? "",
        });
        return true;
    }

    // MARK: Devices

    public async Task<List<JsonObject>> ListDevicesAsync()
    {
        var devices = new List<JsonObject>();
        var homes = (await RequestAsync("GET", "/v1.0/m/life/users/homes")).AsObjects() ?? [];
        foreach (var home in homes)
        {
            // ownerId arrives as a number or a string depending on the account.
            if (home["ownerId"].AsText() is not { } homeId) continue;
            var list = (await RequestAsync("GET", "/v1.0/m/life/ha/home/devices", new JsonObject { ["homeId"] = homeId })).AsObjects() ?? [];
            foreach (var d in list)
            {
                if (d["id"].AsString() is not { } id) continue;
                var codes = Codes(d["status"].AsObjects() ?? []);
                _knownCodes[id] = codes.ToHashSet();
                devices.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = d["name"].AsString() ?? id,
                    ["category"] = d["category"].AsString() ?? "",
                    ["product"] = d["product_name"].AsString() ?? "",
                    ["online"] = d["online"].AsBool() ?? false,
                    ["codes"] = Loose.Array(codes),
                    ["home"] = home["name"].AsString() ?? "",
                });
            }
        }
        return devices;
    }

    internal static List<string> Codes(IEnumerable<JsonObject> status) =>
        status.Select(dp => dp["code"].AsString()).OfType<string>().ToList();

    public async Task<Dictionary<string, List<JsonObject>>> StatusAsync(IReadOnlyList<string> ids)
    {
        var output = new Dictionary<string, List<JsonObject>>();
        foreach (var chunk in ids.Chunk(20))
        {
            var list = (await RequestAsync("GET", "/v1.0/m/life/ha/devices/detail",
                new JsonObject { ["devIds"] = string.Join(",", chunk) })).AsObjects() ?? [];
            foreach (var d in list)
            {
                if (d["id"].AsString() is not { } id || d["status"].AsObjects() is not { } status) continue;
                output[id] = status;
                if (!_knownCodes.TryGetValue(id, out var known)) _knownCodes[id] = known = new HashSet<string>();
                known.UnionWith(Codes(status));
            }
        }
        foreach (var id in ids)
            if (!_knownRanges.ContainsKey(id)) await LoadSpecificationAsync(id);
        return output;
    }

    public async Task SendAsync(IReadOnlyList<JsonObject> commands, string deviceId)
    {
        if (commands.Count == 0) return;
        await RequestAsync("POST", $"/v1.1/m/thing/{deviceId}/commands",
            body: new JsonObject { ["commands"] = Loose.Array(commands.Select(c => c.DeepClone())) });
    }

    /// <summary>Read-only snapshot for diagnostics: raw status and specification of the given devices.</summary>
    public async Task<JsonObject> DiagnosticsAsync(IReadOnlyList<string> ids)
    {
        var output = new JsonObject();
        List<JsonObject> details;
        try
        {
            details = (await RequestAsync("GET", "/v1.0/m/life/ha/devices/detail",
                new JsonObject { ["devIds"] = string.Join(",", ids) })).AsObjects() ?? [];
        }
        catch (Exception)
        {
            details = [];
        }
        foreach (var d in details)
        {
            if (d["id"].AsString() is not { } id) continue;
            var entry = new JsonObject();
            foreach (var key in new[] { "name", "category", "product_name", "online", "status", "sub" })
                entry[key] = d[key]?.DeepClone();
            try { entry["specification"] = await RequestAsync("GET", $"/v1.1/m/life/{id}/specifications"); }
            catch (Exception e) { entry["specification_error"] = e.Message; }
            // How the cloud maps raw data points to status codes; also shows what it last knew.
            try { entry["strategy"] = await RequestAsync("GET", $"/v1.0/m/life/devices/{id}/status"); }
            catch (Exception e) { entry["strategy_error"] = e.Message; }
            output[id] = entry;
        }
        return output;
    }

    public async Task PrepareAsync(string deviceId)
    {
        if (!_knownRanges.ContainsKey(deviceId)) await LoadSpecificationAsync(deviceId);
    }

    /// <summary>
    /// Functions and value ranges of a device; fetched once. Some devices (Zigbee bulbs,
    /// infrared remotes) report no status at all, so the specification is the only place
    /// that says which commands they understand.
    /// </summary>
    private async Task LoadSpecificationAsync(string id)
    {
        _knownRanges[id] = new Ranges(); // remembered even on failure so it is not retried on every poll
        JsonObject spec;
        try
        {
            if (await RequestAsync("GET", $"/v1.1/m/life/{id}/specifications") is not JsonObject s) return;
            spec = s;
        }
        catch (Exception)
        {
            return;
        }
        var ranges = new Ranges();
        var codes = _knownCodes.TryGetValue(id, out var known) ? new HashSet<string>(known) : new HashSet<string>();
        foreach (var key in new[] { "status", "functions" })
        {
            foreach (var item in spec[key].AsObjects() ?? [])
            {
                if (item["code"].AsString() is not { } code) continue;
                codes.Add(code);
                if (item["values"].AsString() is not { } raw || Json.ParseObject(raw) is not { } values
                    || values["min"].AsDouble() is not { } lo || values["max"].AsDouble() is not { } hi) continue;
                ranges[code] = (lo, hi);
            }
        }
        _knownCodes[id] = codes;
        _knownRanges[id] = ranges;
    }

    // MARK: Encrypted, signed transport

    private async Task RefreshIfNeededAsync()
    {
        if (Current is not { } s || _refreshing || !(s.ExpiresAt - 60_000 < TuyaHttp.NowMs(Now))) return;
        _refreshing = true;
        try
        {
            JsonObject? result;
            try { result = await RequestAsync("GET", $"/v1.0/m/token/{s.RefreshToken}", refresh: false) as JsonObject; }
            catch (Exception) { return; }
            if (result?["accessToken"].AsString() is not { } access || result["refreshToken"].AsString() is not { } refresh) return;
            Store(s with
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAt = TuyaHttp.NowMs(Now) + (result["expireTime"].AsDouble() ?? 7200) * 1000,
                Uid = result["uid"].AsString() ?? s.Uid,
            });
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<JsonNode?> RequestAsync(string method, string path, JsonObject? query = null,
                                               JsonObject? body = null, bool refresh = true)
    {
        ReloadSession();
        if (refresh) await RefreshIfNeededAsync();
        if (Current is not { } s) throw new TuyaFailure("Not signed in to Smart Life");

        var rid = Guid.NewGuid().ToString("D"); // lower case, like the SDK's uuid4()
        var hashKey = Transport.Md5Hex(rid + s.RefreshToken);
        var secret = Transport.Secret(rid, hashKey);

        var queryEnc = "";
        var url = s.Endpoint + path;
        if (query is { Count: > 0 })
        {
            queryEnc = Transport.Encrypt(Transport.CompactJson(query), secret);
            url += "?encdata=" + Uri.EscapeDataString(queryEnc);
        }
        var bodyEnc = "";
        if (body is { Count: > 0 }) bodyEnc = Transport.Encrypt(Transport.CompactJson(body), secret);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new TuyaFailure("Bad request URL");

        var t = TuyaHttp.NowMs(Now).ToString(System.Globalization.CultureInfo.InvariantCulture);
        (string, string)[] headers = [("X-appKey", ClientId), ("X-requestId", rid), ("X-sid", ""), ("X-time", t), ("X-token", s.AccessToken)];

        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("X-sign", Transport.Sign(hashKey, headers, queryEnc, bodyEnc));
        if (bodyEnc.Length > 0)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Transport.CompactJson(new JsonObject { ["encdata"] = bodyEnc })));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        var json = await TuyaHttp.JsonAsync(_http, request);
        if (json["success"].AsBool() != true)
            throw new TuyaFailure(Explain(json, "Smart Life request failed"), CodeOf(json));
        if (json["result"].AsString() is not { } cipher) return json["result"]?.DeepClone();
        var plain = Transport.Decrypt(cipher, secret);
        try { return JsonNode.Parse(plain); }
        catch (JsonException) { return JsonValue.Create(plain); }
    }

    private static string CodeOf(JsonObject json) => json["code"].AsText() ?? "";

    private static string Explain(JsonObject json, string fallback)
    {
        var code = CodeOf(json);
        // 2001: the cloud cannot reach the device (unplugged, or only paired over Bluetooth).
        if (code == "2001") return "device is offline — Smart Life cannot reach it over Wi-Fi (code 2001)";
        var message = json["msg"].AsString() ?? fallback;
        return code.Length == 0 ? message : $"{message} (code {code})";
    }

    /// <summary>
    /// The gateway's request envelope: per-request AES-GCM key derived from the request id
    /// and the refresh token, payloads sent as <c>encdata</c>, HMAC over headers + payloads.
    /// </summary>
    internal static class Transport
    {
        private const string Alphabet = "ABCDEFGHJKMNPQRSTWXYZabcdefhijkmnprstwxyz2345678";

        public static string Md5Hex(string text) => TuyaHttp.Hex(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        /// <summary>The first 16 hex digits of HMAC-SHA256(key: request id, message: hash key).</summary>
        public static string Secret(string requestId, string hashKey) =>
            TuyaHttp.Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(requestId), Encoding.UTF8.GetBytes(hashKey)))[..16];

        /// <summary>HMAC-SHA256 (key: hash key) over the non-empty headers as <c>name=value</c> joined by <c>||</c>, then the payloads.</summary>
        public static string Sign(string hashKey, IEnumerable<(string Name, string Value)> headers, string query, string body)
        {
            var text = string.Join("||", headers.Where(h => h.Value.Length > 0).Select(h => $"{h.Name}={h.Value}")) + query + body;
            return TuyaHttp.Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hashKey), Encoding.UTF8.GetBytes(text)));
        }

        public static string CompactJson(JsonObject value) => value.ToJsonString(Json.Compact);

        /// <summary>
        /// base64(nonce) followed by base64(ciphertext + tag); the nonce is 12 characters of
        /// the SDK's alphabet and the key is the 16-character secret itself.
        /// </summary>
        public static string Encrypt(string plain, string secret, string? nonce = null)
        {
            var nonceText = nonce ?? new string(Enumerable.Range(0, 12)
                .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            var nonceBytes = Encoding.UTF8.GetBytes(nonceText);
            var plainBytes = Encoding.UTF8.GetBytes(plain);
            var sealedBytes = new byte[plainBytes.Length + 16];
            using (var aes = new AesGcm(Encoding.UTF8.GetBytes(secret), 16))
                aes.Encrypt(nonceBytes, plainBytes, sealedBytes.AsSpan(0, plainBytes.Length), sealedBytes.AsSpan(plainBytes.Length));
            return Convert.ToBase64String(nonceBytes) + Convert.ToBase64String(sealedBytes);
        }

        /// <summary>The answer's <c>result</c>: base64 of nonce (12 bytes) + ciphertext + tag (16 bytes).</summary>
        public static string Decrypt(string cipher, string secret)
        {
            byte[] data;
            try { data = Convert.FromBase64String(cipher); }
            catch (FormatException) { data = []; }
            if (data.Length <= 28) throw new TuyaFailure("Could not decode the response from Smart Life");
            var plain = new byte[data.Length - 28];
            using (var aes = new AesGcm(Encoding.UTF8.GetBytes(secret), 16))
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(data.Length - 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
