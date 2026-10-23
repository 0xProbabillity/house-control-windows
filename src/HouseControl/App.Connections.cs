using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.Discovery;
using HouseControl.Core.HomeAssistant;
using HouseControl.Web;

namespace HouseControl;

/// <summary>The smart home platforms: Smart Life, Home Assistant and Philips Hue.</summary>
internal sealed partial class App
{
    private HomeAssistantService _homeAssistant = null!;
    private HueService _hue = null!;

    /// <summary>Where a platform's credentials are kept: <c>&lt;name&gt;.json</c>, encrypted for this Windows user.</summary>
    private ISecretStore Vault(string name) => new ProtectedFileSecretStore(Path.Combine(_plans.Directory, $"{name}.json"));

    private DeviceHub CreateHub()
    {
        _homeAssistant = new HomeAssistantService(Vault("homeassistant"));
        _hue = new HueService(Vault("hue"));
        return new DeviceHub(_plans.Directory, [_homeAssistant, _hue], CreateCloud());
    }

    /// <summary>What the editor needs to show the connections (never a token).</summary>
    private void AddConnections(JsonObject boot)
    {
        AddSmartLife(boot);
        boot["services"] = _hub.ServicesInfo;
    }

    /// <summary>The editor's Connections messages. False when <paramref name="type"/> is not one of them.</summary>
    private bool HandleConnection(WebHost host, JsonObject message, string type)
    {
        switch (type)
        {
            case "services.info":
                host.Reply(message, _hub.ServicesInfo);
                return true;
            case "devices.all":
                _ = Answer(host, message, AllDevices());
                return true;
            case "services.discover":
                _ = Answer(host, message, Discover(message.Str("service") ?? ""));
                return true;
            case "homeassistant.connect":
                _ = Answer(host, message, ConnectHomeAssistant(message.Str("url") ?? "", message.Str("token") ?? ""));
                return true;
            case "hue.pair":
                _ = Answer(host, message, PairHue(message.Str("host") ?? "", message.Str("bridgeId"), message.Str("name")));
                return true;
            case "service.disconnect":
                _hub.Disconnect(message.Str("service") ?? "");
                ConnectionsChanged();
                host.Reply(message, _hub.ServicesInfo);
                return true;
        }
        return HandleSmartLife(host, message, type);
    }

    private async Task<JsonObject> AllDevices()
    {
        var (devices, errors) = await _hub.AllDevicesAsync();
        return new JsonObject { ["devices"] = devices, ["errors"] = errors };
    }

    private static async Task<JsonObject> Discover(string service)
    {
        var found = service == "hue" ? await ServiceDiscovery.HueBridgesAsync() : await ServiceDiscovery.HomeAssistantsAsync();
        return new JsonObject { ["found"] = found };
    }

    private async Task<JsonObject> ConnectHomeAssistant(string url, string token)
    {
        await _homeAssistant.ConnectAsync(url, token);
        ConnectionsChanged();
        return _hub.ServicesInfo;
    }

    private async Task<JsonObject> PairHue(string address, string? bridgeId, string? name)
    {
        try
        {
            await _hue.PairAsync(address, bridgeId, name);
        }
        catch (HueButtonNotPressedException)
        {
            return new JsonObject { ["waiting"] = true };
        }
        ConnectionsChanged();
        return _hub.ServicesInfo;
    }
}
