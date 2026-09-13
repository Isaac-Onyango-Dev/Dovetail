using Dovetail.Core;

namespace Dovetail.Diagnostics;

/// <summary>
/// Stage 2 calibration wizard. Guided one step at a time, in the manner of a console's
/// controller setup screen, and it advances by itself so the operator never has to type.
///
/// Every step is event driven: it settles the pad to rest, then waits for the operator to
/// act, then advances on release. The first version used fixed time windows and the trigger
/// step failed because its window opened while the operator was still recentring the stick
/// from the previous step. Waiting for a real change is what made all 25 sweep steps work,
/// so that pattern is used throughout here.
///
/// Steps can be re-run individually with --only, which merges into the saved profile
/// instead of discarding the parts that already passed.
/// </summary>
internal static class Calibrate
{
    // Verified positions from Stage 1. Bytes index into the 8-byte report, report id at 0.
    private const int ByRightX = 1;   // HID Z
    private const int ByRightY = 2;   // HID Rz
    private const int ByLeftX = 3;    // HID X
    private const int ByLeftY = 4;    // HID Y
    private const int ByHatBtn = 5;   // hat in bits 0-3, buttons 1-4 in bits 4-7
    private const int ByButtons = 6;  // buttons 5-12
    private const int ByVendor = 7;   // D-pad duplicate, Home, Start, Select

    private const int L2Bit = 2;      // byte 6, HID button 7
    private const int R2Bit = 3;      // byte 6, HID button 8
    private const int L3Bit = 6;      // byte 6, HID button 11
    private const int R3Bit = 7;      // byte 6, HID button 12

    private static readonly int[] AnalogBytes = [ByRightX, ByRightY, ByLeftX, ByLeftY];

    /// <summary>Floor deadzone in raw counts even when measured noise is zero, to absorb wear.</summary>
    private const int DeadzoneFloor = 8;

    private sealed record AxisDef(string Name, int Byte, string Usage, bool Invert);

    private static readonly AxisDef[] AxisDefs =
    [
        new("leftStickX",  ByLeftX,  "X",  false),
        new("leftStickY",  ByLeftY,  "Y",  true),
        new("rightStickX", ByRightX, "Z",  false),
        new("rightStickY", ByRightY, "Rz", true),
    ];

    private static readonly string[] AllSteps = ["rest", "leftstick", "rightstick", "triggers", "clicks"];

    internal static int Run(string[] args, ushort vid, ushort pid)
    {
        // A profile belongs to a PLAYER SLOT, not to a port and not to a physical unit.
        // Findings Log 7.6: nothing in the HID data distinguishes one pad from the other, and
        // the receiver assigns a channel by power-on order, so neither a unit nor a port is a
        // stable identity. Dovetail behaves like a console: slot 1 is whoever connects first.
        // The slot can be named explicitly with --slot; otherwise the first free one is used.
        string profileDir = Program.ArgValue(args, "--dir")
            ?? Path.GetDirectoryName(Program.ArgValue(args, "--out") ?? "")
            ?? "";
        if (string.IsNullOrWhiteSpace(profileDir))
            profileDir = ProfileStore.Resolve(m => Console.WriteLine(" " + m));
        string? explicitOut = Program.ArgValue(args, "--out");

        var only = Program.ArgValue(args, "--only")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();
        var steps = only is null ? AllSteps : AllSteps.Where(only.Contains).ToArray();
        if (steps.Length == 0)
        {
            Console.Error.WriteLine($" --only matched no step. Valid: {string.Join(", ", AllSteps)}");
            return 1;
        }

        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - STAGE 2 CALIBRATION WIZARD");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (only is not null)
            Console.WriteLine($" re-running only: {string.Join(", ", steps)}   (other results are kept)");
        Console.WriteLine();

        var candidates = HidScan.Enumerate(vid, pid)
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($" No HID interface for VID 0x{vid:X4} / PID 0x{pid:X4}. Connect the controller.");
            return 1;
        }
        Console.WriteLine($" collections present: {string.Join(", ", candidates.Select(c => c.CollectionTag))}");
        Console.WriteLine();

        var registry = SlotRegistry.Load(profileDir);
        int targetSlot = int.TryParse(Program.ArgValue(args, "--slot"), out int askedSlot) && askedSlot >= 1
            ? askedSlot
            : (registry.NextFreeSlotNumber() is int free and > 0 ? free : 1);

