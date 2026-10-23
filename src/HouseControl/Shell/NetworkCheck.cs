using System.Diagnostics;

namespace HouseControl.Shell;

/// <summary>
/// What stands between a paired iPhone and this PC: a network Windows treats as public, or a
/// firewall that has not been told House Control may accept connections. Read-only checks, and
/// fixes that go through Windows' own Settings page and administrator prompt.
/// </summary>
internal static class NetworkCheck
{
    /// <summary>The connected networks Windows treats as public, by name (VPN adapters without traffic left out).</summary>
    public static IReadOnlyList<string> PublicNetworks()
    {
        var names = new List<string>();
        try
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
            foreach (dynamic network in manager.GetNetworks(1)) // NLM_ENUM_NETWORK_CONNECTED
            {
                int category = network.GetCategory(); // 0 public, 1 private, 2 domain
                int connectivity = network.GetConnectivity();
                const int localOrInternet = 0x10 | 0x20 | 0x40; // IPv4 subnet, local network, internet
                if (category == 0 && (connectivity & localOrInternet) != 0) names.Add((string)network.GetName());
            }
        }
        catch (Exception e)
        {
            Log.Error("network check", e);
        }
        return names;
    }

    /// <summary>True when an enabled inbound rule lets this executable accept connections on private networks.</summary>
    public static bool FirewallAllows()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            // Firewall off for private networks: nothing to allow.
            if (!(bool)policy.FirewallEnabled[2]) return true;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app is null || !string.Equals(Environment.ExpandEnvironmentVariables(app), exe, StringComparison.OrdinalIgnoreCase)) continue;
                if ((bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 1 && ((int)rule.Profiles & 2) != 0) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Log.Error("firewall check", e);
            return true; // could not tell: do not nag
        }
    }

    /// <summary>
    /// Adds the inbound rule (private networks only) through netsh, after Windows' administrator
    /// prompt. False when the prompt was declined.
    /// </summary>
    public static bool AllowThroughFirewall()
    {
        var exe = Environment.ProcessPath ?? "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"House Control (iPhone sync)\" dir=in action=allow program=\"{exe}\" profile=private enable=yes")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(15_000);
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the prompt was declined
        }
    }

    /// <summary>Opens Settings where the current network can be made private.</summary>
    public static void OpenNetworkSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("open network settings", e); }
    }
}
