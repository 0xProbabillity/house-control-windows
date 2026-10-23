using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Sync;

/// <summary>
/// A platform this PC cannot reach itself, known from the list another device sent: Apple Home,
/// as seen by the paired iPhone. Its devices can be linked in the editor; they are switched from
/// the iPhone, and stay simulated on the PC.
/// </summary>
public sealed class RemoteCatalogService : IDeviceService
{
    private readonly string _file;
    private JsonObject _catalog;

    public string Id { get; }
    public string Title { get; }
    public event Action? Changed;

    /// <summary>The catalog is cached in <paramref name="file"/>, so the editor has it while the phone is away.</summary>
    public RemoteCatalogService(string id, string title, string file)
    {
        Id = id;
        Title = title;
        _file = file;
        JsonObject? saved = null;
        try { if (File.Exists(file)) saved = Json.ParseObject(File.ReadAllBytes(file)); }
        catch (IOException) { }
        _catalog = saved ?? new JsonObject();
    }

    private JsonArray Devices => _catalog.Arr("devices") ?? new JsonArray();

    public bool IsConnected => Devices.Count > 0;
    public bool ControlsDevices => false;

    public JsonObject Info => new()
    {
        ["connected"] = IsConnected,
        ["remote"] = true,
        ["from"] = _catalog.Str("from") ?? "",
        ["updated"] = _catalog.Num("updated") ?? 0,
        ["count"] = Devices.Count,
    };

    /// <summary>Stores what <paramref name="device"/> (the iPhone's name) reported.</summary>
    public void Update(JsonArray devices, string device)
    {
        _catalog = new JsonObject
        {
            ["devices"] = devices.DeepClone(),
            ["from"] = device,
            ["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        try { Files.WriteAtomic(_file, Encoding.UTF8.GetBytes(Json.Serialize(_catalog))); }
        catch (IOException) { }
        Changed?.Invoke();
    }

    public Task<IReadOnlyList<ServiceDevice>> DevicesAsync(CancellationToken cancel = default)
    {
        var list = new List<ServiceDevice>();
        foreach (var d in Devices.OfType<JsonObject>())
        {
            if (d.Str("id") is not { } id || d.Str("name") is not { } name || d.Str("kind") is not ("light" or "ac" or "sensor")) continue;
            list.Add(new ServiceDevice
            {
                Id = id,
                Name = name,
                Kind = DeviceKinds.Parse(d.Str("kind")),
                Room = d.Str("room"),
                Online = d.Bool("online") ?? true,
                Capabilities = d.Arr("capabilities")?.Select(c => c?.ToString() ?? "").Where(c => c.Length > 0).ToList() ?? [],
                Range = d.Num("min") is { } lo && d.Num("max") is { } hi ? (lo, hi) : null,
                Detail = d.Str("detail") ?? "",
            });
        }
        return Task.FromResult<IReadOnlyList<ServiceDevice>>(list);
    }

    public Task<IReadOnlyDictionary<string, DeviceReading>> ReadAsync(IReadOnlySet<string> ids, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DeviceReading>>(new Dictionary<string, DeviceReading>());

    public Task SendAsync(DeviceKind kind, string id, JsonObject patch, JsonObject state, CancellationToken cancel = default) => Task.CompletedTask;

    public void Disconnect()
    {
        _catalog = new JsonObject();
        try { File.Delete(_file); }
        catch (IOException) { }
    }
}