        string outPath = explicitOut ?? SlotRegistry.PathForSlot(profileDir, targetSlot);
        Console.WriteLine($" calibrating PLAYER {targetSlot}" +
                          (registry.ForSlot(targetSlot) is { } existingProf
                            ? $", replacing the existing \"{existingProf.EffectiveName}\""
                            : ", a new controller"));
        Console.WriteLine($" profile file: {Path.GetFileName(outPath)}");
        Console.WriteLine();

        // Start from this slot's saved profile when it exists, so a --only re-run merges
        // rather than discarding the parts that already passed. A brand-new slot inherits the
        // stick-feel preferences already settled on another slot, because deadzone,
        // anti-deadzone, curve and saturation belong to the operator rather than to a unit,
        // while centres and ranges are measured fresh below.
        CalibrationProfile profile;
        bool merging = File.Exists(outPath);
        if (merging)
        {
            profile = CalibrationProfile.Load(outPath);
            Console.WriteLine($" loaded the existing profile for player {targetSlot}");
        }
        else
        {
            profile = new CalibrationProfile();
            var donor = registry.Slots.Values.FirstOrDefault();
            if (donor is not null)
            {
                Console.WriteLine(" inheriting stick-feel settings from an already-calibrated controller;");
                Console.WriteLine(" this pad's own centres and ranges are measured fresh below");
                profile.Axes = donor.Axes.ToDictionary(kv => kv.Key, kv => new CalibrationProfile.AxisCalibration
                {
                    Byte = kv.Value.Byte,
                    HidUsage = kv.Value.HidUsage,
                    InvertForXInput = kv.Value.InvertForXInput,
                    Deadzone = kv.Value.Deadzone,
                    AntiDeadzone = kv.Value.AntiDeadzone,
                    ResponseCurve = kv.Value.ResponseCurve,
                    Saturation = kv.Value.Saturation,
                    Centre = 128, Min = 128, Max = 128,
                });
                profile.Buttons = donor.Buttons;
                profile.Hat = donor.Hat;
                profile.Triggers = donor.Triggers;
            }
        }
        profile.SlotNumber = targetSlot;
        profile.Name = $"slot{targetSlot}";
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            profile.DisplayName = $"Player {targetSlot} Pad";
        merging = merging && only is not null;
        Console.WriteLine();
        profile.CreatedLocal = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

        // ---- locate the live port -------------------------------------------------
        // A re-run already knows which port the pad is on, so it resolves the collection
        // from the saved report id and asks the operator for nothing. Only a first run
        // needs the press, because idle data cannot distinguish the two ports.
        HidCollectionInfo? live = null;
        byte liveReportId = 0;
        byte[] liveRest = [];


        if (live is null)
        {
            Console.WriteLine(" STEP 0   Finding which adapter port your pad is on.");
            Console.WriteLine("          Press and release any button on the controller now.");
            Console.WriteLine("          Both ports stream identical idle data, so this needs a real press.");
            var choice = DeviceSelector.SelectByActivity(candidates, 120000, Console.WriteLine);
            if (choice is null)
            {
                Console.WriteLine();
                Console.WriteLine(" No collection moved within 120s, so no port could be identified.");
                Console.WriteLine(" The adapter is still streaming idle reports, so this is not a cable fault.");
                Console.WriteLine(" Wake the pad with a button press and run this again. Nothing was saved.");
                return 1;
            }
            live = choice.Info;
            liveReportId = choice.ReportId;
            liveRest = choice.RestState;
            Console.WriteLine($"   pad is on {live.CollectionTag}, report id {liveReportId}");
            Console.WriteLine($"   selected because: {choice.Reason}");
        }
        Console.WriteLine();

        profile.Device = new CalibrationProfile.DeviceIdentity
        {
            Vid = $"0x{live.Vid:X4}",
            Pid = $"0x{live.Pid:X4}",
            Product = live.Product,
            Manufacturer = live.Manufacturer,
            CollectionTag = live.CollectionTag,
            ReportId = liveReportId,
            InstanceId = live.InstanceId,
            DevicePath = live.DevicePath,
            InputReportByteLength = live.Caps.InputReportByteLength,
            RestState = liveRest.Length > 0 ? Hex(liveRest) : profile.Device.RestState,
        };

