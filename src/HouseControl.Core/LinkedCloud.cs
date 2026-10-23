using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// The cloud behind the plan's <c>tuya</c> links (Smart Life). The hub talks to it through this
/// interface only, the same way the Mac app keeps it in a module of its own.
/// </summary>
public interface ILinkedCloud
{
    /// <summary>True when an account is connected and commands can be sent.</summary>
    bool IsConnected { get; }
    /// <summary>What the editor and the settings show for it, under "smartlife".</summary>
    JsonObject Info { get; }
    /// <summary>
    /// Sends the panel's change (<paramref name="patch"/>; <paramref name="state"/> is the device with
    /// it applied) to the device a plan's <c>tuya</c> link names. Throws <see cref="CommandRejection"/>
    /// when part of it did not reach the device.
    /// </summary>
    Task CommandAsync(string appId, string kind, JsonObject link, JsonObject patch, JsonObject state, EventLog log);
    /// <summary>
    /// What the linked devices report (app device id → its kind and <c>tuya</c> link), in the shape
    /// the panel's <c>applyRemote</c> takes; null when the cloud could not be reached.
    /// </summary>
    Task<JsonObject?> PollAsync(IReadOnlyDictionary<string, (string Kind, JsonObject Link)> links);
    /// <summary>Every device of the account, in the editor's terms.</summary>
    Task<IReadOnlyList<JsonObject>> DevicesAsync();
}

/// <summary>The parts of a change that did not reach the device; the panel rolls back only those.</summary>
public sealed class CommandRejection(string message, IReadOnlyList<string> fields) : Exception(message)
{
    public IReadOnlyList<string> Fields { get; } = fields;
}
