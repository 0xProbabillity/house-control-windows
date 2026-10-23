using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.Agent;

namespace HouseControl.Core.Tests;

/// <summary>Stands in for the panel page: answers like <c>web/src/agent/tools.js</c> does.</summary>
internal sealed class FakeHome : IAgentBackend
{
    public List<JsonObject> Requests { get; } = new();

    public Task<JsonObject> AgentRequestAsync(JsonObject request)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(Answer(request));
    }

    public static JsonObject Answer(JsonObject request)
    {
        var permissions = request.Obj("permissions");
        switch (request.Str("op"))
        {
            case "tools":
                var tools = new JsonArray(Tool("get_home", "Look"));
                if (permissions.Bool("control") != false) tools.Add(Tool("set_lights", "Lights"));
                return new JsonObject { ["tools"] = tools };
            case "call":
                if (request.Str("tool") == "set_lights")
                {
                    var on = request.Obj("arguments").Bool("on") ?? false;
                    return new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = on ? "Living · Lamp: on" : "Living · Lamp: off" }),
                        ["isError"] = false,
                        ["summary"] = on ? "On: Lamp" : "Off: Lamp",
                    };
                }
                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Home with 1 lamp" }),
                    ["isError"] = false,
                    ["summary"] = "Looked",
                    ["quiet"] = true,
                    ["structuredContent"] = new JsonObject { ["home"] = "Test" },
                };
            case "guide":
                return new JsonObject { ["text"] = $"# House Control — Test\nbase {request.Str("base")}" };
            case "instructions":
                return new JsonObject { ["text"] = "Call get_home first." };
            case "link":
                var path = string.Join("/", request.Arr("path")?.Select(p => p?.GetValue<string>()) ?? []);
                return new JsonObject { ["text"] = $"Done. {path}", ["summary"] = "On: Lamp", ["ok"] = true };
            default:
                return new JsonObject();
        }
    }

    private static JsonObject Tool(string name, string title) => new()
    {
        ["name"] = name, ["title"] = title, ["description"] = "d", ["inputSchema"] = new JsonObject { ["type"] = "object" },
    };
}

public class AgentGatewayTests
{
    private readonly FakeHome _home = new();
    private readonly AgentGateway _gateway = new("secret-token");
    private readonly List<AgentActivity> _activity = new();

    public AgentGatewayTests()
    {
        _gateway.Backend = _home;
        _gateway.PublicBase = "https://example.trycloudflare.com";
        _gateway.Activity += _activity.Add;
    }

    private static JsonNode? Parse(HttpResponse response) => response.Body.Length == 0 ? null : JsonNode.Parse(response.Body);

    private static HttpRequest Get(string path, Dictionary<string, string>? headers = null) => new("GET", path, headers);

    private static HttpRequest Post(string path, string body) => new("POST", path, body: Encoding.UTF8.GetBytes(body));

