using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Agent;

/// <summary>What the home itself does for an AI: lists its tools, runs them, writes the guide.
/// On the PC this is the panel's web UI, which owns the state of every device.</summary>
public interface IAgentBackend
{
    /// <summary><c>request["op"]</c> is <c>tools</c>, <c>call</c>, <c>guide</c>, <c>instructions</c> or <c>link</c>;
    /// see <c>web/src/agent/tools.js</c>. Throws <see cref="AgentException"/> with a message for the AI.</summary>
    Task<JsonObject> AgentRequestAsync(JsonObject request);
}

/// <summary>A failure explained in words an AI (and the user) can act on.</summary>
public sealed class AgentException(string message) : Exception(message);

/// <summary>What an AI may do through the link.</summary>
/// <param name="Control">Lights, air conditioners and scenes.</param>
/// <param name="Edit">The floor plan: rooms, furniture, devices, scenes.</param>
public readonly record struct AgentPermissions(bool Control = true, bool Edit = true)
{
    public AgentPermissions() : this(true, true) { }

    public JsonObject ToJson() => new() { ["control"] = Control, ["edit"] = Edit };
}

/// <summary>One line of the activity feed the user watches while an AI has access.</summary>
/// <param name="Client">"ChatGPT", "Claude", "Cursor"… or "AI agent" when it does not say.</param>
/// <param name="Quiet">True for reads (the guide, the state): the feed shows them quietly.</param>
public sealed record AgentActivity(DateTimeOffset Date, string Client, string Title, bool Ok = true, bool Quiet = false)
{
    public AgentActivity(string client, string title, bool ok = true, bool quiet = false)
        : this(DateTimeOffset.Now, client, title, ok, quiet) { }

    public JsonObject ToJson() => new()
    {
        ["time"] = Date.ToUnixTimeMilliseconds(),
        ["client"] = Client,
        ["title"] = Title,
        ["ok"] = Ok,
        ["quiet"] = Quiet,
    };
}

/// <summary>
/// The AI gateway: everything an AI can reach, behind one secret path segment.
/// <code>
///     /&lt;token&gt;/                  the guide: plain text an AI reads to learn the home (GET)
///     /&lt;token&gt;/do/&lt;action…&gt;      actions as plain links, for chat assistants that can only open links (GET)
///     /&lt;token&gt;/mcp               Model Context Protocol, Streamable HTTP with JSON responses (POST)
///     /&lt;token&gt;/api/tools         the same tools as JSON (GET), run with POST /api/call or /api/&lt;tool&gt;
///     /&lt;token&gt;/openapi.json      an OpenAPI description of /api, for GPT actions and agent frameworks
/// </code>
/// Anything without the token gets a plain 404, the same as a path that does not exist.
/// Meant to be called from one thread at a time (the app's UI thread); the little state it
/// keeps is locked anyway, so a test may call it from anywhere.
/// </summary>
public sealed class AgentGateway
{
    public static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    public const string Version = "1.0";

    public IAgentBackend? Backend { get; set; }
    public string Token { get; private set; }
    public AgentPermissions Permissions { get; set; } = new();
    /// <summary>The address the AI uses, e.g. <c>https://xyz.trycloudflare.com</c> or <c>http://127.0.0.1:7419</c>.</summary>
    public string PublicBase { get; set; } = "";
    /// <summary>Raised for every line of the activity feed.</summary>
    public event Action<AgentActivity>? Activity;
    /// <summary>Requests per minute before the gateway answers 429.</summary>
    public int RateLimit { get; set; } = 240;

    private readonly object _lock = new();
    /// <summary>Names of MCP clients by session, as they introduced themselves.</summary>
    private Dictionary<string, string> _sessions = new();
    private readonly Queue<DateTimeOffset> _recent = new();

    public AgentGateway(string? token = null)
    {
        Token = token ?? MakeToken();
    }

    /// <summary>128 random bits, URL-safe (22 characters).</summary>
    public static string MakeToken() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void RotateToken()
    {
        Token = MakeToken();
        lock (_lock) _sessions = new();
    }

    public string LinkUrl => $"{PublicBase}/{Token}";
    public string McpUrl => $"{PublicBase}/{Token}/mcp";

    // MARK: Routing

