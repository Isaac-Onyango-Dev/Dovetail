using System.Text.Json;

namespace Dovetail.Core;

/// <summary>
/// A record of the HidHide settings Dovetail itself changed, so an uninstall can undo exactly
/// those and nothing else.
///
/// **Why this file has to exist.** The uninstall rule is that Dovetail removes only its own
/// entries and leaves HidHide otherwise untouched. Applied literally to the allow list that is
/// straightforward, because an entry naming one of our executables is unambiguously ours. It
/// is not straightforward for the two settings setup also changes: hiding the pad, and turning
/// the global cloak on. Neither carries a mark saying who did it.
///
/// Leaving them alone on uninstall is the reading that sounds safest and is in fact the
/// harmful one. Cloaking stays on, the pad stays hidden, and Dovetail's allow-list entries have
/// just been removed - so after uninstalling, the user's controller is invisible to everything
/// that is not DS4Windows or HidHide's own client. The pad appears broken and nothing on the
/// machine explains why.
///
/// So setup records what it changed, and uninstall reverses precisely that. A pad the user had
/// already hidden stays hidden. A cloak that was already on stays on. A cloak Dovetail turned
/// on is turned back off, unless something else is relying on it, which is the check in
/// <see cref="UninstallCleanup"/>.
/// </summary>
public sealed class HidHideState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Device instance ids Dovetail added to HidHide's hidden-device list.</summary>
    public List<string> DevicesWeHid { get; set; } = [];

    /// <summary>True when Dovetail found cloaking off and turned it on.</summary>
    public bool WeTurnedCloakOn { get; set; }

    public string LastConfiguredLocal { get; set; } = "";

    /// <summary>%LOCALAPPDATA%\Dovetail\hidhide-state.json.</summary>
    public static string DefaultPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return local.Length == 0
                ? Path.Combine(Path.GetTempPath(), ProfileStore.ProductFolder, "hidhide-state.json")
                : Path.Combine(local, ProfileStore.ProductFolder, "hidhide-state.json");
        }
    }

    public static HidHideState Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new HidHideState();
        try { return JsonSerializer.Deserialize<HidHideState>(File.ReadAllText(path), Json) ?? new HidHideState(); }
        catch { return new HidHideState(); }
    }

    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Folds a configure run into the record. Additive rather than replacing, because setup can
    /// run more than once - a second controller, or a repair - and each run may add a device the
    /// earlier one did not.
    /// </summary>
    public void Merge(HidHideAccess.Applied applied)
    {
        foreach (var id in applied.DevicesHidden)
            if (!DevicesWeHid.Contains(id, StringComparer.OrdinalIgnoreCase))
                DevicesWeHid.Add(id);

        if (applied.CloakTurnedOn) WeTurnedCloakOn = true;
        LastConfiguredLocal = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    }

    public bool AnythingRecorded => DevicesWeHid.Count > 0 || WeTurnedCloakOn;

    public static bool Delete(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }
}
