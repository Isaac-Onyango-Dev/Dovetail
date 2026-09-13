namespace Dovetail.Core;

/// <summary>
/// Deadzone, anti-deadzone, saturation and response curve: what they mean, what values are
/// legal, and the one implementation that writes them into a profile.
///
/// **Why this is in Core rather than in the tune command.** These four numbers are now settable
/// from two places, the `dovetail-engine tune` command line and the controller cards in the
/// settings window. Two copies of the clamping would drift, and the way they would drift is
/// silent: a settings slider that allowed a curve of 0.05 where the command line clamps to 0.2
/// produces a profile the engine reads back differently from the one the user thought they
/// saved. The ranges, the presets and the write are therefore defined once, here, and both
/// front ends are presentation over this.
///
/// The problem the numbers solve was measured rather than guessed. This pad's sticks are 8-bit,
/// about 127 counts of travel per direction. XInput's own recommended deadzones are 7849 for
/// the left stick and 8689 for the right, and a game applying them discards everything Dovetail
/// emits below roughly 28% of the physical travel: a stick that does nothing for the first
/// third of its movement and then responds abruptly.
/// </summary>
public static class StickFeel
{
    // Ranges. A UI uses these for its slider bounds and Apply enforces them, so a value that
    // reaches a profile is always one the engine can read back meaningfully.
    public const int MinDeadzone = 0;
    public const int MaxDeadzone = 60;
    public const int MinAntiDeadzone = 0;
    /// <summary>Above the larger of the two XInput thresholds there is nothing left to compensate for.</summary>
    public const int MaxAntiDeadzone = 20000;
    public const int MinSaturation = 0;
    /// <summary>127 is the mechanical limit of an 8-bit axis, so saturation cannot exceed it.</summary>
    public const int MaxSaturation = 127;
    public const double MinCurve = 0.2;
    public const double MaxCurve = 4.0;

    /// <summary>
    /// The anti-deadzone a preset uses for each stick: just above what the game throws away.
    /// The 200 is headroom, so a game using exactly the documented threshold still sees the
    /// first movement rather than sitting on the boundary.
    /// </summary>
    public const int AntiDeadzoneHeadroom = 200;

    public static int PresetFloorFor(bool rightStick) =>
        (rightStick ? XInputMapper.XInputRightThumbDeadzone : XInputMapper.XInputLeftThumbDeadzone)
        + AntiDeadzoneHeadroom;

    /// <summary>True when an axis name belongs to the right stick, which has its own threshold.</summary>
    public static bool IsRightStick(string axisName) =>
        axisName.StartsWith("right", StringComparison.OrdinalIgnoreCase);

    public sealed record Preset(string Id, string Label, string Summary,
                                double Curve, int Deadzone, int Saturation, bool UseAntiDeadzone)
    {
        public int LeftFloor => UseAntiDeadzone ? PresetFloorFor(false) : 0;
        public int RightFloor => UseAntiDeadzone ? PresetFloorFor(true) : 0;
    }

    public static readonly Preset[] Presets =
    [
        new("off", "Off",
            "plain linear against the mechanical limits. What Stage 3 validated, and what felt sluggish.",
            1.0, 8, 0, false),

        new("standard", "Standard",
            "floor above the game's deadzone, and full deflection at a firm-but-normal push. Recommended.",
            1.0, 4, 88, true),

        new("precise", "Precise",
            "same as standard, plus a curve that keeps small movements small. Better for aiming.",
            1.45, 4, 88, true),

        new("responsive", "Responsive",
            "reaches full deflection at a light push. Quickest to full movement, least proportional control.",
            0.85, 4, 70, true),

        new("full-range", "Full range",
            "floor above the game's deadzone but full deflection only at the mechanical limit.",
            1.0, 4, 0, true),
    ];

    public static Preset? FindPreset(string? id) =>
        id is null ? null : Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One set of values to write. Every field is optional: null means "leave whatever the
    /// profile already has", which is what makes a partial edit from one slider safe.
    /// </summary>
    public sealed class Settings
    {
        public int? Deadzone { get; set; }
        public double? ResponseCurve { get; set; }
        public int? Saturation { get; set; }

        /// <summary>
        /// Anti-deadzone for both sticks. Set from an explicit value the user typed; a preset
        /// uses <see cref="LeftAntiDeadzone"/> and <see cref="RightAntiDeadzone"/> instead,
        /// because the two sticks have different XInput thresholds to clear.
        /// </summary>
        public int? LeftAntiDeadzone { get; set; }
        public int? RightAntiDeadzone { get; set; }

