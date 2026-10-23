using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core;

namespace HouseControl.Core.Tests;

public class ColorMathTests
{
    [Fact]
    public void RoundTripsHexThroughHsv()
    {
        var hsv = ColorMath.Hsv("#ff8000")!.Value;
        Assert.Equal(30.1, hsv.H, 1);
        Assert.Equal(1, hsv.S, 3);
        Assert.Equal("#ff8000", ColorMath.HexFromHue(hsv.H, hsv.S));
    }

    [Fact]
    public void ConvertsToCieXy()
    {
        var (x, y) = ColorMath.Xy("#ff0000")!.Value;
        Assert.Equal(0.64, x, 2);
        Assert.Equal(0.33, y, 2);
        Assert.Equal("#ff0000", ColorMath.HexFromXy(0.64, 0.33)[..3] + "0000");
    }

    [Fact]
    public void RejectsBadHex() => Assert.Null(ColorMath.Rgb("#12"));

    [Fact]
    public void MapsFanNames()
    {
        var table = FanNames.Table(["Off", "Low", "Middle", "High", "Strong", "Auto"]);
        Assert.Equal("Low", table["low"]);
        Assert.Equal("Middle", table["medium"]);
        Assert.Equal("Strong", table["turbo"]);
        Assert.Equal("Auto", table["auto"]);
    }

    [Fact]
    public void SwitchesOnALampThatGetsAColour()
    {
        var patch = DeviceKinds.WithPower(new JsonObject { ["color"] = "#ff0000" }, new JsonObject { ["on"] = true });
        Assert.True(patch.Bool("on"));
        var off = DeviceKinds.WithPower(new JsonObject { ["color"] = "#ff0000" }, new JsonObject { ["on"] = false });
        Assert.Null(off.Bool("on"));
    }
}

public class PlanStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void StartsOnTheDemoAndSwitchesToTheUsersPlan()
    {
        var store = new PlanStore(_dir);
        Assert.Equal("home", store.ActivePlan);
        Assert.False(store.HasCustomPlan);
        store.Select("custom"); // nothing to select yet
        Assert.Equal("home", store.ActivePlan);

        store.SaveCustom(new JsonObject { ["name"] = "Flat", ["rooms"] = new JsonArray() });
        Assert.Equal("custom", store.ActivePlan);
        Assert.Equal("Flat", store.CustomPlanName);
        Assert.Equal("custom", new PlanStore(_dir).ActivePlan);
        Assert.Equal("Flat", store.BootPayload().Obj("config").Str("name"));
    }

    [Fact]
    public void WritesSortedStableJson()
    {
        var store = new PlanStore(_dir);
        store.SaveCustom(new JsonObject { ["rooms"] = new JsonArray(), ["name"] = "B" });
        var first = store.CustomPlanRevision;
        store.SaveCustom(new JsonObject { ["name"] = "B", ["rooms"] = new JsonArray() });
        Assert.Equal(first, store.CustomPlanRevision);
        Assert.StartsWith("{\r\n  \"name\"".Replace("\r\n", Environment.NewLine), File.ReadAllText(store.CustomPlanPath));
    }

    [Fact]
    public void RefusesSomethingThatIsNotAPlan() =>
        Assert.Throws<InvalidDataException>(() => PlanStore.Validate(Encoding.UTF8.GetBytes("{\"a\":1}")));
}

public class SecretStoreTests
{
    [Fact]
    public void EncryptsWithTheUsersKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "hc-secret-" + Guid.NewGuid().ToString("n"));
        var store = new ProtectedFileSecretStore(path);
        store.Write("token-123"u8.ToArray());
        Assert.DoesNotContain("token-123", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("token-123", Encoding.UTF8.GetString(store.Read()!));
        store.Write(null);
        Assert.False(File.Exists(path));
        Assert.Null(store.Read());
    }
}

public class DeviceHubTests
{
    private sealed class FakeService : IDeviceService
    {
        public string Id => "hue";
        public string Title => "Philips Hue";
        public bool IsConnected => true;
        public JsonObject Info => new() { ["connected"] = true };
        public event Action? Changed;
        public List<string> Sent { get; } = new();
        public HashSet<string> Watched { get; private set; } = new();

        public Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<ServiceDevice>>([new ServiceDevice { Id = "l1", Name = "Lamp", Kind = DeviceKind.Light }]);

        public Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyDictionary<string, DeviceReading>>(new Dictionary<string, DeviceReading>
            {
                ["l1"] = new() { Light = new JsonObject { ["on"] = true, ["brightness"] = 0.5 } },
                ["t1"] = new() { Temperature = 21.5 },
            });

        public async Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default)
        {
            await Task.Delay(patch.Bool("on") == true ? 30 : 1, cancel);
            Sent.Add($"{id}:{patch.ToJsonString()}");
        }

        public void Watch(IReadOnlySet<string> ids) => Watched = ids.ToHashSet();
        public void Disconnect() { }
        public void Fire() => Changed?.Invoke();
    }

    private static JsonObject Plan() => JsonNode.Parse("""
        {"rooms":[{"id":"r","rect":[0,0,1,1]}],
         "lights":[{"id":"lamp","room":"r","type":"ceiling","pos":[0,0],"link":{"service":"hue","id":"l1"}},
                   {"id":"sim","room":"r","type":"ceiling","pos":[0,0]}],
         "acs":[{"id":"ac","room":"r","pos":[0,0],"wall":"north","sensor":{"service":"hue","id":"t1"}}]}
        """)!.AsObject();

    [Fact]
    public async Task ReadsLinkedDevicesAndRoomSensors()
    {
        var service = new FakeService();
        var hub = new DeviceHub(Path.GetTempPath(), [service]);
        hub.SetLinks(Plan());
        Assert.True(hub.CanPoll);
        Assert.Equal(new HashSet<string> { "l1", "t1" }, service.Watched);
        var update = await hub.PollAsync();
        Assert.Equal(0.5, update.Obj("lights").Obj("lamp").Num("brightness"));
        Assert.Equal(21.5, update.Obj("acs").Obj("ac").Num("roomTemp"));
        Assert.Null(update.Obj("lights")!["sim"]);
    }

    [Fact]
    public async Task SendsCommandsToOneDeviceInOrder()
    {
        var service = new FakeService();
        var hub = new DeviceHub(Path.GetTempPath(), [service]);
        hub.SetLinks(Plan());
        var first = hub.CommandAsync(new JsonObject { ["id"] = "lamp", ["patch"] = new JsonObject { ["on"] = true } });
        var second = hub.CommandAsync(new JsonObject { ["id"] = "lamp", ["patch"] = new JsonObject { ["brightness"] = 0.2 } });
        var simulated = await hub.CommandAsync(new JsonObject { ["id"] = "sim", ["patch"] = new JsonObject { ["on"] = true } });
        await Task.WhenAll(first, second);
        Assert.True(simulated.Bool("ok"));
        Assert.Equal(["l1:{\"on\":true}", "l1:{\"brightness\":0.2}"], service.Sent);
    }

    [Fact]
    public async Task CoalescesChangeReports()
    {
        var service = new FakeService();
        var hub = new DeviceHub(Path.GetTempPath(), [service]);
        var stale = 0;
        hub.Stale += () => stale++;
        service.Fire();
        service.Fire();
        service.Fire();
        await Task.Delay(600);
        Assert.Equal(1, stale);
    }
}
