using System.Security.Cryptography;

namespace HouseControl.Core;

/// <summary>Where a secret (the Smart Life session, a Home Assistant token, a Hue key) is kept.</summary>
public interface ISecretStore
{
    byte[]? Read();
    /// <summary>Stores <paramref name="data"/>; null removes the secret.</summary>
    void Write(byte[]? data);
    /// <summary>Changes whenever another process rewrites the secret. Null when that cannot happen.</summary>
    DateTime? Stamp { get; }
}

/// <summary>
/// A file next to the plan, encrypted with Windows' Data Protection API for the signed-in user:
/// another account on this PC, or a copy of the file on another PC, cannot read it.
/// </summary>
public sealed class ProtectedFileSecretStore(string path) : ISecretStore
{
    private static readonly byte[] Entropy = "HouseControl.Secret.v1"u8.ToArray();

    public string Path { get; } = path;

    public byte[]? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(byte[]? data)
    {
        try
        {
            if (data is null)
            {
                File.Delete(Path);
                return;
            }
            Files.WriteAtomic(Path, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
        }
    }

    public DateTime? Stamp => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;
}

/// <summary>Keeps a secret in memory only (tests, throw-away runs).</summary>
public sealed class MemorySecretStore(byte[]? data = null) : ISecretStore
{
    private byte[]? _data = data;
    public byte[]? Read() => _data;
    public void Write(byte[]? data) => _data = data;
    public DateTime? Stamp => null;
}

public static class Files
{
    /// <summary>Writes through a temporary file, so a crash never leaves half a file behind.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }
}
