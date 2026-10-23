using Microsoft.Win32;
using static HouseControl.Native.Win32;

namespace HouseControl.Shell;

/// <summary>
/// Start with Windows: an entry under the user's Run key, the same place Task Manager's
/// Startup apps tab lists and can switch off.
/// </summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "House Control";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    /// <summary>"on", "off", or "disabled" (registered, but switched off in Task Manager or Settings → Apps → Startup).</summary>
    public static string State
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string) return "off";
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // The first byte is 2 (or 6) when enabled, 3 (or 7) when the user switched it off.
            if (approved?.GetValue(Name) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1) return "disabled";
            return "on";
        }
    }

    public static void Set(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            run.SetValue(Name, Command);
            // Turning it on here also clears a "disabled" left by Task Manager.
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (approved.GetValue(Name) is not null) approved.DeleteValue(Name, false);
        }
        else
        {
            run.DeleteValue(Name, false);
        }
    }

    /// <summary>Keeps the entry pointing at this copy when the app was moved or updated.</summary>
    public static void FollowMove()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            run.SetValue(Name, Command);
    }
}

/// <summary>A system-wide keyboard shortcut that opens the panel (Ctrl+Alt+H unless changed).</summary>
internal sealed class HotKey : NativeWindow, IDisposable
{
    private const int Id = 0x4843;
    private bool _registered;
    public event Action? Pressed;

    public HotKey() => CreateHandle(new CreateParams());

    /// <summary>Registers the shortcut; false when another app already has it.</summary>
    public bool Register(int modifiers, int key)
    {
        Unregister();
        const uint MOD_NOREPEAT = 0x4000;
        _registered = RegisterHotKey(Handle, Id, (uint)modifiers | MOD_NOREPEAT, (uint)key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    public static string Describe(int modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(((Keys)key) switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)key).ToString(),
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.Space => "Space",
            var k => k.ToString(),
        });
        return string.Join("+", parts);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) { Pressed?.Invoke(); return; }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}

/// <summary>
/// Notices when a full-screen app — a game, a video, a presentation — comes to the front, so the
/// island can step aside, the way notifications do.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private readonly WinEventProc _callback;
    private readonly IntPtr _hook;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    public bool IsFullscreen { get; private set; }
    public event Action<bool>? Changed;

    public FullscreenWatcher()
    {
        _callback = (_, _, _, _, _, _, _) => Check();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        // A window can also go full screen without changing the foreground (F11 in a browser).
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public void Check()
    {
        var now = Detect();
        if (now == IsFullscreen) return;
        IsFullscreen = now;
        Changed?.Invoke(now);
    }

    private static bool Detect()
    {
        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;
        // Borderless full-screen windows that Windows does not report: one covering its whole monitor.
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow() || window == GetDesktopWindow()) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId) return false;
        if (!GetWindowRect(window, out var rect)) return false;
        var monitor = MonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)).rcMonitor;
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
