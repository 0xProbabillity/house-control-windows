using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Agent;

/// <summary>Where the link can be reached from.</summary>
public enum AgentReach
{
    /// <summary>A public <c>https://….trycloudflare.com</c> address through Cloudflare's quick tunnel.</summary>
    Internet,
    /// <summary>"This PC only": <c>http://127.0.0.1:7419</c>, for apps running on this PC.</summary>
    Local,
}

/// <summary>Where AI Access is.</summary>
public enum AgentPhase { Off, Starting, Live, Failed }

/// <summary>
/// AI Access: a private link that lets an AI assistant (ChatGPT, Claude, an MCP client…)
/// see and run the home, served from this PC.
/// <code>
///     AI ─HTTPS─▶ Cloudflare quick tunnel ─▶ cloudflared on this PC ─▶ 127.0.0.1 gateway ─▶ the panel's store
/// </code>
/// Off until the user starts it. Every start makes a new secret link; Stop (or the auto-stop
/// timer) closes the gateway and the tunnel, and the old address stops existing.
/// </summary>
/// <remarks>
/// <para>Knows nothing of windows or web views; the app shell wires it up:</para>
/// <list type="bullet">
/// <item><c>runInPanel</c> runs <c>window.house.agent(request)</c> in the panel page and returns the
/// JSON string it resolves to. The panel holds the state of every device, so it answers every request.</item>
/// <item><c>reloadAll</c> reloads every view after an AI changed the floor plan (and tells paired phones).</item>
/// <item><see cref="LiveChanged"/>: the shell calls <c>window.house.agentLive(live)</c> in the panel and
/// updates its tray icon and menu.</item>
/// <item>The AI Access window page (<c>web/src/agent/console.js</c>): its boot data carries
/// <c>agent: Status()</c>; the shell hands every message it posts to <see cref="HandleConsoleMessage"/>, and
/// <see cref="AttachConsole"/> gives the controller a way to call <c>window.house.agentStatus(status)</c> in it.</item>
/// </list>
/// <para>The app calls it from its UI thread. The server accepts and reads connections on the thread
/// pool, but every request, every call to <c>runInPanel</c> and every change of state happens on the
/// <see cref="SynchronizationContext"/> current when the controller was made (directly when there was none, in tests).</para>
/// <para>For testing, <c>HOUSE_AGENT_REACH=local|internet</c> overrides the saved reach for one run, and
/// <c>HOUSE_AGENT_LINK_FILE=&lt;path&gt;</c> writes the live link to a file.</para>
/// </remarks>
public sealed class AgentController : IAgentBackend, IDisposable
{
    public AgentGateway Gateway { get; } = new();
    public CloudflareTunnel Tunnel { get; }
    private readonly LoopbackHttpServer _server;
    private readonly PlanStore _plans;
    private readonly SynchronizationContext? _context;
    private readonly Func<JsonObject, Task<string>> _runInPanel;
    private readonly Action _reloadAll;
    private readonly Func<bool> _panelReady;
    private readonly Action<string>? _copyText;
    private readonly Action<string> _openUrl;
    private Action<string, JsonNode?[]>? _callConsole;

    /// <summary>Raised with true when the link goes live and false when it stops working.</summary>
    public event Action<bool>? LiveChanged;

    public AgentPhase Phase { get; private set; } = AgentPhase.Off;
    /// <summary>Why AI Access stopped, when <see cref="Phase"/> is Failed ("needs-cloudflared" when the tunnel is not installed).</summary>
    public string? Error { get; private set; }
    public bool IsLive => Phase == AgentPhase.Live;
    /// <summary>The port the gateway asks for first; any free one when it is taken.</summary>
    public int PreferredPort { get; set; } = 7419;

    private readonly List<AgentActivity> _activity = new();
    /// <summary>What the plan was before each change an AI made, newest last.</summary>
    private readonly List<(byte[]? Data, string Active)> _history = new();
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stopsAt;
    private CancellationTokenSource? _stopTimer;
    private string _tunnelError = "";

