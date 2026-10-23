using System.Drawing;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.SmartLife;
using HouseControl.Core.Sync;
using HouseControl.Shell;
using HouseControl.Web;
using HouseControl.Windows;

namespace HouseControl;

/// <summary>
/// Sync with the iPhone app: this PC appears to it exactly like a paired Mac. The floor plan, the
/// device links and the Home Assistant and Hue connections stay the same on both, whichever one
/// is edited; the phone sends its Apple Home devices, and borrows this screen for its Smart Life
/// sign-in code.
/// </summary>
internal sealed partial class App
{
    private SyncServer _sync = null!;
    private RemoteCatalogService _homeKit = null!;
    private readonly MdnsAdvertiser _advertiser = new();
    private PageWindow? _pairingWindow;
    private PageWindow? _loginWindow;
    private System.Windows.Forms.Timer? _loginExpiry;
    /// <summary>The Smart Life User Code a paired iPhone signed in with, to save typing it here.</summary>
    private string _phoneUserCode = "";

    /// <summary>The name paired iPhones see (HOUSE_COMPUTER_NAME replaces it, for screenshots).</summary>
    private static string ComputerName =>
        Environment.GetEnvironmentVariable("HOUSE_COMPUTER_NAME") is { Length: > 0 } name ? name : Environment.MachineName;

    private void StartSync()
    {
        _sync = new SyncServer(Vault("sync"));
        _sync.Snapshot = () =>
        {
            byte[]? plan = null;
            try { if (_plans.HasCustomPlan) plan = File.ReadAllBytes(_plans.CustomPlanPath); }
            catch (IOException) { }
            return new SyncState(ComputerName, _plans.CustomPlanRevision ?? "", plan, _smartLife.UserCode,
                _smartLife.IsSmartLifeConnected, _hub.ShareableConnections);
        };
        _sync.CatalogReceived += (devices, phone) =>
        {
            _homeKit.Update(devices, phone);
            _editor?.Host.Call("servicesChanged", _hub.ServicesInfo);
            RefreshSettings();
        };
        // A home set up or edited on the iPhone becomes this PC's home too.
        _sync.PlanReceived += ReceivedPlan;
        _sync.ConnectionsReceived += (shared, userCode, _) =>
        {
            if (userCode.Length > 0) _phoneUserCode = userCode;
            if (_hub.AdoptConnections(shared))
            {
                ConnectionsChanged();
                _editor?.Host.Call("servicesChanged", _hub.ServicesInfo);
            }
        };
        _sync.PeersChanged += () =>
        {
            Advertise();
            RefreshPairingWindow();
            RefreshSettings();
        };
        _sync.ShowQr += ShowLoginCode;
        _sync.Diagnostic += Log.Write;
        _sync.Start(); // only listens once a phone has been paired
        // A new Wi-Fi, a cable plugged in, waking up: announce this PC on the network it is on now.
        NetworkChange.NetworkAddressChanged += (_, _) => _instanceContext?.Post(_ => Advertise(), null);
        _instanceContext = SynchronizationContext.Current;
    }

    private SynchronizationContext? _instanceContext;

    /// <summary>Announces this PC (or stops announcing it) according to whether pairing is on.</summary>
    private void Advertise()
    {
        if (_sync.Pairing is { } pairing && _sync.Port is { } port)
        {
            var ok = _advertiser.Advertise(pairing.Name, port, new Dictionary<string, string> { ["id"] = pairing.Id });
            Log.Write($"sync: announced as {pairing.Name} on port {port} at {MdnsAdvertiser.PreferredAddress()}: {(ok ? "ok" : $"failed ({MdnsAdvertiser.LastStatus})")}");
        }
        else
            _advertiser.Withdraw();
    }

    /// <summary>Tells every connected iPhone what is true here now.</summary>
    private void BroadcastToPhones() => _sync?.Broadcast();

    /// <summary>
    /// Stores a plan an iPhone saved. The first one becomes the active home; later ones replace it
    /// (a PC that switched to a demo plan stays there, like the phone does).
    /// </summary>
    private void ReceivedPlan(byte[] data, string phone)
    {
        var first = !_plans.HasCustomPlan;
        try
        {
            _plans.SaveCustom(data, select: first || _plans.ActivePlan == "custom");
        }
        catch (Exception e)
        {
            Log.Error($"plan from {phone}", e);
            return;
        }
        ReloadLinks();
        _panel.Host.Load();
        _island?.Host.Load();
        _widget?.Host.Load();
        BroadcastToPhones();
        RefreshSettings();
        // An open editor takes the new plan unless it holds unsaved changes of its own.
        if (_editor != null && _plans.CustomPlan() is { } plan) _editor.Host.Call("planArrived", plan, phone);
    }

    // MARK: Pair iPhone

    private void PairPhone()
    {
        _panel.Hide();
        _sync.Enable(ComputerName);
        Advertise();
        if (_pairingWindow is null)
        {
            var host = Attach(new WebHost("pairing", () => new JsonObject { ["qr"] = PairingView() }));
            _pairingWindow = new PageWindow(host, "Pair iPhone", new Size(460, 700), new Size(400, 560), _windows, "pairing");
            // Re-checks the network and the firewall while the window is open: making the network
            // private in Settings changes nothing the app is told about.
            var recheck = new System.Windows.Forms.Timer { Interval = 3000 };
            recheck.Tick += (_, _) => RefreshPairingWindow();
            recheck.Start();
            _pairingWindow.FormClosed += (_, _) =>
            {
                recheck.Dispose();
                _pairingWindow = null;
            };
            _ = Start(_pairingWindow.InitializeAsync(), "pairing");
        }
        _pairingWindow.Present();
        RefreshPairingWindow();
    }

