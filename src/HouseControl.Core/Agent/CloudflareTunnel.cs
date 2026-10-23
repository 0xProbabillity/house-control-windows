using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace HouseControl.Core.Agent;

/// <summary>Where the tunnel is.</summary>
public abstract record TunnelState
{
    public sealed record Stopped : TunnelState;
    public sealed record Starting : TunnelState;
    public sealed record Live(string Url) : TunnelState;
    public sealed record Failed(string Message) : TunnelState;
}

/// <summary>
/// Gives the loopback AI gateway a public HTTPS address through a Cloudflare Quick Tunnel:
/// <c>cloudflared tunnel --url http://127.0.0.1:&lt;port&gt;</c>. No Cloudflare account, nothing to
/// configure; every start gets a new random <c>*.trycloudflare.com</c> name, and stopping the
/// process makes that name disappear for good.
/// </summary>
/// <remarks>
/// cloudflared is found where its installer (winget, the MSI) puts it or on the PATH, or
/// downloaded on request from Cloudflare's own GitHub releases into the app's data folder.
/// The process is put in a Windows job object that kills it when the job's last handle
/// closes, which happens when the app exits however it exits: it can never outlive the app.
/// Its output is read on the thread pool; <see cref="Changed"/> is raised on the
/// synchronization context the tunnel was made on.
/// </remarks>
public sealed partial class CloudflareTunnel : IDisposable
{
    private readonly string _directory;
    private readonly SynchronizationContext? _context;
    private Process? _process;
    private string _output = "";
    private string? _publicUrl;
    private CancellationTokenSource? _readyCheck;
    private TunnelState _state = new TunnelState.Stopped();

    public TunnelState State
    {
        get => _state;
        private set
        {
            if (value == _state) return;
            _state = value;
            Changed?.Invoke(value);
        }
    }

    public event Action<TunnelState>? Changed;

    private string PidFile => Path.Combine(_directory, "cloudflared.pid");
    /// <summary>Where a downloaded copy is kept: <c>&lt;data dir&gt;\bin\cloudflared.exe</c>.</summary>
    public string BundledBinary => Path.Combine(_directory, "bin", "cloudflared.exe");

    public CloudflareTunnel(string directory, SynchronizationContext? context = null)
    {
        _directory = directory;
        _context = context;
        KillLeftover();
    }