    private Task<HttpResponse> Rpc(JsonNode body, string? session = null)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json", ["user-agent"] = "Claude-User" };
        if (session != null) headers["mcp-session-id"] = session;
        return _gateway.HandleAsync(new HttpRequest("POST", "/secret-token/mcp", headers, Encoding.UTF8.GetBytes(body.ToJsonString())));
    }

    [Fact]
    public async Task EverythingWithoutTheTokenIsNotFound()
    {
        foreach (var path in new[] { "/", "/wrong-token", "/wrong-token/mcp", "/secret-tokenX/", "/SECRET-TOKEN/" })
            Assert.Equal(404, (await _gateway.HandleAsync(Get(path))).Status);
        Assert.Empty(_home.Requests);
        Assert.Equal(200, (await _gateway.HandleAsync(Get("/robots.txt"))).Status);
    }

    [Fact]
    public async Task GuideIsPlainTextWithTheLink()
    {
        var response = await _gateway.HandleAsync(Get("/secret-token", new() { ["user-agent"] = "Mozilla/5.0 ChatGPT-User/1.0" }));
        Assert.Equal(200, response.Status);
        Assert.StartsWith("text/markdown", response.Headers["Content-Type"]);
        Assert.Contains("base https://example.trycloudflare.com/secret-token", response.BodyText);
        Assert.Equal("ChatGPT", _activity[^1].Client);
    }

    [Fact]
    public async Task LinkActionsPassTheirPath()
    {
        var response = await _gateway.HandleAsync(Get("/secret-token/do/light/living.lamp/on"));
        Assert.Equal("Done. light/living.lamp/on", response.BodyText);
        Assert.Equal("On: Lamp", _activity[^1].Title);
    }

    [Fact]
    public async Task McpHandshakeListAndCall()
    {
        var initialize = await Rpc(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["clientInfo"] = new JsonObject { ["name"] = "claude-ai", ["version"] = "1" },
                ["capabilities"] = new JsonObject(),
            },
        });
        Assert.Equal(200, initialize.Status);
        var session = initialize.Headers["Mcp-Session-Id"];
        var result = Parse(initialize).Obj("result");
        Assert.Equal("2025-06-18", result.Str("protocolVersion"));
        Assert.Equal("Call get_home first.", result.Str("instructions"));
        Assert.Equal("house-control", result.Obj("serverInfo").Str("name"));
        Assert.Equal(1, Parse(initialize)!["id"]!.GetValue<int>());

        var note = await Rpc(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, session);
        Assert.Equal(202, note.Status);
        Assert.Empty(note.Body);

        var list = await Rpc(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, session);
        var names = Parse(list).Obj("result").Arr("tools")!.Select(t => t.Str("name")).ToList();
        Assert.Equal(["get_home", "set_lights"], names);

        var call = await Rpc(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "set_lights", ["arguments"] = new JsonObject { ["lights"] = new JsonArray("living"), ["on"] = true } },
        }, session);
        var callResult = Parse(call).Obj("result")!;
        Assert.False(callResult.Bool("isError"));
        Assert.False(callResult.ContainsKey("summary")); // the feed's summary is not part of the MCP result
        Assert.Equal("Living · Lamp: on", callResult.Arr("content")![0].Str("text"));
        Assert.Equal("Claude", _activity[^1].Client);
        Assert.Equal("On: Lamp", _activity[^1].Title);
    }

    [Fact]
    public async Task McpUnknownMethodsAndBatches()
    {
        var unknown = await Rpc(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "sampling/whatever" });
        Assert.Equal(-32601, Parse(unknown).Obj("error")!["code"]!.GetValue<int>());
        var batch = await Rpc(new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" },
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "prompts/list" }));
        Assert.Equal(2, Parse(batch)!.AsArray().Count);
        Assert.Equal(400, (await _gateway.HandleAsync(Post("/secret-token/mcp", "{nope"))).Status);
        Assert.Equal(405, (await _gateway.HandleAsync(Get("/secret-token/mcp"))).Status);
    }

    [Fact]
    public async Task PermissionsReachTheHome()
    {
        _gateway.Permissions = new AgentPermissions(Control: false, Edit: false);
        var list = await _gateway.HandleAsync(Get("/secret-token/api/tools"));
        Assert.Single(Parse(list).Arr("tools")!);
        Assert.False(_home.Requests[^1].Obj("permissions").Bool("control"));
    }

    [Fact]
    public async Task RestCallAndOpenApi()
    {
        var call = await _gateway.HandleAsync(Post("/secret-token/api/set_lights", """{"lights":["all"],"on":false}"""));
        Assert.Equal(200, call.Status);
        Assert.Equal("Living · Lamp: off", Parse(call).Str("result"));
        var named = await _gateway.HandleAsync(Post("/secret-token/api/call", """{"tool":"get_home","arguments":{}}"""));
        Assert.Equal("Test", Parse(named).Obj("data").Str("home"));
        Assert.Equal(400, (await _gateway.HandleAsync(Post("/secret-token/api/set_lights", "[1]"))).Status);
        var spec = Parse(await _gateway.HandleAsync(Get("/secret-token/openapi.json")));
        Assert.NotNull(spec.Obj("paths").Obj("/api/set_lights"));
        Assert.Equal("https://example.trycloudflare.com/secret-token", spec.Arr("servers")![0].Str("url"));
    }

    [Fact]
    public async Task HeadNeverRunsAnAction()
    {
        var head = await _gateway.HandleAsync(new HttpRequest("HEAD", "/secret-token/do/light/living.lamp/on"));
        Assert.Equal(200, head.Status);
        Assert.Empty(head.Body);
        Assert.DoesNotContain(_home.Requests, r => r.Str("op") == "link");
    }

    [Fact]
    public async Task RateLimit()
    {
        _gateway.RateLimit = 3;
        for (var i = 0; i < 3; i++) await _gateway.HandleAsync(Get("/secret-token/do/state"));
        Assert.Equal(429, (await _gateway.HandleAsync(Get("/secret-token/do/state"))).Status);
    }

    [Fact]
    public void TokensAreLongAndFresh()
    {
        string a = AgentGateway.MakeToken(), b = AgentGateway.MakeToken();
        Assert.Equal(22, a.Length);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void ClientNames()
    {
        Assert.Equal("ChatGPT", AgentGateway.FriendlyName("Mozilla/5.0 AppleWebKit/537.36; compatible; ChatGPT-User/1.0; +https://openai.com/bot"));
        Assert.Equal("Claude Code", AgentGateway.FriendlyName("Claude-User (claude-code/2.1)"));
        Assert.Equal("ChatGPT", AgentGateway.FriendlyName("openai-mcp"));
        Assert.Equal("AI agent", AgentGateway.FriendlyName("python-httpx/0.27"));
    }

    // MARK: HTTP

    [Fact]
    public void ParserHandlesChunkedAndPipelinedRequests()
    {
        var buffer = Encoding.UTF8.GetBytes(
            "POST /t/mcp HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n4\r\n{\"a\"\r\n3\r\n:1}\r\n0\r\n\r\n"
            + "GET /t/?x=a%20b HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var first = Assert.IsType<HttpParseResult.Parsed>(HttpParser.Parse(ref buffer, 1000));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(first.Request.Body));
        Assert.True(first.KeepAlive);
        var second = Assert.IsType<HttpParseResult.Parsed>(HttpParser.Parse(ref buffer, 1000));
        Assert.Equal("/t/", second.Request.Path);
        Assert.Equal("a b", second.Request.Query["x"]);
        Assert.False(second.KeepAlive);
        Assert.Empty(buffer);

        var partial = Encoding.UTF8.GetBytes("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\n12345");
        Assert.IsType<HttpParseResult.Incomplete>(HttpParser.Parse(ref partial, 1000));
        var huge = Encoding.UTF8.GetBytes("POST / HTTP/1.1\r\nContent-Length: 99999\r\n\r\n");
        Assert.Equal(413, Assert.IsType<HttpParseResult.Invalid>(HttpParser.Parse(ref huge, 1000)).Status);
        var garbage = Encoding.UTF8.GetBytes("HELLO\r\n\r\n");
        Assert.Equal(400, Assert.IsType<HttpParseResult.Invalid>(HttpParser.Parse(ref garbage, 1000)).Status);
    }

    [Fact]
    public async Task LoopbackServerEndToEnd()
    {
        using var server = new LoopbackHttpServer(_gateway.HandleAsync);
        var port = server.Start(0);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var baseUrl = $"http://127.0.0.1:{port}/secret-token";
        var guide = await http.GetAsync(baseUrl);
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        Assert.StartsWith("# House Control", await guide.Content.ReadAsStringAsync());
        Assert.Equal("noindex, nofollow", string.Join("", guide.Headers.GetValues("X-Robots-Tag")));

        var post = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        post.Headers.Accept.ParseAdd("application/json, text/event-stream");
        var list = await http.SendAsync(post);
        Assert.Contains("set_lights", await list.Content.ReadAsStringAsync());

        // A chunked body, as some clients send it.
        var chunked = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/set_lights")
        {
            Content = new StreamContent(new MemoryStream("""{"on":true}"""u8.ToArray())),
        };
        chunked.Headers.TransferEncodingChunked = true;
        chunked.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var lit = await http.SendAsync(chunked);
        Assert.Contains("Lamp: on", await lit.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/nope")).StatusCode);
    }
}

/// <summary>The controller with a real server on 127.0.0.1 and a stand-in for the panel page.</summary>
public class AgentControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-agent-tests-" + Guid.NewGuid().ToString("n"));
    private readonly PlanStore _plans;
    private readonly AgentController _agent;
    private readonly List<JsonObject> _panelRequests = new();
    private readonly List<JsonObject> _statuses = new();
    private readonly List<bool> _live = new();
    private readonly List<string> _copied = new();
    private int _reloads;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false });

    public AgentControllerTests()
    {
        _plans = new PlanStore(_dir);
        _agent = new AgentController(_plans, RunInPanel, () => Interlocked.Increment(ref _reloads),
            copyText: _copied.Add, openUrl: _ => { }) { PreferredPort = 0 };
        _agent.LiveChanged += _live.Add;
        _agent.AttachConsole((name, args) =>
        {
            Assert.Equal("agentStatus", name);
            lock (_statuses) _statuses.Add((JsonObject)args[0]!);
        });
    }

    public void Dispose()
    {
        _agent.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>What <c>window.house.agent(request)</c> answers in the panel page.</summary>
    private Task<string> RunInPanel(JsonObject request)
    {
        lock (_panelRequests) _panelRequests.Add(request);
        if (request.Str("op") == "call" && request.Str("tool") is "edit_floor_plan" or "undo_floor_plan_edit")
        {
            var edit = request.Str("tool") == "edit_floor_plan";
            var answer = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = edit ? "Saved." : "Undone." }),
                ["isError"] = false,
                ["summary"] = edit ? "Edited the plan" : "Undid the last floor plan change",
            };
            if (edit) answer["savePlan"] = new JsonObject { ["name"] = request.Obj("arguments").Str("name"), ["rooms"] = new JsonArray() };
            else answer["undo"] = true;
            return Task.FromResult(answer.ToJsonString());
        }
        return Task.FromResult(FakeHome.Answer(request).ToJsonString());
    }

    private JsonObject Status => _agent.Status();

    private async Task<JsonObject> Call(string tool, JsonObject arguments)
    {
        var response = await _http.PostAsync($"{Status.Str("link")}/api/{tool}",
            new StringContent(arguments.ToJsonString(), Encoding.UTF8, "application/json"));
        return Json.ParseObject(await response.Content.ReadAsStringAsync())!;
    }

    private void StartLocally() => _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.start", ["reach"] = "local" });

    [Fact]
    public async Task StartsLocallyServesTheLinkAndStopKillsIt()
    {
        Assert.Equal("off", Status.Str("state"));
        StartLocally();
        Assert.True(_agent.IsLive);
        Assert.Equal([true], _live);
        var link = Status.Str("link")!;
        Assert.StartsWith("http://127.0.0.1:", link);
        Assert.Equal(link + "/mcp", Status.Str("mcp"));
        Assert.Equal("local", Status.Str("reach"));
        Assert.NotNull(Status.Num("stopsAt")); // 8 hours by default
        Assert.Equal("live", _statuses[^1].Str("state"));
        Assert.Equal("Started on this PC", Status.Arr("activity")![^1].Str("title"));

        var guide = await _http.GetStringAsync(link);
        Assert.Contains($"base {link}", guide);
        Assert.Equal("guide", _panelRequests[^1].Str("op"));

        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.stop" });
        Assert.Equal("off", Status.Str("state"));
        Assert.Equal([true, false], _live);
        Assert.Null(Status["link"]);
        Assert.Contains("no longer works", Status.Arr("activity")![^1].Str("title"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetStringAsync(link));
    }

    [Fact]
    public async Task EveryStartMakesANewKey()
    {
        StartLocally();
        var first = Status.Str("link")!;
        _agent.Stop();
        StartLocally();
        var second = Status.Str("link")!;
        Assert.NotEqual(first, second);
        var port = new Uri(second).Port;
        var old = await _http.GetAsync($"http://127.0.0.1:{port}{new Uri(first).AbsolutePath}");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task PermissionsAndSettingsAreKept()
    {
        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.settings", ["control"] = false, ["autoStop"] = "never", ["reach"] = "local" });
        Assert.False(Status.Bool("control"));
        Assert.True(Status.Bool("edit"));
        Assert.Equal("never", Status.Str("autoStop"));
        StartLocally();
        Assert.Null(Status["stopsAt"]);
        var tools = Json.ParseObject(await _http.GetStringAsync($"{Status.Str("link")}/api/tools"))!;
        Assert.Single(tools.Arr("tools")!);
        Assert.False(_panelRequests[^1].Obj("permissions").Bool("control"));

        // A new controller (the next run of the app) reads them back.
        using var again = new AgentController(_plans, RunInPanel, () => { });
        Assert.False(again.Permissions.Control);
        Assert.Equal("never", again.AutoStop);
        Assert.Equal(AgentReach.Local, again.Reach);
    }

    [Fact]
    public async Task PlanEditsAreSavedAndUndone()
    {
        StartLocally();
        Assert.False(Status.Bool("canUndo"));

        var first = await Call("edit_floor_plan", new JsonObject { ["name"] = "First" });
        Assert.True(first.Bool("ok"));
        Assert.Equal("custom", _plans.ActivePlan);
        Assert.Equal("First", _plans.CustomPlanName);
        Assert.Equal(1, _reloads);
        Assert.True(Status.Bool("canUndo"));
        Assert.Equal("First", Status.Str("home"));

        await Call("edit_floor_plan", new JsonObject { ["name"] = "Second" });
        Assert.Equal("Second", _plans.CustomPlanName);

        // The AI undoes its last change…
        var undone = await Call("undo_floor_plan_edit", new JsonObject());
        Assert.StartsWith("Undone: the floor plan is back", undone.Str("result"));
        Assert.Equal("First", _plans.CustomPlanName);

        // …and the user the one before: there was no plan of their own, so the demo comes back.
        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.undo" });
        Assert.False(_plans.HasCustomPlan);
        Assert.Equal("home", _plans.ActivePlan);
        Assert.False(Status.Bool("canUndo"));

        var nothing = await Call("undo_floor_plan_edit", new JsonObject());
        Assert.False(nothing.Bool("ok"));
        Assert.Contains("no floor plan change to undo", nothing.Str("result"));
    }

    [Fact]
    public async Task ADemoOnScreenProtectsTheUsersPlan()
    {
        _plans.SaveCustom(new JsonObject { ["name"] = "Mine", ["rooms"] = new JsonArray() });
        _plans.Select("studio");
        StartLocally();
        var refused = await Call("edit_floor_plan", new JsonObject { ["name"] = "Theirs" });
        Assert.False(refused.Bool("ok"));
        Assert.Contains("demo plan", refused.Str("result"));
        Assert.Equal("Mine", _plans.CustomPlanName);
        Assert.False(Status.Bool("canUndo"));
    }

    [Fact]
    public async Task McpThroughTheController()
    {
        StartLocally();
        var mcp = Status.Str("mcp")!;
        var init = await _http.PostAsync(mcp, new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"cursor"}}}""", Encoding.UTF8, "application/json"));
        var session = init.Headers.GetValues("Mcp-Session-Id").Single();
        var call = new HttpRequestMessage(HttpMethod.Post, mcp)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"set_lights","arguments":{"on":true}}}""",
                Encoding.UTF8, "application/json"),
        };
        call.Headers.Add("Mcp-Session-Id", session);
        var answer = await (await _http.SendAsync(call)).Content.ReadAsStringAsync();
        Assert.Contains("Living · Lamp: on", answer);
        var last = Status.Arr("activity")![^1];
        Assert.Equal("Cursor", last.Str("client"));
        Assert.Equal("On: Lamp", last.Str("title"));
        Assert.Equal("Cursor", _panelRequests[^1].Str("client"));
    }

    [Fact]
    public void ConsoleMessagesReachTheShell()
    {
        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.copy", ["text"] = "the link" });
        Assert.Equal(["the link"], _copied);
        var before = _statuses.Count;
        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "ready" });
        Assert.Equal(before + 1, _statuses.Count);
        _agent.HandleConsoleMessage(new JsonObject { ["type"] = "agent.undo" });
        Assert.False(Status.Arr("activity")![^1].Bool("ok"));
    }

    [Fact]
    public void ExplainsTunnelFailures()
    {
        Assert.Contains("limiting", CloudflareTunnel.Explain("429 Too Many Requests"));
        Assert.Contains("could not be reached", CloudflareTunnel.Explain("dial tcp: lookup api.trycloudflare.com: no such host"));
        Assert.Equal("The tunnel stopped.", CloudflareTunnel.Explain(""));
    }
}