    private JsonObject PairingView()
    {
        var devices = _sync.ConnectedDevices;
        string status;
        var active = devices.Count > 0;
        // What would keep a phone out, checked each time the window is drawn: the phone finds this
        // PC either way, but Windows drops its connection.
        var publicNetworks = active ? Array.Empty<string>() : NetworkCheck.PublicNetworks();
        var firewallBlocks = !active && !NetworkCheck.FirewallAllows();
        var fixes = new JsonArray();
        if (active) status = $"Connected: {string.Join(", ", devices)}";
        else if (!MdnsAdvertiser.IsSupported) status = "This version of Windows cannot announce the PC on the network. Update Windows 10 to 1809 or newer.";
        else if (publicNetworks.Count > 0)
        {
            status = $"Windows treats “{publicNetworks[0]}” as a public network, so the iPhone cannot reach this PC. Make it a private network.";
            fixes.Add(new JsonObject { ["label"] = "Open Network Settings", ["action"] = "pairing.network", ["primary"] = true });
        }
        else if (firewallBlocks)
        {
            status = "Windows Firewall does not let the iPhone connect to House Control yet.";
            fixes.Add(new JsonObject { ["label"] = "Allow in Firewall…", ["action"] = "pairing.firewall", ["primary"] = true });
        }
        else status = "Waiting for an iPhone…";
        fixes.Add(new JsonObject { ["label"] = "Copy Pairing Link", ["action"] = "pairing.copy" });
        fixes.Add(new JsonObject { ["label"] = "Reset Pairing…", ["action"] = "pairing.reset" });
        return new JsonObject
        {
            ["qr"] = _sync.Pairing is { } pairing ? QrPng.DataUrl(pairing.Link) : null,
            ["headline"] = "Scan with House Control on your iPhone",
            ["detail"] = $"On the iPhone open Settings → Pair with Mac, and point the camera at this code. It finds {ComputerName} on your Wi-Fi like a Mac.",
            ["status"] = status,
            ["active"] = active,
            ["steps"] = new JsonArray(
                "Both devices on the same Wi-Fi (or this PC on the same router by cable).",
                "If Windows asks whether House Control may use private networks, allow it.",
                "On the iPhone, allow House Control to use the local network when it asks."),
            ["buttons"] = fixes,
            ["note"] = "Your floor plan, device links and Home Assistant and Hue connections stay the same on both, whichever one you edit. "
                + "They travel straight between them, encrypted with the key in this code; nothing goes through a server.",
        };
    }

    private void RefreshPairingWindow() => _pairingWindow?.Host.Call("qrChanged", PairingView());

    private bool HandleSync(WebHost host, JsonObject message, string type)
    {
        switch (type)
        {
            case "pairing.copy" when _sync.Pairing is { } pairing:
                Clipboard.SetText(pairing.Link);
                return true;
            case "pairing.reset":
                var answer = MessageBox.Show("Every iPhone paired so far stops syncing until it scans the new code.",
                    "Reset pairing?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.OK)
                {
                    _sync.Reset(ComputerName);
                    Advertise();
                    RefreshPairingWindow();
                }
                return true;
            case "pairing.network":
                NetworkCheck.OpenNetworkSettings();
                return true;
            case "pairing.firewall":
                // Windows asks for an administrator's OK; only then is the rule added.
                NetworkCheck.AllowThroughFirewall();
                RefreshPairingWindow();
                return true;
            case "pairPhone":
                PairPhone();
                return true;
        }
        return false;
    }

    // MARK: A phone's Smart Life sign-in, shown here

    /// <summary>
    /// A paired iPhone signs in to Smart Life on its own, but its sign-in code has to be scanned by
    /// the Smart Life app on that same phone, so it borrows this screen. The window closes when the
    /// phone says it is signed in (or gave up), when it is closed, or after ten minutes.
    /// </summary>
    private void ShowLoginCode(string? content, string device)
    {
        _loginExpiry?.Dispose();
        _loginExpiry = null;
        if (content is null || !content.StartsWith("tuyaSmart--qrLogin?token=", StringComparison.Ordinal))
        {
            _loginWindow?.Close();
            return;
        }
        var view = new JsonObject
        {
            ["qr"] = QrPng.DataUrl(content),
            ["headline"] = $"Scan with the Smart Life app on {device}",
            ["detail"] = "Switch to Smart Life on the iPhone, tap the scanner on its Home tab and confirm the sign-in. "
                + "Then return to House Control: this window closes once the iPhone is signed in.",
            ["status"] = $"Requested by {device}",
            ["active"] = true,
        };
        if (_loginWindow is null)
        {
            var host = Attach(new WebHost("pairing", () => new JsonObject { ["qr"] = view }));
            _loginWindow = new PageWindow(host, $"Smart Life Sign-In for {device}", new Size(460, 600), new Size(400, 520), _windows, null);
            _loginWindow.FormClosed += (_, _) => _loginWindow = null;
            _ = Start(_loginWindow.InitializeAsync(), "login code");
        }
        else
        {
            _loginWindow.Host.Call("qrChanged", view);
        }
        _loginWindow.Present();
        _loginExpiry = new System.Windows.Forms.Timer { Interval = 600_000 };
        _loginExpiry.Tick += (_, _) =>
        {
            _loginExpiry?.Dispose();
            _loginExpiry = null;
            _loginWindow?.Close();
        };
        _loginExpiry.Start();
    }

    private void StopSync()
    {
        _advertiser.Dispose();
        _sync?.Dispose();
    }
}
