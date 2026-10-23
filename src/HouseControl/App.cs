using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Shell;
using HouseControl.Web;
using HouseControl.Windows;
using static HouseControl.Native.Win32;

namespace HouseControl;

/// <summary>
/// The app: owns the plan, the device hub and every view of the home, and carries the messages
/// between them. The tray icon, the pinned taskbar button, the jump list, the keyboard shortcut,
/// the panel, the island and the desktop widget all end up here.
/// </summary>
internal sealed partial class App : ApplicationContext
{
    private readonly PlanStore _plans;
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _windows;
    private readonly DeviceHub _hub;
    private readonly SingleInstance _instance;
    private readonly TrayIcon _tray = new();
    private readonly HotKey _hotKey = new();
    private readonly FullscreenWatcher _fullscreen = new();
    private readonly PanelWindow _panel;
    private IslandWindow? _island;
    private DesktopWidget? _widget;
    private PageWindow? _editor;
    private PageWindow? _settingsWindow;
    /// <summary>Where a new editor window opens ("connections", "service:hue", …).</summary>
    private string? _editorSection;
    private readonly System.Windows.Forms.Timer _poll = new();
    private JsonObject _summary = new();
    /// <summary>The panel's last picture of the home (rooms, lamps, ACs, scenes and their state).</summary>
    private JsonObject? _snapshot;
    private string _jumpListKey = "";
    private string _hotKeyProblem = "";

    public App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _plans = new PlanStore();
        Log.Start(_plans.Directory);
        Log.Write($"House Control {Version} starting, data in {_plans.Directory}");
        _settings = new AppSettings(_plans.Directory);
        _windows = new AppSettingsStore(_plans.Directory);
        _hub = CreateHub();
        _hub.Stale += Poll;

        _panel = new PanelWindow(Attach(new WebHost("panel", BootPayload)));
        _panel.VisibilityChanged += _ => SetPolling();
        _ = Start(_panel.InitializeAsync(), "panel");

        _instance.ArgumentsReceived += Run;
        _tray.Clicked += point => TogglePanel(point);
        _tray.MenuRequested += ShowTrayMenu;
        _tray.Show(0, false, _settings.TrayBadge, AgentLive);
        _hotKey.Pressed += () => TogglePanel(null);
        ApplyHotKey();
        _fullscreen.Changed += full => _island?.SetSuppressed(full && _settings.IslandHideInFullscreen);
        _poll.Tick += (_, _) => Poll();
        SystemEvents();

        ReloadLinks();
        StartSync();
        if (IslandWanted) ShowIsland();
        if (_settings.WidgetEnabled) ShowWidget();
        StartWithWindows.FollowMove();
        UpdateJumpList(force: true);

