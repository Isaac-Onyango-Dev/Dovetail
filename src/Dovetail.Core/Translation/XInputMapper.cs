namespace Dovetail.Core;

/// <summary>An XInput gamepad report, in the same shape XInput itself uses.</summary>
public struct XInputReport
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;

    public bool Equals(XInputReport o) =>
        Buttons == o.Buttons && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger &&
        ThumbLX == o.ThumbLX && ThumbLY == o.ThumbLY && ThumbRX == o.ThumbRX && ThumbRY == o.ThumbRY;
}

/// <summary>XInput button flags, from XInput.h.</summary>
public static class XInputButtons
{
    public const ushort DPadUp = 0x0001;
    public const ushort DPadDown = 0x0002;
    public const ushort DPadLeft = 0x0004;
    public const ushort DPadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    public const ushort Guide = 0x0400;      // undocumented but carried by XInput
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    public static string Describe(ushort mask)
    {
        var names = new List<string>();
        if ((mask & DPadUp) != 0) names.Add("Up");
        if ((mask & DPadDown) != 0) names.Add("Down");
        if ((mask & DPadLeft) != 0) names.Add("Left");
        if ((mask & DPadRight) != 0) names.Add("Right");
        if ((mask & Start) != 0) names.Add("Start");
        if ((mask & Back) != 0) names.Add("Back");
        if ((mask & LeftThumb) != 0) names.Add("LeftThumb");
        if ((mask & RightThumb) != 0) names.Add("RightThumb");
        if ((mask & LeftShoulder) != 0) names.Add("LeftShoulder");
        if ((mask & RightShoulder) != 0) names.Add("RightShoulder");
        if ((mask & Guide) != 0) names.Add("Guide");
        if ((mask & A) != 0) names.Add("A");
        if ((mask & B) != 0) names.Add("B");
        if ((mask & X) != 0) names.Add("X");
        if ((mask & Y) != 0) names.Add("Y");
        return names.Count == 0 ? "-" : string.Join("+", names);
    }
}

/// <summary>
/// Maps a decoded <see cref="PadState"/> onto an XInput report, applying the Stage 2
/// calibration.
///
/// Face-button assignment follows the pad's own printed lettering, which is Xbox-style:
/// the upper face button is printed Y, the right one B, the lower one A, the left one X.
/// Stage 1 recorded them as Triangle/Circle/Cross/Square by position, so the two naming
/// schemes meet here and nowhere else.
///
/// Triggers are synthesised. Stage 2 section 2.4 confirmed L2 and R2 are single bits with
/// no analog travel, because both sticks consume all four of the pad's 8-bit axes. A held
/// trigger therefore reports <see cref="CalibrationProfile.TriggerCalibration.HeldValue"/>,
/// 255 by default, and a released one reports 0.
/// </summary>
public sealed class XInputMapper
{
    private readonly CalibrationProfile _p;
    private readonly CalibrationProfile.AxisCalibration _lx, _ly, _rx, _ry;

    public XInputMapper(CalibrationProfile profile)
    {
        _p = profile;
        _lx = Axis("leftStickX", 3, false);
        _ly = Axis("leftStickY", 4, true);
        _rx = Axis("rightStickX", 1, false);
        _ry = Axis("rightStickY", 2, true);
    }

    private CalibrationProfile.AxisCalibration Axis(string name, int fallbackByte, bool invert) =>
        _p.Axes.TryGetValue(name, out var a)
            ? a
            : new CalibrationProfile.AxisCalibration
            {
                Byte = fallbackByte, Centre = 128, Min = 0, Max = 255,
                Deadzone = 8, InvertForXInput = invert
            };