    public async Task<HttpResponse> HandleAsync(HttpRequest request)
    {
        // Link checkers ask with HEAD first: the same answer as GET, without the body.
        // Action links never run for HEAD.
        if (request.Method == "HEAD")
        {
            var path = request.Path;
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() == "do")
                path = path.Split("/do/")[0];
            var response = await HandleAsync(request with { Method = "GET", Path = path });
            response.Headers["Content-Length"] = response.Body.Length.ToString();
            response.Body = [];
            return response;
        }
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
        if (request.Method == "GET" && segments is ["robots.txt"])
        {
            // The guide is fetched by chat assistants on the user's request; it is never indexed (X-Robots-Tag).
            return HttpResponse.Text("User-agent: *\nAllow: /\n");
        }
        if (segments.Count == 0 || !Same(segments[0], Token))
        {
            await Task.Delay(150);
            return HttpResponse.NotFound;
        }
        segments.RemoveAt(0);
        if (!Admit()) return HttpResponse.Text("Too many requests. Wait a minute.\n", 429);
        var client = ClientName(request);

        switch (request.Method, segments.FirstOrDefault() ?? "")
        {
            case ("GET", "" or "guide" or "llms.txt"):
                return await Guide(client);
            case ("GET", "do"):
                return await LinkAction(segments.Skip(1).ToList(), client);
            case ("POST", "mcp"):
                return await Mcp(request, client);
            case ("GET", "mcp"):
                // No server-initiated stream: every answer comes back as JSON.
                return new HttpResponse(405, new() { ["Allow"] = "POST, DELETE" });
            case ("DELETE", "mcp"):
                if (request.Header("mcp-session-id") is { } id) lock (_lock) _sessions.Remove(id);
                return new HttpResponse(204);
            case ("GET", "api") when segments.Count == 2 && segments[1] == "tools":
                return await RestTools();
            case ("POST", "api") when segments.Count == 2:
                return await RestCall(segments[1], request, client);
            case ("GET", "openapi.json"):
                return await OpenApi();
            case ("GET", "plan.json"):
                return await RestCall("get_floor_plan", new HttpRequest("POST", ""), client);
            default:
                return HttpResponse.NotFound;
        }
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>Compares without leaking where the first difference is.</summary>
    internal static bool Same(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    private bool Admit()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            while (_recent.Count > 0 && (now - _recent.Peek()).TotalSeconds > 60) _recent.Dequeue();
            if (_recent.Count >= RateLimit) return false;
            _recent.Enqueue(now);
            return true;
        }
    }

    /// <summary>Who is asking, as well as it can be told: the name an MCP client gave, or its user agent.</summary>
    internal string ClientName(HttpRequest request)
    {
        if (request.Header("mcp-session-id") is { } session)
            lock (_lock)
                if (_sessions.TryGetValue(session, out var name)) return name;
        return FriendlyName(request.Header("user-agent") ?? "");
    }

    public static string FriendlyName(string raw)
    {
        var s = raw.ToLowerInvariant();
        bool Has(string x) => s.Contains(x, StringComparison.Ordinal);
        if (Has("claude-code") || Has("claude code")) return "Claude Code";
        if (Has("chatgpt") || Has("openai")) return "ChatGPT";
        if (Has("claude") || Has("anthropic")) return "Claude";
        if (Has("cursor")) return "Cursor";
        if (Has("perplexity")) return "Perplexity";
        if (Has("gemini") || Has("google")) return "Gemini";
        if (Has("copilot") || Has("vscode") || Has("visual studio")) return "Copilot";
        if (Has("mistral") || Has("le chat")) return "Le Chat";
        if (Has("grok") || Has("xai")) return "Grok";
        if (Has("inspector")) return "MCP Inspector";
        return "AI agent";
    }

    private void Record(string client, string title, bool ok = true, bool quiet = false) =>
        Activity?.Invoke(new AgentActivity(client, title, ok, quiet));

    // MARK: Backend

    private async Task<JsonObject> Ask(JsonObject request)
    {
        if (Backend is not { } backend) throw new AgentException("House Control is not ready yet. Try again in a moment.");
        request["permissions"] = Permissions.ToJson();
        request["base"] = LinkUrl;
        return await backend.AgentRequestAsync(request);
    }

    private async Task<JsonArray> Tools() =>
        (await Ask(new JsonObject { ["op"] = "tools" })).Arr("tools")?.DeepClone().AsArray() ?? new JsonArray();

    /// <summary>Runs one tool. The answer has <c>content</c> (MCP content blocks), <c>isError</c> and <c>summary</c>.</summary>
    private async Task<JsonObject> Call(string tool, JsonObject arguments, string client)
    {
        try
        {
            var result = await Ask(new JsonObject { ["op"] = "call", ["tool"] = tool, ["arguments"] = arguments, ["client"] = client });
            if (result.Str("summary") is { Length: > 0 } summary)
                Record(client, summary, ok: !(result.Bool("isError") ?? false), quiet: result.Bool("quiet") ?? false);
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Record(client, $"{tool} failed: {e.Message}", ok: false);
            return ErrorResult(e.Message);
        }
    }

    internal static JsonObject ErrorResult(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Error: {message}" }),
        ["isError"] = true,
    };

    // MARK: Guide and plain links

    private async Task<HttpResponse> Guide(string client)
    {
        try
        {
            var result = await Ask(new JsonObject { ["op"] = "guide" });
            Record(client, "Read the guide", quiet: true);
            return HttpResponse.Text(result.Str("text") ?? "", type: "text/markdown; charset=utf-8");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HttpResponse.Text($"House Control is not ready: {e.Message}\n", 503);
        }
    }

    private async Task<HttpResponse> LinkAction(List<string> path, string client)
    {
        try
        {
            var result = await Ask(new JsonObject
            {
                ["op"] = "link",
                ["path"] = new JsonArray(path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["client"] = client,
            });
            if (result.Str("summary") is { Length: > 0 } summary) Record(client, summary, ok: result.Bool("ok") ?? true);
            return HttpResponse.Text(result.Str("text") ?? "", (int)(result.Num("status") ?? 200), "text/markdown; charset=utf-8");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HttpResponse.Text($"House Control is not ready: {e.Message}\n", 503);
        }
    }

    // MARK: REST

    private async Task<HttpResponse> RestTools()
    {
        try { return HttpResponse.Json(new JsonObject { ["tools"] = await Tools() }); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HttpResponse.Json(new JsonObject { ["error"] = e.Message }, 503);
        }
    }

    private async Task<HttpResponse> RestCall(string name, HttpRequest request, string client)
    {
        var tool = name;
        var arguments = new JsonObject();
        if (request.Body.Length > 0)
        {
            if (Json.ParseObject(request.Body) is not { } json)
                return HttpResponse.Json(new JsonObject { ["error"] = "The body must be a JSON object." }, 400);
            arguments = json;
        }
        if (name == "call")
        {
            if (arguments.Str("tool") is not { } named)
                return HttpResponse.Json(new JsonObject { ["error"] = "POST /api/call takes {\"tool\": \"…\", \"arguments\": {…}}." }, 400);
            tool = named;
            arguments = arguments.Obj("arguments")?.DeepClone().AsObject() ?? new JsonObject();
        }
        var result = await Call(tool, arguments, client);
        var failed = result.Bool("isError") ?? false;
        var content = result.Arr("content")?.OfType<JsonObject>().ToList() ?? [];
        var body = new JsonObject
        {
            ["ok"] = !failed,
            ["result"] = string.Join("\n\n", content.Select(c => c.Str("text")).OfType<string>()),
        };
        if (content.FirstOrDefault(c => c.Str("type") == "image") is { } image
            && image.Str("data") is { } data && image.Str("mimeType") is { } type)
            body["image"] = $"data:{type};base64,{data}";
        if (result.TryGetPropertyValue("structuredContent", out var structured) && structured is not null)
            body["data"] = structured.DeepClone();
        return HttpResponse.Json(body, failed ? 400 : 200);
    }

    private async Task<HttpResponse> OpenApi()
    {
        try
        {
            var tools = await Tools();
            var paths = new JsonObject();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                if (tool.Str("name") is not { } name) continue;
                paths[$"/api/{name}"] = new JsonObject
                {
                    ["post"] = new JsonObject
                    {
                        ["operationId"] = name,
                        ["summary"] = tool.Str("title") ?? name,
                        ["description"] = tool.Str("description") ?? "",
                        ["requestBody"] = new JsonObject
                        {
                            ["required"] = false,
                            ["content"] = new JsonObject
                            {
                                ["application/json"] = new JsonObject
                                {
                                    ["schema"] = tool["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                                },
                            },
                        },
                        ["responses"] = new JsonObject
                        {
                            ["200"] = new JsonObject
                            {
                                ["description"] = "Done",
                                ["content"] = new JsonObject
                                {
                                    ["application/json"] = new JsonObject
                                    {
                                        ["schema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["ok"] = new JsonObject { ["type"] = "boolean" },
                                                ["result"] = new JsonObject { ["type"] = "string" },
                                                ["data"] = new JsonObject { ["type"] = "object" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                };
            }
            return HttpResponse.Json(new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["info"] = new JsonObject
                {
                    ["title"] = "House Control",
                    ["version"] = Version,
                    ["description"] = "Control the lights and air conditioners of a home and edit its 3D floor plan. Read GET / (the guide) first.",
                },
                ["servers"] = new JsonArray(new JsonObject { ["url"] = LinkUrl }),
                ["paths"] = paths,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HttpResponse.Json(new JsonObject { ["error"] = e.Message }, 503);
        }
    }

    // MARK: MCP

    private async Task<HttpResponse> Mcp(HttpRequest request, string client)
    {
        JsonNode? json;
        try { json = request.Body.Length == 0 ? null : JsonNode.Parse(request.Body); }
        catch (JsonException) { json = null; }
        if (json is null) return HttpResponse.Json(RpcError(null, -32700, "Parse error"), 400);

        string? newSession = null;

        async Task<JsonObject?> One(JsonObject message)
        {
            var hasId = message.TryGetPropertyValue("id", out var id);
            if (message.Str("method") is not { } method)
                return hasId ? RpcError(id, -32600, "Invalid request") : null;
            var parameters = message.Obj("params") ?? new JsonObject();
            if (!hasId || id is null) return null; // a notification
            switch (method)
            {
                case "initialize":
                {
                    var info = parameters.Obj("clientInfo");
                    var raw = info.Str("title") ?? info.Str("name") ?? "";
                    var name = raw.Length == 0 ? client : FriendlyName(raw);
                    if (name == "AI agent" && raw.Length > 0) name = raw;
                    var session = Guid.NewGuid().ToString().ToLowerInvariant();
                    lock (_lock) _sessions[session] = name;
                    newSession = session;
                    Record(name, "Connected", quiet: true);
                    var asked = parameters.Str("protocolVersion") ?? "";
                    string instructions;
                    try { instructions = (await Ask(new JsonObject { ["op"] = "instructions" })).Str("text") ?? ""; }
                    catch (Exception e) when (e is not OperationCanceledException) { instructions = ""; }
                    return RpcResult(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "house-control", ["title"] = "House Control", ["version"] = Version },
                        ["instructions"] = instructions,
                    });
                }
                case "ping":
                    return RpcResult(id, new JsonObject());
                case "tools/list":
                    try { return RpcResult(id, new JsonObject { ["tools"] = await Tools() }); }
                    catch (Exception e) when (e is not OperationCanceledException) { return RpcError(id, -32603, e.Message); }
                case "tools/call":
                {
                    if (parameters.Str("name") is not { } tool) return RpcError(id, -32602, "Missing tool name");
                    var caller = client;
                    if (newSession is { } s) lock (_lock) caller = _sessions.GetValueOrDefault(s, client);
                    var arguments = parameters.Obj("arguments")?.DeepClone().AsObject() ?? new JsonObject();
                    var result = await Call(tool, arguments, caller);
                    result.Remove("summary");
                    result.Remove("quiet");
                    return RpcResult(id, result);
                }
                case "resources/list":
                    return RpcResult(id, new JsonObject { ["resources"] = new JsonArray() });
                case "resources/templates/list":
                    return RpcResult(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
                case "prompts/list":
                    return RpcResult(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    return RpcError(id, -32601, $"Method not found: {method}");
            }
        }

        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        JsonNode? replies;
        if (json is JsonArray batch && batch.All(m => m is JsonObject))
        {
            var output = new JsonArray();
            foreach (var message in batch.OfType<JsonObject>().ToList())
                if (await One(message) is { } reply) output.Add(reply);
            replies = output.Count == 0 ? null : output;
        }
        else if (json is JsonObject message)
        {
            replies = await One(message);
        }
        else
        {
            return HttpResponse.Json(RpcError(null, -32600, "Invalid request"), 400);
        }
        if (newSession is not null) headers["Mcp-Session-Id"] = newSession;
        if (replies is null) return new HttpResponse(202, headers);
        return new HttpResponse(200, headers, Encoding.UTF8.GetBytes(Json.Serialize(replies)));
    }

    internal static JsonObject RpcResult(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    internal static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
