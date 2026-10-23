using System.Buffers.Binary;
using System.Net;
using System.Text;
using HouseControl.Core.Discovery;

namespace HouseControl.Core.Tests;

/// <summary>The multicast DNS packets behind discovery, built by hand, and what is made of them.</summary>
public class DiscoveryTests
{
    /// <summary>Writes a DNS response byte by byte, remembering where names start so later ones can point at them.</summary>
    private sealed class Packet
    {
        private readonly List<byte> _bytes = new();
        private int _records;

        public Packet(ushort flags = 0x8400)
        {
            U16(0); U16(flags); U16(0); U16(0); U16(0); U16(0);
        }

        public int Offset => _bytes.Count;

        private void U16(ushort v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); }
        private void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }

        /// <summary>Labels, then either the end of the name or a pointer to an earlier one. Answers where it starts.</summary>
        public int Name(string[] labels, int? pointer = null)
        {
            var start = Offset;
            foreach (var label in labels)
            {
                var b = Encoding.UTF8.GetBytes(label);
                _bytes.Add((byte)b.Length);
                _bytes.AddRange(b);
            }
            if (pointer is { } p) U16((ushort)(0xC000 | p));
            else _bytes.Add(0);
            return start;
        }

        /// <summary>A record: <paramref name="name"/> writes the owner name, <paramref name="data"/> the data.</summary>
        public void Record(Action name, ushort type, uint ttl, Action data)
        {
            name();
            U16(type); U16(0x8001); U32(ttl);
            var lengthAt = Offset;
            U16(0);
            var start = Offset;
            data();
            var length = Offset - start;
            _bytes[lengthAt] = (byte)(length >> 8);
            _bytes[lengthAt + 1] = (byte)length;
            _records++;
        }

        public void Srv(ushort port, Action target) { U16(0); U16(0); U16(port); target(); }
        public void Txt(params string[] entries)
        {
            foreach (var e in entries)
            {
                var b = Encoding.UTF8.GetBytes(e);
                _bytes.Add((byte)b.Length);
                _bytes.AddRange(b);
            }
        }
        public void Raw(params byte[] bytes) => _bytes.AddRange(bytes);

        public byte[] Bytes()
        {
            var bytes = _bytes.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)_records); // all as answers
            return bytes;
        }
    }

    private static readonly string[] HaService = ["_home-assistant", "_tcp", "local"];
    private static readonly string[] HueService = ["_hue", "_tcp", "local"];

    /// <summary>
    /// What a Home Assistant answers: PTR to its instance, then SRV, TXT and A, every later
    /// name compressed against the earlier ones.
    /// </summary>
    private static byte[] HomeAssistantAnswer(string[] txt, uint ttl = 4500)
    {
        var p = new Packet();
        var service = 0;
        var instance = 0;
        var host = 0;
        p.Record(() => service = p.Name(HaService), MdnsPacket.Ptr, ttl,
                 () => instance = p.Name(["Home"], pointer: service));
        var local = service + 1 + "_home-assistant".Length + 1 + "_tcp".Length; // the "local" label
        p.Record(() => p.Name([], pointer: instance), MdnsPacket.Srv, 120,
                 () => p.Srv(8123, () => host = p.Name(["homeassistant"], pointer: local)));
        p.Record(() => p.Name([], pointer: instance), MdnsPacket.Txt, 4500, () => p.Txt(txt));
        p.Record(() => p.Name([], pointer: host), MdnsPacket.A, 120, () => p.Raw(192, 168, 1, 20));
        return p.Bytes();
    }

    [Fact]
    public void ParsesCompressedNamesAndRecords()
    {
        var records = MdnsPacket.Parse(HomeAssistantAnswer(["location_name=Test Home", "Version=2026.10.0", "flag"]))!;
        Assert.Equal(4, records.Count);
        Assert.Equal("_home-assistant._tcp.local", records[0].Key);
        Assert.Equal(["Home", "_home-assistant", "_tcp", "local"], records[0].Target);
        Assert.Equal("home._home-assistant._tcp.local", records[1].Key);
        Assert.Equal(8123, records[1].Port);
        Assert.Equal(["homeassistant", "local"], records[1].Target);
        Assert.Equal("Test Home", records[2].Txt!["location_name"]);
        Assert.Equal("2026.10.0", records[2].Txt!["version"]); // keys are case-insensitive
        Assert.Equal("", records[2].Txt!["flag"]);
        Assert.Equal("homeassistant.local", records[3].Key);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), records[3].Address);
    }

    [Fact]
    public void TurnsAnswersIntoHomeAssistants()
    {
        var records = MdnsPacket.Parse(HomeAssistantAnswer(["location_name=Test Home", "version=2026.10.0",
            "internal_url=http://192.168.1.20:8123", "base_url=http://other:8123"]))!;
        var found = ServiceDiscovery.Collect(MdnsPacket.Labels("_home-assistant._tcp.local"), records);
        var ha = Assert.Single(found);
        Assert.Equal(("Home", "192.168.1.20", 8123), (ha.Name, ha.Host, ha.Port));
        var json = ServiceDiscovery.HomeAssistantResults(found);
        Assert.Equal("""[{"name":"Test Home","url":"http://192.168.1.20:8123","version":"2026.10.0"}]""", json.ToJsonString());

        // No advertised address: the one it answered from, and its instance name.
        var bare = ServiceDiscovery.Collect(MdnsPacket.Labels("_home-assistant._tcp.local"), MdnsPacket.Parse(HomeAssistantAnswer(["internal_url="]))!);
        Assert.Equal("""[{"name":"Home","url":"http://192.168.1.20:8123","version":""}]""",
            ServiceDiscovery.HomeAssistantResults(bare).ToJsonString());

        // Records for another service are not a Home Assistant.
        Assert.Empty(ServiceDiscovery.Collect(MdnsPacket.Labels("_hue._tcp.local"), records));
    }

    [Fact]
    public void TurnsAnswersIntoHueBridges()
    {
        var p = new Packet();
        var service = 0;
        var real = 0;
        var lab = 0;
        p.Record(() => service = p.Name(HueService), MdnsPacket.Ptr, 4500, () => real = p.Name(["Hue Bridge - 1A2B3C"], pointer: service));
        p.Record(() => p.Name([], pointer: service), MdnsPacket.Ptr, 4500, () => lab = p.Name(["Lab bridge"], pointer: service));
        var local = service + 1 + "_hue".Length + 1 + "_tcp".Length;
        var realHost = 0;
        p.Record(() => p.Name([], pointer: real), MdnsPacket.Srv, 120, () => p.Srv(443, () => realHost = p.Name(["ecb5fa1a2b3c"], pointer: local)));
        p.Record(() => p.Name([], pointer: real), MdnsPacket.Txt, 4500, () => p.Txt("bridgeid=ecb5fafffe1a2b3c", "modelid=BSB002"));
        p.Record(() => p.Name([], pointer: realHost), MdnsPacket.A, 120, () => p.Raw(10, 0, 0, 2));
        // The lab's bridge has no address record: its host name stands in.
        p.Record(() => p.Name([], pointer: lab), MdnsPacket.Srv, 120, () => p.Srv(8443, () => p.Name(["lab-host"], pointer: local)));
        var records = MdnsPacket.Parse(p.Bytes())!;
        var found = ServiceDiscovery.Collect(MdnsPacket.Labels("_hue._tcp.local"), records);
        Assert.Equal(
            """[{"name":"Hue Bridge - 1A2B3C","host":"10.0.0.2","bridgeId":"ecb5fafffe1a2b3c"},{"name":"Lab bridge","host":"lab-host.local:8443","bridgeId":""}]""",
            ServiceDiscovery.HueResults(found).ToJsonString());
    }

    [Fact]
    public void LeavesOutServicesThatSaidGoodbye()
    {
        var records = MdnsPacket.Parse(HomeAssistantAnswer([], ttl: 0))!;
        Assert.Empty(ServiceDiscovery.Collect(MdnsPacket.Labels("_home-assistant._tcp.local"), records));
    }

    [Fact]
    public void RefusesBrokenPacketsWithoutThrowing()
    {
        // A name that points at itself.
        var loop = new Packet();
        loop.Record(() => loop.Name([], pointer: 12), MdnsPacket.A, 1, () => loop.Raw(1, 2, 3, 4));
        Assert.Null(MdnsPacket.Parse(loop.Bytes()));

        var good = HomeAssistantAnswer(["a=b"]);
        for (var cut = 0; cut < good.Length; cut++) Assert.Null(MdnsPacket.Parse(good.AsSpan(0, cut)));

        // A question is not an answer.
        Assert.Null(MdnsPacket.Parse(MdnsPacket.Query([(MdnsPacket.Labels("_hue._tcp.local"), MdnsPacket.Ptr)])));

        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            if (junk.Length > 3) junk[2] |= 0x80;
            _ = MdnsPacket.Parse(junk); // must not throw
        }
    }

    [Fact]
    public void WritesAQuery()
    {
        var query = MdnsPacket.Query([(MdnsPacket.Labels("_hue._tcp.local."), MdnsPacket.Ptr)]);
        byte[] expected =
        [
            0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            4, (byte)'_', (byte)'h', (byte)'u', (byte)'e',
            4, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
            5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            0, 12, 0, 1,
        ];
        Assert.Equal(expected, query);
    }

    [Fact]
    public void AsksForWhatTheAnswersLeftOut()
    {
        var service = MdnsPacket.Labels("_home-assistant._tcp.local");
        Assert.Equal([("_home-assistant._tcp.local", MdnsPacket.Ptr)], Keys(ServiceDiscovery.Questions(service, [])));

        var instance = MdnsPacket.Labels("Home._home-assistant._tcp.local");
        var ptr = new DnsRecord(service, MdnsPacket.Ptr, 4500) { Target = instance };
        Assert.Equal(
            [("_home-assistant._tcp.local", MdnsPacket.Ptr), ("home._home-assistant._tcp.local", MdnsPacket.Srv), ("home._home-assistant._tcp.local", MdnsPacket.Txt)],
            Keys(ServiceDiscovery.Questions(service, [ptr])));

        var srv = new DnsRecord(instance, MdnsPacket.Srv, 120) { Target = MdnsPacket.Labels("ha.local"), Port = 8123 };
        var txt = new DnsRecord(instance, MdnsPacket.Txt, 4500) { Txt = new Dictionary<string, string>() };
        Assert.Equal([("_home-assistant._tcp.local", MdnsPacket.Ptr), ("ha.local", MdnsPacket.A)],
            Keys(ServiceDiscovery.Questions(service, [ptr, srv, txt])));

        static List<(string, ushort)> Keys(List<(IReadOnlyList<string>, ushort)> questions) =>
            questions.Select(q => (MdnsPacket.Key(q.Item1), q.Item2)).ToList();
    }

    [Fact]
    public void ReadsPhilipsDiscoveryService()
    {
        var json = ServiceDiscovery.HueCloudResults("""[{"id":"ecb5fafffe1a2b3c","internalipaddress":"10.0.0.2","port":443},{"id":"x"}]""");
        Assert.Equal("""[{"name":"Hue Bridge","host":"10.0.0.2","bridgeId":"ecb5fafffe1a2b3c"}]""", json.ToJsonString());
        Assert.Empty(ServiceDiscovery.HueCloudResults("<html>rate limited</html>"));
        Assert.Empty(ServiceDiscovery.HueCloudResults("{}"));
    }
}