        using var reader = new HidReader(live.DevicePath, live.Caps.InputReportByteLength);

        // ---- rest-state noise -----------------------------------------------------
        if (steps.Contains("rest") || profile.Axes.Count == 0)
        {
            Console.WriteLine(" STEP 1   Rest-state noise.");
            Console.WriteLine("          Let go of both sticks completely. Do not touch the pad.");
            Settle(reader, "          waiting for the pad to settle");
            Console.WriteLine("          sampling for 6 seconds...");
            var rest = SampleWindow(reader, 6000);
            Console.WriteLine($"   {rest.Count} reports sampled");

            var axes = new Dictionary<string, CalibrationProfile.AxisCalibration>();
            foreach (var a in AxisDefs)
            {
                var vals = rest.Select(r => (int)r[a.Byte]).ToList();
                int centre = Median(vals);
                int noise = vals.Count == 0 ? 0 : vals.Max(v => Math.Abs(v - centre));
                var prev = profile.Axes.GetValueOrDefault(a.Name);
                axes[a.Name] = new CalibrationProfile.AxisCalibration
                {
                    Byte = a.Byte,
                    HidUsage = a.Usage,
                    Centre = centre,
                    RestNoise = noise,
                    InvertForXInput = a.Invert,
                    Min = prev?.Min ?? centre,
                    Max = prev?.Max ?? centre,
                    FullRangeReached = prev?.FullRangeReached ?? false,
                    // Keep a deadzone that was already tuned, and only widen it if this
                    // unit is genuinely noisier. DeadzoneFloor guards a fresh calibration
                    // against shipping zero; it must not veto a lower value the operator
                    // chose on purpose.
                    Deadzone = prev is not null
                        ? Math.Max(prev.Deadzone, noise * 2)
                        : Math.Max(noise * 2, DeadzoneFloor),
                    AntiDeadzone = prev?.AntiDeadzone ?? 0,
                    ResponseCurve = prev?.ResponseCurve ?? 1.0,
                    Saturation = prev?.Saturation ?? 0,
                };
                Console.WriteLine($"   {a.Name,-12} byte {a.Byte}  centre {centre,3}  " +
                                  $"observed {(vals.Count > 0 ? vals.Min() : centre),3}.." +
                                  $"{(vals.Count > 0 ? vals.Max() : centre),3}  noise +/-{noise}");
            }
            profile.Axes = axes;
            Console.WriteLine();
        }

        // ---- stick ranges ---------------------------------------------------------
        foreach (var (stepName, label, xName, yName, n) in new[]
                 {
                     ("leftstick",  "LEFT",  "leftStickX",  "leftStickY",  2),
                     ("rightstick", "RIGHT", "rightStickX", "rightStickY", 3),
                 })
        {
            if (!steps.Contains(stepName)) continue;

            Console.WriteLine($" STEP {n}   {label} stick full range.");
            Console.WriteLine($"          Roll the {label} stick slowly around its full circle,");
            Console.WriteLine("          pushing it hard against the rim. Keep going for 8 seconds.");
            Console.WriteLine("          Starts as soon as you move THAT STICK. A button press will not");
            Console.WriteLine("          start it, so a mistaken trigger pull costs nothing.");
            int bx = profile.Axes.TryGetValue(xName, out var axDef) ? axDef.Byte : 3;
            int by2 = profile.Axes.TryGetValue(yName, out var ayDef) ? ayDef.Byte : 4;
            if (!WaitForAxisChange(reader, [bx, by2], 120000, Console.WriteLine))
            {
                Console.WriteLine($"   {label} stick never moved within 120s. Skipping, previous values kept.");
                Console.WriteLine();
                continue;
            }
            var window = SampleWindow(reader, 8000);

            foreach (var name in new[] { xName, yName })
            {
                if (!profile.Axes.TryGetValue(name, out var a)) continue;
                var vals = window.Select(r => (int)r[a.Byte]).ToList();
                a.Min = Math.Min(a.Min, vals.Count > 0 ? vals.Min() : a.Centre);
                a.Max = Math.Max(a.Max, vals.Count > 0 ? vals.Max() : a.Centre);
                a.FullRangeReached = a.Min <= 2 && a.Max >= 253;
                Console.WriteLine($"   {name,-12} range {a.Min,3}..{a.Max,3}  deadzone +/-{a.Deadzone}  " +
                                  $"{(a.FullRangeReached ? "full range reached" : "DID NOT REACH BOTH RAILS")}");
            }
            Console.WriteLine();
        }

