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
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dovetail Gamepad Emulator";

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

    public static bool Enable(string exePath, string arguments = "--tray")
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(ValueName, $"\"{exePath}\" {arguments}".Trim());
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
            return true;
        }
        catch { return false; }
    }
}
