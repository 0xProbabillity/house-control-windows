using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HouseControl.Core;

/// <summary>
/// Persists which floor plan is active and the user's own plan file.
/// Everything lives in %APPDATA%\HouseControl (or HOUSE_DATA_DIR).
/// </summary>
public sealed class PlanStore
{
    public static readonly string[] Bundled = ["home", "studio"];

    public string Directory { get; }
    public string CustomPlanPath => Path.Combine(Directory, "plan.json");
    private string SettingsPath => Path.Combine(Directory, "settings.json");

    /// <summary>"home" / "studio" for the bundled demos, or "custom" for plan.json.</summary>
    public string ActivePlan { get; private set; } = "home";

    public PlanStore(string? directory = null)
    {
        // HOUSE_DATA_DIR points the app at another folder: a throw-away one for testing or
        // screenshots leaves the real plan and the Smart Life session alone.
        Directory = directory ?? DefaultDirectory();
        System.IO.Directory.CreateDirectory(Directory);
        if (Json.ParseObject(TryRead(SettingsPath)).Str("plan") is { } plan) ActivePlan = plan;
        if (!Bundled.Contains(ActivePlan) && ActivePlan != "custom") ActivePlan = "home";
        if (ActivePlan == "custom" && CustomPlan() is null) ActivePlan = "home";
    }

    public static string DefaultDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("HOUSE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(custom));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HouseControl");
    }

    private static byte[]? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }

    public JsonObject? CustomPlan() => Json.ParseObject(TryRead(CustomPlanPath));

    public bool HasCustomPlan => File.Exists(CustomPlanPath);

    /// <summary>The name the user gave their home, for menus.</summary>
    public string? CustomPlanName => CustomPlan().Str("name");

    /// <summary>Fingerprint of the user's plan as stored.</summary>
    public string? CustomPlanRevision => TryRead(CustomPlanPath) is { } data ? Revision(data) : null;

    public static string Revision(byte[] data) => Convert.ToHexString(SHA256.HashData(data)[..8]).ToLowerInvariant();

    public void Select(string plan)
    {
        if (!(Bundled.Contains(plan) || (plan == "custom" && CustomPlan() is not null))) return;
        ActivePlan = plan;
        Files.WriteAtomic(SettingsPath, Encoding.UTF8.GetBytes(Json.Serialize(new JsonObject { ["plan"] = plan })));
    }

    /// <summary>Stores <paramref name="plan"/> as the user's plan and makes it active.</summary>
    public void SaveCustom(JsonObject plan) => SaveCustom(Encode(plan));

    /// <summary>Stores an already serialised plan byte for byte.</summary>
    public void SaveCustom(byte[] data, bool select = true)
    {
        Files.WriteAtomic(CustomPlanPath, data);
        if (select) Select("custom");
    }

    public static byte[] Encode(JsonObject plan) => Encoding.UTF8.GetBytes(Json.Serialize(Json.Sorted(plan), pretty: true));

    /// <summary>Checks that <paramref name="data"/> is a floor plan at all before it replaces the user's one.</summary>
    public static JsonObject Validate(byte[] data)
    {
        var plan = Json.ParseObject(data);
        if (plan?.Arr("rooms") is null)
            throw new InvalidDataException("This file is not a House Control floor plan (no \"rooms\" list).");
        return plan;
    }

    public void ImportPlan(string path) => SaveCustom(Validate(File.ReadAllBytes(path)));

    /// <summary>Forgets the user's plan and falls back to the bundled demo.</summary>
    public void RemoveCustom()
    {
        File.Delete(CustomPlanPath);
        if (ActivePlan == "custom") Select("home");
    }

    /// <summary>The plan the UI should render: the custom JSON inline, or the name of a bundled demo.</summary>
    public JsonObject BootPayload()
    {
        var boot = new JsonObject { ["plan"] = ActivePlan, ["hasCustomPlan"] = HasCustomPlan };
        if (ActivePlan == "custom" && CustomPlan() is { } plan) boot["config"] = plan;
        return boot;
    }

    /// <summary>The plan the hub links devices from: only the user's own plan has real devices.</summary>
    public JsonObject? LinkedPlan() => ActivePlan == "custom" ? CustomPlan() : null;
}
