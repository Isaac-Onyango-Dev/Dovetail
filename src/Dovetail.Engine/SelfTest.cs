using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Hardware-free checks on the parts of the pipeline that are pure arithmetic or pure bit
/// manipulation. Axis scaling and report decoding are exactly the kind of code that fails
/// quietly: a sign error or an off-by-one deadzone produces a pad that works but feels
/// wrong, which is far harder to diagnose in-game than a dead button.
/// </summary>
internal static class SelfTest
{
    private static int _pass, _fail;

    // Nullable: the claim checks pass ClaimedOwner's result straight in, and null is the
    // expected value for "not claimed" rather than a mistake worth a compiler warning.
    private static void Check(string what, object? actual, object? expected)
    {
        bool ok = Equals(actual?.ToString(), expected?.ToString());
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {what,-52} got {actual ?? "(null)",-8} expected {expected ?? "(null)"}");
    }

    private static void CheckNear(string what, int actual, int expected, int tolerance)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {what,-52} got {actual,-8} expected {expected} +/-{tolerance}");
    }

    internal static int Run(string[] args)
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - SELF TEST (no controller needed)");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        var slots = Program.LoadSlots(args, m => Console.WriteLine("  " + m));
        var profile = slots.Slots.Values.FirstOrDefault();
        if (profile is null)
        {
            Console.WriteLine(" No calibrated slot found, so there is nothing to test against.");
            Console.WriteLine(" Run 'dovetail-diag calibrate' first.");
            return 1;
        }
        Console.WriteLine($"  testing against slot {profile.SlotNumber}, \"{profile.EffectiveName}\"");
        Console.WriteLine();

        // ---- axis scaling --------------------------------------------------------
        var ax = profile.Axes["leftStickX"];
        Console.WriteLine($" Axis scaling, using the calibrated left-stick X (centre {ax.Centre}, "
                          + $"{ax.Min}..{ax.Max}, deadzone {ax.Deadzone}, anti-deadzone {ax.AntiDeadzone}, "
                          + $"saturation {ax.Saturation}, curve {ax.ResponseCurve:0.##}):");
        Check("centre maps to zero", XInputMapper.Scale(ax.Centre, ax), 0);
        Check("inside deadzone, positive side", XInputMapper.Scale(ax.Centre + ax.Deadzone, ax), 0);
        Check("inside deadzone, negative side", XInputMapper.Scale(ax.Centre - ax.Deadzone, ax), 0);
        Check("full right reaches the positive rail", XInputMapper.Scale(ax.Max, ax), short.MaxValue);
        Check("full left reaches the negative rail", XInputMapper.Scale(ax.Min, ax), short.MinValue);

        // What the first step past the deadzone should produce depends on whether
        // anti-deadzone compensation is switched on, so the assertion has to branch. With
        // it off, the first step must be small so there is no jump. With it on, the whole
        // point is that the first step clears the deadzone the game applies, otherwise the
        // first slice of physical travel is discarded and the stick feels sluggish.
        int firstStep = Math.Abs(XInputMapper.Scale(ax.Centre + ax.Deadzone + 1, ax));
        if (ax.AntiDeadzone <= 0)
        {
            CheckNear("anti-deadzone off: first step past the deadzone is small",
                firstStep, 274, 250);
        }
        else
        {
            bool clears = firstStep > XInputMapper.XInputLeftThumbDeadzone;
            bool notWild = firstStep < XInputMapper.XInputLeftThumbDeadzone + 4000;
            Check("anti-deadzone on: first step clears the game's 7849 deadzone", clears, true);
            Check("anti-deadzone on: first step does not overshoot into mid-range", notWild, true);
            Console.WriteLine($"         first step past the deadzone = {firstStep}, " +
                              $"game discards below {XInputMapper.XInputLeftThumbDeadzone}");
        }
        Console.WriteLine();

        Console.WriteLine(" Saturation, full deflection at a comfortable push rather than the mechanical limit:");
        var sat = new CalibrationProfile.AxisCalibration
        {
            Byte = 3, Centre = 128, Min = 0, Max = 255,
            Deadzone = 4, AntiDeadzone = 0, ResponseCurve = 1.0, Saturation = 88
        };
        Check("at the saturation point, output is the rail",
            XInputMapper.Scale(128 + 88, sat), short.MaxValue);
        Check("past the saturation point, output clamps to the rail",
            XInputMapper.Scale(255, sat), short.MaxValue);
        Check("saturation is symmetric on the negative side",
            XInputMapper.Scale(128 - 88, sat), short.MinValue);
        Check("below saturation, output is proportional not clamped",
            XInputMapper.Scale(128 + 46, sat) < short.MaxValue, true);
        var noSat = new CalibrationProfile.AxisCalibration
        {
            Byte = 3, Centre = 128, Min = 0, Max = 255,
            Deadzone = 4, AntiDeadzone = 0, ResponseCurve = 1.0, Saturation = 0
        };
        Check("saturation 0 falls back to the mechanical limit",
            XInputMapper.Scale(128 + 88, noSat) < short.MaxValue, true);
        Console.WriteLine("   Measured on this pad: a normal push deflects 61 of 127 counts, a firm push 88.");
        Console.WriteLine("   Scaling to 127 meant a normal push produced only 60% of full deflection.");
        Console.WriteLine();

        Console.WriteLine(" Axis inversion, using the calibrated left-stick Y (invert = true):");
        var ay = profile.Axes["leftStickY"];
        Check("invert flag is set for Y", ay.InvertForXInput, true);
        Check("raw 0, stick up, maps to the POSITIVE rail",
            XInputMapper.Scale(ay.Min, ay), short.MaxValue);
        Check("raw 255, stick down, maps to the NEGATIVE rail",
            XInputMapper.Scale(ay.Max, ay), short.MinValue);
        Console.WriteLine("   XInput treats Y as positive-up, the pad reports positive-down, so this");
        Console.WriteLine("   inversion is required rather than cosmetic.");
        Console.WriteLine();

        Console.WriteLine(" Axis scaling survives a worn stick that no longer reaches the rails:");
        var worn = new CalibrationProfile.AxisCalibration
        { Byte = 3, Centre = 130, Min = 20, Max = 240, Deadzone = 10, InvertForXInput = false };
        Check("worn centre maps to zero", XInputMapper.Scale(130, worn), 0);
        Check("worn maximum still reaches the rail", XInputMapper.Scale(240, worn), short.MaxValue);
        Check("worn minimum still reaches the rail", XInputMapper.Scale(20, worn), short.MinValue);
        Check("beyond the recorded maximum clamps", XInputMapper.Scale(255, worn), short.MaxValue);
        Console.WriteLine("   Stretching to the measured range is what stops a worn stick losing travel.");
        Console.WriteLine();

        // ---- report decoding -----------------------------------------------------
        Console.WriteLine(" Report decoding, against the Stage 1 and Stage 2 captures:");
        var decoder = new HidDecoder(profile);
        var mapper = new XInputMapper(profile);

        // rest state measured in Stage 1 section 1.6
        var rest = new byte[] { 0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x00, 0x00 };
        var s = decoder.Decode(rest);
        Check("rest: no button active", s.AnyInputActive, false);
        Check("rest: hat is null", s.HatValue, 15);
        var rr = mapper.Map(s);
        Check("rest: XInput buttons are clear", rr.Buttons, (ushort)0);
        Check("rest: both triggers released", $"{rr.LeftTrigger}/{rr.RightTrigger}", "0/0");
        Check("rest: all four thumb axes zero",
            $"{rr.ThumbLX},{rr.ThumbLY},{rr.ThumbRX},{rr.ThumbRY}", "0,0,0,0");
        Console.WriteLine();

        // Every capture below is a verbatim report from the Stage 1 or Stage 2 transcripts.
        var cases = new (string name, byte[] report, Func<PadState, bool> statePredicate,
                         Func<XInputReport, bool> xinputPredicate, string expect)[]
        {
            ("Triangle  02 80 80 80 80 1F 00 00", [0x02,0x80,0x80,0x80,0x80,0x1F,0x00,0x00],
                p => p.Triangle, r => (r.Buttons & XInputButtons.Y) != 0, "XInput Y"),
            ("Circle    02 80 80 80 80 2F 00 00", [0x02,0x80,0x80,0x80,0x80,0x2F,0x00,0x00],
                p => p.Circle, r => (r.Buttons & XInputButtons.B) != 0, "XInput B"),
            ("Cross     02 80 80 80 80 4F 00 00", [0x02,0x80,0x80,0x80,0x80,0x4F,0x00,0x00],
                p => p.Cross, r => (r.Buttons & XInputButtons.A) != 0, "XInput A"),
            ("Square    02 80 80 80 80 8F 00 00", [0x02,0x80,0x80,0x80,0x80,0x8F,0x00,0x00],
                p => p.Square, r => (r.Buttons & XInputButtons.X) != 0, "XInput X"),
            ("L1        02 80 80 80 80 0F 01 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x01,0x00],
                p => p.L1, r => (r.Buttons & XInputButtons.LeftShoulder) != 0, "LeftShoulder"),
            ("R1        02 80 80 80 80 0F 02 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x02,0x00],
                p => p.R1, r => (r.Buttons & XInputButtons.RightShoulder) != 0, "RightShoulder"),
            ("L2        02 80 80 80 80 0F 04 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x04,0x00],
                p => p.L2, r => r.LeftTrigger == 255, "LeftTrigger 255"),
            ("R2        02 80 80 80 80 0F 08 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x08,0x00],
                p => p.R2, r => r.RightTrigger == 255, "RightTrigger 255"),
            ("SELECT    01 80 80 80 80 0F 10 00", [0x01,0x80,0x80,0x80,0x80,0x0F,0x10,0x00],
                p => p.Select, r => (r.Buttons & XInputButtons.Back) != 0, "Back"),
            ("START     01 80 80 80 80 0F 20 00", [0x01,0x80,0x80,0x80,0x80,0x0F,0x20,0x00],
                p => p.Start, r => (r.Buttons & XInputButtons.Start) != 0, "Start"),
            ("L3        02 80 80 80 80 0F 40 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x40,0x00],
                p => p.L3, r => (r.Buttons & XInputButtons.LeftThumb) != 0, "LeftThumb"),
            ("R3        02 80 80 80 80 0F 80 00", [0x02,0x80,0x80,0x80,0x80,0x0F,0x80,0x00],
                p => p.R3, r => (r.Buttons & XInputButtons.RightThumb) != 0, "RightThumb"),
            ("HOME      01 80 80 80 80 0F 00 10", [0x01,0x80,0x80,0x80,0x80,0x0F,0x00,0x10],
                p => p.Home, r => (r.Buttons & XInputButtons.Guide) != 0, "Guide"),
            ("hat UP    02 80 80 80 80 00 00 01", [0x02,0x80,0x80,0x80,0x80,0x00,0x00,0x01],
                p => p.DUp && !p.DDown, r => (r.Buttons & XInputButtons.DPadUp) != 0, "DPadUp"),
            ("hat RIGHT 02 80 80 80 80 02 00 08", [0x02,0x80,0x80,0x80,0x80,0x02,0x00,0x08],
                p => p.DRight && !p.DLeft, r => (r.Buttons & XInputButtons.DPadRight) != 0, "DPadRight"),
            ("hat DOWN  02 80 80 80 80 04 00 02", [0x02,0x80,0x80,0x80,0x80,0x04,0x00,0x02],
                p => p.DDown && !p.DUp, r => (r.Buttons & XInputButtons.DPadDown) != 0, "DPadDown"),
            ("hat LEFT  02 80 80 80 80 06 00 04", [0x02,0x80,0x80,0x80,0x80,0x06,0x00,0x04],
                p => p.DLeft && !p.DRight, r => (r.Buttons & XInputButtons.DPadLeft) != 0, "DPadLeft"),
            ("LS left   02 80 80 00 80 0F 00 00", [0x02,0x80,0x80,0x00,0x80,0x0F,0x00,0x00],
                p => p.LeftX == 0, r => r.ThumbLX == short.MinValue, "ThumbLX at the negative rail"),
            ("LS right  02 80 80 FF 80 0F 00 00", [0x02,0x80,0x80,0xFF,0x80,0x0F,0x00,0x00],
                p => p.LeftX == 255, r => r.ThumbLX == short.MaxValue, "ThumbLX at the positive rail"),
            ("LS up     02 80 80 80 00 0F 00 00", [0x02,0x80,0x80,0x80,0x00,0x0F,0x00,0x00],
                p => p.LeftY == 0, r => r.ThumbLY == short.MaxValue, "ThumbLY positive, inverted"),
            ("LS down   02 80 80 80 FF 0F 00 00", [0x02,0x80,0x80,0x80,0xFF,0x0F,0x00,0x00],
                p => p.LeftY == 255, r => r.ThumbLY == short.MinValue, "ThumbLY negative, inverted"),
            ("RS left   02 00 80 80 80 0F 00 00", [0x02,0x00,0x80,0x80,0x80,0x0F,0x00,0x00],
                p => p.RightX == 0, r => r.ThumbRX == short.MinValue, "ThumbRX at the negative rail"),
            ("RS right  02 FF 80 80 80 0F 00 00", [0x02,0xFF,0x80,0x80,0x80,0x0F,0x00,0x00],
                p => p.RightX == 255, r => r.ThumbRX == short.MaxValue, "ThumbRX at the positive rail"),
            ("RS up     02 80 00 80 80 0F 00 00", [0x02,0x80,0x00,0x80,0x80,0x0F,0x00,0x00],
                p => p.RightY == 0, r => r.ThumbRY == short.MaxValue, "ThumbRY positive, inverted"),
            ("RS down   02 80 FF 80 80 0F 00 00", [0x02,0x80,0xFF,0x80,0x80,0x0F,0x00,0x00],
                p => p.RightY == 255, r => r.ThumbRY == short.MinValue, "ThumbRY negative, inverted"),
        };

        foreach (var c in cases)
        {
            var ps = decoder.Decode(c.report);
            var xr = mapper.Map(ps);
            bool ok = c.statePredicate(ps) && c.xinputPredicate(xr);
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {c.name}  ->  {c.expect}");
            if (!ok)
                Console.WriteLine($"          decoded {ps}");
        }
        Console.WriteLine();

        // ---- regression guards ---------------------------------------------------
        Console.WriteLine(" Regression guards for mistakes already made once on this project:");

        // Findings Log 2.7: Select/Start are HID buttons 9 and 10, not vendor byte 7 bits.
        var sel = decoder.Decode([0x01, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x10, 0x00]);
        var vend = decoder.Decode([0x01, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x00, 0x40]);
        Check("byte 6 bit 4 is SELECT, not a vendor bit", sel.Select, true);
        Check("byte 7 bit 6 is the Android back key, NOT Select", vend.Select, false);
        Check("byte 7 bit 6 sets the Android back flag", vend.AndroidBack, true);
        Check("Android back does not reach XInput", mapper.Map(vend).Buttons, (ushort)0);

        // Findings Log 1.12 / 2.4: triggers are digital, never analog.
        Check("triggers are recorded as digital-only", profile.Triggers.DigitalOnly, true);

        // Stage 1 1.9: shoulders and triggers are not swapped.
        var l1 = decoder.Decode([0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x01, 0x00]);
        Check("byte 6 bit 0 is L1, a shoulder, not a trigger",
            $"{l1.L1}/{l1.L2}", "True/False");
        Console.WriteLine();

        // ---- the wrong-input rule, which a person cannot reach by following the prompts ----
        //
        // Section 5.8: the calibration screen refuses a press that an earlier step already
        // recorded, and says so. That branch only runs when the operator presses the wrong
        // thing, so a clean pass never exercises it and no amount of careful testing by hand
        // will either. These guard it instead.
        Console.WriteLine(" Wrong-input rejection, Section 5.8:");

        byte[] idle = [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x00, 0x00];
        var sweep = new InputSweep();

        Check("an unclaimed press is not refused",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x01, 0x00], idle) is null, true);

        // record L1 the way a completed step does: byte 6 bit 0
        sweep.ClaimBits("COL01", "l1", (6, 0));

        Check("pressing L1 again is refused, and named",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x01, 0x00], idle), "l1");
        Check("a different button is still accepted",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x02, 0x00], idle) is null, true);
        Check("L1 plus an unclaimed bit is accepted, not refused",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x03, 0x00], idle) is null, true);
        Check("the claim does not leak across collections",
            sweep.ClaimedOwner("COL02", [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x01, 0x00], idle) is null, true);
        Check("a report identical to rest is not a press",
            sweep.ClaimedOwner("COL01", rest, idle) is null, true);

        // The hat case, Section 5.8.10: up clears a superset of the bits the other three clear,
        // so a rule that rejected on overlap alone would refuse them all after up was recorded.
        // The vendor byte is what keeps them apart.
        sweep.ClaimBits("COL01", "dpad_up", (5, 0), (5, 1), (5, 2), (5, 3), (7, 0));
        Check("D-pad right survives D-pad up being claimed",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x02, 0x00, 0x08], idle) is null, true);
        Check("D-pad up itself is refused the second time",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x00, 0x00, 0x01], idle), "dpad_up");
        // ---- re-testing one input has to release that input's own claim ----
        //
        // This is the defect the re-test control would have shipped with. The claim exists to
        // refuse a press an earlier step already recorded; on a re-test the step being re-run
        // IS that earlier step, so its own press comes back as "already recorded" and the
        // re-test can never pass. On screen that looks like a button that has stopped working.
        Console.WriteLine(" Re-test releases only its own claim:");

        byte[] l1Press = [0x02, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x01, 0x00];
        Check("before the re-test, L1 is refused", sweep.ClaimedOwner("COL01", l1Press, idle), "l1");
        Check("unclaim reports the bits it released", sweep.Unclaim("l1"), 1);
        Check("L1 IS NOW ACCEPTED AGAIN", sweep.ClaimedOwner("COL01", l1Press, idle) is null, true);
        Check("and D-pad up is still protected",
            sweep.ClaimedOwner("COL01", [0x02, 0x80, 0x80, 0x80, 0x80, 0x00, 0x00, 0x01], idle), "dpad_up");
        Check("unclaiming something never claimed is harmless", sweep.Unclaim("face_down"), 0);
        sweep.Dispose();
        Console.WriteLine();

        // ---- --only selection, shared by the console sweep and the check screen ----
        Console.WriteLine(" Single-input selection:");

        Check("one id selects one step", InputSweep.Select("face_down").Length, 1);
        Check("and it is the one asked for", InputSweep.Select("face_down")[0].Id, "face_down");
        Check("several ids keep the caller's order, not script order",
            string.Join(",", InputSweep.Select("r2,face_up").Select(s => s.Id)), "r2,face_up");
        Check("an unknown id is dropped rather than throwing",
            InputSweep.Select("face_down,nonsense").Length, 1);
        Check("null means the whole script", InputSweep.Select(null).Length, InputSweep.Script.Length);
        var unknown = new List<string>();
        InputSweep.Select("nope", unknown.Add);
        Check("and the unknown id is reported to the caller", string.Join(",", unknown), "nope");
        Console.WriteLine();

        // ---- dual labels ----
        //
        // The prompts stay positional on purpose, so the check can discover an unknown layout.
        // The dual label is the supplement, and the thing worth guarding is that it never
        // becomes the instruction and never contradicts the long-form gloss.
        Console.WriteLine(" Dual button labels:");

        Check("the lower face button is A on Xbox, Cross on PlayStation",
            InputSweep.Script.First(s => s.Id == "face_down").DualLabel, "A / Cross");
        Check("the right face button is B / Circle",
            InputSweep.Script.First(s => s.Id == "face_right").DualLabel, "B / Circle");
        Check("the left trigger is LT / L2",
            InputSweep.Script.First(s => s.Id == "l2").DualLabel, "LT / L2");
        Check("a D-pad direction has no dual label, being the same name everywhere",
            InputSweep.Script.First(s => s.Id == "dpad_up").DualLabel, "");
        Check("nor does a stick push",
            InputSweep.Script.First(s => s.Id == "ls_left").DualLabel, "");

        int labelled = InputSweep.Script.Count(s => s.DualLabel.Length > 0);
        Check("thirteen controls carry both names", labelled, 13);

        // The controls whose symbol is entirely different between pad families. These are the
        // ones a prompt must never lead with, because "Press A" sends somebody holding a
        // PlayStation pad to whatever looks closest and records one input under another's name.
        string[] familyDependent =
            ["face_up", "face_right", "face_down", "face_left", "l1", "r1", "l2", "r2", "l3", "r3"];

        Ok("CONTROLS THAT DIFFER BETWEEN PAD FAMILIES ARE PROMPTED BY POSITION, never by letter",
           InputSweep.Script.Where(s => familyDependent.Contains(s.Id))
               .All(s => !NamesControl(s.Short, s.Xbox) && !NamesControl(s.Short, s.Sony)));

        // The centre cluster is the deliberate exception, and it is not a lapse: these buttons
        // carry the words SELECT and START moulded into this pad, so the printed label is the
        // least ambiguous thing to say. The dual label adds what other pads call them.
        Ok("the centre cluster is still prompted by the label this pad actually carries",
           NamesControl(InputSweep.Script.First(s => s.Id == "select").Short, "SELECT") &&
           NamesControl(InputSweep.Script.First(s => s.Id == "start").Short, "START"));

        Ok("every dual label has two distinct halves",
           InputSweep.Script.All(s => s.DualLabel.Length == 0 ||
               (s.Xbox.Length > 0 && s.Sony.Length > 0 &&
                !s.Xbox.Equals(s.Sony, StringComparison.OrdinalIgnoreCase))));

        // Guards a typo in the column that names the device Dovetail actually emits.
        string[] xbox360Vocabulary =
            ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "Back", "Start", "Guide"];
        Ok("every Xbox label is a real Xbox 360 control name, which is what Dovetail emits",
           InputSweep.Script.Where(s => s.Xbox.Length > 0).All(s => xbox360Vocabulary.Contains(s.Xbox)));
        Console.WriteLine();

        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" {_pass} passed, {_fail} failed");
        Console.WriteLine(new string('=', 74));
        return _fail == 0 ? 0 : 3;
    }

    private static void Ok(string what, bool condition)
    {
        if (condition) _pass++; else _fail++;
        Console.WriteLine($"   {(condition ? "PASS" : "FAIL")}  {what}");
    }

    /// <summary>
    /// Whether a prompt names a control by its printed label, matched on whole words.
    ///
    /// Substring matching is useless here and quietly wrong: the Xbox label for the lower face
    /// button is "A", and "Press the lower face button" contains an A inside "face".
    /// </summary>
    private static bool NamesControl(string text, string label) =>
        label.Length > 0 &&
        text.Split(' ', '.', ',', '-', '(', ')', '/')
            .Any(w => w.Equals(label, StringComparison.OrdinalIgnoreCase));
}
