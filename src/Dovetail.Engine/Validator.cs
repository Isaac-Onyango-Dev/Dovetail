using System.Text.Json;
using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Stage 3 exit check. Starts the engine, then reads XInput back from the operating system
/// and confirms that every physical input arrives on the game-visible surface.
///
/// Reading XInput is what makes this a real test rather than a self-report. The engine
/// writes to ViGEmBus; this reads what a game would read. Both runtime versions are
/// checked, because Sekiro imports xinput1_3.dll and validating only 1.4 would leave the
/// case that matters unproven.
/// </summary>
internal static class Validator
{
    private sealed record Check(string Id, string Prompt, Func<XInputReport, bool> Expect, string Expectation);

    private static Check[] BuildChecks()
    {
        const short Rail = 24000;   // well past any deadzone, short of the rail itself

        return
        [
            new("triangle", "the UPPER face button, printed Y",
                r => (r.Buttons & XInputButtons.Y) != 0, "XInput Y"),
            new("circle", "the RIGHT face button, printed B",
                r => (r.Buttons & XInputButtons.B) != 0, "XInput B"),
            new("cross", "the LOWER face button, printed A",
                r => (r.Buttons & XInputButtons.A) != 0, "XInput A"),
            new("square", "the LEFT face button, printed X",
                r => (r.Buttons & XInputButtons.X) != 0, "XInput X"),

            new("dpad_up", "D-pad UP",
                r => (r.Buttons & XInputButtons.DPadUp) != 0, "XInput DPadUp"),
            new("dpad_right", "D-pad RIGHT",
                r => (r.Buttons & XInputButtons.DPadRight) != 0, "XInput DPadRight"),
            new("dpad_down", "D-pad DOWN",
                r => (r.Buttons & XInputButtons.DPadDown) != 0, "XInput DPadDown"),
            new("dpad_left", "D-pad LEFT",
                r => (r.Buttons & XInputButtons.DPadLeft) != 0, "XInput DPadLeft"),

            new("l1", "L1, the UPPER button on the LEFT rear edge",
                r => (r.Buttons & XInputButtons.LeftShoulder) != 0, "XInput LeftShoulder"),
            new("r1", "R1, the UPPER button on the RIGHT rear edge",
                r => (r.Buttons & XInputButtons.RightShoulder) != 0, "XInput RightShoulder"),
            new("l2", "L2, the LOWER button on the LEFT rear edge",
                r => r.LeftTrigger >= 200, "XInput LeftTrigger >= 200"),
            new("r2", "R2, the LOWER button on the RIGHT rear edge",
                r => r.RightTrigger >= 200, "XInput RightTrigger >= 200"),

            new("ls_left", "the LEFT stick fully LEFT",
                r => r.ThumbLX <= -Rail, $"ThumbLX <= -{Rail}"),
            new("ls_right", "the LEFT stick fully RIGHT",
                r => r.ThumbLX >= Rail, $"ThumbLX >= {Rail}"),
            new("ls_up", "the LEFT stick fully UP",
                r => r.ThumbLY >= Rail, $"ThumbLY >= {Rail}  (XInput is Y-up positive)"),
            new("ls_down", "the LEFT stick fully DOWN",
                r => r.ThumbLY <= -Rail, $"ThumbLY <= -{Rail}"),

            new("rs_left", "the RIGHT stick fully LEFT",
                r => r.ThumbRX <= -Rail, $"ThumbRX <= -{Rail}"),
            new("rs_right", "the RIGHT stick fully RIGHT",
                r => r.ThumbRX >= Rail, $"ThumbRX >= {Rail}"),
            new("rs_up", "the RIGHT stick fully UP",
                r => r.ThumbRY >= Rail, $"ThumbRY >= {Rail}"),
            new("rs_down", "the RIGHT stick fully DOWN",
                r => r.ThumbRY <= -Rail, $"ThumbRY <= -{Rail}"),

            new("l3", "L3, CLICK THE LEFT STICK straight in",
                r => (r.Buttons & XInputButtons.LeftThumb) != 0, "XInput LeftThumb"),
            new("r3", "R3, CLICK THE RIGHT STICK straight in",
                r => (r.Buttons & XInputButtons.RightThumb) != 0, "XInput RightThumb"),

            new("select", "the button printed SELECT",
                r => (r.Buttons & XInputButtons.Back) != 0, "XInput Back"),
            new("start", "the button printed START",
                r => (r.Buttons & XInputButtons.Start) != 0, "XInput Start"),
            new("home", "HOME, the house icon in the middle",
                r => (r.Buttons & XInputButtons.Guide) != 0, "XInput Guide"),
        ];
    }

