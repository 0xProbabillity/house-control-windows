using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HouseControl.Core.Agent;

/// <summary>
/// A deliberately small HTTP/1.1 server for the AI gateway. It listens on the loopback
/// interface only: the outside world reaches it through the tunnel process (which runs
/// on this PC and connects to 127.0.0.1), never directly, and nothing on the local
/// network can see it.
/// </summary>
/// <remarks>
/// Built on <see cref="TcpListener"/> rather than HttpListener, which needs a URL
/// reservation (administrator rights) for anything but localhost names. Connections are
/// accepted and read on the thread pool; <c>handler</c> is called from there too, so the
/// owner marshals it wherever its state lives.
/// </remarks>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private readonly HashSet<TcpClient> _connections = new();

    /// <summary>Requests larger than this are refused (a floor plan is a few dozen kB).</summary>
    public int MaxBody { get; set; } = 4 * 1024 * 1024;

    public LoopbackHttpServer(Func<HttpRequest, Task<HttpResponse>> handler)
    {
        _handler = handler;
    }

    public int? Port { get; private set; }
    public bool IsRunning => _listener != null;

    /// <summary>
    /// Starts listening on 127.0.0.1, on <paramref name="preferredPort"/> if it is free and on
    /// any port otherwise. Returns the port. Throws a <see cref="SocketException"/> when no port can be had.
    /// </summary>
    public int Start(int preferredPort = 0)
    {
        Stop();
        TcpListener listener;
        try
        {
            listener = Listen(preferredPort);
        }
        catch (SocketException) when (preferredPort != 0)
        {
            // The preferred port is taken: any free one will do.
            listener = Listen(0);
        }
        var stop = new CancellationTokenSource();
        lock (_lock)
        {
            _listener = listener;
            _stop = stop;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        _ = AcceptLoop(listener, stop.Token);
        return Port!.Value;
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        // Nobody else may bind the same address and port while the gateway has it.
        listener.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    public void Stop()
    {
        TcpClient[] open;
        lock (_lock)
        {
            _stop?.Cancel();
            _stop = null;
            _listener?.Stop();
            _listener = null;
            Port = null;
            open = _connections.ToArray();
            _connections.Clear();
        }
        foreach (var connection in open) connection.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException) { return; }
            lock (_lock)
            {
                if (stop.IsCancellationRequested) { client.Dispose(); return; }
                _connections.Add(client);
            }
            _ = Serve(client, stop);
        }
    }

    /// <summary>
    /// Serves requests one after another on a kept-alive connection: answers whatever is
    /// already buffered, and reads more when a request is still incomplete.
    /// </summary>
    private async Task Serve(TcpClient client, CancellationToken stop)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var buffer = Array.Empty<byte>();
            var chunk = new byte[256 * 1024];
            while (!stop.IsCancellationRequested)
            {
                switch (HttpParser.Parse(ref buffer, MaxBody))
                {
                    case HttpParseResult.Invalid invalid:
                        await Send(stream, HttpResponse.Text("Bad request\n", invalid.Status), close: true, stop);
                        return;
                    case HttpParseResult.Parsed parsed:
                        HttpResponse response;
                        try { response = await _handler(parsed.Request); }
                        catch (Exception e) when (e is not OperationCanceledException) { response = HttpResponse.Text("Something went wrong.\n", 500); }
                        await Send(stream, response, close: !parsed.KeepAlive, stop);
                        if (!parsed.KeepAlive) return;
                        break;
                    default:
                        var read = await stream.ReadAsync(chunk, stop);
                        if (read == 0) return;
                        var grown = new byte[buffer.Length + read];
                        buffer.CopyTo(grown, 0);
                        Array.Copy(chunk, 0, grown, buffer.Length, read);
                        buffer = grown;
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock) _connections.Remove(client);
            client.Dispose();
        }
    }

    private static async Task Send(NetworkStream stream, HttpResponse response, bool close, CancellationToken stop)
    {
        var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("Content-Length", response.Body.Length.ToString());
        headers["Connection"] = close ? "close" : "keep-alive";
        headers.TryAdd("Cache-Control", "no-store");
        headers["X-Robots-Tag"] = "noindex, nofollow";
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {HttpResponse.Reason(response.Status)}\r\n");
        foreach (var (name, value) in headers.OrderBy(h => h.Key, StringComparer.Ordinal)) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), stop);
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body, stop);
        await stream.FlushAsync(stop);
    }
}
