using System.Drawing;
using System.Runtime.InteropServices;
using HouseControl.Native;
using HouseControl.Web;
using Microsoft.Web.WebView2.Core;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>
/// A borderless, fully transparent window whose page is drawn by WebView2 straight into a
/// DirectComposition visual: the web content draws the card, its rounded corners and its
/// shadow itself, and whatever it leaves transparent shows the desktop through. Mouse input is
/// handed to the page here; the keyboard goes to it directly once it has focus.
/// </summary>
internal abstract class OverlayWindow : NativeWindow, IDisposable
{
    private Composition? _composition;
    private CoreWebView2CompositionController? _controller;
    private bool _trackingLeave;
    private IntPtr _cursor;
    private bool _clickThrough;

    public WebHost Host { get; }
    public bool IsShown { get; private set; }
    protected CoreWebView2CompositionController? Controller => _controller;

    /// <summary>The window's scale: 1.0 at 96 DPI (100 %), 1.5 at 150 %â€¦</summary>
    public double Scale => Handle == IntPtr.Zero ? 1 : GetDpiForWindow(Handle) / 96.0;

    protected OverlayWindow(WebHost host, int exStyle)
    {
        Host = host;
        CreateHandle(new CreateParams
        {
            Caption = "House Control",
            Style = WS_POPUP,
            ExStyle = exStyle | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW,
            X = -32000,
            Y = -32000,
            Width = 1,
            Height = 1,
        });
    }

    /// <summary>Creates the WebView2 inside and loads the page.</summary>
    public async Task InitializeAsync()
    {
        var environment = await WebHost.EnvironmentAsync();
        _controller = await environment.CreateCoreWebView2CompositionControllerAsync(Handle);
        _composition = new Composition(Handle);
        _controller.RootVisualTarget = _composition.Visual;
        _composition.Commit();
        _controller.DefaultBackgroundColor = Color.Transparent;
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.RasterizationScale = Scale;
        _controller.IsVisible = IsShown; // the window may have been shown while this was being created
        _controller.CursorChanged += (_, _) =>
        {
            _cursor = _controller.Cursor;
            if (_trackingLeave) SetCursor(_cursor);
        };
        UpdateBounds();
        Host.Attach(_controller.CoreWebView2);
        Host.Load();
    }

