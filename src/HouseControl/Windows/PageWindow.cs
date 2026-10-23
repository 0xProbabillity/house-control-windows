using System.Drawing;
using HouseControl.Native;
using HouseControl.Shell;
using HouseControl.Web;
using Microsoft.Web.WebView2.WinForms;

namespace HouseControl.Windows;

/// <summary>
/// A regular window around one page of the web UI: the home editor, Settings and AI Access.
/// Dark title bar, and on Windows 11 its rounded corners; remembers its size and place.
/// </summary>
internal sealed class PageWindow : Form
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly string? _placementKey;
    private readonly AppSettingsStore _store;
    public WebHost Host { get; }

    public PageWindow(WebHost host, string title, Size size, Size minimum, AppSettingsStore store, string? placementKey)
    {
        Host = host;
        _store = store;
        _placementKey = placementKey;
        Text = title;
        Icon = AppIcon.Large;
        BackColor = Color.FromArgb(10, 14, 26);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = LogicalToDeviceUnits(size);
        MinimumSize = LogicalToDeviceUnits(minimum);
        KeyPreview = true;
        _view.DefaultBackgroundColor = BackColor;
        Controls.Add(_view);
        if (placementKey != null && store.Placement(placementKey) is { } saved && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.StyleWindow(Handle, mica: false);
    }

    public async Task InitializeAsync()
    {
        await _view.EnsureCoreWebView2Async(await WebHost.EnvironmentAsync());
        Host.Attach(_view.CoreWebView2);
        Host.Load();
    }

    /// <summary>Brings the window to the front, restoring it if it was minimised.</summary>
    public void Present()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Win32.SetForegroundWindow(Handle);
        _view.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_placementKey != null && WindowState == FormWindowState.Normal) _store.SetPlacement(_placementKey, Bounds);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _view.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Where the regular windows were last left (<c>windows.json</c>).</summary>
internal sealed class AppSettingsStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "windows.json");

    public Rectangle? Placement(string key)
    {
        var json = TryRead();
        return json?[key] is System.Text.Json.Nodes.JsonArray { Count: 4 } a
            ? new Rectangle((int)a[0]!, (int)a[1]!, (int)a[2]!, (int)a[3]!)
            : null;
    }

    public void SetPlacement(string key, Rectangle r)
    {
        var json = TryRead() ?? new System.Text.Json.Nodes.JsonObject();
        json[key] = new System.Text.Json.Nodes.JsonArray(r.X, r.Y, r.Width, r.Height);
        try { Core.Files.WriteAtomic(_path, System.Text.Encoding.UTF8.GetBytes(Core.Json.Serialize(json))); }
        catch (IOException) { }
    }

    private System.Text.Json.Nodes.JsonObject? TryRead()
    {
        try { return File.Exists(_path) ? Core.Json.ParseObject(File.ReadAllBytes(_path)) : null; }
        catch (IOException) { return null; }
    }
}
