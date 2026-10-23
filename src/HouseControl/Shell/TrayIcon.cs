using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using HouseControl.Native;
using static HouseControl.Native.Win32;

namespace HouseControl.Shell;

/// <summary>
/// The house in the notification area. Left click opens the panel, right click the menu. The
/// icon is drawn for the taskbar's own theme and size: a house outline when everything is off,
/// lit warm when lights are on, with how many in a badge.
/// </summary>
internal sealed class TrayIcon : NativeWindow, IDisposable
{
    private const int CallbackMessage = WM_APP + 1;
    private const uint Id = 1;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private IntPtr _icon;
    private string _tip = "House Control";
    private (int Lights, bool Ac, bool Badge, bool Agent) _drawn = (-1, false, false, false);
    private bool _added;

    /// <summary>Left click (or Enter / Space on the focused icon); the point is the icon's centre.</summary>
    public event Action<Point>? Clicked;
    /// <summary>Right click or the menu key; the point is where the menu should open.</summary>
    public event Action<Point>? MenuRequested;

    public TrayIcon()
    {
        CreateHandle(new CreateParams { Caption = "House Control Tray" });
    }

    public void Show(int lightsOn, bool acOn, bool badge, bool agent)
    {
        Redraw(lightsOn, acOn, badge, agent);
        Add();
    }

    /// <summary>Updates the picture and the tooltip; skips the redraw when nothing visible changed.</summary>
    public void Update(int lightsOn, bool acOn, bool badge, bool agent, string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (_drawn != (lightsOn, acOn, badge, agent)) Redraw(lightsOn, acOn, badge, agent);
        Modify();
    }

    private void Redraw(int lightsOn, bool acOn, bool badge, bool agent)
    {
        _drawn = (lightsOn, acOn, badge, agent);
        var old = _icon;
        _icon = Draw(lightsOn, acOn, badge, agent, GetDpiForWindow(Handle) is var dpi and > 0 ? dpi : 96);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>The icon's rectangle on screen (to open the panel right above it).</summary>
    public Rectangle? Bounds
    {
        get
        {
            var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = Id };
            return Shell_NotifyIconGetRect(ref id, out var r) == 0 ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;
        }
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = Id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        uVersionOrTimeout = 4, // NOTIFYICON_VERSION_4
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added) Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void Modify()
    {
        if (!_added) { Add(); return; }
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == CallbackMessage)
        {
            var anchor = new Point(LoWord(m.WParam), HiWord(m.WParam));
            switch (LoWord(m.LParam))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    var icon = Bounds;
                    Clicked?.Invoke(icon is { } r ? new Point(r.Left + r.Width / 2, r.Top + r.Height / 2) : anchor);
                    break;
                case WM_CONTEXTMENU:
                    MenuRequested?.Invoke(anchor);
                    break;
            }
            return;
        }
        if (m.Msg == (int)TaskbarCreated)
        {
            // Explorer restarted: the icon has to be added again.
            _added = false;
            Add();
            return;
        }
        if (m.Msg is WM_DPICHANGED or WM_SETTINGCHANGE or WM_DISPLAYCHANGE)
        {
            Redraw(_drawn.Lights, _drawn.Ac, _drawn.Badge, _drawn.Agent);
            Modify();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyHandle();
    }

    /// <summary>Draws the house for the current taskbar theme at the size the notification area uses.</summary>
    internal static IntPtr Draw(int lightsOn, bool acOn, bool badge, bool agent, uint dpi)
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        var light = SystemUsesLightTheme;
        var ink = light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 247, 250);
        var warm = Color.FromArgb(255, 180, 107);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var u = size / 16f;
            var on = lightsOn > 0 || acOn;
            // Roof and walls on a 16-unit grid.
            using var house = new GraphicsPath();
            house.AddLines(new[]
            {
                new PointF(8 * u, 1.6f * u), new PointF(15 * u, 7.6f * u), new PointF(13 * u, 7.6f * u),
                new PointF(13 * u, 14.4f * u), new PointF(3 * u, 14.4f * u), new PointF(3 * u, 7.6f * u),
                new PointF(1 * u, 7.6f * u),
            });
            house.CloseFigure();
            if (on)
            {
                using var glow = new SolidBrush(warm);
                g.FillPath(glow, house);
                using var door = new SolidBrush(Color.FromArgb(120, 62, 22));
                g.FillRectangle(door, 6.6f * u, 9.6f * u, 2.8f * u, 4.8f * u);
            }
            else
            {
                using var pen = new Pen(ink, Math.Max(1f, 1.35f * u)) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, house);
            }
            if (agent)
            {
                // AI Access is live: a small sparkle on the roof's right.
                using var spark = new SolidBrush(Color.FromArgb(195, 178, 255));
                g.FillEllipse(spark, 12.2f * u, 0.4f * u, 3.4f * u, 3.4f * u);
            }
            if (badge && lightsOn > 0)
            {
                var text = lightsOn > 9 ? "9+" : lightsOn.ToString();
                var d = (text.Length > 1 ? 10.5f : 8.4f) * u;
                var box = new RectangleF(size - d, size - 8.4f * u, d, 8.4f * u);
                using var fill = new SolidBrush(light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 247, 250));
                using var pill = Pill(box);
                g.FillPath(fill, pill);
                using var font = new Font("Segoe UI", 6.4f * u, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(light ? Color.White : Color.FromArgb(20, 20, 24));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, textBrush, new RectangleF(box.X, box.Y + 0.3f * u, box.Width, box.Height), format);
            }
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    // Shell_NotifyIcon
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
}

/// <summary>The app's icon, from the executable's resources.</summary>
internal static class AppIcon
{
    private static Icon? _large;
    public static Icon Large => _large ??= Load(256);

    private static Icon Load(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("HouseControl.ico")!;
        return new Icon(stream, size, size);
    }
}
