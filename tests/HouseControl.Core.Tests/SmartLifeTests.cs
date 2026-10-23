using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.SmartLife;

namespace HouseControl.Core.Tests;

/// <summary>
/// The Smart Life / Tuya module: command building, the infrared remote, the transport and
/// the hub routing plan links through it. Everything runs offline against a fake Tuya.
/// </summary>
public class SmartLifeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-smartlife-" + Guid.NewGuid().ToString("n"));

    public SmartLifeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Json(List<JsonObject> commands) => SelfTest.SortedJson(commands);

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // MARK: Ported from the Mac app's tests

    [Fact]
    public void InfraredACCommands()
    {
        var ir = new HashSet<string> { "PowerOn", "PowerOff", "T", "M", "F" };
        Assert.Equal("""[{"code":"PowerOn","value":"PowerOn"},{"code":"M","value":1},{"code":"T","value":24}]""",
            Json(TuyaMapper.AcCommands(Obj("""{"on":true,"mode":"heat","target":24.0}"""), new(), ir)));
        Assert.Equal("""[{"code":"F","value":3}]""", Json(TuyaMapper.AcCommands(Obj("""{"fan":"high"}"""), new(), ir)));
    }

    [Fact]
    public void BulbCommandsUseTheDeviceRanges()
    {
        var bulb = new HashSet<string> { "switch_led", "work_mode", "bright_value_v2", "temp_value_v2", "colour_data_v2" };
        var ranges = new Dictionary<string, (double Min, double Max)> { ["bright_value_v2"] = (10, 1000), ["temp_value_v2"] = (0, 1000) };
        Assert.Equal("""[{"code":"switch_led","value":true},{"code":"bright_value_v2","value":505}]""",
            Json(TuyaMapper.LightCommands(Obj("""{"on":true,"brightness":0.5}"""), new(), new(), bulb, ranges)));
        Assert.Equal("""[{"code":"switch_1","value":true}]""",
            Json(TuyaMapper.LightCommands(Obj("""{"on":true}"""), new(), new(), new HashSet<string>())));
        var state = TuyaMapper.LightState(
            [Obj("""{"code":"switch_led","value":true}"""), Obj("""{"code":"bright_value_v2","value":505.0}""")], ranges);
        Assert.True(state.Bool("on"));
        Assert.Equal(0.5, state.Num("brightness") ?? 0, 3);
    }

    [Fact]
    public async Task InfraredRemoteReplaysStateAfterARefusedKey()
    {
        var lines = await SelfTest.InfraredTranscriptAsync();
        Assert.Equal(6, lines.Count);
        Assert.Contains("kept for the next switch-on", lines[0]);
        Assert.True(lines[1].EndsWith("→ ok") && lines[1].Contains("PowerOn=PowerOn") && lines[1].Contains("F=3"), lines[1]);
        Assert.True(lines[2].Contains("T=25 ✗") && lines[2].Contains("M=0") && lines[2].EndsWith("→ ok"), lines[2]);
        Assert.Contains("rejected [\"target\"]", lines[4]);
        Assert.Contains("PowerOff=PowerOff", lines[5]);
    }

    [Fact]
    public void TransportEnvelopeMatchesTheReferenceSDK()
    {
        const string rid = "11111111-2222-3333-4444-555555555555";
        var hashKey = SmartLifeService.Transport.Md5Hex(rid + "refresh-token");
        var secret = SmartLifeService.Transport.Secret(rid, hashKey);
        var cipher = SmartLifeService.Transport.Encrypt("{\"homeId\":\"42\"}", secret, nonce: "ABCDEFGHJKMN");
        Assert.Equal("{\"homeId\":\"42\"}", Decrypt(cipher, secret));
        Assert.Equal(16, secret.Length);
        // The nonce travels as its own base64 block, ahead of the ciphertext and tag.
        Assert.StartsWith(Convert.ToBase64String("ABCDEFGHJKMN"u8.ToArray()), cipher);
    }

    [Fact]
    public async Task HubSimulatesUnlinkedDevicesAndReadsLinks()
    {
        var hub = new DeviceHub(_dir, [], new SmartLifePlatform(_dir, _ => new MemorySecretStore()));
        hub.SetLinks(Obj("""
            {"lights":[{"id":"a","tuya":{"deviceId":"dev1"}},{"id":"b"},{"id":"c","tuya":{"deviceId":""}}],
             "acs":[{"id":"ac","tuya":{"deviceId":"dev2","sensorId":"s1"}}]}
            """));
        Assert.Equal(2, hub.LinkedDeviceCount);
        Assert.False(hub.IsConnected);
        Assert.False(hub.CanPoll);
        var reply = await hub.CommandAsync(Obj("""{"id":"a","kind":"light","patch":{"on":true}}"""));
        Assert.True(reply.Bool("ok")); // not signed in: nothing is sent anywhere
        hub.SetLinks(null);
        Assert.False(hub.HasLinks);
    }

    [Fact]
    public void SmartLifeDevicesGetAKind()
    {
        Assert.Equal("light", SmartLifePlatform.Device(Obj("""{"id":"1","category":"dj","codes":["switch_led","bright_value_v2","temp_value_v2"]}""")).Str("kind"));
        Assert.Equal("ac", SmartLifePlatform.Device(Obj("""{"id":"2","category":"infrared_ac","codes":["PowerOn","T"]}""")).Str("kind"));
        Assert.Equal("sensor", SmartLifePlatform.Device(Obj("""{"id":"3","category":"wsdcg","codes":["temp_current"]}""")).Str("kind"));
        Assert.Equal("""["brightness"]""",
            SmartLifePlatform.Device(Obj("""{"id":"1","category":"dj","codes":["switch_led","bright_value_v2"]}"""))["capabilities"]!.ToJsonString());
    }

    // MARK: Self-test output

    [Fact]
    public void SelfTestPrintsTheEnvelopeAndTheCommands()
    {
        var output = new StringWriter();
        Assert.Null(SelfTest.Run(["--something-else"], output));
        Assert.Equal(0, SelfTest.Run(["--selftest-transport"], output));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // Known answers for the fixed inputs (hash key, secret and signature checked against an independent HMAC / MD5).
        Assert.Equal(
        [
            "81542abb4375f4f3f5779f8c4889cd91",
            "a43689fcfc68604f",
            "QUJDREVGR0hKS01OFU1ZSihrcsNcuGsQrbBdtDiy67jIifj11G/ygakQKg==",
            "{\"homeId\":\"42\"}",
            "60ec38e3fb489a22392137194744761b160e46530d2a9e12f62338ae7fd569e5",
        ], lines);

        var mapper = SelfTest.MapperLines();
        Assert.Equal("""bulb 2700K → [{"code":"work_mode","value":"white"},{"code":"temp_value_v2","value":116}]""", mapper[4]);
        Assert.Equal("""bulb colour → [{"code":"work_mode","value":"colour"},{"code":"colour_data_v2","value":"{\"h\":3,\"s\":690,\"v\":800}"}]""", mapper[5]);
    }

    [Fact]
    public void QrCodeIsAPngDataUrl()
    {
        var url = QrPng.DataUrl("tuyaSmart--qrLogin?token=abc");
        Assert.StartsWith("data:image/png;base64,", url);
        var png = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    // MARK: Smart Life against a fake gateway

    [Fact]
    public async Task SignsInWithTheQrCodeAndKeepsTokensPrivate()
    {
        var fake = new FakeTuya();
        var vault = new MemorySecretStore();
        var platform = new SmartLifePlatform(_dir, name => name == "smartlife" ? vault : new MemorySecretStore(), fake);

        var (token, content) = await platform.BeginLoginAsync(" ab-12 ");
        Assert.Equal("qr-token", token);
        Assert.Equal("tuyaSmart--qrLogin?token=qr-token", content);
        Assert.Equal("POST", fake.Requests[0].Method);
        // The code is sent as "ab%2D12"; .NET's Uri normalizes an escaped unreserved character back to "-", which means the same.
        Assert.Equal($"{SmartLifeService.LoginHost}/v1.0/m/life/home-assistant/qrcode/tokens?clientid=HA_3y9q4ak7g4ephrvke&usercode=ab-12&schema=haauthorize",
            fake.Requests[0].Url);
        await Assert.ThrowsAsync<TuyaFailure>(() => platform.BeginLoginAsync("  "));

        Assert.False(await platform.FinishLoginAsync(token, "ab-12")); // not confirmed in the phone app yet
        Assert.False(platform.IsSmartLifeConnected);
        fake.Confirmed = true;
        Assert.True(await platform.FinishLoginAsync(token, "ab-12"));
        Assert.Equal("GET", fake.Requests[^1].Method);
        Assert.Equal($"{SmartLifeService.LoginHost}/v1.0/m/life/home-assistant/qrcode/tokens/qr-token?clientid=HA_3y9q4ak7g4ephrvke&usercode=ab-12",
            fake.Requests[^1].Url);

        Assert.True(platform.IsSmartLifeConnected);
        Assert.True(platform.IsConnected);
        Assert.Equal("ab-12", platform.UserCode);
        Assert.Equal("someone", platform.SmartLifeInfo.Str("account"));
        Assert.DoesNotContain("access-1", platform.Info.ToJsonString());
        var stored = Obj(Encoding.UTF8.GetString(vault.Read()!));
        Assert.Equal("refresh-1", stored.Str("refreshToken"));
        Assert.Equal(FakeTuya.Start + 7_200_000, stored.Num("expiresAt"));
        Assert.Equal("https://gw.test", stored.Str("endpoint"));

        // A second platform on the same vault picks the session up.
        Assert.True(new SmartLifePlatform(_dir, name => name == "smartlife" ? vault : new MemorySecretStore(), fake).IsSmartLifeConnected);

        platform.SignOut();
        Assert.False(platform.IsSmartLifeConnected);
        Assert.Null(vault.Read());
    }

    private async Task<(SmartLifePlatform Platform, FakeTuya Fake, MemorySecretStore Vault)> SignedInAsync()
    {
        var fake = new FakeTuya { Confirmed = true };
        var vault = new MemorySecretStore();
        var platform = new SmartLifePlatform(_dir, name => name == "smartlife" ? vault : new MemorySecretStore(), fake);
        platform.SmartLife.Now = () => DateTimeOffset.FromUnixTimeMilliseconds((long)FakeTuya.Start);
        Assert.True(await platform.FinishLoginAsync("qr-token", "ab-12"));
        platform.Infrared.Spacing = TimeSpan.Zero;
        return (platform, fake, vault);
    }

    [Fact]
    public async Task ListsDevicesThroughTheEncryptedSignedEnvelope()
    {
        var (platform, fake, _) = await SignedInAsync();
        var devices = await platform.DevicesAsync();
        Assert.Equal(2, devices.Count);
        var lamp = devices.Single(d => d.Str("id") == "bulb1");
        Assert.Equal("light", lamp.Str("kind"));
        Assert.Equal("Home", lamp.Str("home"));
        Assert.Equal("Bulb · dj", lamp.Str("detail"));
        Assert.Equal("""["brightness"]""", lamp["capabilities"]!.ToJsonString());
        // ownerId arrived as a number and went out, encrypted, as the string the gateway expects.
        Assert.Contains(fake.Requests, r => r.Path == "/v1.0/m/life/ha/home/devices" && r.Query == """{"homeId":"42"}""");
        Assert.All(fake.Requests.Where(r => r.Url.StartsWith("https://gw.test")), r => Assert.True(r.SignatureValid, r.Url));
    }

    [Fact]
    public async Task SendsLightCommandsAndReadsTheStateBack()
    {
        var (platform, fake, _) = await SignedInAsync();
        var log = new EventLog(_dir);
        await platform.CommandAsync("lamp", "light", Obj("""{"deviceId":"bulb1"}"""), Obj("""{"on":true,"brightness":0.5}"""),
            Obj("""{"on":true,"brightness":0.5}"""), log);
        var send = fake.Requests.Single(r => r.Path == "/v1.1/m/thing/bulb1/commands");
        Assert.Equal("POST", send.Method);
        Assert.True(send.SignatureValid);
        Assert.Equal("""{"commands":[{"code":"switch_led","value":true},{"code":"bright_value_v2","value":505}]}""", send.Body);
        // The specification was read before the first command, so its range was used.
        Assert.Contains(fake.Requests, r => r.Path == "/v1.1/m/life/bulb1/specifications");
        Assert.Contains("lamp switch_led=true bright_value_v2=505 → ok", File.ReadAllText(log.FilePath));
        Assert.DoesNotContain("access-1", File.ReadAllText(log.FilePath));

        var update = await platform.PollAsync(new Dictionary<string, (string, JsonObject)>
        {
            ["lamp"] = ("light", Obj("""{"deviceId":"bulb1"}""")),
            ["ac"] = ("ac", Obj("""{"deviceId":"ac1","sensorId":"th1"}""")),
        });
        Assert.Equal(0.5, update.Obj("lights").Obj("lamp").Num("brightness") ?? 0, 3);
        Assert.True(update.Obj("lights").Obj("lamp").Bool("on"));
        Assert.Equal(21.5, update.Obj("acs").Obj("ac").Num("roomTemp"));
        Assert.Equal("cool", update.Obj("acs").Obj("ac").Str("mode"));
        Assert.Equal(23, update.Obj("acs").Obj("ac").Num("target"));
        Assert.False(update.Obj("features").Obj("ac").Bool("infrared"));
        Assert.Contains(fake.Requests, r => r.Path == "/v1.0/m/life/ha/devices/detail");
    }

    [Fact]
    public async Task ExplainsAnOfflineDevice()
    {
        var (platform, _, _) = await SignedInAsync();
        var log = new EventLog(_dir);
        var error = await Assert.ThrowsAsync<TuyaFailure>(() => platform.CommandAsync("lamp", "light",
            Obj("""{"deviceId":"offline1"}"""), Obj("""{"on":true}"""), Obj("""{"on":true}"""), log));
        Assert.Equal("2001", error.Code);
        Assert.Equal("device is offline — Smart Life cannot reach it over Wi-Fi (code 2001)", error.Message);
        Assert.Contains("lamp switch_1=true → device is offline", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public async Task RefreshesAnExpiredTokenFirst()
    {
        var (platform, fake, vault) = await SignedInAsync();
        platform.SmartLife.Now = () => DateTimeOffset.FromUnixTimeMilliseconds((long)FakeTuya.Start + 7_200_000);
        await platform.ListDevicesAsync();
        var gateway = fake.Requests.Where(r => r.Url.StartsWith("https://gw.test")).ToList();
        Assert.Equal("/v1.0/m/token/refresh-1", gateway[0].Path);
        Assert.Equal("access-1", gateway[0].Token);
        Assert.All(gateway.Skip(1), r => Assert.Equal("access-2", r.Token));
        Assert.All(gateway, r => Assert.True(r.SignatureValid, r.Url));
        var stored = Obj(Encoding.UTF8.GetString(vault.Read()!));
        Assert.Equal("refresh-2", stored.Str("refreshToken"));
        Assert.Equal("uid-2", stored.Str("uid"));
        Assert.Equal(FakeTuya.Start + 7_200_000 + 3_600_000, stored.Num("expiresAt"));
    }

    [Fact]
    public async Task RejectsAnInfraredKeyTheRemoteLacks()
    {
        var (platform, fake, _) = await SignedInAsync();
        var log = new EventLog(_dir);
        var rejected = await Assert.ThrowsAsync<CommandRejection>(() => platform.CommandAsync("ac", "ac",
            Obj("""{"deviceId":"ir1"}"""), Obj("""{"target":31}"""),
            Obj("""{"on":true,"mode":"cool","target":31,"fan":"high"}"""), log));
        Assert.Equal(["target"], rejected.Fields);
        Assert.Equal("this remote has no infrared code for that setting (Tuya code 2008)", rejected.Message);
        // One key per request: the refused key, the replayed state, the key once more.
        var sent = fake.Requests.Where(r => r.Path == "/v1.1/m/thing/ir1/commands").Select(r => r.Body).ToList();
        Assert.Equal(
        [
            """{"commands":[{"code":"T","value":31}]}""",
            """{"commands":[{"code":"PowerOn","value":"PowerOn"}]}""",
            """{"commands":[{"code":"M","value":0}]}""",
            """{"commands":[{"code":"F","value":3}]}""",
            """{"commands":[{"code":"T","value":31}]}""",
        ], sent);
        Assert.Contains("ac target refused (2008): replaying fan, mode, on", File.ReadAllText(log.FilePath));
    }

    // MARK: Tuya Cloud project

    [Fact]
    public async Task TuyaCloudProjectSignsRequestsAndRenewsAnExpiredToken()
    {
        var fake = new FakeTuya();
        var vault = new MemorySecretStore();
        var platform = new SmartLifePlatform(_dir, name => name == "tuya" ? vault : new MemorySecretStore(), fake);
        Assert.False(platform.IsConnected);
        platform.SaveTuya(" project-id ", " project-secret ", "eu");
        Assert.True(platform.IsConnected);
        Assert.False(platform.IsSmartLifeConnected);
        Assert.Equal("""{"configured":true,"accessId":"project-id","region":"eu","hasSecret":true}""", platform.TuyaInfo.ToJsonString());
        platform.SaveTuya("project-id", "", "eu"); // an empty secret keeps the stored one
        Assert.Equal("project-secret", Obj(Encoding.UTF8.GetString(vault.Read()!)).Str("accessSecret"));

        fake.ExpireCloudTokenOnce = true;
        var update = await platform.PollAsync(new Dictionary<string, (string, JsonObject)>
        {
            ["lamp"] = ("light", Obj("""{"deviceId":"bulb1"}""")),
        });
        Assert.True(update.Obj("lights").Obj("lamp").Bool("on"));
        var cloud = fake.Requests.Where(r => r.Url.StartsWith("https://openapi.tuyaeu.com")).ToList();
        Assert.Equal(["/v1.0/token?grant_type=1", "/v1.0/iot-03/devices/status?device_ids=bulb1",
                      "/v1.0/token?grant_type=1", "/v1.0/iot-03/devices/status?device_ids=bulb1"],
            cloud.Select(r => r.PathAndQuery));
        Assert.All(cloud, r => Assert.True(r.SignatureValid, r.Url));
        Assert.Equal(["", "cloud-token-1", "", "cloud-token-2"], cloud.Select(r => r.Token ?? ""));

        await platform.CommandAsync("lamp", "light", Obj("""{"deviceId":"bulb1"}"""), Obj("""{"on":false}"""), Obj("""{"on":false}"""), new EventLog(_dir));
        var send = fake.Requests[^1];
        Assert.Equal("/v1.0/iot-03/devices/bulb1/commands", send.PathAndQuery);
        Assert.Equal("""{"commands":[{"code":"switch_led","value":false}]}""", send.Body);
        Assert.True(send.SignatureValid);

        platform.SaveTuya("", "", "eu");
        Assert.False(platform.IsConnected);
        Assert.Null(vault.Read());
    }

    // MARK: Fake Tuya

    /// <summary>Opens a gateway envelope the way Tuya does: base64(nonce) followed by base64(ciphertext + tag).</summary>
    private static string Decrypt(string envelope, string secret)
    {
        var nonce = Convert.FromBase64String(envelope[..16]);
        var sealedBytes = Convert.FromBase64String(envelope[16..]);
        var plain = new byte[sealedBytes.Length - 16];
        using var aes = new AesGcm(Encoding.UTF8.GetBytes(secret), 16);
        aes.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>An answer's <c>result</c>: base64 of nonce + ciphertext + tag.</summary>
    private static string Seal(string plain, string secret)
    {
        var envelope = SmartLifeService.Transport.Encrypt(plain, secret);
        return Convert.ToBase64String(Convert.FromBase64String(envelope[..16]).Concat(Convert.FromBase64String(envelope[16..])).ToArray());
    }

    private sealed record Seen(string Method, string Url, string Path, string PathAndQuery, string? Query, string? Body, string? Token, bool SignatureValid);

    /// <summary>
    /// Tuya's login host, a device-sharing gateway at https://gw.test and the Tuya Cloud EU
    /// host. It checks every signature, decrypts every payload and answers like the real ones.
    /// </summary>
    private sealed class FakeTuya : HttpMessageHandler
    {
        public const double Start = 1_700_000_000_000;
        public bool Confirmed { get; set; }
        public bool ExpireCloudTokenOnce { get; set; }
        public List<Seen> Requests { get; } = new();
        private string _refresh = "refresh-1";
        private string _access = "access-1";
        private int _cloudTokens;

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static HttpResponseMessage Reply(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (uri.Host == "apigw.iotbing.com") return Login(request, uri);
            if (uri.Host == "gw.test") return Gateway(request, uri, body);
            if (uri.Host == "openapi.tuyaeu.com") return Cloud(request, uri, body);
            throw new HttpRequestException("unexpected host " + uri.Host);
        }

        private HttpResponseMessage Login(HttpRequestMessage request, Uri uri)
        {
            Requests.Add(new Seen(request.Method.Method, uri.AbsoluteUri, uri.AbsolutePath, uri.PathAndQuery, null, null, null, true));
            if (request.Method == HttpMethod.Post)
                return Reply("""{"success":true,"result":{"qrcode":"qr-token"},"t":1700000000000}""");
            if (!Confirmed) return Reply("""{"success":false,"code":"A0001","msg":"waiting for confirmation"}""");
            return Reply("""
                {"success":true,"t":1700000000000,"result":{"access_token":"access-1","refresh_token":"refresh-1",
                 "expire_time":7200,"uid":"uid-1","endpoint":"https://gw.test","username":"someone"}}
                """);
        }

        private HttpResponseMessage Gateway(HttpRequestMessage request, Uri uri, string? body)
        {
            var rid = Header(request, "X-requestId")!;
            var token = Header(request, "X-token");
            var hashKey = SmartLifeService.Transport.Md5Hex(rid + _refresh);
            var secret = SmartLifeService.Transport.Secret(rid, hashKey);
            string queryEnc = "", bodyEnc = "";
            if (uri.Query.StartsWith("?encdata="))
            {
                Assert.DoesNotContain("+", uri.Query);
                Assert.DoesNotContain("/", uri.Query);
                queryEnc = Uri.UnescapeDataString(uri.Query["?encdata=".Length..]);
            }
            if (body != null)
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString());
                bodyEnc = Obj(body).Str("encdata")!;
            }
            (string, string)[] headers =
            [
                ("X-appKey", Header(request, "X-appKey")!), ("X-requestId", rid), ("X-sid", Header(request, "X-sid") ?? ""),
                ("X-time", Header(request, "X-time")!), ("X-token", token ?? ""),
            ];
            var valid = Header(request, "X-appKey") == "HA_3y9q4ak7g4ephrvke" && token == _access
                && Header(request, "X-sign") == SmartLifeService.Transport.Sign(hashKey, headers, queryEnc, bodyEnc);
            var query = queryEnc.Length > 0 ? Decrypt(queryEnc, secret) : null;
            var plainBody = bodyEnc.Length > 0 ? Decrypt(bodyEnc, secret) : null;
            Requests.Add(new Seen(request.Method.Method, uri.AbsoluteUri, uri.AbsolutePath, uri.PathAndQuery, query, plainBody, token, valid));
            if (!valid) return Reply("""{"success":false,"code":1004,"msg":"sign invalid"}""");

            string Ok(string plain) => $$"""{"success":true,"t":1700000000000,"result":"{{Seal(plain, secret)}}"}""";
            var path = uri.AbsolutePath;
            switch (path)
            {
                case "/v1.0/m/token/refresh-1":
                    _refresh = "refresh-2";
                    _access = "access-2";
                    return Reply(Ok("""{"accessToken":"access-2","refreshToken":"refresh-2","expireTime":3600,"uid":"uid-2"}"""));
                case "/v1.0/m/life/users/homes":
                    return Reply(Ok("""[{"ownerId":42,"name":"Home"}]"""));
                case "/v1.0/m/life/ha/home/devices":
                    return Reply(Ok("""
                        [{"id":"bulb1","name":"Lamp","category":"dj","product_name":"Bulb","online":true,
                          "status":[{"code":"switch_led","value":true},{"code":"bright_value_v2","value":505}]},
                         {"id":"ac1","name":"AC","category":"kt","product_name":"","online":true,
                          "status":[{"code":"switch","value":true},{"code":"temp_set","value":23},{"code":"mode","value":"cold"}]}]
                        """));
                case "/v1.0/m/life/ha/devices/detail":
                    return Reply(Ok("""
                        [{"id":"bulb1","status":[{"code":"switch_led","value":true},{"code":"bright_value_v2","value":505}]},
                         {"id":"ac1","status":[{"code":"switch","value":true},{"code":"temp_set","value":23},{"code":"mode","value":"cold"}]},
                         {"id":"th1","status":[{"code":"va_temperature","value":215}]}]
                        """));
                case "/v1.1/m/life/bulb1/specifications":
                    return Reply(Ok("""{"functions":[{"code":"switch_led","values":"{}"},{"code":"bright_value_v2","values":"{\"min\":10,\"max\":1000}"}],"status":[]}"""));
                case "/v1.1/m/life/ac1/specifications":
                    return Reply(Ok("""{"functions":[{"code":"switch"},{"code":"temp_set","values":"{\"min\":16,\"max\":30}"},{"code":"mode"}],"status":[]}"""));
                case "/v1.1/m/life/ir1/specifications":
                    return Reply(Ok("""{"functions":[{"code":"PowerOn"},{"code":"PowerOff"},{"code":"M","values":"{\"min\":0,\"max\":4}"},{"code":"T"},{"code":"F"}],"status":[]}"""));
                case "/v1.1/m/thing/offline1/commands":
                    return Reply("""{"success":false,"code":2001,"msg":"device is offline"}""");
                case "/v1.1/m/thing/ir1/commands" when plainBody!.Contains("\"T\""):
                    return Reply("""{"success":false,"code":2008,"msg":"command or value not support"}""");
                case var p when p.StartsWith("/v1.1/m/thing/"):
                    return Reply("""{"success":true,"result":true}""");
                case var p when p.StartsWith("/v1.1/m/life/"):
                    return Reply("""{"success":false,"code":1106,"msg":"permission deny"}""");
                default:
                    return Reply("""{"success":false,"code":404,"msg":"unknown path"}""");
            }
        }

        private HttpResponseMessage Cloud(HttpRequestMessage request, Uri uri, string? body)
        {
            var token = Header(request, "access_token");
            var bytes = body is null ? null : Encoding.UTF8.GetBytes(body);
            var expected = TuyaService.Sign("project-id", "project-secret", token ?? "", Header(request, "t")!,
                request.Method.Method, bytes, uri.PathAndQuery);
            var valid = Header(request, "client_id") == "project-id" && Header(request, "sign_method") == "HMAC-SHA256"
                && Header(request, "sign") == expected && expected == expected.ToUpperInvariant();
            Requests.Add(new Seen(request.Method.Method, uri.AbsoluteUri, uri.AbsolutePath, uri.PathAndQuery, null, body, token, valid));
            if (!valid) return Reply("""{"success":false,"code":1004,"msg":"sign invalid"}""");
            if (uri.AbsolutePath == "/v1.0/token")
            {
                _cloudTokens++;
                return Reply($$$"""{"success":true,"result":{"access_token":"cloud-token-{{{_cloudTokens}}}","expire_time":7200}}""");
            }
            if (ExpireCloudTokenOnce)
            {
                ExpireCloudTokenOnce = false;
                return Reply("""{"success":false,"code":1010,"msg":"token invalid"}""");
            }
            if (uri.AbsolutePath == "/v1.0/iot-03/devices/status")
                return Reply("""{"success":true,"result":[{"id":"bulb1","status":[{"code":"switch_led","value":true}]}]}""");
            return Reply("""{"success":true,"result":true}""");
        }
    }
}