        // ---- triggers -------------------------------------------------------------
        if (steps.Contains("triggers"))
        {
            Console.WriteLine(" STEP 4   Triggers.");
            Console.WriteLine("          Each one waits for you. Take as long as you like.");
            Console.WriteLine();

            Console.WriteLine("   L2: pull the LEFT trigger all the way in, hold a moment, release.");
            var lt = WaitForBitCycle(reader, ByButtons, L2Bit, 90000);
            Report("L2", lt);

            Console.WriteLine("   R2: now the RIGHT trigger, the same way.");
            var rt = WaitForBitCycle(reader, ByButtons, R2Bit, 90000);
            Report("R2", rt);

            var analogMoved = lt.AnalogBytesMoved.Union(rt.AnalogBytesMoved).ToList();
            profile.Triggers = new CalibrationProfile.TriggerCalibration
            {
                DigitalOnly = analogMoved.Count == 0,
                LeftVerified = lt.Pressed,
                RightVerified = rt.Pressed,
            };
            Console.WriteLine(profile.Triggers.DigitalOnly
                ? "   confirmed digital-only: no analog byte moved while either trigger travelled"
                : $"   NOTE: analog byte(s) {string.Join(",", analogMoved)} moved while a trigger was held.");
            Console.WriteLine();

            if (profile.Buttons.TryGetValue("l2", out var b2)) b2.Verified = lt.Pressed;
            if (profile.Buttons.TryGetValue("r2", out var b3)) b3.Verified = rt.Pressed;
        }

        // ---- L3 / R3 isolation ----------------------------------------------------
        if (steps.Contains("clicks"))
        {
            Console.WriteLine(" STEP 5   Stick-click isolation test.");
            Console.WriteLine("          This is the input Steam Input drops, so it gets its own test.");
            Console.WriteLine();

            profile.StickClicks.Left = ClickTest(reader, "LEFT", "L3", L3Bit,
                ByLeftX, ByLeftY, profile.Axes["leftStickX"], profile.Axes["leftStickY"]);
            profile.StickClicks.Right = ClickTest(reader, "RIGHT", "R3", R3Bit,
                ByRightX, ByRightY, profile.Axes["rightStickX"], profile.Axes["rightStickY"]);

            if (profile.Buttons.TryGetValue("l3", out var bl)) bl.Verified = profile.StickClicks.Left.Isolated;
            if (profile.Buttons.TryGetValue("r3", out var br)) br.Verified = profile.StickClicks.Right.Isolated;
        }

        // ---- button and hat tables from Stage 1 -----------------------------------
        if (profile.Buttons.Count == 0) profile.Buttons = DefaultButtons();
        if (profile.Hat.Directions.Count == 0) profile.Hat = DefaultHat();

        profile.Notes = [
            "Axis bytes, button bits and the vendor byte come from the Stage 1 sweep, 25 of 25 inputs measured on the wire.",
            "Select, Start and Home live in vendor byte 7, not in HID buttons 9 and 10. Buttons 9 and 10 are declared by the descriptor but never fire.",
            $"Deadzone floor of {DeadzoneFloor} raw counts is applied even when measured rest noise is zero, to absorb stick wear over time.",
            "Every step is event driven; it waits for the operator rather than running a fixed clock.",
        ];

        profile.Save(outPath);

