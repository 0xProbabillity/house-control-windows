using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core.Sync;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace HouseControl.Core.Tests;

public class SyncTests
{
    [Fact]
    public void PairingLinkRoundTrips()
    {
        var pairing = SyncPairing.Generate("Study PC");
        Assert.StartsWith("housecontrol://pair?v=1&id=", pairing.Link);
        Assert.Contains("name=Study%20PC", pairing.Link);
        var read = SyncPairing.FromLink(pairing.Link);
        Assert.Equal(pairing, read);
        Assert.Equal(pairing, SyncPairing.Decode(pairing.Encode()));
        Assert.Null(SyncPairing.FromLink("housecontrol://pair?v=2&id=x&key=AAAA"));
        Assert.Null(SyncPairing.FromLink("https://example.com"));
    }

    [Fact]
    public void ReadsTheMacsLinkFormat()
    {
        // As the Mac app writes it: URLComponents, base64url key without padding.
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var link = $"housecontrol://pair?v=1&id=abc-123&name=My%20Mac&key={SyncPairing.Base64Url(key)}";
        var pairing = SyncPairing.FromLink(link)!;
        Assert.Equal("abc-123", pairing.Id);
        Assert.Equal("My Mac", pairing.Name);
        Assert.Equal(key, pairing.Key);
    }

    [Fact]
    public void DerivesThePreSharedKeyLikeTheMac()
    {
        // HMAC-SHA256(key = 32 zero bytes, "HouseControl sync v1"), the same as CryptoKit computes.
        var pairing = new SyncPairing("id", "PC", new byte[32]);
        var expected = System.Security.Cryptography.HMACSHA256.HashData(new byte[32], Encoding.UTF8.GetBytes("HouseControl sync v1"));
        Assert.Equal(expected, pairing.PreSharedKey);
    }

    [Fact]
    public void StateCarriesThePlanOnlyWhenNeeded()
    {
        var state = new SyncState("PC", "abcd", Encoding.UTF8.GetBytes("{\"rooms\":[]}"), "eu123", true, new JsonObject { ["hue"] = new JsonObject() });
        var full = state.Message(includingPlan: true);
        Assert.Equal("state", full.Str("type"));
        Assert.Equal("PC", full.Str("mac"));
        Assert.Equal("{\"rooms\":[]}", full.Str("plan"));
        Assert.True(full.Bool("smartLife"));
        Assert.NotNull(full.Obj("connections"));
        Assert.Null(state.Message(includingPlan: false)["plan"]);
    }

    private sealed class Phone : IDisposable
    {
        private readonly TlsClientProtocol _tls;
        public Stream Stream => _tls.Stream;

        public Phone(int port, byte[] psk)
        {
            var socket = new TcpClient("127.0.0.1", port);
            _tls = new TlsClientProtocol(socket.GetStream());
            _tls.Connect(new Client(psk));
        }

        public void Send(JsonObject message)
        {
            var frame = SyncFraming.Frame(message);
            Stream.Write(frame, 0, frame.Length);
            Stream.Flush();
        }

        public JsonObject Receive() => SyncFraming.Read(Stream)!;

        public void Dispose() => _tls.Close();

        /// <summary>
        /// What the iPhone's Network framework offers (seen in the app's log): TLS 1.2, PSK identity
        /// "housecontrol", AES-GCM and CCM, and a status_request extension asking for OCSP.
        /// </summary>
        private sealed class Client(byte[] psk) : PskTlsClient(new BcTlsCrypto(new SecureRandom()),
            new BasicTlsPskIdentity(Encoding.UTF8.GetBytes("housecontrol"), psk))
        {
            protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();
            protected override int[] GetSupportedCipherSuites() =>
            [
                CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256, CipherSuite.TLS_PSK_WITH_AES_256_GCM_SHA384,
                CipherSuite.TLS_PSK_WITH_AES_256_CCM, CipherSuite.TLS_PSK_WITH_AES_128_CCM,
            ];

            public override IDictionary<int, byte[]> GetClientExtensions()
            {
                var extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
                TlsExtensionsUtilities.AddStatusRequestExtension(extensions,
                    new CertificateStatusRequest(CertificateStatusType.ocsp, new OcspStatusRequest(null, null)));
                return extensions;
            }

            // The server must not take up status_request: no certificate goes with a pre-shared key.
            public override void ProcessServerExtensions(IDictionary<int, byte[]> serverExtensions)
            {
                if (serverExtensions != null && serverExtensions.ContainsKey(ExtensionType.status_request))
                    throw new TlsFatalAlert(AlertDescription.decode_error, "status_request answered for a PSK handshake");
                base.ProcessServerExtensions(serverExtensions);
            }
        }
    }

    private static (SyncServer server, SyncPairing pairing) StartServer(Func<SyncState> snapshot)
    {
        var server = new SyncServer(new MemorySecretStore()) { Snapshot = snapshot };
        var pairing = server.Enable("Test PC");
        return (server, pairing);
    }

