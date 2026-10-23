using System.Text.Json.Nodes;
using HouseControl.Core;
using Microsoft.Web.WebView2.Core;

namespace HouseControl.Web;

/// <summary>
/// One view of the web UI ("panel", "island", "widget", "editor", "settings" or "agent") plus the
/// JSON message bridge in both directions. Whether it is drawn into a plain window or a
/// transparent composition window is up to whoever hosts it.
/// </summary>
internal sealed class WebHost
{
    /// <summary>The web UI's origin: a reserved name, served from the folder by WebView2 itself.</summary>
    public const string HostName = "housecontrol.example";
    public static readonly string Origin = $"https://{HostName}/";

    private static CoreWebView2Environment? _environment;
    private static Task<CoreWebView2Environment>? _creating;

    private readonly Func<JsonObject> _boot;
    private string? _bootScriptId;
    private int _nextEval;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _evals = new();
    private readonly Queue<string> _queued = new();
    private System.Windows.Forms.Timer? _evalTimer;

    public string Mode { get; }
    public CoreWebView2? Core { get; private set; }
    /// <summary>True once the page has started and reported "ready"; false again while it reloads.</summary>
    public bool IsReady { get; private set; }
    public event Action<WebHost, JsonObject>? Message;

    public WebHost(string mode, Func<JsonObject> boot)
    {
        Mode = mode;
        _boot = boot;
    }

    /// <summary>
    /// The one browser environment every view shares: one browser process for the whole app,
    /// with its profile in %LOCALAPPDATA%\HouseControl\WebView2.
    /// </summary>
    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (_environment != null) return Task.FromResult(_environment);
        return _creating ??= Create();

