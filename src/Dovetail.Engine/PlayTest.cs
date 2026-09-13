using System.Text.Json;
using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// The Stage 4 in-game checklist recorder.
///
/// Section 6 Stage 4 asks for a pass or fail per input, per game, with specific evidence.
/// Two halves of that can be separated: whether an input actually reached the game, which a
/// machine can prove, and whether the right thing happened on screen, which only the player
/// can say.
///
/// This records the first half objectively while the operator plays, by reading XInput back
/// out of the OS for the whole session and marking each expected input as seen the moment it
/// appears with a real magnitude. What it deliberately does not do is claim the second half.
/// A row marked seen means "this reached the game", not "lock-on fired". The report says so.
/// </summary>
internal static class PlayTest
{
    private sealed class Item
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string InGameMeaning { get; init; }
        public required Func<XInputReport, bool> Seen { get; init; }
        public bool Observed;
        public string FirstSeenAt = "";
        public string Evidence = "";
    }

    private static List<Item> Checklist()
    {
        const short Move = 12000;   // a real push, not deadzone noise
        return
        [
            new() { Id = "ls_left",  Label = "left stick LEFT",  InGameMeaning = "move left",
                    Seen = r => r.ThumbLX <= -Move },
            new() { Id = "ls_right", Label = "left stick RIGHT", InGameMeaning = "move right",
                    Seen = r => r.ThumbLX >= Move },
            new() { Id = "ls_up",    Label = "left stick UP",    InGameMeaning = "move forward",
                    Seen = r => r.ThumbLY >= Move },
            new() { Id = "ls_down",  Label = "left stick DOWN",  InGameMeaning = "move back",
                    Seen = r => r.ThumbLY <= -Move },
            new() { Id = "rs_left",  Label = "right stick LEFT", InGameMeaning = "camera left",
                    Seen = r => r.ThumbRX <= -Move },
            new() { Id = "rs_right", Label = "right stick RIGHT",InGameMeaning = "camera right",
                    Seen = r => r.ThumbRX >= Move },
            new() { Id = "rs_up",    Label = "right stick UP",   InGameMeaning = "camera up",
                    Seen = r => r.ThumbRY >= Move },
            new() { Id = "rs_down",  Label = "right stick DOWN", InGameMeaning = "camera down",
                    Seen = r => r.ThumbRY <= -Move },

            new() { Id = "a", Label = "A  (Cross)",    InGameMeaning = "jump / confirm",
                    Seen = r => (r.Buttons & XInputButtons.A) != 0 },
            new() { Id = "b", Label = "B  (Circle)",   InGameMeaning = "attack / cancel",
                    Seen = r => (r.Buttons & XInputButtons.B) != 0 },
            new() { Id = "x", Label = "X  (Square)",   InGameMeaning = "interact",
                    Seen = r => (r.Buttons & XInputButtons.X) != 0 },
            new() { Id = "y", Label = "Y  (Triangle)", InGameMeaning = "item / menu",
                    Seen = r => (r.Buttons & XInputButtons.Y) != 0 },

            new() { Id = "dpad_up",    Label = "D-pad UP",    InGameMeaning = "item select up",
                    Seen = r => (r.Buttons & XInputButtons.DPadUp) != 0 },
            new() { Id = "dpad_down",  Label = "D-pad DOWN",  InGameMeaning = "item select down",
                    Seen = r => (r.Buttons & XInputButtons.DPadDown) != 0 },
            new() { Id = "dpad_left",  Label = "D-pad LEFT",  InGameMeaning = "item select left",
                    Seen = r => (r.Buttons & XInputButtons.DPadLeft) != 0 },
            new() { Id = "dpad_right", Label = "D-pad RIGHT", InGameMeaning = "item select right",
                    Seen = r => (r.Buttons & XInputButtons.DPadRight) != 0 },

            new() { Id = "lb", Label = "L1 / LB", InGameMeaning = "block / guard",
                    Seen = r => (r.Buttons & XInputButtons.LeftShoulder) != 0 },
            new() { Id = "rb", Label = "R1 / RB", InGameMeaning = "attack / prosthetic",
                    Seen = r => (r.Buttons & XInputButtons.RightShoulder) != 0 },
            new() { Id = "lt", Label = "L2 / LT", InGameMeaning = "prosthetic / secondary",
                    Seen = r => r.LeftTrigger >= 200 },
            new() { Id = "rt", Label = "R2 / RT", InGameMeaning = "secondary attack",
                    Seen = r => r.RightTrigger >= 200 },

            new() { Id = "l3", Label = "L3  left stick click",  InGameMeaning = "LOCK ON, the charter's named evidence",
                    Seen = r => (r.Buttons & XInputButtons.LeftThumb) != 0 },
            new() { Id = "r3", Label = "R3  right stick click", InGameMeaning = "reset camera",
                    Seen = r => (r.Buttons & XInputButtons.RightThumb) != 0 },

            new() { Id = "back",  Label = "SELECT / Back", InGameMeaning = "map / sub-menu",
                    Seen = r => (r.Buttons & XInputButtons.Back) != 0 },
            new() { Id = "start", Label = "START / Menu",  InGameMeaning = "pause menu",
                    Seen = r => (r.Buttons & XInputButtons.Start) != 0 },
        ];
    }

    internal static int Run(string[] args)
    {
        int minutes = int.TryParse(Program.ArgValue(args, "--minutes"), out int m) ? m : 20;
        string game = Program.ArgValue(args, "--game") ?? "Sekiro";
        string outPath = Program.ArgValue(args, "--out")
            ?? Path.Combine(AppContext.BaseDirectory, "stage4-playtest.json");

        var transcript = new List<string>();
        void W(string s = "") { Console.WriteLine(s); transcript.Add(s); }

        W(new string('=', 74));
        W($" DOVETAIL - STAGE 4 IN-GAME CHECKLIST: {game}");
        W(new string('=', 74));
        W($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}, running up to {minutes} minutes");
        W();
        W(" Play normally. Every input below is marked off as soon as it reaches the game.");
        W(" This proves an input ARRIVED. Whether the right thing happened on screen is");
        W(" yours to report, and the summary asks for it rather than assuming it.");
        W();

        var slots = Program.LoadSlots(args, s => W("  " + s));
        W();

        var reader = new XInputReader("xinput1_4.dll");
        if (!reader.Loaded) { Console.Error.WriteLine(" xinput1_4.dll did not load"); return 1; }

        using var engine = new DovetailEngine(slots, s => W("  " + s));
        if (!engine.Start()) { Console.Error.WriteLine($" {engine.Bus.Status}"); return 1; }
        engine.Rescan();
        using var pump = new Runner.Pump(engine);

        W(" Press a button on the pad you are playing with, then start the game.");
        var bindBy = DateTime.UtcNow.AddMinutes(3);
        PadChannel? bound = null;
        while (DateTime.UtcNow < bindBy)
        {
            bound = engine.Channels.FirstOrDefault(c => c.Bound);
            if (bound is not null) break;
            Thread.Sleep(30);
        }
        if (bound is null) { W(" no pad produced input within 3 minutes"); return 1; }
        Thread.Sleep(400);
        int slot = bound.Virtual?.UserIndex ?? 0;
        W($" pad bound on {bound.Tag}, XInput slot {slot}. Recording.");
        W();

        var list = Checklist();
        var started = DateTime.Now;
        var deadline = DateTime.UtcNow.AddMinutes(minutes);
        int lastDone = -1;

        while (DateTime.UtcNow < deadline)
        {
            var st = reader.Read(slot);
            if (st is not null)
            {
                foreach (var it in list.Where(i => !i.Observed && i.Seen(st.Value)))
                {
                    it.Observed = true;
                    it.FirstSeenAt = DateTime.Now.ToString("HH:mm:ss");
                    it.Evidence = $"buttons=0x{st.Value.Buttons:X4} LT={st.Value.LeftTrigger} " +
                                  $"RT={st.Value.RightTrigger} L({st.Value.ThumbLX},{st.Value.ThumbLY}) " +
                                  $"R({st.Value.ThumbRX},{st.Value.ThumbRY})";
                    W($"  [{list.Count(i => i.Observed),2}/{list.Count}] {it.Label,-26} seen at {it.FirstSeenAt}");
                }
            }

            int done = list.Count(i => i.Observed);
            if (done != lastDone)
            {
                lastDone = done;
                if (done == list.Count)
                {
                    W();
                    W(" Every input on the checklist has reached the game. Recording stops here.");
                    break;
                }
            }
            Thread.Sleep(6);
        }

        // ---- report ----
        W();
        W(new string('=', 74));
        W(" CHECKLIST RESULT");
        W(new string('=', 74));
        W($" {"input",-26} {"reached the game",-17} {"at",-9} in-game meaning");
        W($" {new string('-', 25)} {new string('-', 16)} {new string('-', 8)} ----------------");
        foreach (var it in list)
            W($" {it.Label,-26} {(it.Observed ? "YES" : "NOT SEEN"),-17} {it.FirstSeenAt,-9} {it.InGameMeaning}");
        W();

        int seen = list.Count(i => i.Observed);
        W($" {seen} of {list.Count} inputs reached the game.");
        var missing = list.Where(i => !i.Observed).ToList();
        if (missing.Count > 0)
        {
            W($" not exercised: {string.Join(", ", missing.Select(i => i.Id))}");
            W(" Those were not pressed during the session, or did not arrive. The distinction");
            W(" matters, so they are reported as NOT SEEN rather than as failures.");
        }
        W();
        var l3 = list.First(i => i.Id == "l3");
        W($" L3, the charter's named evidence: {(l3.Observed ? "reached the game" : "NOT SEEN")}");
        if (l3.Observed) W($"   {l3.Evidence}");
        W();
        W(" STILL NEEDED FROM THE PLAYER, which no measurement can supply:");
        W("   1. Did the menus navigate correctly, one step per press?");
        W("   2. Did lock-on actually engage when you clicked L3?");
        W("   3. Did anything do the WRONG thing rather than nothing?");
        W("   4. Did anything feel wrong even though it worked?");
        W(new string('=', 74));

        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            stage = 4,
            game,
            startedLocal = started.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            padTag = bound.Tag,
            reportId = bound.ReportId,
            xinputSlot = slot,
            inputsReachingGame = seen,
            totalInputs = list.Count,
            note = "Observed means the input reached the game through XInput. Whether the " +
                   "correct in-game action resulted is a player observation, recorded separately.",
            items = list.Select(i => new { i.Id, i.Label, i.InGameMeaning, i.Observed, i.FirstSeenAt, i.Evidence }),
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        File.WriteAllLines(Path.ChangeExtension(outPath, ".txt"), transcript);
        Console.WriteLine($" results: {outPath}");

        reader.Dispose();
        return seen == list.Count ? 0 : 3;
    }
}
