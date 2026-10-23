using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Agent;

/// <summary>One HTTP request, as much of it as the AI gateway needs.</summary>
public sealed record HttpRequest
{
    public string Method { get; init; } = "GET";
    /// <summary>The path without the query, still percent-encoded: the router decodes it segment by segment.</summary>
    public string Path { get; init; } = "/";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    /// <summary>Header names in lower case.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[] Body { get; init; } = [];

    public HttpRequest() { }

    public HttpRequest(string method, string path, IReadOnlyDictionary<string, string>? headers = null, byte[]? body = null)
    {
        Method = method;
        Path = path;
        Headers = headers ?? new Dictionary<string, string>();
        Body = body ?? [];
    }

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>What the gateway answers. Header names compare without regard to case.</summary>
public sealed class HttpResponse
{
    public int Status { get; set; }
    public Dictionary<string, string> Headers { get; }
    public byte[] Body { get; set; }

    public HttpResponse(int status = 200, Dictionary<string, string>? headers = null, byte[]? body = null)
    {
        Status = status;
        Headers = new Dictionary<string, string>(headers ?? new(), StringComparer.OrdinalIgnoreCase);
        Body = body ?? [];
    }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public static HttpResponse Text(string text, int status = 200, string type = "text/plain; charset=utf-8") =>
        new(status, new() { ["Content-Type"] = type }, Encoding.UTF8.GetBytes(text));

    /// <summary>JSON with sorted keys and unescaped slashes, so the same answer always reads the same.</summary>
    public static HttpResponse Json(JsonNode node, int status = 200) =>
        new(status, new() { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes(Core.Json.Serialize(Core.Json.Sorted(node))));

    /// <summary>A fresh 404 each time: a response can be changed by whoever holds it.</summary>
    public static HttpResponse NotFound => Text("Not found\n", 404);

    internal static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => status < 400 ? "OK" : "Error",
    };
}

/// <summary>What <see cref="HttpParser.Parse"/> found in the buffer.</summary>
public abstract record HttpParseResult
{
    /// <summary>The request is not all there yet: read more.</summary>
    public sealed record Incomplete : HttpParseResult;
    /// <summary>Not HTTP the server will answer; reply with <paramref name="Status"/> and close.</summary>
    public sealed record Invalid(int Status) : HttpParseResult;
    public sealed record Parsed(HttpRequest Request, bool KeepAlive) : HttpParseResult;
}

/// <summary>Parses HTTP/1.1 requests out of a byte buffer (fixed length and chunked bodies).</summary>
public static class HttpParser
{
    private static readonly byte[] Separator = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>On success the request's bytes are removed from <paramref name="buffer"/>.</summary>
    public static HttpParseResult Parse(ref byte[] buffer, int maxBody)
    {
        var end = IndexOf(buffer, Separator, 0);
        if (end < 0) return buffer.Length > 64 * 1024 ? new HttpParseResult.Invalid(400) : new HttpParseResult.Incomplete();
        string head;
        try { head = StrictUtf8.GetString(buffer, 0, end); }
        catch (DecoderFallbackException) { return new HttpParseResult.Invalid(400); }
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return new HttpParseResult.Invalid(400);
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        var cursor = end + Separator.Length;
        var body = new MemoryStream();
        if (headers.TryGetValue("transfer-encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var lineEnd = IndexOf(buffer, LineEnd, cursor);
                if (lineEnd < 0) return new HttpParseResult.Incomplete();
                var sizeText = Encoding.ASCII.GetString(buffer, cursor, lineEnd - cursor).Split(';')[0].Trim();
                if (!int.TryParse(sizeText, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var size) || size < 0)
                    return new HttpParseResult.Invalid(400);
                cursor = lineEnd + LineEnd.Length;
                if (size == 0)
                {
                    // Trailers, if any, end with an empty line.
                    var trailerEnd = IndexOf(buffer, LineEnd, cursor);
                    if (trailerEnd < 0) return new HttpParseResult.Incomplete();
                    if (trailerEnd != cursor)
                    {
                        var rest = IndexOf(buffer, Separator, cursor);
                        if (rest < 0) return new HttpParseResult.Incomplete();
                        cursor = rest + Separator.Length;
                    }
                    else
                    {
                        cursor = trailerEnd + LineEnd.Length;
                    }
                    break;
                }
                if (body.Length + size > maxBody) return new HttpParseResult.Invalid(413);
                if (buffer.Length - cursor < size + 2) return new HttpParseResult.Incomplete();
                body.Write(buffer, cursor, size);
                cursor += size + 2;
            }
        }
        else if (headers.TryGetValue("content-length", out var lengthText))
        {
            if (!long.TryParse(lengthText, out var length) || length < 0) return new HttpParseResult.Invalid(400);
            if (length > maxBody) return new HttpParseResult.Invalid(413);
            if (buffer.Length - cursor < length) return new HttpParseResult.Incomplete();
            body.Write(buffer, cursor, (int)length);
            cursor += (int)length;
        }

        var (path, query) = SplitTarget(parts[1]);
        var request = new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            Headers = headers,
            Body = body.ToArray(),
        };
        var keepAlive = !string.Equals(headers.GetValueOrDefault("connection"), "close", StringComparison.OrdinalIgnoreCase)
                        && parts[2] == "HTTP/1.1";
        buffer = buffer[cursor..];
        return new HttpParseResult.Parsed(request, keepAlive);
    }

    /// <summary>The still-encoded path and the decoded query of a request target ("/a/b?x=1" or an absolute URL).</summary>
    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var uri))
            target = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        var mark = target.IndexOf('?');
        var path = mark < 0 ? target : target[..mark];
        var query = new Dictionary<string, string>();
        if (mark >= 0)
        {
            foreach (var item in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = item.IndexOf('=');
                var name = Unescape(eq < 0 ? item : item[..eq]);
                query[name] = eq < 0 ? "" : Unescape(item[(eq + 1)..]);
            }
        }
        return (path.Length == 0 ? "/" : path, query);
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var at = haystack.AsSpan(start).IndexOf(needle);
        return at < 0 ? -1 : at + start;
    }
}
