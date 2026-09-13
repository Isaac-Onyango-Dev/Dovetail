using System.Text.Json;

namespace Dovetail.Core;

/// <summary>
/// Application settings, separate from calibration because they are about Dovetail's
/// behaviour rather than about a controller. Holds the Section 7 decisions that have to
/// persist between runs.
/// </summary>
public sealed class DovetailSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// False until the first-run flow has completed. Section 7.1: the first launch shows the
    /// full window and walks through setup, and only after calibration is confirmed does
    /// Dovetail hand over to the tray. Every launch after that is tray-only.
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>
    /// Whether the one-time "Dovetail will now run in the background" dialog has been shown.
    /// Tracked separately from <see cref="FirstRunCompleted"/> so that the handover message
    /// appears exactly once even if setup is revisited.
    /// </summary>
    public bool HandoverExplained { get; set; }

    /// <summary>
    /// Whether the intro animation has played. Separate from <see cref="FirstRunCompleted"/>
    /// on purpose: setup can be abandoned and resumed, and the intro should still not play a
    /// second time. It is written before the animation starts rather than after, so being
    /// killed part way through cannot leave it replaying on every launch.
    /// </summary>
    public bool IntroPlayed { get; set; }

    /// <summary>
    /// Section 7.5: auto-start is wanted, but the user is asked once on first login rather
    /// than having it enabled silently. Null means not yet asked.
    /// </summary>
    public bool? AutoStartOnLogin { get; set; }
    public bool AutoStartPromptShown { get; set; }

    public bool ShowConnectNotifications { get; set; } = true;
    public bool ShowDisconnectNotifications { get; set; } = true;
    public bool ShowUnrecognisedNotifications { get; set; } = true;

    /// <summary>Last game profile applied, for display only.</summary>
    public string LastGameApplied { get; set; } = "";

    public static string DefaultPath(string profileDirectory) =>
        Path.Combine(profileDirectory, "settings.json");

    public static DovetailSettings Load(string path)
    {
        if (!File.Exists(path)) return new DovetailSettings();
        try { return JsonSerializer.Deserialize<DovetailSettings>(File.ReadAllText(path), Json) ?? new DovetailSettings(); }
        catch { return new DovetailSettings(); }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// Reads and writes the Windows "run at login" entry for the current user.
///
/// HKCU Run is used rather than a scheduled task or the Startup folder. It needs no
/// elevation, it is trivially visible to the user in Task Manager's Startup tab, and it is
/// exactly the mechanism this project already audited and disabled for x360ce in Stage 0, so
/// the operator can inspect and remove it the same way.
///
/// **The value name changed with the rename, and that is a migration hazard rather than a
/// cosmetic edit.** A Run value is keyed by its name, so writing the new one does not replace
/// the old one: it leaves a second entry pointing at Bridge.exe, an executable the packaging
/// step no longer produces. Windows would then try to launch a missing file at every sign-in.
/// Every write therefore deletes the legacy name first, and <see cref="Disable"/> removes both.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dovetail Gamepad Emulator";

    /// <summary>
    /// The name this entry carried before the Stage 6 rename. Read to migrate an existing
    /// install, and deleted whenever the new value is written or removed. Kept public so the
    /// uninstaller cleans up a machine that never ran a renamed build.
    /// </summary>
    public const string LegacyValueName = "Bridge Gamepad Emulator";

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static string? CurrentCommand()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    /// <summary>The legacy entry's command, or null when there is none left to clean up.</summary>
    public static string? LegacyCommand()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(LegacyValueName) as string;
    }

    public static bool Enable(string exePath, string arguments = "--tray")
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(ValueName, $"\"{exePath}\" {arguments}".Trim());
            // After the new value, never before: a failure between the two leaves the old
            // entry still starting something that works, rather than no entry at all.
            DeleteLegacy(k);
            return true;
        }
        catch { return false; }
    }

    public static bool Disable()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k is null) return true;
            if (k.GetValue(ValueName) is not null) k.DeleteValue(ValueName);
            DeleteLegacy(k);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Drops the pre-rename entry if it is still there. Called on every write and every
    /// removal, so an install that predates the rename is tidied by the first launch of a
    /// renamed build without the user ever seeing the stale entry.
    /// </summary>
    public static bool RemoveLegacy()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            return k is null || DeleteLegacy(k);
        }
        catch { return false; }
    }

    private static bool DeleteLegacy(Microsoft.Win32.RegistryKey k)
    {
        try
        {
            if (k.GetValue(LegacyValueName) is not null) k.DeleteValue(LegacyValueName);
            return true;
        }
        catch { return false; }
    }
}
