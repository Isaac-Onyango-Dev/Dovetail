using System.Diagnostics;
using System.Text.Json;

namespace Dovetail.Core;

/// <summary>
/// Per-game stick settings, switched automatically by watching which game is running.
///
/// Section 5.4 and the Section 7 decision: Dovetail registers a game's executable and applies
/// that game's settings when it sees the process, and it **never launches the game**. The
/// operator was explicit about that, and Stage 4 proved it is unnecessary: Sekiro was started
/// from its own desktop shortcut with Dovetail running separately and detection worked, because
/// the ViGEmBus device exists at the driver level before any game starts. Steam Input needs a
/// launch wrapper only because it hooks the specific process it launches.
///
/// Only the feel settings are per game. Button mapping, byte positions and axis assignment
/// are properties of the hardware and stay in the slot calibration, so a game profile cannot
/// accidentally break a pad's mapping.
/// </summary>
public sealed class GameProfile
{
    public string Name { get; set; } = "";
    /// <summary>Executable file name without a path, for example "sekiro.exe". Matched case-insensitively.</summary>
    public string ExecutableName { get; set; } = "";
    /// <summary>Full path, recorded for display and for the user to recognise the entry.</summary>
    public string FullPath { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public int? Deadzone { get; set; }
    public int? AntiDeadzone { get; set; }
    public int? AntiDeadzoneRight { get; set; }
    public int? Saturation { get; set; }
    public double? ResponseCurve { get; set; }
    /// <summary>Trigger value emitted while held. Lets a game that is too twitchy on full-scale digital triggers use less.</summary>
    public int? TriggerHeldValue { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Applies this game's overrides onto a copy of a slot calibration.</summary>
    public void ApplyTo(CalibrationProfile target)
    {
        foreach (var (name, axis) in target.Axes)
        {
            bool right = name.StartsWith("right", StringComparison.OrdinalIgnoreCase);
            if (Deadzone is int dz) axis.Deadzone = Math.Clamp(dz, 0, 60);
            if (Saturation is int sat) axis.Saturation = Math.Clamp(sat, 0, 127);
            if (ResponseCurve is double c) axis.ResponseCurve = Math.Clamp(c, 0.2, 4.0);

            int? floor = right ? (AntiDeadzoneRight ?? AntiDeadzone) : AntiDeadzone;
            if (floor is int f) axis.AntiDeadzone = Math.Max(0, f);
        }
        if (TriggerHeldValue is int t) target.Triggers.HeldValue = Math.Clamp(t, 0, 255);
    }
}

/// <summary>
/// The registered games, and a watcher that reports which one is in the foreground of the
/// process list. Polling the process list is deliberate: it needs no hook, no injection and
/// no elevation, which keeps Dovetail at the input-driver level that Section 4.2 restricts it
/// to.
/// </summary>
public sealed class GameLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public List<GameProfile> Games { get; set; } = [];

    public static string DefaultPath(string profileDirectory) =>
        Path.Combine(profileDirectory, "games.json");

    public static GameLibrary Load(string path)
    {
        if (!File.Exists(path)) return new GameLibrary();
        try { return JsonSerializer.Deserialize<GameLibrary>(File.ReadAllText(path), Json) ?? new GameLibrary(); }
        catch { return new GameLibrary(); }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public GameProfile Add(string exePath, string? name = null)
    {
        string exe = Path.GetFileName(exePath);
        var existing = Games.FirstOrDefault(g =>
            g.ExecutableName.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.FullPath = exePath;
            if (name is not null) existing.Name = name;
            return existing;
        }

        var g = new GameProfile
        {
            Name = name ?? Path.GetFileNameWithoutExtension(exePath),
            ExecutableName = exe,
            FullPath = exePath,
        };
        Games.Add(g);
        return g;
    }

    public bool Remove(string executableName) =>
        Games.RemoveAll(g => g.ExecutableName.Equals(executableName, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// The registered game that is currently running, or null. When several are running the
    /// first registered match wins, which is deterministic and explainable rather than
    /// whichever the OS happened to list first.
    /// </summary>
    public GameProfile? FindRunning()
    {
        if (Games.Count == 0) return null;

        HashSet<string> running;
        try
        {
            running = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n! + ".exe")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }

        return Games.FirstOrDefault(g =>
            g.Enabled &&
            !string.IsNullOrWhiteSpace(g.ExecutableName) &&
            running.Contains(g.ExecutableName));
    }
}