    public XInputReport Map(PadState s)
    {
        ushort b = 0;
        if (s.Triangle) b |= XInputButtons.Y;
        if (s.Circle) b |= XInputButtons.B;
        if (s.Cross) b |= XInputButtons.A;
        if (s.Square) b |= XInputButtons.X;
        if (s.L1) b |= XInputButtons.LeftShoulder;
        if (s.R1) b |= XInputButtons.RightShoulder;
        if (s.L3) b |= XInputButtons.LeftThumb;
        if (s.R3) b |= XInputButtons.RightThumb;
        if (s.Select) b |= XInputButtons.Back;
        if (s.Start) b |= XInputButtons.Start;
        if (s.Home) b |= XInputButtons.Guide;
        if (s.DUp) b |= XInputButtons.DPadUp;
        if (s.DDown) b |= XInputButtons.DPadDown;
        if (s.DLeft) b |= XInputButtons.DPadLeft;
        if (s.DRight) b |= XInputButtons.DPadRight;

        byte held = (byte)Math.Clamp(_p.Triggers.HeldValue, 0, 255);
        byte rel = (byte)Math.Clamp(_p.Triggers.ReleasedValue, 0, 255);

        return new XInputReport
        {
            Buttons = b,
            LeftTrigger = s.L2 ? held : rel,
            RightTrigger = s.R2 ? held : rel,
            ThumbLX = Scale(s.LeftX, _lx),
            ThumbLY = Scale(s.LeftY, _ly),
            ThumbRX = Scale(s.RightX, _rx),
            ThumbRY = Scale(s.RightY, _ry),
        };
    }

    /// <summary>XInput's own recommended stick deadzones, from XInput.h.</summary>
    public const int XInputLeftThumbDeadzone = 7849;
    public const int XInputRightThumbDeadzone = 8689;

    /// <summary>
    /// Maps a raw 0..255 axis count onto the full XInput short range.
    ///
    /// Four things happen here, and each one exists because of a measurement:
    ///
    /// 1. The measured centre is used rather than an assumed 128, because a worn or
    ///    off-centre stick does not rest at the nominal midpoint.
    /// 2. The calibrated deadzone is applied around that centre, sized from the rest-state
    ///    noise actually observed.
    /// 3. The travel that remains on each side is stretched to the rail. Without this a pad
    ///    whose measured maximum is 250 could never report full deflection.
    /// 4. The output floor is lifted to <see cref="CalibrationProfile.AxisCalibration.AntiDeadzone"/>,
    ///    and shaped by <see cref="CalibrationProfile.AxisCalibration.ResponseCurve"/>.
    ///
    /// Step 4 is the one that makes this pad usable. This stick has 8-bit resolution, so
    /// only about 127 counts of travel per direction, and a game applying XInput's
    /// recommended 7849 deadzone discards everything Dovetail emits below roughly 28% of that
    /// travel. Lifting the floor above the game's threshold means the first millimetre of
    /// physical movement produces in-game movement.
    /// </summary>
    public static short Scale(int raw, CalibrationProfile.AxisCalibration a)
    {
        int centre = a.Centre;
        int dz = Math.Max(0, a.Deadzone);
        int deviation = raw - centre;

        if (Math.Abs(deviation) <= dz) return 0;

        bool positive = deviation > 0;

        // Full deflection is reached at the saturation point when one is set, otherwise at
        // the mechanical limit. Saturation exists because the mechanical limit is not the
        // limit people actually use; see AxisCalibration.Saturation.
        int reach = a.Saturation > dz
            ? a.Saturation
            : (positive ? a.Max - centre : centre - a.Min);
        int span = Math.Max(1, reach - dz);

        // 0..1 across the usable travel outside the deadzone
        double t = Math.Clamp((Math.Abs(deviation) - dz) / (double)span, 0.0, 1.0);

        double curve = a.ResponseCurve > 0 ? a.ResponseCurve : 1.0;
        if (Math.Abs(curve - 1.0) > 1e-9) t = Math.Pow(t, curve);

        // The rail this direction is heading for, once inversion is accounted for.
        bool goesPositive = positive ^ a.InvertForXInput;
        double rail = goesPositive ? short.MaxValue : -(double)short.MinValue;

        int floor = Math.Clamp(a.AntiDeadzone, 0, (int)rail - 1);
        double magnitude = floor + t * (rail - floor);

        double scaled = goesPositive ? magnitude : -magnitude;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