        Run(args);
        TestHooks();
    }

    public static string Version =>
        typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    private static async Task Start(Task task, string what)
    {
        try { await task; }
        catch (Exception e) { Log.Error($"start {what}", e); }
    }

    private WebHost Attach(WebHost host)
    {
        host.Message += Handle;
        return host;
    }

    // MARK: Arguments (a second start, the jump list, shortcuts)

    /// <summary>
    /// Carries out what House Control was started with: <c>--scene &lt;id&gt;</c>, <c>--all-off</c>,
    /// <c>--settings</c>, <c>--edit</c>, <c>--ai</c>, <c>--quit</c>; no argument opens the panel,
    /// <c>--background</c> (Start with Windows) does nothing.
    /// </summary>
    private void Run(string[] args)
    {
        var first = args.FirstOrDefault()?.ToLowerInvariant();
        switch (first)
        {
            case null when _instance.IsFirst && args.Length == 0 && !_started:
                // The very first start: say hello by opening the panel once it is loaded.
                _started = true;
                WhenPanelReady(() => TogglePanel(null));
                break;
            case null:
            case "--open":
            case "--toggle":
                GetCursorPos(out var p);
                // From the taskbar button the panel opens above it; otherwise by the clock.
                TogglePanel(IsOverTaskbar(new Point(p.X, p.Y)) ? new Point(p.X, p.Y) : null);
                break;
            case "--background":
                break;
            case "--scene" when args.Length > 1:
                WidgetAction(new JsonObject { ["kind"] = "scene", ["id"] = args[1] });
                break;
            case "--all-off":
                WidgetAction(new JsonObject { ["kind"] = "allLights", ["on"] = false });
                break;
            case "--all-on":
                WidgetAction(new JsonObject { ["kind"] = "allLights", ["on"] = true });
                break;
            case "--settings":
                ShowSettings();
                break;
            case "--edit":
                ShowEditor(null);
                break;
            case "--ai":
                OpenAgent();
                break;
            case "--quit":
                Quit();
                break;
        }
        _started = true;
    }

    private bool _started;

    private static bool IsOverTaskbar(Point p)
    {
        var screen = Placement.ScreenAt(p);
        return !screen.Work.Contains(p) && screen.Bounds.Contains(p);
    }

    /// <summary>Runs an action as a tap on the panel would, once the panel page can take it.</summary>
    private void WidgetAction(JsonObject action) => WhenPanelReady(() => _panel.Host.Call("widget", new JsonArray(action)));

    private readonly List<Action> _whenReady = new();

    private void WhenPanelReady(Action action)
    {
        if (_panel.Host.IsReady) action();
        else _whenReady.Add(action);
    }

    // MARK: Panel

    private void TogglePanel(Point? anchor)
    {
        if (!_panel.Host.IsReady && _panel.Host.Core is null) return;
        _panel.Toggle(anchor);
        if (_panel.IsOpen) _panel.Host.Call("openAtLogin", StartWithWindows.State == "on" ? "on" : "off");
    }

    // MARK: Boot payloads

    private JsonObject BootPayload()
    {
        var boot = _plans.BootPayload();
        AddConnections(boot);
        if (_editorSection != null) boot["section"] = _editorSection;
        switch (Environment.GetEnvironmentVariable("HOUSE_OPEN_EDITOR"))
        {
            case "smartlife": boot["section"] = "service:smartlife"; break;
            case "homeassistant" or "hue": boot["section"] = $"service:{Environment.GetEnvironmentVariable("HOUSE_OPEN_EDITOR")}"; break;
            case "connections": boot["section"] = "connections"; break;
        }
        boot["agentLive"] = AgentLive;
        boot["openAtLogin"] = StartWithWindows.State == "on" ? "on" : "off";
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_DEVICE") is { Length: > 0 } device) boot["openDevice"] = device;
        return boot;
    }

    private JsonObject IslandBoot()
    {
        var boot = _plans.BootPayload();
        boot["island"] = _island?.BootInfo ?? new JsonObject();
        return boot;
    }

    private JsonObject WidgetBoot()
    {
        var boot = _plans.BootPayload();
        boot["island"] = new JsonObject { ["desk"] = true, ["locked"] = _settings.WidgetLocked, ["notchHeight"] = 30, ["notchWidth"] = 190 };
        return boot;
    }

    // MARK: Messages from the pages

    private void Handle(WebHost host, JsonObject message)
    {
        var type = message.Str("type");
        switch (type)
        {
            case "ready" or "summary":
                if (type == "ready" && host == _panel.Host)
                {
                    foreach (var action in _whenReady.ToList()) action();
                    _whenReady.Clear();
                }
                if (type == "ready" && HandleAgent(host, message, type)) break;
                // A view that has just loaded takes over what the panel shows.
                if (type == "ready" && host != _panel.Host) _panel.Host.Call("share");
                if ((message.Obj("summary") ?? (message.ContainsKey("lightsOn") ? message : null)) is { } summary && host == _panel.Host)
                    UpdateStatus(summary);
                break;
            case "state":
                // The panel, the island and the widget are views of one home: what changes in one
                // is shown by the others.
                var state = (JsonObject)message.DeepClone();
                state.Remove("type");
                foreach (var other in Views().Where(v => v != host)) other.Call("adopt", state);
                break;
            case "command":
                _ = Answer(host, message, _hub.CommandAsync(message));
                break;
            case "widgets":
                if (host == _panel.Host && message.Obj("snapshot") is { } snapshot)
                {
                    _snapshot = (JsonObject)snapshot.DeepClone();
                    UpdateJumpList();
                }
                break;
            case "close":
                if (host == _panel.Host) _panel.Hide();
                break;
            case "selectPlan":
                if (message.Str("plan") is { } plan) SelectPlan(plan);
                break;
            case "loadPlan": LoadPlan(); break;
            case "openAgent": OpenAgent(); break;
            case "stopAgent": StopAgent(); break;
            case "revealPlan": RevealPlan(); break;
            case "revealLog": RevealLog(); break;
            case "openEditor": ShowEditor(message.Str("section")); break;
            case "openSettings": ShowSettings(message.Str("pane")); break;
            case "openLink": if (message.Str("url") is { } url) WebHost.OpenExternally(url); break;
            case "quit": Quit(); break;
            case "setOpenAtLogin": SetStartWithWindows(message.Bool("on") == true); break;

            // Editor
            case "savePlan":
                if (message.Obj("config") is not { } config) return;
                try
                {
                    _plans.SaveCustom(config);
                    ReloadAll();
                    host.Reply(message, new JsonObject { ["ok"] = true, ["path"] = _plans.CustomPlanPath });
                }
                catch (Exception e)
                {
                    host.Reply(message, new JsonObject { ["error"] = e.Message });
                }
                break;
            case "importPlan":
                ImportPlan(host, message);
                break;
            case "exportPlan":
                ExportPlan(host, message);
                break;

            // Settings window
            case "settings.set":
                ChangeSetting(message.Str("key") ?? "", message["value"]);
                break;
            case "settings.hotkey":
                SetHotKey(host, message);
                break;
            case "settings.resetWidget":
                _widget?.ResetPosition();
                break;

            default:
                if (type != null && HandleSync(host, message, type)) return;
                if (type != null && HandleConnection(host, message, type)) return;
                if (type != null && HandleAgent(host, message, type)) return;
                break;
        }
    }

    private static async Task Answer(WebHost host, JsonObject message, Task<JsonObject> work)
    {
        try { host.Reply(message, await work); }
        catch (Exception e) { host.Reply(message, new JsonObject { ["error"] = e.Message }); }
    }

    private IEnumerable<WebHost> Views()
    {
        yield return _panel.Host;
        if (_island is { IsShown: true }) yield return _island.Host;
        if (_widget is { IsShown: true }) yield return _widget.Host;
    }

    // MARK: Status: the tray icon, its tooltip and menu

    private void UpdateStatus(JsonObject summary)
    {
        _summary = summary;
        RefreshTray();
    }

    private void RefreshTray()
    {
        var lights = (int)(_summary.Num("lightsOn") ?? 0);
        var total = (int)(_summary.Num("lightsTotal") ?? 0);
        var ac = (int)(_summary.Num("acOn") ?? 0);
        var tip = "House Control";
        if (total > 0) tip += lights > 0 ? $"\n{lights} of {total} lights on" : "\nAll lights off";
        if (ac > 0 && _summary.Num("acTarget") is { } target) tip += $" · AC {target:0}°";
        if (AgentLive) tip += "\nAI Access is live";
        _tray.Update(lights, ac > 0, _settings.TrayBadge, AgentLive, tip);
    }

    private void ShowTrayMenu(Point at)
    {
        _panel.Hide();
        var menu = new TrayMenu();
        menu.Add("Open House Control", () => TogglePanel(null), isDefault: true);
        var scenes = _snapshot?.Arr("scenes")?.OfType<JsonObject>().ToList() ?? [];
        if (scenes.Count > 0)
        {
            var current = _snapshot.Str("scene");
            menu.Submenu("Scenes", scenes.Select<JsonObject, (string, bool, Action?)>(s => (s.Str("name") ?? s.Str("id") ?? "Scene",
                s.Str("id") == current,
                (Action?)(() => WidgetAction(new JsonObject { ["kind"] = "scene", ["id"] = s.Str("id") })))));
        }
        var lightsOn = (int)(_summary.Num("lightsOn") ?? 0);
        menu.Add(lightsOn > 0 ? "Turn All Lights Off" : "Turn All Lights On",
            () => WidgetAction(new JsonObject { ["kind"] = "allLights", ["on"] = lightsOn == 0 }));
        menu.Separator();
        menu.Add("Edit Home…", () => ShowEditor(null));
        menu.Add("Load Floor Plan…", LoadPlan);
        menu.Add("Show Island", () => ChangeSetting("islandEnabled", JsonValue.Create(!_settings.IslandEnabled)), _settings.IslandEnabled);
        menu.Add("Desktop Widget", () => ChangeSetting("widgetEnabled", JsonValue.Create(!_settings.WidgetEnabled)), _settings.WidgetEnabled);
        var startup = StartWithWindows.State;
        menu.Add(startup == "disabled" ? "Start with Windows (off in Task Manager)" : "Start with Windows",
            () => SetStartWithWindows(startup != "on"), startup == "on");
        menu.Add("Pair iPhone…", PairPhone);
        foreach (var phone in _sync.ConnectedDevices) menu.Add($"{phone} is connected", PairPhone, isChecked: true);
        menu.Add(AgentLive ? "AI Access — Live" : "AI Access…", OpenAgent);
        if (AgentLive) menu.Add("Stop AI Access", StopAgent);
        menu.Separator();
        menu.Add("Settings…", () => ShowSettings());
        menu.Submenu("About House Control", [
            ($"House Control {Version}", false, null),
            ("Source Code on GitHub", false, () => WebHost.OpenExternally(About.Source)),
            ("Report a Problem", false, () => WebHost.OpenExternally(About.Issues)),
            ("Reveal Plan File", false, RevealPlan),
            ("Reveal Device Log", false, RevealLog),
        ]);
        menu.Add("Reload", ReloadAll);
        menu.Add("Quit House Control", Quit);
        menu.Show(_tray.Handle, at);
    }

    // MARK: Jump list

    /// <summary>The taskbar button's menu: the home's scenes and the everyday commands.</summary>
    private void UpdateJumpList(bool force = false)
    {
        var scenes = _snapshot?.Arr("scenes")?.OfType<JsonObject>()
            .Select(s => (Id: s.Str("id") ?? "", Name: s.Str("name") ?? s.Str("id") ?? "Scene"))
            .Where(s => s.Id.Length > 0 && !s.Id.Contains('"'))
            .ToList() ?? [];
        var key = string.Join("|", scenes.Select(s => $"{s.Id}={s.Name}"));
        if (!force && key == _jumpListKey) return;
        _jumpListKey = key;
        JumpList.Apply("Scenes",
            scenes.Select(s => new JumpList.Entry(s.Name, $"--scene \"{s.Id}\"", $"Set every light and AC for “{s.Name}”")).ToList(),
            [
                new JumpList.Entry("Open the panel", "--open", "Your home in 3D, its lights and air conditioners"),
                new JumpList.Entry("Turn all lights off", "--all-off", "Switches every light of the home off"),
                null,
                new JumpList.Entry("Edit home…", "--edit", "Rooms, furniture, lamps, ACs and their real devices"),
                new JumpList.Entry("Settings…", "--settings", "The island, the widget, connections and more"),
            ]);
    }

    // MARK: Plans

    private void ReloadLinks() => _hub.SetLinks(_plans.LinkedPlan());

    /// <summary>A platform was connected or disconnected: read the devices again.</summary>
    private void ConnectionsChanged()
    {
        ReloadLinks();
        SetPolling(force: true);
        BroadcastToPhones();
        RefreshSettings();
    }

    private void ReloadAll()
    {
        ReloadLinks();
        _panel.Host.Load();
        _island?.Host.Load();
        _widget?.Host.Load();
        BroadcastToPhones();
        RefreshSettings();
    }

    private void SelectPlan(string plan)
    {
        if (plan == _plans.ActivePlan) return;
        _plans.Select(plan);
        ReloadAll();
        _editor?.Host.Load();
    }

    private void LoadPlan()
    {
        _panel.Hide();
        using var dialog = new OpenFileDialog { Title = "Choose a floor plan", Filter = "Floor plan (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            _plans.ImportPlan(dialog.FileName);
            ReloadAll();
            _editor?.Host.Load();
        }
        catch (Exception e)
        {
            Alert("Could not load this floor plan", e.Message);
        }
    }

    private static void ImportPlan(WebHost host, JsonObject message)
    {
        using var dialog = new OpenFileDialog { Title = "Import a floor plan", Filter = "Floor plan (*.json)|*.json" };
        if (dialog.ShowDialog() != DialogResult.OK) { host.Reply(message, new JsonObject { ["cancelled"] = true }); return; }
        try
        {
            host.Reply(message, new JsonObject { ["config"] = PlanStore.Validate(File.ReadAllBytes(dialog.FileName)) });
        }
        catch (Exception e)
        {
            host.Reply(message, new JsonObject { ["error"] = e.Message });
        }
    }

    private static void ExportPlan(WebHost host, JsonObject message)
    {
        if (message.Obj("config") is not { } config) return;
        var name = string.Concat((config.Str("name") ?? "home").ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
        using var dialog = new SaveFileDialog { Title = "Export the floor plan", Filter = "Floor plan (*.json)|*.json", FileName = $"{name}.json" };
        if (dialog.ShowDialog() != DialogResult.OK) { host.Reply(message, new JsonObject { ["cancelled"] = true }); return; }
        try
        {
            File.WriteAllBytes(dialog.FileName, PlanStore.Encode(config));
            host.Reply(message, new JsonObject { ["ok"] = true });
        }
        catch (Exception e)
        {
            host.Reply(message, new JsonObject { ["error"] = e.Message });
        }
    }

    private void RevealPlan()
    {
        var path = _plans.ActivePlan == "custom" && _plans.HasCustomPlan
            ? _plans.CustomPlanPath
            : Path.Combine(WebHost.WebDirectory, "plans", $"{_plans.ActivePlan}.json");
        Reveal(path);
    }

    private void RevealLog()
    {
        if (!File.Exists(_hub.Log.FilePath)) _hub.Log.Write("log started");
        Reveal(_hub.Log.FilePath);
    }

    private static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception e) { Log.Error("reveal", e); }
    }

    private static void Alert(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // MARK: Windows

    private void ShowEditor(string? section)
    {
        _panel.Hide();
        if (section != null) _editor?.Host.Call("openSection", section);
        _editorSection = section;
        if (_editor is null)
        {
            var host = Attach(new WebHost("editor", BootPayload));
            _editor = new PageWindow(host, "House Control — Edit Home", new Size(1240, 800), new Size(980, 640), _windows, "editor");
            _editor.FormClosed += (_, _) => _editor = null;
            _ = Start(_editor.InitializeAsync(), "editor");
        }
        _editorSection = null;
        _editor.Present();
    }

    private void ShowSettings(string? pane = null)
    {
        _panel.Hide();
        if (_settingsWindow is null)
        {
            var host = Attach(new WebHost("settings", () =>
            {
                var boot = _plans.BootPayload();
                boot["settings"] = SettingsState();
                if (pane != null) boot["pane"] = pane;
                return boot;
            }));
            _settingsWindow = new PageWindow(host, "House Control Settings", new Size(860, 640), new Size(680, 480), _windows, "settings");
            _settingsWindow.FormClosed += (_, _) => _settingsWindow = null;
            _ = Start(_settingsWindow.InitializeAsync(), "settings");
        }
        else if (pane != null)
        {
            _settingsWindow.Host.Call("showPane", pane);
        }
        _settingsWindow.Present();
    }

    /// <summary>Everything the Settings window shows, as it is right now.</summary>
    private JsonObject SettingsState()
    {
        var state = _settings.ToJson();
        state["startWithWindows"] = StartWithWindows.State;
        state["hotkeyText"] = HotKey.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey);
        state["hotkeyProblem"] = _hotKeyProblem;
        state["version"] = Version;
        state["dataDirectory"] = _plans.Directory;
        state["displays"] = new JsonArray(Placement.All.Select((s, i) => (JsonNode)new JsonObject
        {
            ["id"] = s.Primary ? "" : s.Device,
            ["name"] = $"Display {i + 1}{(s.Primary ? " (main)" : "")} · {s.Bounds.Width}×{s.Bounds.Height}",
        }).ToArray());
        var plans = new JsonArray();
        if (_plans.HasCustomPlan) plans.Add(new JsonObject { ["id"] = "custom", ["name"] = _plans.CustomPlanName ?? "My home" });
        plans.Add(new JsonObject { ["id"] = "home", ["name"] = "Demo · Apartment" });
        plans.Add(new JsonObject { ["id"] = "studio", ["name"] = "Demo · Studio" });
        state["plans"] = plans;
        state["activePlan"] = _plans.ActivePlan;
        state["services"] = _hub.ServicesInfo;
        state["aiLive"] = AgentLive;
        state["paired"] = _sync.IsEnabled;
        state["phones"] = new JsonArray(_sync.ConnectedDevices.Select(p => (JsonNode)p).ToArray());
        state["computerName"] = ComputerName;
        state["windows11"] = IsWindows11;
        return state;
    }

    private void RefreshSettings() => _settingsWindow?.Host.Call("settingsChanged", SettingsState());

    private void ChangeSetting(string key, JsonNode? value)
    {
        switch (key)
        {
            case "startWithWindows":
                SetStartWithWindows(value?.GetValueKind() == System.Text.Json.JsonValueKind.True);
                return;
            case "plan":
                if (value?.GetValue<string>() is { } plan) SelectPlan(plan);
                RefreshSettings();
                return;
        }
        if (!_settings.Set(key, value)) return;
        switch (key)
        {
            case "islandEnabled":
                if (IslandWanted) ShowIsland(); else _island?.Hide();
                break;
            case "islandRest" or "islandDisplay" or "islandPeek" or "islandWidth" or "islandOpacity":
                if (_island != null)
                {
                    _island.Place();
                    _island.Host.Load();
                }
                break;
            case "islandHideInFullscreen":
                _island?.SetSuppressed(_settings.IslandHideInFullscreen && _fullscreen.IsFullscreen);
                break;
            case "widgetEnabled":
                if (_settings.WidgetEnabled) ShowWidget(); else _widget?.Hide();
                SetPolling();
                break;
            case "widgetLocked":
                _widget?.Host.Call("setLocked", _settings.WidgetLocked);
                break;
            case "hotkeyEnabled":
                ApplyHotKey();
                break;
            case "trayBadge":
                RefreshTray();
                break;
        }
        RefreshSettings();
    }

    private void SetStartWithWindows(bool on)
    {
        try { StartWithWindows.Set(on); }
        catch (Exception e) { Alert("Could not change Start with Windows", e.Message); }
        _panel.Host.Call("openAtLogin", StartWithWindows.State == "on" ? "on" : "off");
        RefreshSettings();
    }

    private void ApplyHotKey()
    {
        _hotKeyProblem = "";
        if (!_settings.HotkeyEnabled) { _hotKey.Unregister(); return; }
        if (!_hotKey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey))
            _hotKeyProblem = $"{HotKey.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey)} is already used by another app.";
    }

    private void SetHotKey(WebHost host, JsonObject message)
    {
        var modifiers = (int)(message.Num("modifiers") ?? 0);
        var key = (int)(message.Num("key") ?? 0);
        if (modifiers == 0 || key == 0)
        {
            host.Reply(message, new JsonObject { ["error"] = "Use at least one of Ctrl, Alt or Win with a key." });
            return;
        }
        var before = (_settings.HotkeyModifiers, _settings.HotkeyKey);
        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyKey = key;
        _settings.HotkeyEnabled = true;
        ApplyHotKey();
        if (_hotKeyProblem.Length > 0)
        {
            var problem = _hotKeyProblem;
            (_settings.HotkeyModifiers, _settings.HotkeyKey) = before;
            ApplyHotKey();
            host.Reply(message, new JsonObject { ["error"] = problem });
        }
        else
        {
            _settings.Save();
            host.Reply(message, new JsonObject { ["ok"] = true, ["text"] = HotKey.Describe(modifiers, key) });
        }
        RefreshSettings();
    }

    // MARK: Island and widget

    private bool IslandWanted => Environment.GetEnvironmentVariable("HOUSE_ISLAND") is { } forced ? forced != "0" : _settings.IslandEnabled;

    private void ShowIsland()
    {
        if (_island is null)
        {
            _island = new IslandWindow(Attach(new WebHost("island", IslandBoot)), _settings);
            _island.ExpandedChanged += _ => SetPolling();
            _island.Place();
            _ = Start(_island.InitializeAsync(), "island");
        }
        _island.Show();
        _island.SetSuppressed(_settings.IslandHideInFullscreen && _fullscreen.IsFullscreen);
    }

    private void ShowWidget()
    {
        if (_widget is null)
        {
            _widget = new DesktopWidget(Attach(new WebHost("widget", WidgetBoot)), _settings);
            _widget.Place();
            _ = Start(_widget.InitializeAsync(), "widget");
        }
        _widget.Show();
    }

    // MARK: Device state

    /// <summary>
    /// Real devices are read while there is something on screen to show them in: every 12 s
    /// while the panel or the island is open, every minute for the desktop widget alone.
    /// </summary>
    private void SetPolling(bool force = false)
    {
        var open = _panel.IsOpen || _island?.IsExpanded == true;
        var interval = open ? 12_000 : _widget is { IsShown: true } ? 60_000 : 0;
        if (interval == 0 || !_hub.CanPoll)
        {
            _poll.Stop();
            return;
        }
        if (open || force) Poll();
        if (_poll.Enabled && _poll.Interval == interval) return;
        _poll.Interval = interval;
        _poll.Stop();
        _poll.Start();
    }

    private bool _polling;

    private async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (await _hub.PollAsync() is { } update) _panel.Host.Call("applyRemote", update); // the others adopt it from the panel
        }
        catch (Exception e)
        {
            Log.Error("poll", e);
        }
        finally
        {
            _polling = false;
        }
    }

    // MARK: System

    private void SystemEvents()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            _island?.Place();
            if (_widget is { IsShown: true }) _widget.Place();
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => RefreshTray();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            // Back from sleep: devices may have changed while the PC was away.
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) SetPolling(force: true);
        };
    }

    /// <summary>
    /// HOUSE_SNAPSHOT_DIR=&lt;folder&gt; writes panel.png, island.png, widget.png and settings.png there a
    /// few seconds after launch; HOUSE_OPEN_* open windows at launch (screenshots and UI tests).
    /// </summary>
    private void TestHooks()
    {
        void Later(int ms, Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = ms };
            timer.Tick += (_, _) => { timer.Dispose(); action(); };
            timer.Start();
        }
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_SETTINGS") is { } pane)
            Later(800, () => ShowSettings(pane is "" or "1" ? null : pane));
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_EDITOR") is not null) Later(800, () => ShowEditor(null));
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_PAIRING") is not null) Later(800, PairPhone);
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_AGENT") is { } agent)
            Later(800, () => { OpenAgent(); if (agent == "start") StartAgent(); });
        if (Environment.GetEnvironmentVariable("HOUSE_OPEN_ON_LAUNCH") is not null) Later(1500, () => { if (!_panel.IsOpen) TogglePanel(null); });
        if (Environment.GetEnvironmentVariable("HOUSE_SNAPSHOT_DIR") is { Length: > 0 } folder)
        {
            var delay = int.TryParse(Environment.GetEnvironmentVariable("HOUSE_SNAPSHOT_DELAY"), out var s) ? s * 1000 : 6000;
            Later(delay, async () =>
            {
                try
                {
                    // Every animation at its end state first: the camera sweep, lamps fading in.
                    foreach (var view in new[] { _panel.Host, _island?.Host, _widget?.Host })
                        if (view != null) try { await view.EvalAsync("window.__house?.world?.settle?.(8);", TimeSpan.FromSeconds(10)); } catch (Exception) { }
                    await _panel.Host.SnapshotAsync(Path.Combine(folder, "panel.png"));
                    if (_island != null) await _island.Host.SnapshotAsync(Path.Combine(folder, "island.png"));
                    if (_widget != null) await _widget.Host.SnapshotAsync(Path.Combine(folder, "widget.png"));
                    if (_settingsWindow != null) await _settingsWindow.Host.SnapshotAsync(Path.Combine(folder, "settings.png"));
                    if (_editor != null) await _editor.Host.SnapshotAsync(Path.Combine(folder, "editor.png"));
                }
                catch (Exception e) { Log.Error("snapshot", e); }
            });
        }
    }

    private bool _quitting;

    /// <summary>
    /// Quits in two steps: closes every window first, then ends the message loop a moment later.
    /// WebView2 takes its windows down asynchronously; ending the loop at once leaves that half done
    /// and Windows stops the process from inside its messaging (0xC0000602).
    /// </summary>
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        StopAgentForQuit();
        _tray.Dispose();
        foreach (var form in Application.OpenForms.Cast<Form>().ToList()) form.Close();
        _panel.Hide();
        _island?.Hide();
        _widget?.Hide();
        var later = new System.Windows.Forms.Timer { Interval = 400 };
        later.Tick += (_, _) =>
        {
            later.Dispose();
            StopSync();
            Log.Write("House Control quit");
            // Everything worth keeping is on disk by now. Once a WebView2 has lived in a regular
            // window, Windows' own messaging fails while the process unloads (0xC0000602, in
            // CoreMessaging), so the process ends here instead of unwinding.
            TerminateProcess(GetCurrentProcess(), 0);
            ExitThread();
        };
        later.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    protected override void ExitThreadCore()
    {
        StopSync();
        _tray.Dispose();
        _hotKey.Dispose();
        _fullscreen.Dispose();
        _poll.Dispose();
        // Every window with a WebView in it closes while the message loop still runs (the editor,
        // Settings, Pair iPhone, AI Access, a phone's sign-in code…): one left to the end of the
        // process takes it down from inside Windows' messaging.
        foreach (var form in Application.OpenForms.Cast<Form>().ToList()) form.Close();
        _island?.Dispose();
        _widget?.Dispose();
        _panel.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Where to find House Control.</summary>
internal static class About
{
    public const string Developer = "0xProbability";
    public const string Source = "https://github.com/0xProbabillity/house-control-windows";
    public const string Issues = "https://github.com/0xProbabillity/house-control-windows/issues";
    public const string Releases = "https://github.com/0xProbabillity/house-control-windows/releases";
    public const string MacApp = "https://github.com/0xProbabillity/house-control";
}