        // ---- summary ---------------------------------------------------------------
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" CALIBRATION SUMMARY");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" {"axis",-12} {"byte",4} {"centre",6} {"min",4} {"max",4} {"noise",5} {"dz",4}  range");
        Console.WriteLine($" {new string('-', 11)} {new string('-', 4)} {new string('-', 6)} {new string('-', 4)} {new string('-', 4)} {new string('-', 5)} {new string('-', 4)}  -----");
        foreach (var (name, a) in profile.Axes)
            Console.WriteLine($" {name,-12} {a.Byte,4} {a.Centre,6} {a.Min,4} {a.Max,4} {a.RestNoise,5} {a.Deadzone,4}  " +
                              $"{(a.FullRangeReached ? "ok" : "INCOMPLETE")}");
        Console.WriteLine();
        Console.WriteLine($" triggers   digital-only={profile.Triggers.DigitalOnly}  " +
                          $"L2 verified={profile.Triggers.LeftVerified}  R2 verified={profile.Triggers.RightVerified}");
        Console.WriteLine();
        foreach (var (label, r) in new[] { ("L3", profile.StickClicks.Left), ("R3", profile.StickClicks.Right) })
            Console.WriteLine($" {label}  hold={(r.HoldPassed ? "PASS" : "FAIL")} ({r.HoldMillisecondsObserved}ms)  " +
                              $"taps={r.TapsDetected}/{r.TapsRequested} {(r.TapsPassed ? "PASS" : "FAIL")}  " +
                              $"max axis disturbance={r.MaxAxisDisturbance}  " +
                              $"isolated={(r.Isolated ? "YES" : "NO")}");
        Console.WriteLine();

        bool allGood = profile.Axes.Values.All(a => a.FullRangeReached)
                       && profile.Triggers.LeftVerified && profile.Triggers.RightVerified
                       && profile.StickClicks.Left.Isolated && profile.StickClicks.Right.Isolated;
        Console.WriteLine(allGood
            ? " ALL CHECKS PASSED. Stage 2 exit criteria met."
            : " SOME CHECKS DID NOT PASS. Re-run just the affected step, for example:\n" +
              "     dovetail-diag calibrate --only triggers --out <same profile path>");
        Console.WriteLine($" calibration saved to {outPath}");
        return allGood ? 0 : 3;
    }

    // ---------------------------------------------------------------- steps

    private sealed record BitCycle(
        bool Pressed, int HoldMs, List<int> AnalogBytesMoved, int MaxAxisDeviation, string Note);

    private static void Report(string label, BitCycle c)
    {
        Console.WriteLine(c.Pressed
            ? $"       {label} registered. held {c.HoldMs}ms. " +
              $"analog bytes that moved while held: {(c.AnalogBytesMoved.Count == 0 ? "none" : string.Join(",", c.AnalogBytesMoved))}"
            : $"       {label} NOT DETECTED. {c.Note}");
    }

    /// <summary>
    /// Settles the pad, then waits indefinitely (to the timeout) for one press-and-release
    /// of the given bit, measuring hold time and whether any analog byte moved meanwhile.
    /// </summary>
    private static BitCycle WaitForBitCycle(HidReader reader, int byteIndex, int bit, int timeoutMs)
    {
        var rest = reader.WaitForRest(300, 8000);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        // wait for the press
        DateTime pressedAt = default;
        while (DateTime.UtcNow < deadline)
        {
            var r = reader.Read(25);
            if (r is null || r.Length <= byteIndex) continue;
            if ((r[byteIndex] & (1 << bit)) != 0) { pressedAt = DateTime.UtcNow; break; }
        }
        if (pressedAt == default)
            return new BitCycle(false, 0, [], 0, $"no press seen within {timeoutMs / 1000}s.");

        // measure the hold and watch the analog bytes
        var moved = new HashSet<int>();
        int maxDev = 0;
        var holdDeadline = DateTime.UtcNow.AddMilliseconds(20000);
        while (DateTime.UtcNow < holdDeadline)
        {
            var r = reader.Read(25);
            if (r is null || r.Length <= byteIndex) continue;

            foreach (int ab in AnalogBytes)
            {
                if (ab >= r.Length) continue;
                int centre = rest is { Length: > 0 } && ab < rest.Length ? rest[ab] : 128;
                int dev = Math.Abs(r[ab] - centre);
                if (dev > 24) moved.Add(ab);
                maxDev = Math.Max(maxDev, dev);
            }

            if ((r[byteIndex] & (1 << bit)) == 0) break;    // released
        }
        int held = (int)(DateTime.UtcNow - pressedAt).TotalMilliseconds;
        return new BitCycle(true, held, moved.ToList(), maxDev, "");
    }

    /// <summary>
    /// The isolation test the charter asks for: press-and-hold, then quick taps, while
    /// watching whether the stick axes move at the same time.
    /// </summary>
    private static CalibrationProfile.StickClickTest.ClickResult ClickTest(
        HidReader reader, string label, string name, int bit,
        int axisXByte, int axisYByte,
        CalibrationProfile.AxisCalibration ax, CalibrationProfile.AxisCalibration ay)
    {
        var result = new CalibrationProfile.StickClickTest.ClickResult { TapsRequested = 5 };

        Console.WriteLine($"   {name} part 1, PRESS AND HOLD.");
        Console.WriteLine($"        Push the {label} stick straight down and hold for about 2 seconds,");
        Console.WriteLine("        without leaning it. Waits for you.");
        var hold = WaitForBitCycle(reader, ByButtons, bit, 90000);
        result.HoldMillisecondsObserved = hold.HoldMs;
        result.HoldPassed = hold.Pressed && hold.HoldMs >= 800;
        result.MaxAxisDisturbance = hold.MaxAxisDeviation;
        Console.WriteLine(hold.Pressed
            ? $"        held {hold.HoldMs}ms  {(result.HoldPassed ? "PASS" : "FAIL, needed 800ms or more")}  " +
              $"max axis deviation while held: {hold.MaxAxisDeviation} counts"
            : $"        NOT DETECTED. {hold.Note}");

        Console.WriteLine($"   {name} part 2, QUICK TAPS.");
        Console.WriteLine($"        Tap the {label} stick in 5 times, quickly and distinctly.");
        var taps = CountTaps(reader, ByButtons, bit, 5, 90000, 5000, axisXByte, axisYByte, ax.Centre, ay.Centre);
        result.TapsDetected = taps.count;
        result.TapsPassed = taps.count >= 5;
        result.MaxAxisDisturbance = Math.Max(result.MaxAxisDisturbance, taps.maxDev);
        Console.WriteLine($"        {taps.count} clean press-release cycles detected  " +
                          $"{(result.TapsPassed ? "PASS" : "FAIL, needed 5")}");

        int dz = Math.Max(ax.Deadzone, ay.Deadzone);
        result.Isolated = result.HoldPassed && result.TapsPassed;
        result.Note = result.MaxAxisDisturbance > dz * 3
            ? $"click registered, but the stick moved up to {result.MaxAxisDisturbance} counts during the test, " +
              $"more than three times the {dz}-count deadzone. The bit is real; the stick was leaned."
            : "click registered cleanly with the stick near centre.";
        Console.WriteLine($"        {result.Note}");
        Console.WriteLine();
        return result;
    }

    /// <summary>
    /// Waits for the first tap, then counts press-release cycles until the target is met or
    /// the operator stops tapping for <paramref name="idleMs"/>.
    /// </summary>
    private static (int count, int maxDev) CountTaps(
        HidReader reader, int byteIndex, int bit, int needed, int firstTimeoutMs, int idleMs,
        int axisXByte, int axisYByte, int centreX, int centreY)
    {
        int count = 0, maxDev = 0;
        bool last = false;
        var firstDeadline = DateTime.UtcNow.AddMilliseconds(firstTimeoutMs);
        var idleDeadline = DateTime.UtcNow.AddMilliseconds(firstTimeoutMs);
        bool started = false;

        while (DateTime.UtcNow < (started ? idleDeadline : firstDeadline))
        {
            var r = reader.Read(25);
            if (r is null || r.Length <= byteIndex) continue;

            bool now = (r[byteIndex] & (1 << bit)) != 0;
            if (now)
            {
                if (axisXByte < r.Length) maxDev = Math.Max(maxDev, Math.Abs(r[axisXByte] - centreX));
                if (axisYByte < r.Length) maxDev = Math.Max(maxDev, Math.Abs(r[axisYByte] - centreY));
            }
            if (now && !last) { started = true; }
            if (!now && last)
            {
                count++;
                idleDeadline = DateTime.UtcNow.AddMilliseconds(idleMs);
                if (count >= needed) break;
            }
            last = now;
        }
        return (count, maxDev);
    }

    // ---------------------------------------------------------------- helpers

    private static void Settle(HidReader reader, string message)
    {
        Console.WriteLine($"{message}...");
        reader.WaitForRest(400, 10000);
    }

    /// <summary>
    /// Blocks until one of the named axis bytes moves clear of its rest value. Button
    /// presses and the other stick are ignored, and the operator is told once when they
    /// move something the step is not asking for.
    /// </summary>
    private static bool WaitForAxisChange(
        HidReader reader, int[] axisBytes, int timeoutMs, Action<string>? log = null)
    {
        var rest = reader.WaitForRest(250, 8000);
        if (rest is null || rest.Length == 0) return false;

        const int MoveThreshold = 20;   // well clear of any deadzone, still a light push
        bool warnedButton = false, warnedOtherStick = false;
        int[] allAxes = AnalogBytes;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var r = reader.Read(25);
            if (r is null || r.Length < rest.Length) continue;

            foreach (int b in axisBytes)
                if (b < r.Length && Math.Abs(r[b] - rest[b]) > MoveThreshold) return true;

            if (!warnedButton &&
                ((r[ByHatBtn] & 0xF0) != (rest[ByHatBtn] & 0xF0) ||
                 r[ByButtons] != rest[ByButtons] || r[ByVendor] != rest[ByVendor]))
            {
                warnedButton = true;
                log?.Invoke("          (that was a button, not the stick. Still waiting for the stick.)");
            }

            if (!warnedOtherStick)
                foreach (int b in allAxes)
                {
                    if (axisBytes.Contains(b) || b >= r.Length) continue;
                    if (Math.Abs(r[b] - rest[b]) <= MoveThreshold) continue;
                    warnedOtherStick = true;
                    log?.Invoke("          (that was the other stick. Still waiting for this one.)");
                    break;
                }
        }
        return false;
    }

    private static List<byte[]> SampleWindow(HidReader reader, int ms)
    {
        var all = new List<byte[]>();
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            var r = reader.Read(20);
            if (r is { Length: > 0 }) all.Add(r);
        }
        return all;
    }

    private static Dictionary<string, CalibrationProfile.ButtonBit> DefaultButtons() => new()
    {
        ["triangle"] = new() { Byte = ByHatBtn, Bit = 4, HidButton = 1, XInput = "Y", Verified = true },
        ["circle"]   = new() { Byte = ByHatBtn, Bit = 5, HidButton = 2, XInput = "B", Verified = true },
        ["cross"]    = new() { Byte = ByHatBtn, Bit = 6, HidButton = 3, XInput = "A", Verified = true },
        ["square"]   = new() { Byte = ByHatBtn, Bit = 7, HidButton = 4, XInput = "X", Verified = true },
        ["l1"]       = new() { Byte = ByButtons, Bit = 0, HidButton = 5, XInput = "LeftShoulder", Verified = true },
        ["r1"]       = new() { Byte = ByButtons, Bit = 1, HidButton = 6, XInput = "RightShoulder", Verified = true },
        ["l2"]       = new() { Byte = ByButtons, Bit = L2Bit, HidButton = 7, XInput = "LeftTrigger" },
        ["r2"]       = new() { Byte = ByButtons, Bit = R2Bit, HidButton = 8, XInput = "RightTrigger" },
        ["l3"]       = new() { Byte = ByButtons, Bit = L3Bit, HidButton = 11, XInput = "LeftThumb" },
        ["r3"]       = new() { Byte = ByButtons, Bit = R3Bit, HidButton = 12, XInput = "RightThumb" },
        ["select"]   = new() { Byte = ByVendor, Bit = 6, XInput = "Back", VendorDefined = true, Verified = true },
        ["start"]    = new() { Byte = ByVendor, Bit = 5, XInput = "Start", VendorDefined = true, Verified = true },
        ["home"]     = new() { Byte = ByVendor, Bit = 4, XInput = "Guide", VendorDefined = true, Verified = true },
    };

    private static CalibrationProfile.HatCalibration DefaultHat() => new()
    {
        Byte = ByHatBtn, BitOffset = 0, BitCount = 4, NullValue = 15,
        Directions = new Dictionary<string, int>
        {
            ["up"] = 0, ["upRight"] = 1, ["right"] = 2, ["downRight"] = 3,
            ["down"] = 4, ["downLeft"] = 5, ["left"] = 6, ["upLeft"] = 7
        }
    };

    private static int Median(List<int> v)
    {
        if (v.Count == 0) return 128;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    private static string Hex(byte[] b) =>
        b.Length == 0 ? "(no report)" : string.Join(" ", b.Select(x => x.ToString("X2")));
}
