using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace HouseControl.Core.Discovery;

/// <summary>
/// Finds Home Assistant servers and Hue Bridges on the local network with multicast DNS (what
/// Apple calls Bonjour), so connecting one is a click rather than typing an address. Windows
/// has no browsing API worth depending on, so this asks the network itself: a query to
/// 224.0.0.251:5353 on every network adapter, then whatever comes back for a few seconds.
/// </summary>
public static class ServiceDiscovery
{
    /// <summary>One service instance on the network.</summary>
    public sealed record Found(string Name, string Host, int Port, IReadOnlyDictionary<string, string> Txt)
    {
        public JsonObject ToJson() => new()
        {
            ["name"] = Name,
            ["host"] = Host,
            ["port"] = Port,
            ["txt"] = new JsonObject(Txt.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        };
    }

    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Home Assistant servers: their advertised address when they have one.</summary>
    public static async Task<JsonArray> HomeAssistantsAsync()
    {
        try { return HomeAssistantResults(await BrowseAsync("_home-assistant._tcp")); }
        catch (Exception) { return new JsonArray(); }
    }

    /// <summary>Hue Bridges: multicast DNS first, then Philips' discovery service for networks that block it.</summary>
    public static async Task<JsonArray> HueBridgesAsync()
    {
        var found = new JsonArray();
        try { found = HueResults(await BrowseAsync("_hue._tcp")); }
        catch (Exception) { }
        if (found.Count > 0) return found;
        try
        {
            var text = await Http.GetStringAsync("https://discovery.meethue.com/");
            return HueCloudResults(text);
        }
        catch (Exception)
        {
            return found;
        }
    }

    internal static JsonArray HomeAssistantResults(IEnumerable<Found> found) =>
        new(found.Select(f =>
        {
            var url = new[] { f.Txt.GetValueOrDefault("internal_url"), f.Txt.GetValueOrDefault("base_url") }
                          .FirstOrDefault(u => !string.IsNullOrEmpty(u))
                      ?? $"http://{f.Host}:{f.Port}";
            return (JsonNode?)new JsonObject
            {
                ["name"] = f.Txt.GetValueOrDefault("location_name") ?? f.Name,
                ["url"] = url,
                ["version"] = f.Txt.GetValueOrDefault("version") ?? "",
            };
        }).ToArray());

    internal static JsonArray HueResults(IEnumerable<Found> found) =>
        new(found.Select(f => (JsonNode?)new JsonObject
        {
            // Real bridges serve HTTPS on 443; anything else (a lab's bridge) keeps its port.
            ["name"] = f.Name,
            ["host"] = f.Port == 443 ? f.Host : $"{f.Host}:{f.Port}",
            ["bridgeId"] = f.Txt.GetValueOrDefault("bridgeid") ?? "",
        }).ToArray());

    /// <summary>What discovery.meethue.com answers: <c>[{"id": "…", "internalipaddress": "…"}]</c>.</summary>
    internal static JsonArray HueCloudResults(string text)
    {
        var bridges = new JsonArray();
        try
        {
            if (JsonNode.Parse(text) is not JsonArray list) return bridges;
            foreach (var bridge in list.OfType<JsonObject>())
            {
                if (bridge.Str("internalipaddress") is not { Length: > 0 } ip) continue;
                bridges.Add(new JsonObject { ["name"] = "Hue Bridge", ["host"] = ip, ["bridgeId"] = bridge.Str("id") ?? "" });
            }
        }
        catch (System.Text.Json.JsonException) { }
        return bridges;
    }

    // MARK: Browsing

