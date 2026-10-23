using System.Drawing;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Web;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>
/// The island: a small black tab hanging from the top edge of the screen. At rest it shows what
/// is on; when the pointer rests on it, it springs open into a live model of the home with
/// scenes and devices; when something changes elsewhere it peeks out to say so.
///
/// It never takes the keyboard from the app you are working in, lets every click through while
/// it is closed, and steps aside while a full-screen app, a game or a presentation is in front.
/// </summary>
internal sealed class IslandWindow : OverlayWindow
{
    // Must match island.css: the open island, and the transparent room around it for its shadow.
    private const int OpenWidth = 664, OpenHeight = 292, CanvasWidth = 820, CanvasHeight = 470;
    private const int TabWidth = 190, Ear = 64;

    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _pointer = new() { Interval = 40 };
    private Rectangle _tab;       // the tab at rest, in screen pixels
    private Placement.Screen _screen;
    private DateTime? _since;     // when the pointer entered (or left) the zone that changes the state
    private bool _suppressed;     // a full-screen app is in front

    public bool IsExpanded { get; private set; }
    public event Action<bool>? ExpandedChanged;

    public IslandWindow(WebHost host, AppSettings settings) : base(host, WS_EX_TOPMOST | WS_EX_NOACTIVATE)
    {
        _settings = settings;
        _pointer.Tick += (_, _) => PointerMoved();
    }

    protected override bool ActivatesOnClick => false;

    /// <summary>The tab's height: a title bar's, so it lines up with the windows under it.</summary>
    public int TabHeight => _settings.IslandRest == "slim" ? 6 : 30;

    /// <summary>What the web side needs to draw itself.</summary>
    public JsonObject BootInfo => new()
    {
        ["notchWidth"] = TabWidth,
        ["notchHeight"] = 30,
        ["hasNotch"] = false,
        ["rest"] = _settings.IslandRest,
        ["peek"] = _settings.IslandPeek,
        ["expanded"] = Environment.GetEnvironmentVariable("HOUSE_ISLAND_EXPANDED") is { Length: > 0 },
    };

    public void Show()
    {
        Place();
        if (!_suppressed) ShowWindowCore(activate: false);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetClickThrough(!IsExpanded);
        _pointer.Start();
        if (BootInfo.Bool("expanded") == true) SetExpanded(true);
    }

    public void Hide()
    {
        _pointer.Stop();
        SetExpanded(false);
        HideWindowCore();
    }

    /// <summary>A full-screen app came to the front, or left it.</summary>
    public void SetSuppressed(bool on)
    {
        if (on == _suppressed) return;
        _suppressed = on;
        if (!_pointer.Enabled) return; // switched off altogether
        if (on)
        {
            SetExpanded(false);
            HideWindowCore();
        }
        else
        {
            ShowWindowCore(activate: false);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>The display it lives on: the chosen one, or the primary display.</summary>
    private Placement.Screen ChosenScreen() =>
        Placement.All.FirstOrDefault(s => s.Device == _settings.IslandDisplay) is { Handle: not 0 } chosen ? chosen : Placement.Primary;

    /// <summary>Pins the window to the top centre of its display (below a taskbar that is up there).</summary>
    public void Place()
    {
        _screen = ChosenScreen();
        var s = _screen.Scale;
        int Px(double dip) => (int)Math.Round(dip * s);
        var top = Placement.TaskbarEdge(_screen) == Placement.Edge.Top ? _screen.Work.Top : _screen.Bounds.Top;
        var center = _screen.Bounds.Left + _screen.Bounds.Width / 2;
        SetBounds(new Rectangle(center - Px(CanvasWidth) / 2, top, Px(CanvasWidth), Px(CanvasHeight)));
        _tab = new Rectangle(center - Px(TabWidth) / 2, top, Px(TabWidth), Px(Math.Max(TabHeight, 6)));
    }

    protected override void OnDpiChanged() => Place();

    private void PointerMoved()
    {
        if (!IsShown || _suppressed || BootInfo.Bool("expanded") == true) return;
        GetCursorPos(out var p);
        var s = _screen.Scale;
        int Px(double dip) => (int)Math.Round(dip * s);
        bool inside;
        TimeSpan dwell;
        if (IsExpanded)
        {
            var open = new Rectangle(_tab.Left + _tab.Width / 2 - Px(OpenWidth) / 2 - Px(14), _tab.Top,
                Px(OpenWidth + 28), Px(30 + OpenHeight + 18));
            inside = open.Contains(p.X, p.Y);
            dwell = TimeSpan.FromMilliseconds(320);
            Track(!inside, dwell, () => SetExpanded(false));
        }
        else
        {
            // The tab and its ears; a pointer pushed against the top edge counts a little wider.
            var zone = Rectangle.Inflate(_tab, Px(Ear), 0);
            zone.Height = Math.Max(zone.Height, Px(8));
            inside = zone.Contains(p.X, p.Y);
            dwell = TimeSpan.FromMilliseconds(Math.Clamp(_settings.IslandDelay, 40, 1500));
            Track(inside, dwell, () => SetExpanded(true));
        }
    }

    /// <summary>Changes state once the pointer has stayed in (or out) long enough; brushing past does nothing.</summary>
    private void Track(bool wants, TimeSpan dwell, Action change)
    {
        if (!wants) { _since = null; return; }
        _since ??= DateTime.Now;
        if (DateTime.Now - _since.Value < dwell) return;
        // A button held down means a drag in another app passing by: wait until it is let go.
        if (!IsExpanded && (GetAsyncKeyState(0x01) & 0x8000) != 0) return;
        _since = null;
        change();
    }

    public void SetExpanded(bool on)
    {
        if (on == IsExpanded) return;
        IsExpanded = on;
        SetClickThrough(!on);
        Host.Call("islandState", on ? "expanded" : "collapsed");
        ExpandedChanged?.Invoke(on);
    }

    public override void Dispose()
    {
        _pointer.Dispose();
        base.Dispose();
    }
}
