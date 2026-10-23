using System.Drawing;
using System.Text.Json.Nodes;
using HouseControl.Web;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>
/// The panel: a flyout above the taskbar, like the ones Windows opens from the clock or the
/// network icon. It opens beside the tray icon (or where the taskbar button was clicked), slides
/// in from the taskbar, and goes away as soon as anything else is clicked.
/// </summary>
internal sealed class PanelWindow : OverlayWindow
{
    // Must match `html.windows #app` in style.css: the card and the transparent margin its shadow uses.
    private const int CardWidth = 440, CardHeight = 780, Pad = 14, Gap = 10;

    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 130 };
    private DateTime _lastHide = DateTime.MinValue;
    /// <summary>Keeps the panel open when it loses focus (HOUSE_PIN=1, for UI automation and screenshots).</summary>
    private readonly bool _pinned = Environment.GetEnvironmentVariable("HOUSE_PIN") is { Length: > 0 };

    public event Action<bool>? VisibilityChanged;
    public bool IsOpen { get; private set; }

    public PanelWindow(WebHost host) : base(host, WS_EX_TOPMOST)
    {
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (IsOpen) return;
            HideWindowCore();
            Host.Call("hidden");
        };
    }

    /// <summary>
    /// Opens the panel, or closes it when it is open. <paramref name="anchor"/> is the point it
    /// should sit beside: the tray icon, or the cursor for a taskbar button; null for the default
    /// corner by the clock.
    /// </summary>
    public void Toggle(Point? anchor)
    {
        if (IsOpen) { Hide(); return; }
        // The click that dismissed the panel (focus moving to the taskbar) must not reopen it.
        if ((DateTime.Now - _lastHide).TotalMilliseconds < 300) return;
        Show(anchor);
    }

    public void Show(Point? anchor)
    {
        _hideTimer.Stop();
        GetCursorPos(out var cursor);
        var screen = Placement.ScreenAt(anchor ?? new Point(cursor.X, cursor.Y));
        var edge = Placement.TaskbarEdge(screen);
        var s = screen.Scale;
        int Px(double dip) => (int)Math.Round(dip * s);

        var work = screen.Work;
        var width = Px(CardWidth + 2 * Pad);
        var height = Math.Min(Px(CardHeight + 2 * Pad), work.Height + Px(2 * Pad) - Px(2 * Gap));
        var gap = Px(Gap) - Px(Pad); // the card keeps Gap from the work area's edges; the shadow may reach past it
        var spot = anchor ?? new Point(work.Right, work.Bottom);
        int x, y;
        switch (edge)
        {
            case Placement.Edge.Top:
                x = anchor is null ? work.Right - width - gap : spot.X - width / 2;
                y = work.Top + gap;
                break;
            case Placement.Edge.Left:
                x = work.Left + gap;
                y = anchor is null ? work.Bottom - height - gap : spot.Y - height + Px(Pad + 40);
                break;
            case Placement.Edge.Right:
                x = work.Right - width - gap;
                y = anchor is null ? work.Bottom - height - gap : spot.Y - height + Px(Pad + 40);
                break;
            default:
                x = anchor is null ? work.Right - width - gap : spot.X - width / 2;
                y = work.Bottom - height - gap;
                break;
        }
        x = Math.Clamp(x, work.Left + gap, work.Right - width - gap);
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        SetBounds(new Rectangle(x, y, width, height));

        IsOpen = true;
        Host.Call("placement", edge.Name());
        Host.Call("shown");
        ShowWindowCore(activate: true);
        VisibilityChanged?.Invoke(true);
    }

    public void Hide()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _lastHide = DateTime.Now;
        Host.Call("leave");
        _hideTimer.Stop();
        _hideTimer.Start();
        VisibilityChanged?.Invoke(false);
    }

    protected override void OnActivated(bool active)
    {
        if (!active && !_pinned) Hide();
    }

    protected override void WndProc(ref Message m)
    {
        // Esc from anywhere in the panel closes it (the page asks for it with "close" too).
        if (m.Msg == WM_KEYDOWN && (int)m.WParam == 0x1B) { Hide(); return; }
        base.WndProc(ref m);
    }

    public JsonObject Geometry => new() { ["card"] = new JsonArray(CardWidth, CardHeight), ["pad"] = Pad };
}
