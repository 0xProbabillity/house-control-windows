using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// A small text log of what was sent to real devices and what the cloud answered:
/// <c>events.log</c> next to the plan. It is the first thing to look at when a device
/// does not react. Never contains tokens; it stays on this PC.
/// </summary>
public sealed class EventLog(string directory)
{
    private const int Limit = 256 * 1024;
    private readonly object _gate = new();

    public string FilePath { get; } = Path.Combine(directory, "events.log");

    public void Write(string line)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} {line}\n";
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > Limit)
                {
                    // Keep the newer half once the file has grown past the limit.
                    var old = File.ReadAllText(FilePath);
                    var tail = old[^(Limit / 2)..];
                    var cut = tail.IndexOf('\n');
                    File.WriteAllText(FilePath, (cut >= 0 ? tail[(cut + 1)..] : tail) + text, Encoding.UTF8);
                    return;
                }
                File.AppendAllText(FilePath, text, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string Describe(IEnumerable<JsonObject> commands) =>
        string.Join(" ", commands.Select(c => $"{c["code"]?.ToJsonString().Trim('"') ?? "?"}={c["value"]?.ToJsonString().Trim('"') ?? "?"}"));
}
