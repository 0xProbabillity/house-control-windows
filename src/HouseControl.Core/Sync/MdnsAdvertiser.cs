using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HouseControl.Core.Sync;

/// <summary>
/// Announces this PC on the local network the way a Mac does with Bonjour:
/// <c>&lt;name&gt;._housecontrol._tcp.local</c> with the pairing's id in its TXT record, so a paired
/// iPhone finds it. Windows' own multicast DNS service answers for it (DnsServiceRegister,
/// Windows 10 1809 and newer); nothing here listens on port 5353 itself.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private IntPtr _instance;
    private IntPtr _request;
    private string? _current;
    /// <summary>Kept alive for as long as Windows may call it.</summary>
    private static readonly RegisterComplete Completed = (status, _, instance) =>
    {
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
        LastStatus = status;
    };

    /// <summary>What Windows said about the last registration: 0 when the name is on the network.</summary>
    public static uint? LastStatus { get; private set; }

    /// <summary>True when this version of Windows can announce services at all.</summary>
    public static bool IsSupported
    {
        get
        {
            try { return NativeLibrary.TryLoad("dnsapi.dll", out var lib) && NativeLibrary.TryGetExport(lib, "DnsServiceRegister", out _); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Announces (or re-announces, after a change of name, port or network) the service.</summary>
    public bool Advertise(string instanceName, int port, IReadOnlyDictionary<string, string> txt)
    {
        var host = Dns.GetHostName();
        var address = PreferredAddress();
        var key = $"{instanceName}|{port}|{address}|{string.Join(",", txt.Select(p => $"{p.Key}={p.Value}"))}";
        if (key == _current && _request != IntPtr.Zero) return true;
        Withdraw();
        if (!IsSupported) return false;

        var keys = txt.Keys.ToArray();
        var values = keys.Select(k => txt[k]).ToArray();
        var ip4 = address is { } a ? BitConverter.ToUInt32(a.GetAddressBytes(), 0) : 0u;
        var ip4Ptr = IntPtr.Zero;
        try
        {
            if (address != null)
            {
                ip4Ptr = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(ip4Ptr, unchecked((int)ip4));
            }
            _instance = DnsServiceConstructInstance($"{Escape(instanceName)}.{SyncFraming.ServiceType}.local", $"{host}.local",
                ip4Ptr, IntPtr.Zero, (ushort)port, 0, 0, (uint)keys.Length, keys, values);
        }
        finally
        {
            if (ip4Ptr != IntPtr.Zero) Marshal.FreeHGlobal(ip4Ptr);
        }
        if (_instance == IntPtr.Zero) return false;

        _request = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterRequest>());
        Marshal.StructureToPtr(new RegisterRequest
        {
            Version = 1, // DNS_QUERY_REQUEST_VERSION1
            InterfaceIndex = 0, // every interface
            ServiceInstance = _instance,
            Callback = Marshal.GetFunctionPointerForDelegate(Completed),
        }, _request, false);
        var result = DnsServiceRegister(_request, IntPtr.Zero);
        if (result != DnsRequestPending)
        {
            LastStatus = result;
            Free();
            return false;
        }
        _current = key;
        return true;
    }

    /// <summary>Takes the announcement off the network.</summary>
    public void Withdraw()
    {
        if (_request != IntPtr.Zero)
        {
            DnsServiceDeRegister(_request, IntPtr.Zero);
            // Windows tells the callback when the goodbye has gone out; the request has to stay valid until then.
            Thread.Sleep(50);
        }
        Free();
        _current = null;
    }

    private void Free()
    {
        if (_instance != IntPtr.Zero) DnsServiceFreeInstance(_instance);
        _instance = IntPtr.Zero;
        if (_request != IntPtr.Zero) Marshal.FreeHGlobal(_request);
        _request = IntPtr.Zero;
    }

    public void Dispose() => Withdraw();

    /// <summary>Dots and backslashes in an instance name are escaped, as DNS-SD requires.</summary>
    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace(".", "\\.");

    /// <summary>
    /// The IPv4 address phones on the Wi-Fi reach this PC at: that of the Wi-Fi or wired adapter
    /// with a default gateway. A VPN tunnel (WireGuard, OpenVPN, NordLynx…) or a virtual switch
    /// has an address no phone on the Wi-Fi can reach, so it comes last, even with a gateway.
    /// </summary>
    public static IPAddress? PreferredAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n, p: n.GetIPProperties()))
            .SelectMany(x => x.p.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                .Select(u => (x.n, x.p, u.Address)))
            .OrderBy(x => IsTunnel(x.n))
            .ThenByDescending(x => x.n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                                   or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT)
            .ThenByDescending(x => x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
        return candidates.Select(x => x.Address).FirstOrDefault();
    }

    /// <summary>A VPN tunnel or a virtual adapter, by its type or what its driver calls it.</summary>
    internal static bool IsTunnel(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        var text = $"{n.Name} {n.Description}";
        return new[] { "VPN", "TAP", "TUN", "WireGuard", "Wintun", "NordLynx", "Tunnel", "Virtual", "Hyper-V", "VMware", "VirtualBox", "Loopback" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private const uint DnsRequestPending = 9506;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Callback;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertyCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
}
