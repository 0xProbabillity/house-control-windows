using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HouseControl.Core;

/// <summary>
/// Trusts each Hue Bridge's self-signed certificate the first time, and only that
/// certificate afterwards.
/// </summary>
/// <remarks>
/// Used as the TLS validation callback of the Hue service's own HTTP clients only, so nothing
/// else in the app trusts a certificate it would otherwise refuse. TLS handshakes happen on
/// pool threads, hence the lock.
/// </remarks>
internal sealed class BridgeTrust
{
    private readonly object _lock = new();
    /// <summary>
    /// Bare host → the fingerprint expected from it. A host that is here with null is being
    /// paired right now: whatever it presents is accepted and remembered.
    /// </summary>
    private readonly Dictionary<string, string?> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public void Pin(string host, string? fingerprint)
    {
        lock (_lock) _pins[Bare(host)] = fingerprint;
    }

    /// <summary>The fingerprint of the certificate <paramref name="host"/> presented last.</summary>
    public string? Fingerprint(string host)
    {
        lock (_lock) return _seen.TryGetValue(Bare(host), out var f) ? f : null;
    }

    /// <summary>"192.168.1.2:8443" → "192.168.1.2", "[fe80::1]:443" → "fe80::1": what TLS handshakes name.</summary>
    public static string Bare(string host)
    {
        if (host.StartsWith('[') && host.IndexOf(']') is var end and > 0) return host[1..end];
        var parts = host.Split(':');
        return parts.Length == 2 ? parts[0] : host;
    }

    /// <summary>SHA-256 of the certificate's DER bytes, lowercase hex.</summary>
    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    /// <summary>The <see cref="RemoteCertificateValidationCallback"/> of the Hue clients.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var host = sender switch
        {
            SslStream ssl => ssl.TargetHostName,
            HttpRequestMessage request => request.RequestUri?.Host,
            _ => null,
        };
        if (host is null || certificate is null) return errors == SslPolicyErrors.None;
        host = Bare(host);
        var fingerprint = FingerprintOf(certificate);
        bool known;
        string? expected;
        lock (_lock)
        {
            known = _pins.TryGetValue(host, out expected);
            _seen[host] = fingerprint;
        }
        if (!known) return errors == SslPolicyErrors.None; // not a bridge we know: the usual checks
        if (expected is null) return true;                 // pairing now
        return string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase);
    }
}
