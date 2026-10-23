using System.Text.Json.Nodes;

namespace HouseControl.Core.SmartLife;

/// <summary>
/// Drives air conditioners that are a remote control learned by an infrared blaster.
/// </summary>
/// <remarks>
/// Such an AC is not a device with a state of its own. Tuya keeps a virtual remote in
/// the cloud; every change is one key press (<c>PowerOn</c>, <c>PowerOff</c>, <c>M</c>, <c>T</c>, <c>F</c>) that
/// the blaster transmits, and the cloud works out the infrared code from the state it
/// remembers for that remote. Three things follow:
/// <list type="bullet">
/// <item>keys go out one at a time, a second apart (several in one request are refused);</item>
/// <item>nothing is transmitted while the AC is off: what changed is sent when it is switched on;</item>
/// <item>when a key is refused (code 2008) the cloud's idea of the remote usually differs
/// from ours, e.g. it is in a mode with a fixed temperature. The rest of our state is
/// replayed once and the key retried.</item>
/// </list>
/// Used from the UI thread only.
/// </remarks>
internal sealed class InfraredRemote
{
    /// <summary>The parts of the change that did not reach the AC.</summary>
    public sealed class Rejection(string message, IReadOnlyList<string> fields) : Exception(message)
    {
        public IReadOnlyList<string> Fields { get; } = fields;
    }

    /// <summary>Time between two key presses of the same remote.</summary>
    public TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(1);
    private static readonly string[] Fields = ["on", "mode", "target", "fan"];
    private readonly Dictionary<string, DateTimeOffset> _lastKey = new();
    /// <summary>Settings changed while the AC was off, to be sent along when it is switched on.</summary>
    private readonly Dictionary<string, HashSet<string>> _deferred = new();

    private static JsonObject Only(JsonObject source, Func<string, bool> keep) =>
        new(source.Where(p => keep(p.Key) && p.Value is not null)
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));

    /// <summary>
    /// Brings the remote <paramref name="deviceId"/> from where it was to <paramref name="state"/> (the
    /// app's state with <paramref name="patch"/> already applied). <paramref name="send"/> transmits one
    /// command; <paramref name="note"/> receives log lines.
    /// </summary>
    public async Task ApplyAsync(JsonObject patch, JsonObject state, string deviceId,
                                 Func<JsonObject, Task> send, Action<string> note)
    {
        var isOn = state["on"].AsBool() ?? false;
        var wanted = Only(patch, Fields.Contains);
        if (!isOn && !wanted.ContainsKey("on"))
        {
            if (wanted.Count == 0) return;
            if (!_deferred.TryGetValue(deviceId, out var set)) _deferred[deviceId] = set = new HashSet<string>();
            set.UnionWith(wanted.Select(p => p.Key));
            note($"off: {string.Join(", ", wanted.Select(p => p.Key).Order(StringComparer.Ordinal))} kept for the next switch-on");
            return;
        }
        if (wanted["on"].AsBool() == true && _deferred.Remove(deviceId, out var kept))
        {
            foreach (var field in kept)
                if (!wanted.ContainsKey(field) && state[field] is { } value) wanted[field] = value.DeepClone();
        }
        if (wanted["on"].AsBool() == false) _deferred.Remove(deviceId);

        var keys = TuyaMapper.InfraredKeys(wanted);
        var index = 0;
        var replayed = false;
        while (index < keys.Count)
        {
            var key = keys[index];
            try
            {
                await PressAsync(key, deviceId, send);
                index++;
            }
            catch (Exception error)
            {
                var pending = keys.Skip(index).Select(k => k.Field).ToList();
                if (TuyaFailure.CodeOf(error) == "2008" && !replayed && isOn && key.Field != "on")
                {
                    replayed = true;
                    var done = pending.Concat(keys.Take(index).Select(k => k.Field)).ToHashSet();
                    var rest = Only(state, f => Fields.Contains(f) && !done.Contains(f));
                    note($"{key.Field} refused (2008): replaying {string.Join(", ", rest.Select(p => p.Key).Order(StringComparer.Ordinal))}");
                    foreach (var other in TuyaMapper.InfraredKeys(rest))
                    {
                        try { await PressAsync(other, deviceId, send); }
                        catch { /* the retried key decides the outcome */ }
                    }
                    continue;
                }
                var reason = TuyaFailure.CodeOf(error) == "2008"
                    ? "this remote has no infrared code for that setting (Tuya code 2008)"
                    : error.Message;
                throw new Rejection(reason, pending);
            }
        }
    }

    private async Task PressAsync(TuyaMapper.InfraredKey key, string deviceId, Func<JsonObject, Task> send)
    {
        if (_lastKey.TryGetValue(deviceId, out var last))
        {
            var wait = Spacing - (DateTimeOffset.UtcNow - last);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait); }
                catch (OperationCanceledException) { }
            }
        }
        try
        {
            await send((JsonObject)key.Command.DeepClone());
        }
        finally
        {
            _lastKey[deviceId] = DateTimeOffset.UtcNow;
        }
    }
}