    [Fact]
    public void APairedPhoneSaysHelloAndGetsThePlan()
    {
        var plan = Encoding.UTF8.GetBytes("{\"name\":\"Flat\",\"rooms\":[]}");
        var (server, pairing) = StartServer(() => new SyncState("Test PC", PlanStore.Revision(plan), plan, "", false, null));
        using var _ = server;
        Assert.NotNull(server.Port);
        using var phone = new Phone(server.Port!.Value, pairing.PreSharedKey);
        phone.Send(new JsonObject { ["type"] = "hello", ["device"] = "Test iPhone", ["rev"] = "" });
        var state = phone.Receive();
        Assert.Equal("state", state.Str("type"));
        Assert.Equal("Test PC", state.Str("mac"));
        Assert.Equal(Encoding.UTF8.GetString(plan), state.Str("plan"));
        SpinWait.SpinUntil(() => server.ConnectedDevices.Count == 1, 2000);
        Assert.Equal(["Test iPhone"], server.ConnectedDevices);

        // Connecting again later works too (each handshake gets its own copy of the key).
        using (var again = new Phone(server.Port!.Value, pairing.PreSharedKey))
        {
            again.Send(new JsonObject { ["type"] = "hello", ["device"] = "Test iPhone" });
            Assert.Equal("state", again.Receive().Str("type"));
        }

        // Saying hello with the revision it already has: no plan this time.
        phone.Send(new JsonObject { ["type"] = "hello", ["device"] = "Test iPhone", ["rev"] = PlanStore.Revision(plan) });
        Assert.Null(phone.Receive()["plan"]);
    }

    [Fact]
    public void TakesAPlanAndConnectionsFromThePhone()
    {
        var (server, pairing) = StartServer(() => new SyncState("Test PC", "", null, "", false, null));
        using var _ = server;
        byte[]? received = null;
        JsonObject? connections = null;
        string? userCode = null;
        server.PlanReceived += (data, _) => received = data;
        server.ConnectionsReceived += (shared, code, _) => { connections = shared; userCode = code; };
        using var phone = new Phone(server.Port!.Value, pairing.PreSharedKey);
        phone.Send(new JsonObject { ["type"] = "hello", ["device"] = "Test iPhone" });
        phone.Receive();
        phone.Send(new JsonObject { ["type"] = "plan", ["plan"] = "{\"rooms\":[{\"id\":\"r\"}]}" });
        phone.Send(new JsonObject { ["type"] = "plan", ["plan"] = "{\"not\":\"a plan\"}" });
        phone.Send(new JsonObject { ["type"] = "connections", ["connections"] = new JsonObject { ["hue"] = new JsonObject { ["removed"] = true, ["updated"] = 5 } }, ["userCode"] = "eu1" });
        SpinWait.SpinUntil(() => connections != null, 3000);
        Assert.Equal("{\"rooms\":[{\"id\":\"r\"}]}", Encoding.UTF8.GetString(received!));
        Assert.True(connections.Obj("hue").Bool("removed"));
        Assert.Equal("eu1", userCode);
    }

    [Fact]
    public void AnUnpairedPhoneCannotConnect()
    {
        var (server, _) = StartServer(() => new SyncState("Test PC", "", null, "", false, null));
        using var __ = server;
        var wrong = new SyncPairing("x", "y", new byte[32]).PreSharedKey;
        Assert.ThrowsAny<Exception>(() => new Phone(server.Port!.Value, wrong));
    }

    [Fact]
    public void ResettingThePairingLocksOutOldPhones()
    {
        var (server, pairing) = StartServer(() => new SyncState("Test PC", "", null, "", false, null));
        using var _ = server;
        var fresh = server.Reset("Test PC");
        Assert.NotEqual(pairing.Key, fresh.Key);
        // A phone still holding the old key is refused, and does not spoil the next one.
        Assert.ThrowsAny<Exception>(() => new Phone(server.Port!.Value, pairing.PreSharedKey));
        using var phone = new Phone(server.Port!.Value, fresh.PreSharedKey);
        phone.Send(new JsonObject { ["type"] = "hello", ["device"] = "Test iPhone" });
        Assert.Equal("state", phone.Receive().Str("type"));
    }

    [Fact]
    public void TheNewerSideOfASharedConnectionWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-sync-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var service = new FakeShared();
        var hub = new DeviceHub(dir, [service]);
        hub.MarkChanged("hue");
        var ours = hub.ShareableConnections.Obj("hue")!.Num("updated")!.Value;
        // Older than ours: ignored.
        Assert.False(hub.AdoptConnections(new JsonObject { ["hue"] = new JsonObject { ["host"] = "old", ["updated"] = ours - 10 } }));
        Assert.Equal("mine", service.Shareable!.Str("host"));
        // Newer: taken over, and its stamp kept.
        Assert.True(hub.AdoptConnections(new JsonObject { ["hue"] = new JsonObject { ["host"] = "theirs", ["updated"] = ours + 10 } }));
        Assert.Equal("theirs", service.Shareable!.Str("host"));
        Assert.Equal(ours + 10, hub.ShareableConnections.Obj("hue")!.Num("updated"));
        // Removed on the phone later still: disconnected here, and said so to the next phone.
        Assert.True(hub.AdoptConnections(new JsonObject { ["hue"] = new JsonObject { ["removed"] = true, ["updated"] = ours + 20 } }));
        Assert.True(hub.ShareableConnections.Obj("hue")!.Bool("removed"));
        Directory.Delete(dir, true);
    }

    private sealed class FakeShared : IDeviceService, ISharedConnection
    {
        private JsonObject? _config = new() { ["host"] = "mine" };
        public string Id => "hue";
        public string Title => "Hue";
        public bool IsConnected => _config != null;
        public JsonObject Info => new();
        public event Action? Changed { add { } remove { } }
        public JsonObject? Shareable => _config;
        public void Adopt(JsonObject? shared) => _config = shared;
        public Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default) => Task.FromResult<IReadOnlyList<ServiceDevice>>([]);
        public Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyDictionary<string, DeviceReading>>(new Dictionary<string, DeviceReading>());
        public Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default) => Task.CompletedTask;
        public void Disconnect() => _config = null;
    }
}