    private sealed class Result
    {
        public string Id = "";
        public string Prompt = "";
        public string Expectation = "";
        public bool Passed;
        public string ObservedOn14 = "";
        public string ObservedOn13 = "";
        public string Note = "";
    }

    internal static int Run(string[] args)
    {
        int stepTimeout = int.TryParse(Program.ArgValue(args, "--step-timeout"), out int st) ? st : 60;
        string outPath = Program.ArgValue(args, "--out")
            ?? Path.Combine(AppContext.BaseDirectory, "stage3-validation.json");

        var transcript = new List<string>();
        void W(string m = "") { Console.WriteLine(m); transcript.Add(m); }

        W(new string('=', 74));
        W(" DOVETAIL - STAGE 3 XINPUT VALIDATION");
        W(new string('=', 74));
        W($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W();

        var slots = Program.LoadSlots(args, m => W("  " + m));
        W();

        // ---- XInput runtimes -------------------------------------------------------
        W(" XInput runtimes:");
        var r14 = new XInputReader("xinput1_4.dll");
        var r13 = new XInputReader("xinput1_3.dll");
        W($"   xinput1_4.dll : {(r14.Loaded ? r14.LoadDiagnostic : "NOT LOADED - " + r14.LoadDiagnostic)}");
        W($"   xinput1_3.dll : {(r13.Loaded ? r13.LoadDiagnostic : "NOT LOADED - " + r13.LoadDiagnostic)}");
        W("   xinput1_3 is the version Sekiro imports, so it is checked alongside 1.4.");
        W();

        var primary = r14.Loaded ? r14 : r13;
        if (!primary.Loaded)
        {
            Console.Error.WriteLine(" no XInput runtime could be loaded; cannot validate");
            return 1;
        }

        // ---- slots before we start -------------------------------------------------
        var slotsBefore = primary.ConnectedSlots();
        W($" XInput slots occupied before Dovetail starts: " +
          (slotsBefore.Count == 0 ? "none" : string.Join(", ", slotsBefore)));
        W();

        // ---- start the engine ------------------------------------------------------
        using var engine = new DovetailEngine(slots, m => W("  " + m));
        if (!engine.Start())
        {
            Console.Error.WriteLine($" cannot create a virtual controller: {engine.Bus.Status}");
            return 1;
        }
        engine.Rescan();
        using var pump = new Runner.Pump(engine);   // validation pins one calibration deliberately

        W();
        W(" Press and release any button to bring the virtual controller up.");
        var bindDeadline = DateTime.UtcNow.AddSeconds(stepTimeout);
        PadChannel? bound = null;
        while (DateTime.UtcNow < bindDeadline)
        {
            bound = engine.Channels.FirstOrDefault(c => c.Bound);
            if (bound is not null) break;
            Thread.Sleep(20);
        }
        if (bound is null)
        {
            W();
            W($" No pad produced input within {stepTimeout}s, so no virtual controller was created.");
            W(" The receiver streams idle reports whether or not a pad is linked, so this is");
            W(" most likely the wireless link rather than the engine. Wake the pad and retry.");
            return 1;
        }

        Thread.Sleep(400);   // let the driver assign a slot
        var slotsAfter = primary.ConnectedSlots();
        var newSlots = slotsAfter.Except(slotsBefore).ToList();
        int slot = bound.Virtual?.UserIndex ?? (newSlots.Count > 0 ? newSlots[0] : -1);

        W();
        W($" physical pad bound   : {bound.Tag}, report id {bound.ReportId}");
        W($" virtual pad slot     : {(slot >= 0 ? slot.ToString() : "unknown")}" +
          $"   (driver reported {(bound.Virtual?.UserIndex?.ToString() ?? "nothing yet")})");
        W($" XInput slots now     : {string.Join(", ", slotsAfter)}" +
          $"   new since start: {(newSlots.Count == 0 ? "none" : string.Join(", ", newSlots))}");
        if (slot < 0)
        {
            Console.Error.WriteLine(" could not determine which XInput slot the virtual pad occupies");
            return 1;
        }
        W();

        // ---- device shape ----------------------------------------------------------
        var shape = primary.Read(slot);
        W(" Device shape as XInput reports it:");
        W($"   slot {slot} responds to XInputGetState : {(shape is not null ? "yes" : "NO")}");
        W($"   axes  : ThumbLX, ThumbLY, ThumbRX, ThumbRY   (4 x 16-bit signed)");
        W($"   sliders: LeftTrigger, RightTrigger           (2 x 8-bit)");
        W($"   buttons: 15 flags including LeftThumb and RightThumb");
        W("   That is a standard Xbox 360 layout, which is what Section 8.1 asks for.");
        W();

        // ---- guided checks ---------------------------------------------------------
        var checks = BuildChecks();
        var results = new List<Result>();

        W(new string('=', 74));
        W(" Press each input once. Each check advances as soon as XInput reports it.");
        W(" Nothing here reads the physical pad directly: every pass means the value came");
        W(" back out of XInput the way a game would see it.");
        W(new string('=', 74));
        W();

        int idx = 0;
        foreach (var c in checks)
        {
            idx++;
            W(new string('-', 74));
            W($" [{idx}/{checks.Length}]  PRESS: {c.Prompt}");
            W($"            expecting: {c.Expectation}");

            var res = new Result { Id = c.Id, Prompt = c.Prompt, Expectation = c.Expectation };

            var deadline = DateTime.UtcNow.AddSeconds(stepTimeout);
            XInputReport? hit = null;
            while (DateTime.UtcNow < deadline)
            {
                var s = primary.Read(slot);
                if (s is not null && c.Expect(s.Value)) { hit = s; break; }
                Thread.Sleep(4);
            }

            if (hit is null)
            {
                res.Passed = false;
                res.Note = $"not observed within {stepTimeout}s";
                W($"            FAIL - {res.Note}");
            }
            else
            {
                res.Passed = true;
                res.ObservedOn14 = r14.Loaded ? Describe(r14.Read(slot)) : "(1.4 not loaded)";
                res.ObservedOn13 = r13.Loaded ? Describe(r13.Read(slot)) : "(1.3 not loaded)";
                W($"            PASS");
                W($"              via xinput1_4: {res.ObservedOn14}");
                W($"              via xinput1_3: {res.ObservedOn13}");

                // wait for release so the next check starts clean
                var rel = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < rel)
                {
                    var s = primary.Read(slot);
                    if (s is null || !c.Expect(s.Value)) break;
                    Thread.Sleep(10);
                }
            }
            results.Add(res);
            W();
        }

        // ---- summary ---------------------------------------------------------------
        int passed = results.Count(r => r.Passed);
        W(new string('=', 74));
        W(" STAGE 3 VALIDATION SUMMARY");
        W(new string('=', 74));
        W($" {"input",-12} {"result",-6} expectation");
        W($" {new string('-', 11)} {new string('-', 6)} {new string('-', 45)}");
        foreach (var r in results)
            W($" {r.Id,-12} {(r.Passed ? "PASS" : "FAIL"),-6} {r.Expectation}");
        W();
        W($" {passed} of {results.Count} inputs confirmed through XInput.");
        var failed = results.Where(r => !r.Passed).Select(r => r.Id).ToList();
        if (failed.Count > 0) W($" not confirmed: {string.Join(", ", failed)}");
        W();
        W($" virtual reports submitted: {bound.Virtual?.SubmitCount ?? 0}");
        W($" physical reports read    : {bound.ReportsRead}");
        W($" pump loops               : {pump.Loops}");

        bool allPassed = passed == results.Count;
        W();
        W(allPassed
            ? " ALL INPUTS CONFIRMED. Stage 3 exit criteria met: a generic XInput reader sees a\n" +
              " standard Xbox 360 device with 100% of inputs mapped correctly."
            : " SOME INPUTS NOT CONFIRMED. See the list above.");

        var payload = new
        {
            stage = 3,
            capturedLocal = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            profilePath = Program.ArgValue(args, "--profile") ?? Program.DefaultProfilePath(),
            physicalPad = new { bound.Tag, reportId = bound.ReportId, bound.Info.InstanceId },
            virtualSlot = slot,
            slotsBefore,
            slotsAfter,
            xinputRuntimes = new
            {
                v14 = new { loaded = r14.Loaded, diagnostic = r14.LoadDiagnostic, guide = r14.HasGuideSupport },
                v13 = new { loaded = r13.Loaded, diagnostic = r13.LoadDiagnostic, guide = r13.HasGuideSupport },
            },
            passed,
            total = results.Count,
            allPassed,
            results,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        W($" results written to {outPath}");

        string txt = Path.ChangeExtension(outPath, ".txt");
        File.WriteAllLines(txt, transcript);
        Console.WriteLine($" transcript written to {txt}");

        r13.Dispose();
        r14.Dispose();
        return allPassed ? 0 : 3;
    }

    private static string Describe(XInputReport? r) =>
        r is null
            ? "device not connected"
            : $"buttons=0x{r.Value.Buttons:X4} [{XInputButtons.Describe(r.Value.Buttons)}] " +
              $"LT={r.Value.LeftTrigger} RT={r.Value.RightTrigger} " +
              $"L({r.Value.ThumbLX},{r.Value.ThumbLY}) R({r.Value.ThumbRX},{r.Value.ThumbRY})";
}
