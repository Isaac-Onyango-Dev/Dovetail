using System.Text.Json;
using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Proves two physical pads drive two independent XInput players, which is what a local
/// multiplayer game like FIFA needs.
///
/// This has never been exercised, because until now only one pad had ever been powered on
/// at a time: every session in the artifacts folder shows activity on a single port. The
/// engine was written to support N pads mapping to N virtual devices, but untested support
/// is a claim rather than a capability.
///
/// Independence is the part that actually matters and the part most likely to be wrong. Two
/// pads appearing in two slots proves nothing if pressing a button on one moves both, or if
/// both end up bound to the same collection. So each pad is tested for a press that appears
/// in its own slot and, at the same moment, does *not* appear in the other.
/// </summary>
internal static class MultiPad
{
    private sealed class PadResult
    {
        public string Tag = "";
        public byte ReportId;
        public string Name = "";
        public int DovetailSlot;          // the player slot Dovetail assigned
        public int XInputSlot = -1;     // the slot the XInput driver assigned
        public bool PressSeenInOwnSlot;
        public bool BledIntoOtherSlot;
        public string Evidence = "";
    }

    internal static int Run(string[] args)
    {
        int stepTimeout = int.TryParse(Program.ArgValue(args, "--step-timeout"), out int st) ? st : 90;
        string outPath = Program.ArgValue(args, "--out")
            ?? Path.Combine(AppContext.BaseDirectory, "stage4-multipad.json");

        var transcript = new List<string>();
        void W(string m = "") { Console.WriteLine(m); transcript.Add(m); }

        W(new string('=', 74));
        W(" DOVETAIL - TWO PADS AS TWO XINPUT PLAYERS");
        W(new string('=', 74));
        W($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W();

        var slots = Program.LoadSlots(args, m => W("  " + m));
        W();

        var reader = new XInputReader("xinput1_4.dll");
        if (!reader.Loaded) { Console.Error.WriteLine(" xinput1_4.dll did not load"); return 1; }

        var slotsBefore = reader.ConnectedSlots();
        W($" XInput slots occupied before Dovetail starts: " +
          (slotsBefore.Count == 0 ? "none" : string.Join(", ", slotsBefore)));
        W();

        using var engine = new DovetailEngine(slots, m => W("  " + m));
        if (!engine.Start()) { Console.Error.WriteLine($" {engine.Bus.Status}"); return 1; }
        engine.Rescan();
        using var pump = new Runner.Pump(engine);

        // ---- bring both pads up ----
        W(new string('=', 74));
        W(" STEP 1   Turn BOTH pads on, then press a button on EACH of them.");
        W("          A pad only gets a virtual controller once it produces input, so both");
        W("          need a press. Order matters: the first to move becomes player 1.");
        W(new string('=', 74));
        W();

        var deadline = DateTime.UtcNow.AddSeconds(stepTimeout);
        var announced = new HashSet<string>();
        while (DateTime.UtcNow < deadline)
        {
            foreach (var ch in engine.Channels.Where(c => c.Bound))
            {
                if (!announced.Add(ch.Tag)) continue;
                Thread.Sleep(350);   // let the driver assign a slot
                W($"   \"{ch.DisplayName}\" (Dovetail slot {ch.Slot}, report id {ch.ReportId}) " +
                  $"-> XInput slot {ch.Virtual?.UserIndex?.ToString() ?? "pending"}");
            }
            if (engine.Channels.Count(c => c.Bound) >= 2) break;
            Thread.Sleep(40);
        }
        Thread.Sleep(500);

        var bound = engine.Channels.Where(c => c.Bound).ToList();
        W();
        if (bound.Count < 2)
        {
            W($" Only {bound.Count} pad produced input within {stepTimeout}s.");
            W();
            W(" This is the thing to check first, and it is almost certainly the hardware");
            W(" rather than Dovetail: every previous session in this project shows exactly one");
            W(" port active at a time, which is what a receiver that assigns a channel at");
            W(" pad power-on does when only one pad is on.");
            W();
            W(" Try, in order:");
            W("   1. Power BOTH pads on, one after the other, then press a button on each.");
            W("   2. If the second never appears, the pads are contesting one channel.");
            W("      Power both off, turn on pad A and wait for it to pair, then pad B.");
            W("   3. Check the second pad's batteries. A pad with a weak battery pairs and");
            W("      then drops, which this project has already seen twice.");
            W("   4. Some receivers have a pairing button that must be pressed per pad.");
            reader.Dispose();
            return 3;
        }

        var results = bound.Select(c => new PadResult
        {
            Tag = c.Tag,
            ReportId = c.ReportId,
            Name = c.DisplayName,
            DovetailSlot = c.Slot,
            XInputSlot = c.Virtual?.UserIndex ?? -1,
        }).ToList();

        var slotsAfter = reader.ConnectedSlots();
        W($" XInput slots now occupied : {string.Join(", ", slotsAfter)}");
        W($" new since Dovetail started  : {string.Join(", ", slotsAfter.Except(slotsBefore))}");
        W();
        W(" Slot assignment:");
        foreach (var r in results)
            W($"   \"{r.Name}\" Dovetail slot {r.DovetailSlot}, report id {r.ReportId}  ->  " +
              $"XInput slot {r.XInputSlot} = player {r.XInputSlot + 1}");
        W();

        bool distinctSlots = results.Select(r => r.XInputSlot).Distinct().Count() == results.Count
                             && results.All(r => r.XInputSlot >= 0);
        W($" each pad has its own slot : {distinctSlots}");
        if (!distinctSlots)
            W("   PROBLEM: two pads share a slot, or a slot was never assigned.");
        W();

        // ---- independence ----
        W(new string('=', 74));
        W(" STEP 2   Independence. Press and HOLD a face button on ONE pad at a time.");
        W("          A press must appear in that pad's slot and NOT in the other.");
        W(new string('=', 74));
        W();

        foreach (var r in results)
        {
            W(new string('-', 74));
            W($" Hold any FACE BUTTON on \"{r.Name}\", the pad in XInput slot {r.XInputSlot} " +
              $"(player {r.XInputSlot + 1}).");
            W(" Hold it until this line reports a result.");

            var stepDeadline = DateTime.UtcNow.AddSeconds(stepTimeout);
            bool seen = false;
            var others = results.Where(o => o.XInputSlot != r.XInputSlot).ToList();
            ushort otherMaskAtPress = 0;
            ushort ownMaskAtPress = 0;

            while (DateTime.UtcNow < stepDeadline)
            {
                var own = reader.Read(r.XInputSlot);
                if (own is null) { Thread.Sleep(10); continue; }

                const ushort faces = XInputButtons.A | XInputButtons.B | XInputButtons.X | XInputButtons.Y;
                if ((own.Value.Buttons & faces) == 0) { Thread.Sleep(8); continue; }

                ownMaskAtPress = own.Value.Buttons;
                // read every other slot in the same instant
                foreach (var o in others)
                {
                    var st2 = reader.Read(o.XInputSlot);
                    if (st2 is not null) otherMaskAtPress |= (ushort)(st2.Value.Buttons & faces);
                }
                seen = true;
                break;
            }

            r.PressSeenInOwnSlot = seen;
            r.BledIntoOtherSlot = otherMaskAtPress != 0;
            r.Evidence = seen
                ? $"slot {r.XInputSlot} reported [{XInputButtons.Describe(ownMaskAtPress)}]; " +
                  (others.Count == 0
                    ? "no other slot to check"
                    : $"other slot(s) reported [{XInputButtons.Describe(otherMaskAtPress)}]")
                : $"no face button seen in slot {r.XInputSlot} within {stepTimeout}s";

            W(seen
                ? $"   {(r.BledIntoOtherSlot ? "FAIL" : "PASS")}  {r.Evidence}"
                : $"   FAIL  {r.Evidence}");
            W();

            // wait for release so the next pad's test starts clean
            var rel = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < rel)
            {
                var s2 = reader.Read(r.XInputSlot);
                if (s2 is null || (s2.Value.Buttons & (XInputButtons.A | XInputButtons.B |
                                                       XInputButtons.X | XInputButtons.Y)) == 0) break;
                Thread.Sleep(15);
            }
        }

        // ---- verdict ----
        bool allIndependent = results.All(r => r.PressSeenInOwnSlot && !r.BledIntoOtherSlot);
        W(new string('=', 74));
        W(" SUMMARY");
        W(new string('=', 74));
        W($" {"controller",-18} {"dovetail slot",-12} {"xinput slot",-12} {"player",-7} {"own press",-10} bled over");
        W($" {new string('-', 17)} {new string('-', 11)} {new string('-', 11)} {new string('-', 6)} {new string('-', 9)} ---------");
        foreach (var r in results)
            W($" {r.Name,-18} {r.DovetailSlot,-12} {r.XInputSlot,-12} {r.XInputSlot + 1,-7} " +
              $"{(r.PressSeenInOwnSlot ? "yes" : "NO"),-10} {(r.BledIntoOtherSlot ? "YES" : "no")}");
        W();
        W($" pads bound              : {results.Count}");
        W($" distinct XInput slots   : {distinctSlots}");
        W($" inputs stay independent : {allIndependent}");
        W();

        bool pass = results.Count >= 2 && distinctSlots && allIndependent;
        W(pass
            ? " PASS: two pads drive two independent XInput players. A local multiplayer title\n" +
              " such as FIFA will see two controllers."
            : " FAIL: see the table above. Two pads are not yet independent players.");
        W(new string('=', 74));

        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            test = "two pads as two XInput players",
            capturedLocal = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            slotsBefore,
            slotsAfter,
            padsBound = results.Count,
            distinctSlots,
            allIndependent,
            pass,
            pads = results,
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        File.WriteAllLines(Path.ChangeExtension(outPath, ".txt"), transcript);
        Console.WriteLine($" results: {outPath}");

        reader.Dispose();
        return pass ? 0 : 3;
    }
}
