using System.Runtime.InteropServices;
using System.Text;
using static HouseControl.Native.Win32;

namespace HouseControl.Shell;

/// <summary>
/// One House Control per user. Starting it again — from the pinned taskbar button, the Start
/// menu, a jump list entry or a shortcut — hands its arguments to the copy that is running,
/// which opens the panel or runs the scene, and the new process ends.
/// </summary>
internal sealed class SingleInstance : NativeWindow, IDisposable
{
    /// <summary>
    /// A copy with its own data folder (HOUSE_DATA_DIR: tests, screenshots) is an instance of its own,
    /// so it never hands its arguments — or a --quit — to the copy you use every day.
    /// </summary>
    private static readonly string Scope = Environment.GetEnvironmentVariable("HOUSE_DATA_DIR") is { Length: > 0 } dir
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant())))[..12]
        : "";
    private static readonly string WindowTitle = "HouseControl.Instance.7f3c" + Scope;
    private const int CopyDataTag = 0x48434C31; // "HCL1"
    private readonly Mutex _mutex;

    public bool IsFirst { get; }
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(true, $@"Local\HouseControl.{Environment.UserName}{Scope}", out var created);
        IsFirst = created;
        if (IsFirst) CreateHandle(new CreateParams { Caption = WindowTitle });
    }

    /// <summary>Gives the running copy the arguments; false when it could not be reached.</summary>
    public static bool Forward(string[] args)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var target = FindWindow(null, WindowTitle);
            if (target != IntPtr.Zero)
            {
                // The running copy may take the foreground: this process has it, being just started by the user.
                GetWindowThreadProcessId(target, out var pid);
                AllowSetForegroundWindow(pid);
                var text = string.Join("\u001f", args);
                var bytes = Encoding.UTF8.GetBytes(text);
                var buffer = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
                try
                {
                    Marshal.Copy(bytes, 0, buffer, bytes.Length);
                    var data = new COPYDATASTRUCT { dwData = new IntPtr(CopyDataTag), cbData = bytes.Length, lpData = buffer };
                    SendMessage(target, WM_COPYDATA, IntPtr.Zero, ref data);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            Thread.Sleep(150); // the first copy may still be starting
        }
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_COPYDATA)
        {
            var data = Marshal.PtrToStructure<COPYDATASTRUCT>(m.LParam);
            if (data.dwData == new IntPtr(CopyDataTag))
            {
                var bytes = new byte[data.cbData];
                if (data.cbData > 0) Marshal.Copy(data.lpData, bytes, 0, data.cbData);
                var text = Encoding.UTF8.GetString(bytes);
                var args = text.Length == 0 ? [] : text.Split('\u001f');
                // Answer the sender first; run the request once this message is done.
                var context = SynchronizationContext.Current;
                if (context != null) context.Post(_ => ArgumentsReceived?.Invoke(args), null);
                else ArgumentsReceived?.Invoke(args);
                m.Result = new IntPtr(1);
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (IsFirst)
        {
            DestroyHandle();
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
