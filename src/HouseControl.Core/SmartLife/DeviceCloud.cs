using System.Text.Json.Nodes;
using Ranges = System.Collections.Generic.Dictionary<string, (double Min, double Max)>;

namespace HouseControl.Core.SmartLife;

/// <summary>What the app needs from a device backend, whichever way the account is connected.</summary>
internal interface IDeviceCloud
{
    bool IsConfigured { get; }
    IReadOnlyDictionary<string, HashSet<string>> KnownCodes { get; }
    /// <summary>Numeric ranges of each device's data points (brightness scales differ per model).</summary>
    IReadOnlyDictionary<string, Ranges> KnownRanges { get; }
    Task<List<JsonObject>> ListDevicesAsync();
    Task<Dictionary<string, List<JsonObject>>> StatusAsync(IReadOnlyList<string> ids);
    Task SendAsync(IReadOnlyList<JsonObject> commands, string deviceId);
    /// <summary>Makes sure the device's functions are known before the first command is built.</summary>
    Task PrepareAsync(string deviceId);
}

/// <summary>A request to Tuya failed; <see cref="Code"/> is Tuya's error code when the cloud answered with one ("2008", "1109", …).</summary>
public sealed class TuyaFailure(string message, string code = "") : ServiceFailure(message)
{
    public string Code { get; } = code;

    /// <summary>Tuya's error code behind a failed request, whichever backend raised it.</summary>
    internal static string CodeOf(Exception error) => (error as TuyaFailure)?.Code ?? "";
}

/// <summary>The plain HTTP part both backends share: one client, a JSON object back.</summary>
internal static class TuyaHttp
{
    /// <summary>Seconds a request may take, as in the Mac app.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>The client every platform made by the app shares.</summary>
    public static readonly HttpClient Shared = Create(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    public static HttpClient Create(HttpMessageHandler handler) => new(handler) { Timeout = Timeout };

    /// <summary>Sends <paramref name="request"/> and reads the answer as a JSON object, whatever the HTTP status.</summary>
    public static async Task<JsonObject> JsonAsync(HttpClient http, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await http.SendAsync(request);
            var data = await response.Content.ReadAsByteArrayAsync();
            return Json.ParseObject(data) ?? throw new TuyaFailure("Unexpected response from Tuya");
        }
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static long NowMs(Func<DateTimeOffset> now) => now().ToUnixTimeMilliseconds();
}