        public int? AntiDeadzone
        {
            set { LeftAntiDeadzone = value; RightAntiDeadzone = value; }
        }

        public static Settings FromPreset(Preset p) => new()
        {
            Deadzone = p.Deadzone,
            ResponseCurve = p.Curve,
            Saturation = p.Saturation,
            LeftAntiDeadzone = p.LeftFloor,
            RightAntiDeadzone = p.RightFloor,
        };

        /// <summary>Every field taken from an axis, for seeding a UI from the saved profile.</summary>
        public static Settings FromAxis(CalibrationProfile.AxisCalibration a) => new()
        {
            Deadzone = a.Deadzone,
            ResponseCurve = a.ResponseCurve,
            Saturation = a.Saturation,
            LeftAntiDeadzone = a.AntiDeadzone,
            RightAntiDeadzone = a.AntiDeadzone,
        };
    }

    public static int ClampDeadzone(int v) => Math.Clamp(v, MinDeadzone, MaxDeadzone);
    public static int ClampAntiDeadzone(int v) => Math.Clamp(v, MinAntiDeadzone, MaxAntiDeadzone);
    public static int ClampSaturation(int v) => Math.Clamp(v, MinSaturation, MaxSaturation);
    public static double ClampCurve(double v) => Math.Clamp(v, MinCurve, MaxCurve);

    /// <summary>
    /// Writes the settings into every axis of a profile, clamped. Does not save: the caller
    /// decides whether this is a preview or a commit.
    /// </summary>
    public static void Apply(CalibrationProfile profile, Settings s)
    {
        foreach (var (name, axis) in profile.Axes)
        {
            bool right = IsRightStick(name);
            int? floor = right ? s.RightAntiDeadzone : s.LeftAntiDeadzone;

            if (floor is not null) axis.AntiDeadzone = ClampAntiDeadzone(floor.Value);
            if (s.ResponseCurve is not null) axis.ResponseCurve = ClampCurve(s.ResponseCurve.Value);
            if (s.Deadzone is not null) axis.Deadzone = ClampDeadzone(s.Deadzone.Value);
            if (s.Saturation is not null) axis.Saturation = ClampSaturation(s.Saturation.Value);
        }
    }

    /// <summary>The note line a tuning leaves in the profile, replacing any earlier one.</summary>
    public const string NotePrefix = "Stick feel tuned";

    public static void Note(CalibrationProfile profile, string? presetId, string source)
    {
        var left = profile.Axes.GetValueOrDefault("leftStickX");
        var right = profile.Axes.GetValueOrDefault("rightStickX");
        if (left is null) return;

        string note =
            $"{NotePrefix} {DateTime.Now:yyyy-MM-dd HH:mm} ({source}): " +
            $"deadzone {left.Deadzone}, " +
            $"anti-deadzone L{left.AntiDeadzone}/R{right?.AntiDeadzone ?? left.AntiDeadzone}, " +
            $"curve {left.ResponseCurve:0.##}, saturation {left.Saturation}" +
            (presetId is not null ? $", preset '{presetId}'" : "");

        profile.Notes.RemoveAll(n => n.StartsWith(NotePrefix, StringComparison.Ordinal));
        profile.Notes.Add(note);
    }

    /// <summary>
    /// How much of the stick's travel the game actually receives, as a percentage.
    ///
    /// This is the number the settings are for, so it is worth showing rather than leaving the
    /// user to infer it from four unrelated figures. Computed by scaling real raw values through
    /// the same <see cref="XInputMapper.Scale"/> the engine uses, not by a formula that
    /// approximates it, so what the card claims and what the game gets cannot disagree.
    /// </summary>
    public static int UsableTravelPercent(CalibrationProfile.AxisCalibration axis, bool rightStick)
    {
        int threshold = rightStick
            ? XInputMapper.XInputRightThumbDeadzone
            : XInputMapper.XInputLeftThumbDeadzone;

        int span = axis.Max - axis.Centre;
        if (span <= 0) return 0;

        int reaching = 0;
        const int samples = 100;
        for (int i = 1; i <= samples; i++)
        {
            int raw = (int)Math.Round(axis.Centre + (double)i / samples * span);
            if (Math.Abs(XInputMapper.Scale(raw, axis)) > threshold) reaching++;
        }
        return reaching;
    }
}
