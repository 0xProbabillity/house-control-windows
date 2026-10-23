using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core;

namespace HouseControl;

/// <summary>
/// The shell's own preferences (<c>app-settings.json</c> next to the plan): the island, the desktop
/// widget, the keyboard shortcut. Which plan is active lives in the plan store's settings.json.
/// </summary>
internal sealed class AppSettings
{
    private readonly string _path;

    public bool IslandEnabled { get; set; } = true;
    /// <summary>"tab" (a small black tab with what is on) or "slim" (a thin line until the pointer comes).</summary>
    public string IslandRest { get; set; } = "tab";
    /// <summary>How long the pointer rests on the island before it opens, in milliseconds.</summary>
    public int IslandDelay { get; set; } = 220;
    /// <summary>Peek out when something changes elsewhere.</summary>
    public bool IslandPeek { get; set; } = true;
    /// <summary>Step aside while a full-screen app, a game or a presentation is in front.</summary>
    public bool IslandHideInFullscreen { get; set; } = true;
    /// <summary>"compact" (as wide as what it shows), "medium" or "wide" (the width of a MacBook's notch).</summary>
    public string IslandWidth { get; set; } = "compact";
    /// <summary>How opaque the tab's black is at rest and while peeking, in percent (20…100).</summary>
    public int IslandOpacity { get; set; } = 80;
    /// <summary>The display's device name ("\\.\DISPLAY2"); empty for the primary display.</summary>
    public string IslandDisplay { get; set; } = "";

    public bool WidgetEnabled { get; set; }
    public bool WidgetLocked { get; set; }
    public Point? WidgetPosition { get; set; }

    public bool HotkeyEnabled { get; set; } = true;
    /// <summary>MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8.</summary>
    public int HotkeyModifiers { get; set; } = 2 | 1;
    /// <summary>A virtual-key code; 'H' by default.</summary>
    public int HotkeyKey { get; set; } = 'H';

    /// <summary>The tray icon carries how many lights are on.</summary>
    public bool TrayBadge { get; set; } = true;

    public AppSettings(string directory)
    {
        _path = Path.Combine(directory, "app-settings.json");
        Load();
    }

    private void Load()
    {
        JsonObject? json;
        try { json = File.Exists(_path) ? Json.ParseObject(File.ReadAllBytes(_path)) : null; }
        catch (IOException) { json = null; }
        if (json is null) return;
        IslandEnabled = json.Bool("islandEnabled") ?? IslandEnabled;
        IslandRest = json.Str("islandRest") is "slim" ? "slim" : "tab";
        IslandDelay = (int)(json.Num("islandDelay") ?? IslandDelay);
        IslandPeek = json.Bool("islandPeek") ?? IslandPeek;
        IslandHideInFullscreen = json.Bool("islandHideInFullscreen") ?? IslandHideInFullscreen;
        IslandDisplay = json.Str("islandDisplay") ?? "";
        IslandWidth = json.Str("islandWidth") is "medium" or "wide" ? json.Str("islandWidth")! : "compact";
        IslandOpacity = Math.Clamp((int)(json.Num("islandOpacity") ?? IslandOpacity), 20, 100);
        WidgetEnabled = json.Bool("widgetEnabled") ?? WidgetEnabled;
        WidgetLocked = json.Bool("widgetLocked") ?? WidgetLocked;
        if (json.Num("widgetX") is { } x && json.Num("widgetY") is { } y) WidgetPosition = new Point((int)x, (int)y);
        HotkeyEnabled = json.Bool("hotkeyEnabled") ?? HotkeyEnabled;
        HotkeyModifiers = (int)(json.Num("hotkeyModifiers") ?? HotkeyModifiers);
        HotkeyKey = (int)(json.Num("hotkeyKey") ?? HotkeyKey);
        TrayBadge = json.Bool("trayBadge") ?? TrayBadge;
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["islandEnabled"] = IslandEnabled,
            ["islandRest"] = IslandRest,
            ["islandDelay"] = IslandDelay,
            ["islandPeek"] = IslandPeek,
            ["islandHideInFullscreen"] = IslandHideInFullscreen,
            ["islandDisplay"] = IslandDisplay,
            ["islandWidth"] = IslandWidth,
            ["islandOpacity"] = IslandOpacity,
            ["widgetEnabled"] = WidgetEnabled,
            ["widgetLocked"] = WidgetLocked,
            ["hotkeyEnabled"] = HotkeyEnabled,
            ["hotkeyModifiers"] = HotkeyModifiers,
            ["hotkeyKey"] = HotkeyKey,
            ["trayBadge"] = TrayBadge,
        };
        if (WidgetPosition is { } p) { json["widgetX"] = p.X; json["widgetY"] = p.Y; }
        return json;
    }

    public void Save()
    {
        // A throw-away run (HOUSE_DATA_DIR) still saves: that folder is its own.
        try { Files.WriteAtomic(_path, Encoding.UTF8.GetBytes(Json.Serialize(ToJson(), pretty: true))); }
        catch (IOException e) { Log.Error("save settings", e); }
    }

    /// <summary>Applies one change from the Settings window. Returns false for an unknown key.</summary>
    public bool Set(string key, JsonNode? value)
    {
        bool B() => value?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        switch (key)
        {
            case "islandEnabled": IslandEnabled = B(); break;
            case "islandRest": IslandRest = value?.GetValue<string>() == "slim" ? "slim" : "tab"; break;
            case "islandDelay": IslandDelay = Math.Clamp((int)value!.GetValue<double>(), 40, 1500); break;
            case "islandPeek": IslandPeek = B(); break;
            case "islandHideInFullscreen": IslandHideInFullscreen = B(); break;
            case "islandDisplay": IslandDisplay = value?.GetValue<string>() ?? ""; break;
            case "islandWidth": IslandWidth = value?.GetValue<string>() is "medium" or "wide" ? value.GetValue<string>() : "compact"; break;
            case "islandOpacity": IslandOpacity = Math.Clamp((int)value!.GetValue<double>(), 20, 100); break;
            case "widgetEnabled": WidgetEnabled = B(); break;
            case "widgetLocked": WidgetLocked = B(); break;
            case "hotkeyEnabled": HotkeyEnabled = B(); break;
            case "trayBadge": TrayBadge = B(); break;
            default: return false;
        }
        Save();
        return true;
    }
}