        static async Task<CoreWebView2Environment> Create()
        {
            var data = Environment.GetEnvironmentVariable("HOUSE_DATA_DIR") is { Length: > 0 } custom
                ? Path.Combine(Path.GetFullPath(custom), "WebView2")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HouseControl", "WebView2");
            var options = new CoreWebView2EnvironmentOptions
            {
                // Nothing here is a web page from the internet: no SmartScreen lookups, no
                // "translate this page", no autofill.
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection,Translate,AutofillServerCommunication",
            };
            _environment = await CoreWebView2Environment.CreateAsync(null, data, options);
            return _environment;
        }
    }

    /// <summary>Where the built web UI lives: next to the executable, or HOUSE_WEB_DIR.</summary>
    public static string WebDirectory =>
        Environment.GetEnvironmentVariable("HOUSE_WEB_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(AppContext.BaseDirectory, "web");

    public void Attach(CoreWebView2 core)
    {
        Core = core;
        var devTools = Environment.GetEnvironmentVariable("HOUSE_DEVTOOLS") is { Length: > 0 };
        var settings = core.Settings;
        settings.AreDevToolsEnabled = devTools;
        settings.AreDefaultContextMenusEnabled = devTools;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = devTools;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsBuiltInErrorPageEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, WebDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // The UI never navigates away; anything external opens in the default browser.
            if (IsOwn(e.Uri)) return;
            e.Cancel = true;
            OpenExternally(e.Uri);
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"web ({Mode}): {e.ProcessFailedKind} ({e.Reason}), reloading");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                Load();
        };
        if (Environment.GetEnvironmentVariable("HOUSE_EVAL_DIR") is { Length: > 0 } folder) WatchScripts(folder);
    }

    private static bool IsOwn(string uri) =>
        uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || (DevUrl is { } dev && uri.StartsWith(dev, StringComparison.OrdinalIgnoreCase));

    private static string? DevUrl => Environment.GetEnvironmentVariable("HOUSE_DEV_URL") is { Length: > 0 } url ? url : null;

    public static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Error("open link", e);
        }
    }

    /// <summary>(Re)loads the page with a fresh boot payload.</summary>
    public async void Load()
    {
        if (Core is not { } core) return;
        IsReady = false;
        try
        {
            if (_bootScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_bootScriptId);
            var boot = _boot();
            boot["mode"] = Mode;
            boot["platform"] = "windows";
            _bootScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__HOUSE_BOOT__ = {Json.Serialize(boot)};\n{ErrorReporter}");
            core.Navigate(DevUrl is { } dev ? $"{dev.TrimEnd('/')}/?mode={Mode}" : $"{Origin}index.html?mode={Mode}");
        }
        catch (Exception e)
        {
            Log.Error($"load {Mode}", e);
        }
    }

    /// <summary>Script errors in the page are written to the app's log.</summary>
    private const string ErrorReporter = """
        (() => {
          const tell = (text) => window.chrome.webview.postMessage({ type: 'log', text: String(text) });
          window.addEventListener('error', (e) => tell(`${e.message} (${(e.filename || '').split('/').pop()}:${e.lineno})`), true);
          window.addEventListener('unhandledrejection', (e) => tell(`unhandled: ${e.reason && e.reason.message || e.reason}`));
        })();
        """;

    /// <summary>Calls <c>window.house.&lt;function&gt;(…args)</c> in the page, if it is listening.</summary>
    public void Call(string function, params JsonNode?[] args)
    {
        if (Core is not { } core) return;
        var list = string.Join(",", args.Select(a => Json.Serialize(a)));
        var script = $"window.house && window.house.{function} && window.house.{function}({list});";
        try
        {
            _ = core.ExecuteScriptAsync(script);
        }
        catch (Exception e)
        {
            Log.Error($"call {Mode}.{function}", e);
        }
    }

    public void Reply(JsonObject message, JsonNode? result)
    {
        if (message.Num("callId") is not { } id) return;
        Call("reply", (int)id, result);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in the page as the body of an async function and returns what
    /// it returned, as JSON. Throws when the script threw.
    /// </summary>
    public async Task<JsonNode?> EvalAsync(string body, TimeSpan? timeout = null)
    {
        if (Core is not { } core) throw new InvalidOperationException($"The {Mode} page is not loaded");
        var id = ++_nextEval;
        var done = new TaskCompletionSource<JsonNode?>();
        _evals[id] = done;
        var script = $$"""
            (async () => {
              let message;
              try {
                const value = await (async () => { {{body}}
                })();
                message = { type: '__eval', id: {{id}}, ok: true, value: value === undefined ? null : value };
              } catch (e) {
                message = { type: '__eval', id: {{id}}, ok: false, error: String((e && e.stack) || e) };
              }
              window.chrome.webview.postMessage(message);
            })();
            """;
        await core.ExecuteScriptAsync(script);
        var finished = await Task.WhenAny(done.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(60)));
        _evals.Remove(id);
        if (finished != done.Task) throw new TimeoutException($"The {Mode} page did not answer");
        return await done.Task;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? body;
        try { body = Json.ParseObject(e.WebMessageAsJson); }
        catch { return; }
        if (body is null) return;
        switch (body.Str("type"))
        {
            case "log":
                Log.Write($"web ({Mode}): {body.Str("text")}");
                return;
            case "__eval":
                if (body.Num("id") is { } id && _evals.TryGetValue((int)id, out var done))
                {
                    if (body.Bool("ok") == true) done.TrySetResult(body["value"]?.DeepClone());
                    else done.TrySetException(new InvalidOperationException(body.Str("error") ?? "script failed"));
                }
                return;
            case "ready":
                IsReady = true;
                break;
        }
        try
        {
            Message?.Invoke(this, body);
        }
        catch (Exception ex)
        {
            Log.Error($"message {body.Str("type")} from {Mode}", ex);
        }
    }

    /// <summary>Writes a PNG of what the page shows.</summary>
    public async Task SnapshotAsync(string path)
    {
        if (Core is not { } core) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }

    /// <summary>
    /// HOUSE_EVAL_DIR=&lt;folder&gt;: scripted UI checks. A file <c>&lt;mode&gt;.js</c> put there is run in
    /// that page as the body of an async function, then removed; what it returns is written to
    /// <c>&lt;mode&gt;.json</c> (or <c>{"error": …}</c>). For test instances only.
    /// </summary>
    private void WatchScripts(string folder)
    {
        var script = Path.Combine(folder, $"{Mode}.js");
        var result = Path.Combine(folder, $"{Mode}.json");
        _evalTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _evalTimer.Tick += async (_, _) =>
        {
            if (!File.Exists(script)) return;
            string source;
            try
            {
                source = File.ReadAllText(script);
                File.Delete(script);
            }
            catch (IOException) { return; }
            JsonObject outcome;
            try { outcome = new JsonObject { ["result"] = await EvalAsync(source) }; }
            catch (Exception e) { outcome = new JsonObject { ["error"] = e.Message }; }
            Files.WriteAtomic(result, System.Text.Encoding.UTF8.GetBytes(Json.Serialize(outcome, pretty: true)));
        };
        _evalTimer.Start();
    }
}
