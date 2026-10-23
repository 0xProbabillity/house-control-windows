using System.Diagnostics;
using System.Text;

namespace HouseControl;

/// <summary>
/// The app's own diagnostics (<c>app.log</c> next to the plan): script errors from the pages and
/// anything unexpected in the shell. Device commands go to <c>events.log</c> instead.
/// </summary>
internal static class Log
{
    private const int Limit = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Start(string directory) => _path = Path.Combine(directory, "app.log");

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
        Debug.WriteLine(text);
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > Limit)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, text + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }

    public static void Error(string what, Exception e) => Write($"{what}: {e.GetType().Name}: {e.Message}");
}