    /// <param name="plans">The plan store: an AI's edits are saved there, and undone from there.</param>
    /// <param name="runInPanel">Runs <c>window.house.agent(request)</c> in the panel page; returns its JSON string.</param>
    /// <param name="reloadAll">Reloads every view after the plan changed.</param>
    /// <param name="copyText">Puts text on the clipboard ("agent.copy" from the window). Null ignores it.</param>
    /// <param name="openUrl">Opens an http(s) address in the browser ("agent.open"). Defaults to the shell's default browser.</param>
    /// <param name="panelReady">False while the panel page is (re)loading: requests wait for it, up to 15 s. Defaults to always ready.</param>
    public AgentController(PlanStore plans, Func<JsonObject, Task<string>> runInPanel, Action reloadAll,
        Action<string>? copyText = null, Action<string>? openUrl = null, Func<bool>? panelReady = null)
    {
        _plans = plans;
        _runInPanel = runInPanel;
        _reloadAll = reloadAll;
        _copyText = copyText;
        _openUrl = openUrl ?? OpenInBrowser;
        _panelReady = panelReady ?? (() => true);
        _context = SynchronizationContext.Current;
        _settingsPath = Path.Combine(plans.Directory, "agent.json");
        LoadSettings();
        Tunnel = new CloudflareTunnel(plans.Directory, _context);
        _server = new LoopbackHttpServer(request => OnContext(() => Gateway.HandleAsync(request)));
        Gateway.Backend = this;
        Gateway.Permissions = Permissions;
        Gateway.Activity += Log;
        Tunnel.Changed += TunnelChanged;
    }

    // MARK: Settings

    private readonly string _settingsPath;
    private AgentReach _savedReach = AgentReach.Internet;
    private string _autoStop = "8h";
    private AgentPermissions _permissions = new();

    /// <summary>Over the internet or this PC only. <c>HOUSE_AGENT_REACH=local|internet</c> overrides the saved choice for one run.</summary>
    public AgentReach Reach
    {
        get => ParseReach(Environment.GetEnvironmentVariable("HOUSE_AGENT_REACH")) ?? _savedReach;
        private set { _savedReach = value; SaveSettings(); }
    }

    /// <summary>"1h", "8h" or "never".</summary>
    public string AutoStop
    {
        get => _autoStop;
        private set { _autoStop = value; SaveSettings(); }
    }

    public AgentPermissions Permissions
    {
        get => _permissions;
        private set
        {
            _permissions = value;
            Gateway.Permissions = value;
            SaveSettings();
        }
    }

    private static AgentReach? ParseReach(string? text) => text switch
    {
        "internet" => AgentReach.Internet,
        "local" => AgentReach.Local,
        _ => null,
    };

    private static string ReachName(AgentReach reach) => reach == AgentReach.Local ? "local" : "internet";

    /// <summary>The three settings live in agent.json next to the plan; nothing else about AI Access is stored.</summary>
    private void LoadSettings()
    {
        JsonObject? saved = null;
        try { if (File.Exists(_settingsPath)) saved = Json.ParseObject(File.ReadAllBytes(_settingsPath)); }
        catch (IOException) { }
        _savedReach = ParseReach(saved.Str("reach")) ?? AgentReach.Internet;
        _autoStop = saved.Str("autoStop") is "1h" or "8h" or "never" ? saved.Str("autoStop")! : "8h";
        _permissions = new AgentPermissions(saved.Bool("control") ?? true, saved.Bool("edit") ?? true);
    }

