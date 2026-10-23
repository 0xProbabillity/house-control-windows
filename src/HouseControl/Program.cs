using HouseControl.Native;
using HouseControl.Shell;

namespace HouseControl;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // `--selftest-mapper`, `--selftest-infrared`, `--selftest-transport` and `--diagnose <id>…`
        // print their result and exit without starting the app.
        if (SelfTests.Run(args) is { } code) return code;

        // One identity for the process, its pinned taskbar button, its Start menu entry and its jump list.
        Win32.SetCurrentProcessExplicitAppUserModelID(JumpList.AppId);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            SingleInstance.Forward(args);
            return 0;
        }
        // The installer asks a running copy to quit; with none running there is nothing to do.
        if (args.FirstOrDefault() == "--quit") return 0;

        // Everything awaited from here on comes back to this (STA, UI) thread: WebView2 and the
        // device clients count on it. No Control exists yet to install it by itself.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Win32.FollowSystemThemeInMenus();
        Application.ThreadException += (_, e) => Log.Error("unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        if (!WebView2Runtime.IsInstalled())
        {
            WebView2Runtime.Explain();
            return 1;
        }
        Application.Run(new App(instance, args));
        return 0;
    }
}

/// <summary>The Microsoft Edge WebView2 Runtime draws every view; Windows 10 and 11 normally have it.</summary>
internal static class WebView2Runtime
{
    public static bool IsInstalled()
    {
        try { return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    public static void Explain()
    {
        var answer = MessageBox.Show(
            "House Control needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and recent Windows 10.\n\n" +
            "Open Microsoft's download page now?",
            "House Control", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) Web.WebHost.OpenExternally("https://developer.microsoft.com/microsoft-edge/webview2/");
    }
}
