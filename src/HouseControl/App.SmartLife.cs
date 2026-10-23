using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.SmartLife;
using HouseControl.Web;

namespace HouseControl;

/// <summary>Smart Life / Tuya: the QR sign-in, the optional Tuya Cloud project, and the device list.</summary>
internal sealed partial class App
{
    private SmartLifePlatform _smartLife = null!;

    private ILinkedCloud CreateCloud() => _smartLife = new SmartLifePlatform(_plans.Directory, Vault);

    private void AddSmartLife(JsonObject boot)
    {
        boot["tuya"] = _smartLife.TuyaInfo;
        var account = _smartLife.SmartLifeInfo;
        // Not signed in here yet: offer the User Code a paired iPhone signed in with.
        if (!_smartLife.IsSmartLifeConnected && _phoneUserCode.Length > 0) account["userCode"] = _phoneUserCode;
        boot["smartLife"] = account;
    }

    private bool HandleSmartLife(WebHost host, JsonObject message, string type)
    {
        switch (type)
        {
            case "tuya.save":
                try
                {
                    _smartLife.SaveTuya(message.Str("accessId") ?? "", message.Str("accessSecret") ?? "", message.Str("region") ?? "eu");
                    ConnectionsChanged();
                    host.Reply(message, _smartLife.TuyaInfo);
                }
                catch (Exception e)
                {
                    host.Reply(message, new JsonObject { ["error"] = e.Message });
                }
                return true;
            case "tuya.devices":
                _ = Answer(host, message, TuyaDevices());
                return true;
            case "smartlife.begin":
                _ = Answer(host, message, BeginSmartLife(message.Str("userCode") ?? ""));
                return true;
            case "smartlife.poll":
                _ = Answer(host, message, PollSmartLife(message.Str("token") ?? "", message.Str("userCode") ?? ""));
                return true;
            case "smartlife.signOut":
                _smartLife.SignOut();
                ConnectionsChanged();
                host.Reply(message, _smartLife.SmartLifeInfo);
                return true;
        }
        return false;
    }

    private async Task<JsonObject> TuyaDevices()
    {
        var devices = await _smartLife.ListDevicesAsync();
        return new JsonObject { ["devices"] = new JsonArray(devices.Select(d => (JsonNode)d.DeepClone()).ToArray()) };
    }

    private async Task<JsonObject> BeginSmartLife(string userCode)
    {
        var (token, qr) = await _smartLife.BeginLoginAsync(userCode);
        return new JsonObject { ["token"] = token, ["qr"] = QrPng.DataUrl(qr) };
    }

    private async Task<JsonObject> PollSmartLife(string token, string userCode)
    {
        var done = await _smartLife.FinishLoginAsync(token, userCode);
        if (done) ConnectionsChanged();
        return new JsonObject { ["connected"] = done, ["info"] = _smartLife.SmartLifeInfo };
    }
}

/// <summary>
/// <c>HouseControl.exe --selftest-mapper</c>, <c>--selftest-infrared</c>, <c>--selftest-transport</c> and
/// <c>--diagnose &lt;device id&gt;…</c> print to the console they were started from, then exit.
/// </summary>
internal static class SelfTests
{
    public static int? Run(string[] args)
    {
        if (!args.Any(a => a.StartsWith("--selftest", StringComparison.Ordinal) || a == "--diagnose")) return null;
        // A Windows app has no console of its own: borrow the one it was started from.
        AttachConsole(-1);
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        return SelfTest.Run(args, output) ?? 0;
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
}
