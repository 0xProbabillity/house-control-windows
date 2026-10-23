using System.Drawing;
using System.Text;
using HouseControl.Web;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>
/// The home on the desktop: the open island as a card that lives under every window, like a
/// desktop gadget. Drag it by its title to move it; it remembers where it was put.
///
/// It stays below every app; when the desktop itself comes to the front (Show desktop, Win+D,
/// or a click on the wallpaper) it is lifted just above it, so it is there whenever the desktop is.
/// </summary>
internal sealed class DesktopWidget : OverlayWindow
{
    // Must match `.isle.desk` in island.css.
    private const int Width = 664, Height = 322, Pad = 28;

    private readonly AppSettings _settings;
    private readonly WinEventProc _foregroundChanged;
    private IntPtr _hook;
    /// <summary>True while the desktop is in front and the widget sits above it.</summary>
    private bool _overDesktop;
    public event Action? Moved;

    public DesktopWidget(WebHost host, AppSettings settings) : base(host, WS_EX_NOACTIVATE)
    {
        _settings = settings;
        _foregroundChanged = (_, _, window, _, _, _, _) => ForegroundChanged(window);
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (_overDesktop && IsDesktop(GetForegroundWindow())) RiseAboveDesktop();
        };
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
        if (_hook == IntPtr.Zero)
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundChanged, 0, 0, WINEVENT_OUTOFCONTEXT);
        ForegroundChanged(GetForegroundWindow());
    }

    public void Hide()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        HideWindowCore();
    }

    private void ForegroundChanged(IntPtr window)
    {
        if (!IsShown || window == Handle) return;
        _overDesktop = IsDesktop(window);
        if (Environment.GetEnvironmentVariable("HOUSE_DEBUG_WIDGET") is { Length: > 0 })
        {
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            Log.Write($"widget: foreground {name} → {(_overDesktop ? "over desktop" : "bottom")}");
        }
        if (_overDesktop)
        {
            // Show desktop brings the desktop forward a moment after it becomes the foreground
            // window: rise above it now and once more when it has settled.
            RiseAboveDesktop();
            _settle.Stop();
            _settle.Start();
        }
        else
        {
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 350 };

    private void RiseAboveDesktop()
    {
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>The desktop: Progman, or the WorkerW that draws a slideshow or Spotlight wallpaper.</summary>
    private static bool IsDesktop(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var name = new StringBuilder(32);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }

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
            case WM_WINDOWPOSCHANGING when !_overDesktop:
            {
                // Under every app, however something tried to bring it forward.
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

    public override void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        base.Dispose();
    }
}
