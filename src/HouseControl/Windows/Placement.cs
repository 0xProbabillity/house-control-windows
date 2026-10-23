using System.Drawing;
using HouseControl.Native;
using static HouseControl.Native.Win32;

namespace HouseControl.Windows;

/// <summary>Where the taskbar is, and where things go relative to it, in physical pixels.</summary>
internal static class Placement
{
    public enum Edge { Bottom, Top, Left, Right }

    /// <summary>The monitor under a point: its full rectangle, work area (without the taskbar) and scale.</summary>
    public readonly record struct Screen(IntPtr Handle, Rectangle Bounds, Rectangle Work, double Scale, string Device, bool Primary);

    public static Screen ScreenAt(Point p) => Describe(MonitorFromPoint(new POINT(p.X, p.Y), MONITOR_DEFAULTTONEAREST));

    public static Screen Primary => Describe(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY));

    public static IReadOnlyList<Screen> All => Monitors().Select(Describe).ToList();

    public static Screen Describe(IntPtr monitor)
    {
        var info = MonitorInfo(monitor);
        static Rectangle R(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return new Screen(monitor, R(info.rcMonitor), R(info.rcWork), MonitorDpi(monitor) / 96.0, info.szDevice, (info.dwFlags & 1) != 0);
    }

    /// <summary>The edge of the screen the taskbar is on, for the monitor that has it.</summary>
    public static Edge TaskbarEdge(Screen screen)
    {
        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
        {
            var bar = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            if (bar.IntersectsWith(screen.Bounds))
            {
                return data.uEdge switch
                {
                    ABE_TOP => Edge.Top,
                    ABE_LEFT => Edge.Left,
                    ABE_RIGHT => Edge.Right,
                    _ => Edge.Bottom,
                };
            }
        }
        // A secondary display without a taskbar, or one that auto-hides: guess from the work area.
        if (screen.Work.Top > screen.Bounds.Top) return Edge.Top;
        if (screen.Work.Left > screen.Bounds.Left) return Edge.Left;
        if (screen.Work.Right < screen.Bounds.Right) return Edge.Right;
        return Edge.Bottom;
    }

    public static string Name(this Edge edge) => edge.ToString().ToLowerInvariant();
}
