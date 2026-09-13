using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dovetail.Core;

/// <summary>
/// The Stage 2 calibration baseline. This is the file the translation engine reads in
/// Stage 3, so the shape is deliberately explicit: every number that affects the emitted
/// XInput state is named and stored, rather than being recomputed from assumptions.
/// </summary>
public sealed class CalibrationProfile
{
    public int SchemaVersion { get; set; } = 2;
    public string Name { get; set; } = "default";
    public string CreatedLocal { get; set; } = "";
    public string Tool { get; set; } = "dovetail-diag calibrate";

    /// <summary>
    /// Player slot this profile belongs to, 1 to 4. Zero means unassigned.
    ///
    /// **Identity is by slot, not by physical unit, and that is deliberate.** Findings Log
    /// 4.9 and 7.6: both pads report identical vendor id, product id, revision and product
    /// string, neither reports a serial number, and the receiver assigns a channel by
    /// power-on order. There is nothing to identify a specific piece of hardware with. So
    /// Dovetail behaves the way a console does: whoever connects first is player 1. The
    /// calibration and the name belong to the slot.
    /// </summary>
    public int SlotNumber { get; set; }

    /// <summary>
    /// Name shown in notifications and in the tray settings list, for example
    /// "Player 1 Pad". Falls back to "Dovetail Controller N" rather than to the device's own
    /// product string, because "Twin USB Joystick" twice over tells the user nothing in a
    /// two-pad setup.
    /// </summary>
    public string DisplayName { get; set; } = "";

    public string EffectiveName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? $"Dovetail Controller {(SlotNumber > 0 ? SlotNumber : 1)}"
            : DisplayName;

    public DeviceIdentity Device { get; set; } = new();
    public Dictionary<string, AxisCalibration> Axes { get; set; } = [];
    public Dictionary<string, ButtonBit> Buttons { get; set; } = [];
    public HatCalibration Hat { get; set; } = new();
    public TriggerCalibration Triggers { get; set; } = new();
    public StickClickTest StickClicks { get; set; } = new();
    public List<string> Notes { get; set; } = [];

    public sealed class DeviceIdentity
    {
        public string Vid { get; set; } = "";
        public string Pid { get; set; } = "";
        public string Product { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string CollectionTag { get; set; } = "";
        public byte ReportId { get; set; }
        public string InstanceId { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public int InputReportByteLength { get; set; }
        public string RestState { get; set; } = "";
        public string SelectionRule { get; set; } =
            "Resolve by report id in byte 0, never by enumeration index. See Stage 1 finding 1.8.";
    }

    public sealed class AxisCalibration
    {
        public int Byte { get; set; }
        public string HidUsage { get; set; } = "";
        /// <summary>Measured resting value, not assumed to be 128.</summary>
        public int Centre { get; set; }
        /// <summary>Lowest value reached during full-range rotation.</summary>
        public int Min { get; set; }
        /// <summary>Highest value reached during full-range rotation.</summary>
        public int Max { get; set; }
        /// <summary>Widest excursion from centre seen while the stick was untouched.</summary>
        public int RestNoise { get; set; }
        /// <summary>Deadzone in raw counts, applied around Centre.</summary>
        public int Deadzone { get; set; }
        /// <summary>True when higher raw values must map to lower XInput values.</summary>
        public bool InvertForXInput { get; set; }
        public bool FullRangeReached { get; set; }

        /// <summary>
        /// Output value, in XInput units, that the very first movement past
        /// <see cref="Deadzone"/> produces. Compensates for the deadzone the *game* applies
        /// on top of ours.
        ///
        /// XInput's own recommended deadzones are 7849 for the left stick and 8689 for the
        /// right, and games generally use them or something close. Without compensation the
        /// first roughly 28% of this pad's physical travel produces output the game throws
        /// away, which reads as a stick that is sluggish to respond and then abrupt. Setting
        /// this just above the game's threshold means physical movement maps to in-game
        /// movement from the moment the stick leaves centre.
        ///
        /// Zero disables it and restores the plain linear mapping.
        /// </summary>
        public int AntiDeadzone { get; set; }

        /// <summary>
        /// Shape of the response between the deadzone and full deflection. 1.0 is linear.
        /// Above 1.0 gives finer control near centre at the cost of needing more travel for
        /// the same output, which suits aiming. Below 1.0 reaches large values sooner.
        /// </summary>
        public double ResponseCurve { get; set; } = 1.0;

        /// <summary>
        /// Deviation from <see cref="Centre"/>, in raw counts, at which the axis should
        /// report full deflection. Zero means use <see cref="Max"/> and <see cref="Min"/>,
        /// the mechanical limits.
        ///
        /// This exists because the mechanical limit is not the limit people use. Calibration
        /// deliberately asks for the stick to be pushed hard against its rim, so Min and Max
        /// record the extremes of the hardware. Free-play measurement on this pad showed a
        /// median deviation of 61 counts out of a possible 127, and 88 at the ninetieth
        /// percentile. Scaling to 127 therefore means a normal push produces about 60% of
        /// full deflection and the game never sees full speed without an uncomfortable
        /// shove. Setting saturation to a comfortable push makes that push mean "maximum".
        ///
        /// Too low a value costs proportional control near the top of the range, so this is
        /// a preference rather than a correctness setting.
        /// </summary>
        public int Saturation { get; set; }
    }

    public sealed class ButtonBit
    {
        public int Byte { get; set; }
        public int Bit { get; set; }
        public int? HidButton { get; set; }
        public string XInput { get; set; } = "";
        public bool VendorDefined { get; set; }
        public bool Verified { get; set; }
    }

    public sealed class HatCalibration
    {
        public int Byte { get; set; } = 5;
        public int BitOffset { get; set; }
        public int BitCount { get; set; } = 4;
        public int NullValue { get; set; } = 15;
        public Dictionary<string, int> Directions { get; set; } = [];
        public List<int> ValuesObserved { get; set; } = [];
    }

    public sealed class TriggerCalibration
    {
        /// <summary>This pad reports triggers as single bits; there is no analog travel.</summary>
        public bool DigitalOnly { get; set; } = true;
        public int HeldValue { get; set; } = 255;
        public int ReleasedValue { get; set; }
        public bool LeftVerified { get; set; }
        public bool RightVerified { get; set; }
        public string Rationale { get; set; } =
            "All four 8-bit axes are consumed by the two sticks, so L2/R2 carry no analog value. " +
            "XInput trigger bytes are synthesised from the button bits.";
    }

    public sealed class StickClickTest
    {
        public ClickResult Left { get; set; } = new();
        public ClickResult Right { get; set; } = new();

        public sealed class ClickResult
        {
            public bool HoldPassed { get; set; }
            public int HoldMillisecondsObserved { get; set; }
            public int TapsRequested { get; set; }
            public int TapsDetected { get; set; }
            public bool TapsPassed { get; set; }
            public int MaxAxisDisturbance { get; set; }
            public bool Isolated { get; set; }
            public string Note { get; set; } = "";
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static CalibrationProfile Load(string path) =>
        JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"could not parse {path}");
}