    protected void SetBounds(Rectangle pixels)
    {
        SetWindowPos(Handle, IntPtr.Zero, pixels.X, pixels.Y, pixels.Width, pixels.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateBounds();
    }

    public Rectangle Bounds
    {
        get
        {
            GetWindowRect(Handle, out var r);
            return new Rectangle(r.Left, r.Top, r.Width, r.Height);
        }
    }

    private void UpdateBounds()
    {
        if (_controller is null) return;
        GetClientRect(Handle, out var r);
        _controller.Bounds = new Rectangle(0, 0, Math.Max(1, r.Width), Math.Max(1, r.Height));
    }

    protected void ShowWindowCore(bool activate)
    {
        IsShown = true;
        if (_controller != null) _controller.IsVisible = true;
        ShowWindow(Handle, activate ? SW_SHOW : SW_SHOWNA);
        if (activate)
        {
            SetForegroundWindow(Handle);
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    protected void HideWindowCore()
    {
        IsShown = false;
        ShowWindow(Handle, SW_HIDE);
        // A hidden WebView stops drawing and slows its timers: nothing is spent on what nobody sees.
        if (_controller != null) _controller.IsVisible = false;
    }

    /// <summary>
    /// Lets clicks fall through to whatever is underneath (the island at rest, which is shown
    /// but must never be in the way).
    /// </summary>
    protected void SetClickThrough(bool on)
    {
        if (on == _clickThrough || Handle == IntPtr.Zero) return;
        _clickThrough = on;
        var style = (long)GetWindowLongPtr(Handle, GWL_EXSTYLE);
        style = on ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~(long)(WS_EX_TRANSPARENT | WS_EX_LAYERED);
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style));
        if (on) SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    private const uint LWA_ALPHA = 0x2;

    protected virtual void OnDpiChanged() { }
    protected virtual void OnActivated(bool active) { }
    protected virtual bool ActivatesOnClick => true;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEMOVE:
                if (!_trackingLeave)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _trackingLeave = TrackMouseEvent(ref track);
                }
                Forward(CoreWebView2MouseEventKind.Move, m.WParam, m.LParam, 0);
                return;
            case WM_MOUSELEAVE:
                _trackingLeave = false;
                _controller?.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
                return;
            case WM_LBUTTONDOWN: Press(CoreWebView2MouseEventKind.LeftButtonDown, ref m); return;
            case WM_LBUTTONUP: Release(CoreWebView2MouseEventKind.LeftButtonUp, ref m); return;
            case WM_LBUTTONDBLCLK: Press(CoreWebView2MouseEventKind.LeftButtonDoubleClick, ref m); return;
            case WM_RBUTTONDOWN: Press(CoreWebView2MouseEventKind.RightButtonDown, ref m); return;
            case WM_RBUTTONUP: Release(CoreWebView2MouseEventKind.RightButtonUp, ref m); return;
            case WM_RBUTTONDBLCLK: Press(CoreWebView2MouseEventKind.RightButtonDoubleClick, ref m); return;
            case WM_MBUTTONDOWN: Press(CoreWebView2MouseEventKind.MiddleButtonDown, ref m); return;
            case WM_MBUTTONUP: Release(CoreWebView2MouseEventKind.MiddleButtonUp, ref m); return;
            case WM_MBUTTONDBLCLK: Press(CoreWebView2MouseEventKind.MiddleButtonDoubleClick, ref m); return;
            case WM_XBUTTONDOWN: Press(CoreWebView2MouseEventKind.XButtonDown, ref m, (uint)HiWord(m.WParam)); return;
            case WM_XBUTTONUP: Release(CoreWebView2MouseEventKind.XButtonUp, ref m, (uint)HiWord(m.WParam)); return;
            case WM_XBUTTONDBLCLK: Press(CoreWebView2MouseEventKind.XButtonDoubleClick, ref m, (uint)HiWord(m.WParam)); return;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                // Wheel messages carry screen coordinates.
                var p = new POINT(LoWord(m.LParam), HiWord(m.LParam));
                ScreenToClient(Handle, ref p);
                var kind = m.Msg == WM_MOUSEWHEEL ? CoreWebView2MouseEventKind.Wheel : CoreWebView2MouseEventKind.HorizontalWheel;
                _controller?.SendMouseInput(kind, (CoreWebView2MouseEventVirtualKeys)(LoWord(m.WParam) & 0xFFFF),
                    unchecked((uint)HiWord(m.WParam)), new Point(p.X, p.Y));
                return;
            }
            case WM_SETCURSOR:
                if (LoWord(m.LParam) == HTCLIENT && _cursor != IntPtr.Zero)
                {
                    SetCursor(_cursor);
                    m.Result = new IntPtr(1);
                    return;
                }
                break;
            case WM_MOUSEACTIVATE:
                if (!ActivatesOnClick)
                {
                    m.Result = new IntPtr(MA_NOACTIVATE);
                    return;
                }
                break;
            case WM_ACTIVATE:
                var active = LoWord(m.WParam) != WA_INACTIVE;
                if (active) _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                OnActivated(active);
                break;
            case WM_SIZE:
                UpdateBounds();
                break;
            case WM_DPICHANGED:
                if (_controller != null) _controller.RasterizationScale = HiWord(m.WParam) / 96.0;
                OnDpiChanged();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void Forward(CoreWebView2MouseEventKind kind, IntPtr wParam, IntPtr lParam, uint data) =>
        _controller?.SendMouseInput(kind, (CoreWebView2MouseEventVirtualKeys)(LoWord(wParam) & 0xFFFF), data,
            new Point(LoWord(lParam), HiWord(lParam)));

    private void Press(CoreWebView2MouseEventKind kind, ref Message m, uint data = 0)
    {
        // Keep receiving the pointer while a button is held, so drags can leave the window.
        if (GetCapture() != Handle) SetCapture(Handle);
        Forward(kind, m.WParam, m.LParam, data);
    }

    private void Release(CoreWebView2MouseEventKind kind, ref Message m, uint data = 0)
    {
        Forward(kind, m.WParam, m.LParam, data);
        const int buttons = 0x0001 | 0x0002 | 0x0010 | 0x0020 | 0x0040; // MK_LBUTTON | RBUTTON | MBUTTON | XBUTTON1 | XBUTTON2
        if ((LoWord(m.WParam) & buttons) == 0 && GetCapture() == Handle) ReleaseCapture();
    }

    /// <summary>Starts moving the window with the pointer, as if its title bar were dragged.</summary>
    protected void BeginDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    public virtual void Dispose()
    {
        _controller?.Close();
        _composition?.Dispose();
        DestroyHandle();
    }
}