    private void SaveSettings()
    {
        var json = new JsonObject
        {
            ["reach"] = ReachName(_savedReach),
            ["autoStop"] = _autoStop,
            ["control"] = _permissions.Control,
            ["edit"] = _permissions.Edit,
        };
        try { Files.WriteAtomic(_settingsPath, Encoding.UTF8.GetBytes(Json.Serialize(json, pretty: true))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // MARK: Start and stop

    public void Start()
    {
        if (Phase is AgentPhase.Starting or AgentPhase.Live) return;
        if (Reach == AgentReach.Internet && Tunnel.Binary is null)
        {
            Phase = AgentPhase.Failed;
            Error = "needs-cloudflared";
            Push();
            return;
        }
        Gateway.RotateToken();
        Gateway.Permissions = Permissions;
        Phase = AgentPhase.Starting;
        Error = null;
        _tunnelError = "";
        Push();
        int port;
        try
        {
            port = _server.Start(PreferredPort);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            Fail($"The local server could not start: {e.Message}");
            return;
        }
        if (Reach == AgentReach.Local)
        {
            Gateway.PublicBase = $"http://127.0.0.1:{port}";
            GoLive();
        }
        else
        {
            Tunnel.Start(port);
        }
    }

    private void TunnelChanged(TunnelState state)
    {
        switch (state)
        {
            case TunnelState.Live live:
                if (Phase != AgentPhase.Starting) return;
                Gateway.PublicBase = live.Url.TrimEnd('/');
                GoLive();
                break;
            case TunnelState.Failed failed:
                if (Phase is not (AgentPhase.Starting or AgentPhase.Live)) return;
                Fail(failed.Message);
                break;
        }
    }

    private void GoLive()
    {
        Phase = AgentPhase.Live;
        _startedAt = DateTimeOffset.Now;
        ScheduleAutoStop();
        Log(new AgentActivity("House Control", Reach == AgentReach.Local ? "Started on this PC" : "Link is live"));
        LiveChanged?.Invoke(true);
        Push();
        // HOUSE_AGENT_LINK_FILE=<path> writes the live link there, for scripted tests.
        if (Environment.GetEnvironmentVariable("HOUSE_AGENT_LINK_FILE") is { Length: > 0 } path)
        {
            try { File.WriteAllText(path, Gateway.LinkUrl); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Fail(string message)
    {
        var wasLive = Phase == AgentPhase.Live;
        Tunnel.Stop();
        _server.Stop();
        CancelAutoStop();
        Phase = AgentPhase.Failed;
        Error = message;
        if (wasLive) LiveChanged?.Invoke(false);
        Push();
    }

    /// <summary>Closes the gateway and the tunnel; the link is dead for good. The app calls it with
    /// "House Control quit" when it exits.</summary>
    public void Stop(string reason = "Stopped")
    {
        if (Phase == AgentPhase.Off) return;
        var wasLive = Phase == AgentPhase.Live;
        Tunnel.Stop();
        _server.Stop();
        CancelAutoStop();
        _stopsAt = null;
        _startedAt = null;
        Phase = AgentPhase.Off;
        Error = null;
        Gateway.RotateToken(); // the old link is dead even if the same address came back
        if (wasLive)
        {
            Log(new AgentActivity("House Control", $"{reason} — the link no longer works"));
            LiveChanged?.Invoke(false);
        }
        Push();
    }

    public void Dispose()
    {
        Stop("House Control quit");
        Tunnel.Dispose();
        _server.Dispose();
    }

    private void CancelAutoStop()
    {
        _stopTimer?.Cancel();
        _stopTimer = null;
    }

    private void ScheduleAutoStop()
    {
        CancelAutoStop();
        double? hours = AutoStop switch { "1h" => 1, "8h" => 8, _ => null };
        if (hours is not { } h || _startedAt is not { } started)
        {
            _stopsAt = null;
            return;
        }
        var when = started.AddHours(h);
        _stopsAt = when;
        var timer = _stopTimer = new CancellationTokenSource();
        var delay = when - DateTimeOffset.Now;
        _ = StopLater(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, timer.Token);
    }

    private async Task StopLater(TimeSpan delay, CancellationToken cancel)
    {
        try { await Task.Delay(delay, cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Post(() =>
        {
            if (!cancel.IsCancellationRequested) Stop("Stopped automatically");
        });
    }

    private void Log(AgentActivity entry)
    {
        lock (_activity)
        {
            _activity.Add(entry);
            if (_activity.Count > 200) _activity.RemoveRange(0, _activity.Count - 200);
        }
        Push();
    }

    // MARK: Requests from the AI (IAgentBackend)

    public async Task<JsonObject> AgentRequestAsync(JsonObject request)
    {
        await WaitForPanel();
        var text = await _runInPanel(request);
        if (Json.ParseObject(text) is not { } result) throw new AgentException("House Control did not understand the request.");
        if (result.Obj("savePlan") is { } plan)
        {
            result.Remove("savePlan");
            try
            {
                SavePlan(plan);
            }
            catch (Exception e) when (e is AgentException or IOException or UnauthorizedAccessException)
            {
                result = GatewayError(e.Message, $"Floor plan not saved: {e.Message}");
            }
        }
        if (result.Bool("undo") == true)
        {
            result.Remove("undo");
            try
            {
                Undo();
                result["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "Undone: the floor plan is back to how it was before the last change. The 3D model is updating.",
                });
            }
            catch (Exception e) when (e is AgentException or IOException or UnauthorizedAccessException)
            {
                result = GatewayError(e.Message, e.Message);
            }
        }
        return result;
    }

    private static JsonObject GatewayError(string message, string summary)
    {
        var result = AgentGateway.ErrorResult(message);
        result["summary"] = summary;
        return result;
    }

    /// <summary>The panel page, once it has (re)loaded: a request that arrives while the plan is
    /// being swapped waits for the new one.</summary>
    private async Task WaitForPanel()
    {
        for (var i = 0; i < 150; i++)
        {
            if (_panelReady()) return;
            await Task.Delay(100);
        }
        throw new AgentException("House Control is busy loading the home. Try again in a moment.");
    }

    private void SavePlan(JsonObject plan)
    {
        if (_plans.ActivePlan != "custom" && _plans.HasCustomPlan)
            throw new AgentException("The user is looking at a demo plan, and their own home is saved separately. Ask them to switch to \"My home\" (gear menu in the panel) first.");
        var previous = _plans.HasCustomPlan ? File.ReadAllBytes(_plans.CustomPlanPath) : null;
        var previousActive = _plans.ActivePlan;
        _plans.SaveCustom(plan);
        _history.Add((previous, previousActive));
        if (_history.Count > 30) _history.RemoveAt(0);
        _reloadAll();
        Push();
    }

    /// <summary>Puts back the floor plan as it was before the last change an AI made.</summary>
    /// <exception cref="AgentException">There is nothing to undo.</exception>
    public void Undo()
    {
        if (_history.Count == 0) throw new AgentException("There is no floor plan change to undo.");
        var last = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        if (last.Data is { } data)
        {
            _plans.SaveCustom(data, select: true);
        }
        else
        {
            _plans.RemoveCustom();
            _plans.Select(last.Active);
        }
        _reloadAll();
        Log(new AgentActivity("House Control", "Undid the last floor plan change"));
    }

    public bool CanUndo => _history.Count > 0;

    // MARK: The AI Access window

    /// <summary>
    /// Connects the AI Access window page. <paramref name="callPage"/> calls <c>window.house.&lt;name&gt;(...args)</c>
    /// in it; the controller uses it for <c>agentStatus(status)</c> whenever anything changes.
    /// </summary>
    public void AttachConsole(Action<string, JsonNode?[]> callPage)
    {
        _callConsole = callPage;
        Push();
    }

    /// <summary>The window closed: no more status updates.</summary>
    public void DetachConsole() => _callConsole = null;

    /// <summary>
    /// A message the AI Access window posted (<c>{type, …}</c>): <c>ready</c>, <c>agent.start</c> {reach},
    /// <c>agent.stop</c>, <c>agent.settings</c> {control, edit, autoStop, reach}, <c>agent.copy</c> {text},
    /// <c>agent.open</c> {url}, <c>agent.undo</c> and <c>agent.install</c>. Others are ignored.
    /// </summary>
    public void HandleConsoleMessage(JsonObject message)
    {
        switch (message.Str("type"))
        {
            case "agent.start":
                if (ParseReach(message.Str("reach")) is { } reach) Reach = reach;
                Start();
                break;
            case "agent.stop":
                Stop();
                break;
            case "agent.settings":
                var permissions = Permissions;
                if (message.Bool("control") is { } control) permissions = permissions with { Control = control };
                if (message.Bool("edit") is { } edit) permissions = permissions with { Edit = edit };
                if (permissions != Permissions) Permissions = permissions;
                if (message.Str("autoStop") is "1h" or "8h" or "never")
                {
                    AutoStop = message.Str("autoStop")!;
                    if (Phase == AgentPhase.Live) ScheduleAutoStop();
                }
                if (ParseReach(message.Str("reach")) is { } r && Phase is not (AgentPhase.Live or AgentPhase.Starting)) Reach = r;
                Push();
                break;
            case "agent.copy":
                _copyText?.Invoke(message.Str("text") ?? "");
                break;
            case "agent.open":
                if (Uri.TryCreate(message.Str("url"), UriKind.Absolute, out var url) && (url.Scheme == "http" || url.Scheme == "https"))
                    _openUrl(url.AbsoluteUri);
                break;
            case "agent.undo":
                try { Undo(); }
                catch (Exception e) when (e is AgentException or IOException or UnauthorizedAccessException)
                {
                    Log(new AgentActivity("House Control", e.Message, ok: false));
                }
                Push();
                break;
            case "agent.install":
                _ = InstallTunnelAsync();
                break;
            case "ready":
                Push();
                break;
        }
    }

    /// <summary>Downloads cloudflared (see <see cref="CloudflareTunnel.DownloadAsync"/>), showing progress in the window.
    /// Failures end up in the status' <c>tunnel.error</c>.</summary>
    public async Task InstallTunnelAsync()
    {
        if (Tunnel.IsDownloading) return;
        _tunnelError = "";
        var download = Tunnel.DownloadAsync();
        Push();
        try
        {
            await download;
            if (Phase == AgentPhase.Failed && Error == "needs-cloudflared")
            {
                Phase = AgentPhase.Off;
                Error = null;
            }
        }
        catch (Exception e) when (e is AgentException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            _tunnelError = e.Message;
        }
        Push();
    }

    /// <summary>What the AI Access window shows, as <c>window.house.agentStatus</c> and the
    /// <c>agent</c> boot value take it.</summary>
    public JsonObject Status()
    {
        JsonArray activity;
        lock (_activity) activity = new JsonArray(_activity.TakeLast(80).Select(a => (JsonNode)a.ToJson()).ToArray());
        var status = new JsonObject
        {
            ["reach"] = ReachName(Reach),
            ["autoStop"] = AutoStop,
            ["control"] = Permissions.Control,
            ["edit"] = Permissions.Edit,
            ["canUndo"] = CanUndo,
            ["activity"] = activity,
            ["home"] = _plans.ActivePlan == "custom" ? _plans.CustomPlanName ?? "My home" : "Demo home",
            ["tunnel"] = new JsonObject
            {
                ["installed"] = Tunnel.Binary is not null,
                ["downloading"] = Tunnel.IsDownloading,
                ["error"] = _tunnelError,
            },
        };
        switch (Phase)
        {
            case AgentPhase.Off: status["state"] = "off"; break;
            case AgentPhase.Starting: status["state"] = "starting"; break;
            case AgentPhase.Live: status["state"] = "live"; break;
            case AgentPhase.Failed:
                status["state"] = Error == "needs-cloudflared" ? "setup" : "error";
                status["error"] = Error;
                break;
        }
        if (Phase == AgentPhase.Live)
        {
            status["link"] = Gateway.LinkUrl;
            status["mcp"] = Gateway.McpUrl;
            status["startedAt"] = (_startedAt ?? DateTimeOffset.Now).ToUnixTimeMilliseconds();
            if (_stopsAt is { } stopsAt) status["stopsAt"] = stopsAt.ToUnixTimeMilliseconds();
        }
        return status;
    }

    private void Push() => _callConsole?.Invoke("agentStatus", [Status()]);

    // MARK: Threads

    /// <summary>Runs <paramref name="work"/> on the app's context and hands back its result on any thread.</summary>
    private Task<T> OnContext<T>(Func<Task<T>> work)
    {
        if (_context is null || SynchronizationContext.Current == _context) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ =>
        {
            try { done.SetResult(await work()); }
            catch (Exception e) { done.SetException(e); }
        }, null);
        return done.Task;
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
