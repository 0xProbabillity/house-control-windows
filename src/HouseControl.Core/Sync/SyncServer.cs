using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace HouseControl.Core.Sync;

/// <summary>
/// This PC's side of the sync with the iPhone app: listens on the local network and hands the
/// floor plan, the Smart Life User Code and the Home Assistant and Hue connections to the phones
/// that were paired with it, and takes what they send back. It speaks exactly what the Mac app
/// speaks — TLS 1.2 with a pre-shared key, length-prefixed JSON — so the iPhone app needs no
/// change. Nothing listens until a phone has been paired for the first time.
/// </summary>
/// <remarks>
/// Connections are accepted and read on the thread pool; every message is handled, and every
/// event raised, on the synchronization context the server was created on.
/// </remarks>
public sealed class SyncServer : IDisposable
{
    private readonly ISecretStore _store;
    private readonly SynchronizationContext? _context;
    private readonly List<Peer> _peers = new();
    private Socket? _listener;
    private CancellationTokenSource? _stop;

    public SyncPairing? Pairing { get; private set; }
    public bool IsEnabled => Pairing != null;
    /// <summary>The port the listener got, while it runs.</summary>
    public int? Port { get; private set; }

    /// <summary>What to send: read every time a phone says hello or <see cref="Broadcast"/> is called.</summary>
    public Func<SyncState>? Snapshot { get; set; }
    /// <summary>A phone connected or disconnected, or the listener started or stopped.</summary>
    public event Action? PeersChanged;
    /// <summary>
    /// A phone asks this PC to show the QR code of its own Smart Life sign-in (the content), or to
    /// take it down again (null). The second argument is the phone's name.
    /// </summary>
    public event Action<string?, string>? ShowQr;
    /// <summary>A phone sent the devices of its Apple Home (which only it can reach), for the editor.</summary>
    public event Action<JsonArray, string>? CatalogReceived;
    /// <summary>A phone sent the floor plan it edited (the file byte for byte, already checked to be a plan).</summary>
    public event Action<byte[], string>? PlanReceived;
    /// <summary>A phone sent its Home Assistant and Hue connections and the Smart Life User Code it signed in with.</summary>
    public event Action<JsonObject, string, string>? ConnectionsReceived;

    private sealed class Peer(Socket socket)
    {
        public Socket Socket { get; } = socket;
        public Stream? Stream { get; set; }
        public string Name { get; set; } = "";
        /// <summary>The plan revision this phone is known to hold.</summary>
        public string Revision { get; set; } = "";
        public Channel<byte[]> Outbox { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public bool Closed { get; set; }
    }

    /// <summary><paramref name="store"/> keeps the pairing (sync.json next to the plan, encrypted for this Windows user).</summary>
    public SyncServer(ISecretStore store)
    {
        _store = store;
        _context = SynchronizationContext.Current;
        Pairing = SyncPairing.Decode(store.Read());
    }

    /// <summary>Names of the phones connected right now.</summary>
    public IReadOnlyList<string> ConnectedDevices
    {
        get
        {
            lock (_peers) return _peers.Where(p => p.Name.Length > 0).Select(p => p.Name).Distinct().Order().ToList();
        }
    }

    /// <summary>Makes sure this PC can be paired with, and returns what the phone has to scan.</summary>
    public SyncPairing Enable(string name)
    {
        if (Pairing is not { } current) return Reset(name);
        if (current.Name != name)
        {
            // The PC was renamed: same key, new name on the network and in the code.
            Pairing = current with { Name = name };
            _store.Write(Pairing.Encode());
            Restart();
        }
        else
        {
            Start();
        }
        return Pairing;
    }

    /// <summary>A new key: every phone paired so far has to be paired again.</summary>
    public SyncPairing Reset(string name)
    {
        Pairing = SyncPairing.Generate(name);
        _store.Write(Pairing.Encode());
        Restart();
        return Pairing;
    }

    public void Disable()
    {
        Pairing = null;
        _store.Write(null);
        Stop();
    }

    /// <summary>Starts listening, on every interface, if this PC has been paired before.</summary>
    public void Start()
    {
        if (Pairing is null || _listener != null) return;
        var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        listener.Listen(8);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var stop = _stop = new CancellationTokenSource();
        var psk = Pairing.PreSharedKey;
        _ = Task.Run(() => AcceptLoop(listener, psk, stop.Token));
        PeersChanged?.Invoke();
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        try { _listener?.Close(); } catch (SocketException) { }
        _listener = null;
        Port = null;
        foreach (var peer in _peers.ToList()) Close(peer);
        _peers.Clear();
        PeersChanged?.Invoke();
    }

    private void Restart()
    {
        Stop();
        Start();
    }

    public void Dispose() => Stop();

    /// <summary>Sends the current state to every phone (after the plan was saved, or a connection changed).</summary>
    public void Broadcast()
    {
        if (Snapshot?.Invoke() is not { } state) return;
        foreach (var peer in _peers.Where(p => p.Name.Length > 0)) Send(state, peer);
    }