    /// <summary>The cloudflared to run, if there is one.</summary>
    public string? Binary
    {
        get
        {
            var candidates = new List<string> { BundledBinary };
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var dir = Environment.GetFolderPath(root);
                if (dir.Length > 0) candidates.Add(Path.Combine(dir, "cloudflared", "cloudflared.exe"));
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { candidates.Add(Path.Combine(dir.Trim('"'), "cloudflared.exe")); }
                catch (ArgumentException) { }
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public void Start(int port)
    {
        Stop();
        if (Binary is not { } binary)
        {
            State = new TunnelState.Failed("Cloudflare's connector (cloudflared) is not installed.");
            return;
        }
        // cloudflared reads %USERPROFILE%\.cloudflared\config.yml when it exists; a quick tunnel must not
        // pick up the user's own named-tunnel settings, so it gets an empty file of its own.
        var config = Path.Combine(_directory, "cloudflared.yml");
        try
        {
            if (!File.Exists(config)) File.WriteAllText(config, "# House Control quick tunnel\n");
        }
        catch (IOException) { }
        var info = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _directory,
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--config", config, "--metrics", "127.0.0.1:0", "--url", $"http://127.0.0.1:{port}" })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Post(() => Consume(process, e.Data + "\n")); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Post(() => Consume(process, e.Data + "\n")); };
        process.Exited += (_, _) => Post(() => Exited(process));
        _output = "";
        _publicUrl = null;
        State = new TunnelState.Starting();
        try
        {
            process.Start();
            KillOnAppExit.Add(process);
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { File.WriteAllText(PidFile, process.Id.ToString()); }
            catch (IOException) { }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            State = new TunnelState.Failed($"Could not start cloudflared: {e.Message}");
        }
    }

    public void Stop()
    {
        _readyCheck?.Cancel();
        _readyCheck = null;
        if (_process is { } process)
        {
            _process = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        TryDelete(PidFile);
        _publicUrl = null;
        State = new TunnelState.Stopped();
    }

    public void Dispose() => Stop();

    private void Exited(Process finished)
    {
        if (!ReferenceEquals(_process, finished)) return;
        _process = null;
        finished.Dispose();
        TryDelete(PidFile);
        var tail = string.Join(" ", _output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
        State = new TunnelState.Failed(Explain(tail));
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")]
    private static partial Regex QuickTunnelUrl();

    private void Consume(Process from, string text)
    {
        if (!ReferenceEquals(_process, from)) return;
        _output += text;
        if (_output.Length > 20_000) _output = _output[^10_000..];
        if (_publicUrl == null && QuickTunnelUrl().Match(_output) is { Success: true } match) _publicUrl = match.Value;
        // The address is printed before the edge accepts it; it works once a connection is registered.
        if (_publicUrl is { } url && State is TunnelState.Starting && _readyCheck == null
            && _output.Contains("Registered tunnel connection", StringComparison.Ordinal))
        {
            var check = _readyCheck = new CancellationTokenSource();
            _ = ReadyAfterDelay(url, check.Token);
        }
        if (State is TunnelState.Starting
            && (_output.Contains("failed to request quick Tunnel", StringComparison.Ordinal) || _output.Contains("429 Too Many Requests", StringComparison.Ordinal)))
        {
            var tail = string.Join(" ", _output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
            Stop();
            State = new TunnelState.Failed(Explain(tail));
        }
    }

    private async Task ReadyAfterDelay(string url, CancellationToken cancel)
    {
        // A moment for the new name to reach Cloudflare's DNS everywhere.
        try { await Task.Delay(1500, cancel); }
        catch (OperationCanceledException) { return; }
        Post(() =>
        {
            if (cancel.IsCancellationRequested || _process == null || State is not TunnelState.Starting) return;
            State = new TunnelState.Live(url);
        });
    }

    internal static string Explain(string log)
    {
        var lower = log.ToLowerInvariant();
        if (lower.Contains("429")) return "Cloudflare is limiting new quick tunnels right now. Try again in a minute.";
        if (lower.Contains("no such host") || lower.Contains("dial tcp") || lower.Contains("network is unreachable"))
            return "Cloudflare could not be reached. Check the internet connection.";
        return log.Length == 0 ? "The tunnel stopped." : $"The tunnel stopped: {(log.Length > 160 ? log[..160] : log)}";
    }

    /// <summary>
    /// A cloudflared left running by a previous run of the app would keep an address alive
    /// that leads nowhere; it is stopped at launch. (The job object makes this rare: it only
    /// matters if the app died between starting cloudflared and putting it in the job.)
    /// </summary>
    private void KillLeftover()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            if (int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                try
                {
                    using var leftover = Process.GetProcessById(pid);
                    if (leftover.ProcessName.Equals("cloudflared", StringComparison.OrdinalIgnoreCase)) leftover.Kill();
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
        }
        catch (IOException) { }
        TryDelete(PidFile);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Post(Action action)
    {
        if (_context is null) { lock (this) action(); }
        else _context.Post(_ => action(), null);
    }

    // MARK: Download

    public bool IsDownloading { get; private set; }

    /// <summary>
    /// Fetches the latest cloudflared for this PC from Cloudflare's GitHub releases and keeps
    /// it only if it carries a valid Authenticode signature issued to Cloudflare.
    /// </summary>
    public async Task DownloadAsync(HttpClient? http = null, CancellationToken cancel = default)
    {
        IsDownloading = true;
        var bin = Path.GetDirectoryName(BundledBinary)!;
        var work = Path.Combine(bin, $"download-{Guid.NewGuid():n}.exe");
        try
        {
            Directory.CreateDirectory(bin);
            var client = http ?? new HttpClient();
            try
            {
                var assets = RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? new[] { "cloudflared-windows-arm64.exe", "cloudflared-windows-amd64.exe" } // x64 runs under emulation when there is no Arm build
                    : new[] { "cloudflared-windows-amd64.exe" };
                HttpResponseMessage? response = null;
                foreach (var asset in assets)
                {
                    response?.Dispose();
                    response = await client.GetAsync($"https://github.com/cloudflare/cloudflared/releases/latest/download/{asset}",
                        HttpCompletionOption.ResponseHeadersRead, cancel);
                    if (response.IsSuccessStatusCode) break;
                }
                using (response)
                {
                    if (response is not { IsSuccessStatusCode: true })
                        throw new AgentException($"GitHub answered {(int?)response?.StatusCode ?? 0}.");
                    await using var file = File.Create(work);
                    await response.Content.CopyToAsync(file, cancel);
                }
            }
            finally
            {
                if (http is null) client.Dispose();
            }
            if (!SignedByCloudflare(work)) throw new AgentException("The downloaded file is not signed by Cloudflare, so it was not kept.");
            File.Move(work, BundledBinary, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            throw new AgentException($"The download failed: {e.Message}");
        }
        finally
        {
            TryDelete(work);
            IsDownloading = false;
        }
    }

    /// <summary>A valid Authenticode signature (checked by Windows itself) whose signer names Cloudflare.</summary>
    internal static bool SignedByCloudflare(string path)
    {
        if (!Authenticode.Verify(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return signer.Subject.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// A job object for the whole app, made with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Its handle is
/// never closed on purpose: Windows closes it when the app's process ends (normally, by a crash
/// or from Task Manager), and then kills every process in the job.
/// </summary>
internal static class KillOnAppExit
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Add(Process process)
    {
        var job = Job.Value;
        if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Asks Windows (WinVerifyTrust) whether a file's Authenticode signature is valid and trusted.</summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool Verify(string path)
    {
        var file = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WINTRUST_DATA data);
}
