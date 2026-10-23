using System.Drawing;
using HouseControl.Web;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>
/// The home on the desktop: the open island as a card that lives under every window, like a
/// desktop gadget. Drag it by its title to move it; it remembers where it was put.
/// </summary>
internal sealed class DesktopWidget : OverlayWindow
{
    // Must match `.isle.desk` in island.css.
    private const int Width = 664, Height = 322, Pad = 28;

    private readonly AppSettings _settings;
    public event Action? Moved;

    public DesktopWidget(WebHost host, AppSettings settings) : base(host, WS_EX_NOACTIVATE)
    {
        _settings = settings;
        Host.Message += (_, message) =>
        {
            if (message["type"]?.GetValue<string>() == "widget.drag" && !_settings.WidgetLocked) BeginDrag();
        };
    }

    protected override bool ActivatesOnClick => false;

    public void Show()
    {
        Place();
        ShowWindowCore(activate: false);
        SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Hide() => HideWindowCore();

    /// <summary>Where it was left, if that is still on a screen; otherwise the top right of the primary display.</summary>
    public void Place()
    {
        var saved = _settings.WidgetPosition;
        var screen = saved is { } at ? Placement.ScreenAt(at) : Placement.Primary;
        var s = screen.Scale;
        int Px(double dip) => (int)Math.Round(dip * s);
        var size = new Size(Px(Width + 2 * Pad), Px(Height + 2 * Pad));
        var origin = saved is { } p && screen.Work.Contains(p)
            ? p
            : new Point(screen.Work.Right - size.Width - Px(8), screen.Work.Top + Px(8));
        SetBounds(new Rectangle(origin, size));
    }

    public void ResetPosition()
    {
        _settings.WidgetPosition = null;
        _settings.Save();
        if (IsShown) Show();
    }

    protected override void OnDpiChanged() => Place();

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_WINDOWPOSCHANGING:
            {
                // Stays under every other window, however it was brought forward.
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                if ((pos.flags & SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, m.LParam, false);
                }
                break;
            }
            case WM_EXITSIZEMOVE:
                _settings.WidgetPosition = Bounds.Location;
                _settings.Save();
                Moved?.Invoke();
                break;
        }
        base.WndProc(ref m);
    }
}