    private void Send(SyncState state, Peer peer)
    {
        Send(peer, state.Message(includingPlan: state.Revision != peer.Revision));
        peer.Revision = state.Revision;
    }

    private static void Send(Peer peer, JsonObject message)
    {
        if (!peer.Closed) peer.Outbox.Writer.TryWrite(SyncFraming.Frame(message));
    }

    // MARK: Connections (thread pool)

    private async Task AcceptLoop(Socket listener, byte[] psk, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptAsync(stop); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { await Task.Delay(500, CancellationToken.None); continue; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => Serve(socket, psk, stop));
        }
    }

    private void Serve(Socket socket, byte[] psk, CancellationToken stop)
    {
        var peer = new Peer(socket);
        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            // A phone that does not hold the key fails the handshake; one that never finishes it is dropped.
            socket.ReceiveTimeout = 15_000;
            var tls = new TlsServerProtocol(new NetworkStream(socket, ownsSocket: true));
            tls.Accept(new PskServer(psk));
            socket.ReceiveTimeout = 0;
            peer.Stream = tls.Stream;
        }
        catch (Exception)
        {
            try { socket.Close(); } catch (SocketException) { }
            return;
        }

        OnContext(() =>
        {
            if (stop.IsCancellationRequested) { Close(peer); return; }
            _peers.Add(peer);
        });
        _ = Task.Run(() => WriteLoop(peer));
        try
        {
            while (!stop.IsCancellationRequested && SyncFraming.Read(peer.Stream!) is { } message)
                OnContext(() => Handle(message, peer));
        }
        catch (Exception)
        {
            // The phone went away (the app left the screen, Wi-Fi changed): it reconnects by itself.
        }
        OnContext(() =>
        {
            Close(peer);
            if (_peers.Remove(peer) && peer.Name.Length > 0) PeersChanged?.Invoke();
        });
    }

    private static async Task WriteLoop(Peer peer)
    {
        try
        {
            await foreach (var frame in peer.Outbox.Reader.ReadAllAsync())
            {
                peer.Stream!.Write(frame, 0, frame.Length);
                peer.Stream.Flush();
            }
        }
        catch (Exception)
        {
            try { peer.Socket.Close(); } catch (SocketException) { }
        }
    }

    private static void Close(Peer peer)
    {
        if (peer.Closed) return;
        peer.Closed = true;
        peer.Outbox.Writer.TryComplete();
        try { peer.Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        try { peer.Socket.Close(); } catch (Exception) { }
    }

    private void OnContext(Action action)
    {
        if (_context is null) lock (_peers) action();
        else _context.Post(_ => action(), null);
    }

    // MARK: Messages (UI thread)

    private void Handle(JsonObject message, Peer peer)
    {
        if (peer.Closed) return;
        switch (message.Str("type"))
        {
            case "hello":
                var isNew = peer.Name.Length == 0;
                peer.Name = message.Str("device") is { Length: > 0 } device ? device : "iPhone";
                peer.Revision = message.Str("rev") ?? "";
                if (Snapshot?.Invoke() is { } state) Send(state, peer);
                if (isNew) PeersChanged?.Invoke();
                break;
            case "showQR":
                if (peer.Name.Length == 0) return;
                ShowQr?.Invoke(message.Str("content"), peer.Name);
                break;
            case "catalog":
                if (peer.Name.Length == 0 || message.Arr("devices") is not { } devices) return;
                CatalogReceived?.Invoke(devices, peer.Name);
                break;
            case "plan":
                if (peer.Name.Length == 0 || message.Str("plan") is not { } text) return;
                var data = Encoding.UTF8.GetBytes(text);
                try { PlanStore.Validate(data); }
                catch (InvalidDataException) { return; }
                // The phone holds this revision now: the broadcast that follows need not echo it.
                peer.Revision = PlanStore.Revision(data);
                PlanReceived?.Invoke(data, peer.Name);
                break;
            case "connections":
                if (peer.Name.Length == 0 || message.Obj("connections") is not { } shared) return;
                ConnectionsReceived?.Invoke(shared, message.Str("userCode") ?? "", peer.Name);
                break;
        }
    }

    // MARK: TLS with a pre-shared key

    /// <summary>
    /// TLS 1.2, TLS_PSK_WITH_AES_128_GCM_SHA256 and nothing else, with the key derived from the
    /// pairing: a phone that does not hold the pairing key cannot complete the handshake.
    /// </summary>
    internal sealed class PskServer(byte[] psk) : PskTlsServer(new BcTlsCrypto(new SecureRandom()), new Identities(psk))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    }

    /// <summary>The phone names itself "housecontrol"; whatever it says, only the right key completes the handshake.</summary>
    private sealed class Identities(byte[] psk) : TlsPskIdentityManager
    {
        public byte[]? GetHint() => null;
        // A copy every time: BouncyCastle wipes the key it was given once the handshake is done.
        public byte[] GetPsk(byte[] identity) => (byte[])psk.Clone();
    }
}