    /// <summary>
    /// Everything advertising <paramref name="type"/> ("_hue._tcp", "_home-assistant._tcp")
    /// within <paramref name="seconds"/>, sorted by name. Never throws: a network that cannot be
    /// asked simply has nothing to show.
    /// </summary>
    public static async Task<IReadOnlyList<Found>> BrowseAsync(string type, double seconds = 2.5)
    {
        var service = MdnsPacket.Labels(type + ".local");
        var records = new List<DnsRecord>();
        var sockets = OpenSockets();
        if (sockets.Count == 0) return [];
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var receivers = sockets.Select(s => ReceiveAllAsync(s, records, stop.Token)).ToList();
            // Ask three times: the first query names the service, the later ones ask for whatever
            // the answers so far left out (the address behind an instance, say), and repeat the
            // first for responders that missed it.
            for (var round = 0; round < 3 && !stop.IsCancellationRequested; round++)
            {
                List<DnsRecord> known;
                lock (records) known = records.ToList();
                var query = MdnsPacket.Query(Questions(service, known));
                foreach (var socket in sockets.Where(s => s.Sends))
                {
                    try { await socket.Socket.SendToAsync(query, SocketFlags.None, Group); }
                    catch (Exception) { }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(seconds / 3), stop.Token); }
                catch (OperationCanceledException) { }
            }
            await Task.WhenAll(receivers);
        }
        catch (Exception)
        {
            // Return whatever was heard.
        }
        finally
        {
            foreach (var s in sockets) s.Socket.Dispose();
        }
        lock (records) return Collect(service, records);
    }

    private sealed record Endpoint(Socket Socket, bool Sends);

    /// <summary>
    /// One socket per IPv4 adapter that sends the queries (responders answer it directly), plus
    /// one on port 5353 that hears the answers sent to the whole group, when Windows lets us share it.
    /// </summary>
    private static List<Endpoint> OpenSockets()
    {
        var sockets = new List<Endpoint>();
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                addresses.AddRange(adapter.GetIPProperties().UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)));
            }
        }
        catch (NetworkInformationException) { }

        foreach (var address in addresses.Distinct())
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(new Endpoint(socket, Sends: true));
            }
            catch (Exception)
            {
                socket?.Dispose();
            }
        }
        if (sockets.Count == 0) return sockets;

        Socket? group = null;
        try
        {
            group = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            group.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            group.Bind(new IPEndPoint(IPAddress.Any, 5353));
            var joined = 0;
            foreach (var address in addresses.Distinct())
            {
                try
                {
                    group.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, address));
                    joined++;
                }
                catch (SocketException) { }
            }
            if (joined > 0) sockets.Add(new Endpoint(group, Sends: false));
            else group.Dispose();
        }
        catch (Exception)
        {
            group?.Dispose();
        }
        return sockets;
    }

    private static async Task ReceiveAllAsync(Endpoint endpoint, List<DnsRecord> records, CancellationToken stop)
    {
        var buffer = new byte[9000];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var result = await endpoint.Socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop);
                if (MdnsPacket.Parse(buffer.AsSpan(0, result.ReceivedBytes)) is { } parsed)
                    lock (records) records.AddRange(parsed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
            {
                // An ICMP "port unreachable" from an earlier send, or an oversized packet: keep listening.
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>The PTR question for the service, then SRV/TXT for instances and A for hosts still unexplained.</summary>
    internal static List<(IReadOnlyList<string>, ushort)> Questions(IReadOnlyList<string> service, IReadOnlyCollection<DnsRecord> known)
    {
        var questions = new List<(IReadOnlyList<string>, ushort)> { (service, MdnsPacket.Ptr) };
        var serviceKey = MdnsPacket.Key(service);
        var instances = known.Where(r => r.Type == MdnsPacket.Ptr && r.Key == serviceKey && r.Target != null)
            .Select(r => r.Target!).DistinctBy(MdnsPacket.Key).ToList();
        foreach (var instance in instances)
        {
            var key = MdnsPacket.Key(instance);
            var srv = known.FirstOrDefault(r => r.Type == MdnsPacket.Srv && r.Key == key);
            if (srv is null) questions.Add((instance, MdnsPacket.Srv));
            if (!known.Any(r => r.Type == MdnsPacket.Txt && r.Key == key)) questions.Add((instance, MdnsPacket.Txt));
            if (srv?.Target is { } host && !known.Any(r => r.Type == MdnsPacket.A && r.Key == MdnsPacket.Key(host)))
                questions.Add((host, MdnsPacket.A));
            if (questions.Count >= 30) break;
        }
        return questions;
    }

    /// <summary>
    /// Pieces the records heard into services: PTR names the instances, SRV gives each one's
    /// host and port, TXT its details, A the host's address. An instance without an SRV record
    /// cannot be reached and is left out; one whose host has no IPv4 address keeps the host name.
    /// </summary>
    internal static List<Found> Collect(IReadOnlyList<string> service, IReadOnlyCollection<DnsRecord> records)
    {
        var serviceKey = MdnsPacket.Key(service);
        var found = new List<Found>();
        var instances = records.Where(r => r.Type == MdnsPacket.Ptr && r.Key == serviceKey && r.Target is { Count: > 0 })
            .Select(r => r.Target!).DistinctBy(MdnsPacket.Key);
        foreach (var instance in instances)
        {
            var key = MdnsPacket.Key(instance);
            // A goodbye (TTL 0) means the service just left.
            if (records.Any(r => r.Type == MdnsPacket.Ptr && r.Key == serviceKey && r.Ttl == 0
                                 && r.Target != null && MdnsPacket.Key(r.Target) == key)
                && !records.Any(r => r.Type == MdnsPacket.Ptr && r.Ttl > 0 && r.Target != null && MdnsPacket.Key(r.Target) == key))
                continue;
            var srv = records.LastOrDefault(r => r.Type == MdnsPacket.Srv && r.Key == key && r.Target is { Count: > 0 });
            if (srv is null) continue;
            var hostKey = MdnsPacket.Key(srv.Target!);
            var address = records.LastOrDefault(r => r.Type == MdnsPacket.A && r.Key == hostKey && r.Address != null)?.Address;
            var host = address?.ToString() ?? string.Join(".", srv.Target!);
            var txt = records.LastOrDefault(r => r.Type == MdnsPacket.Txt && r.Key == key)?.Txt ?? new Dictionary<string, string>();
            found.Add(new Found(instance[0], host, srv.Port, txt));
        }
        return found.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }
}
