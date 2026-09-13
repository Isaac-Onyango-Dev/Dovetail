using System.Diagnostics;
using System.Text.Json;
using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Answers one question with evidence: when buttons are pressed rapidly, does Dovetail lose
/// any of them, or does the console display merely fail to show them?
///
/// The console view in 'run' samples state every 250 ms and prints whatever it finds at that
/// instant, so a press and release inside 250 ms can be invisible there while having been
/// handled correctly. That is a display limit, not an input limit, but saying so is not the
/// same as proving it. This measures three independent counts on the real path and compares
/// them:
///
///   1. button edges seen in the raw HID reports, at full report rate
///   2. reports submitted to the virtual pad
///   3. distinct button states observed by reading XInput back out of the OS
///
/// If those agree, nothing is lost and the console was the only thing behind. It also
/// records the end-to-end latency from a HID edge to that edge being visible in XInput,
/// which is the number that actually matters while playing.
/// </summary>
internal static class BurstTest
{
    private sealed record Edge(long Ticks, string Tag, ushort Mask, string Names, bool Submitted);

    internal static int Run(string[] args)
    {
        int seconds = int.TryParse(Program.ArgValue(args, "--seconds"), out int s) ? s : 20;
        string outPath = Program.ArgValue(args, "--out")
            ?? Path.Combine(AppContext.BaseDirectory, "stage4-burst-test.json");

        var transcript = new List<string>();
        void W(string m = "") { Console.WriteLine(m); transcript.Add(m); }

        W(new string('=', 74));
        W(" DOVETAIL - RAPID INPUT LOSS AND LATENCY TEST");
        W(new string('=', 74));
        W($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($" high-resolution timer frequency: {Stopwatch.Frequency} ticks/s");
        W();

        var slots = Program.LoadSlots(args, m => W("  " + m));
        W();

        var r13 = new XInputReader("xinput1_3.dll");
        if (!r13.Loaded) { Console.Error.WriteLine(" xinput1_3.dll did not load"); return 1; }

        using var engine = new DovetailEngine(slots, m => W("  " + m));
        if (!engine.Start()) { Console.Error.WriteLine($" {engine.Bus.Status}"); return 1; }

        // ---- record every HID button edge at full rate ----
        var edges = new List<Edge>();
        var edgeLock = new object();
        var lastMask = new Dictionary<string, ushort>();
        long hidReports = 0;

        engine.StateDecoded += (ch, state, report, submitted) =>
        {
            Interlocked.Increment(ref hidReports);
            lock (edgeLock)
            {
                ushort prev = lastMask.GetValueOrDefault(ch.Tag);
                if (report.Buttons == prev) return;
                lastMask[ch.Tag] = report.Buttons;
                edges.Add(new Edge(Stopwatch.GetTimestamp(), ch.Tag, report.Buttons,
                                   XInputButtons.Describe(report.Buttons), submitted));
            }
        };

        engine.Rescan();
        using var pump = new Runner.Pump(engine);

        W(" Press any button to bring the virtual pad up.");
        var bindDeadline = DateTime.UtcNow.AddSeconds(45);
        PadChannel? bound = null;
        while (DateTime.UtcNow < bindDeadline)
        {
            bound = engine.Channels.FirstOrDefault(c => c.Bound);
            if (bound is not null) break;
            Thread.Sleep(15);
        }
        if (bound is null) { W(" no pad produced input; is the link up?"); return 1; }
        Thread.Sleep(300);
        int slot = bound.Virtual?.UserIndex ?? 0;
        W($" pad bound on {bound.Tag}, virtual pad on XInput slot {slot}");
        W();

        // ---- poll XInput as fast as possible on its own thread ----
        var seen = new List<(long ticks, ushort mask, uint packet)>();
        var seenLock = new object();
        bool stopPolling = false;
        long pollCount = 0;
        var poller = new Thread(() =>
        {
            uint lastPacket = 0; bool first = true;
            while (!Volatile.Read(ref stopPolling))
            {
                var st = r13.Read(slot);
                uint packet = r13.LastPacketNumber;
                Interlocked.Increment(ref pollCount);
                if (st is not null && (first || packet != lastPacket))
                {
                    first = false; lastPacket = packet;
                    lock (seenLock) seen.Add((Stopwatch.GetTimestamp(), st.Value.Buttons, packet));
                }
                // A free spin reached roughly 467 kHz, which pins a core and perturbs the
                // very thread being measured. This still samples far faster than the 100 Hz
                // the pad reports, so it cannot miss a change.
                Thread.SpinWait(200);
            }
        }) { IsBackground = true, Name = "xinput-poller", Priority = ThreadPriority.AboveNormal };
        poller.Start();

        // ---- the burst ----
        W(new string('=', 74));
        W($" MASH BUTTONS AS FAST AS YOU CAN FOR {seconds} SECONDS.");
        W(" Use the face buttons, the shoulders, anything. Go as fast as you physically can,");
        W(" and deliberately include some very quick taps.");
        W(new string('=', 74));
        W();

        long t0 = Stopwatch.GetTimestamp();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
            lock (edgeLock) Console.Write($"\r  {left,3}s left   HID edges {edges.Count,4}   reports {Interlocked.Read(ref hidReports),7}   ");
            Thread.Sleep(200);
        }
        Volatile.Write(ref stopPolling, true);
        poller.Join(500);
        Console.WriteLine();
        W();

        // ---- correlate ----
        List<Edge> e; List<(long ticks, ushort mask, uint packet)> x;
        lock (edgeLock) e = edges.ToList();
        lock (seenLock) x = seen.ToList();

        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        W(" COUNTS");
        W(new string('-', 74));
        W($"   HID reports decoded                  : {hidReports}");
        W($"   XInput polls performed               : {pollCount}");
        W($"   button-state changes in HID          : {e.Count}");
        W($"   of those, submitted to the virtual pad: {e.Count(y => y.Submitted)}");
        W($"   button-state changes seen via XInput : {Math.Max(0, x.Count - 1)}");
        W($"   virtual pad submit counter           : {bound.Virtual?.SubmitCount ?? 0}");
        W();

        // match each HID edge to the first XInput observation carrying the same mask after it
        // Pair each HID edge with the nearest-in-time XInput observation carrying the same
        // mask. Sequence walking was tried first and got this wrong twice: masks repeat
        // constantly when mashing, and the bind press contributes edges from before the
        // measurement window, so any index-based alignment drifts and each edge ends up
        // paired with the observation belonging to the NEXT press. The reported latency was
        // then really the gap between presses. The poller samples roughly every 9 us, so a
        // real change is observed essentially simultaneously and nearest-in-time is
        // unambiguous.
        const double PairWindowMs = 50.0;
        var latencies = new List<double>();
        int matched = 0, unmatched = 0;
        var unmatchedList = new List<string>();
        foreach (var edge in e)
        {
            double edgeMs = ms(edge.Ticks - t0);
            double best = double.MaxValue, bestSigned = 0;
            foreach (var obs in x)
            {
                if (obs.mask != edge.Mask) continue;
                double signed = ms(obs.ticks - edge.Ticks);
                double abs = Math.Abs(signed);
                if (abs < best) { best = abs; bestSigned = signed; }
            }
            if (best <= PairWindowMs) { latencies.Add(bestSigned); matched++; }
            else
            {
                unmatched++;
                if (unmatchedList.Count < 12)
                    unmatchedList.Add($"{edgeMs,9:0.00} ms  0x{edge.Mask:X4} [{edge.Names}] on {edge.Tag}" +
                                      (edgeMs < 0 ? "   (before the window opened, the bind press)" : ""));
            }
        }

        W(" DID ANYTHING GET LOST?");
        W(new string('-', 74));
        W($"   HID edges that appeared in XInput    : {matched} of {e.Count}");
        W($"   HID edges NOT found in XInput        : {unmatched}");
        if (unmatchedList.Count > 0)
        {
            W("   examples not found:");
            foreach (var u in unmatchedList) W($"     {u}");
            W("   NOTE: edges at a negative timestamp are the press used to bind the pad,");
            W("   which happens before the measurement window opens. Those are expected.");
        }
        W();

        if (latencies.Count > 0)
        {
            latencies.Sort();
            double P(double q) => latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * q))];
            W(" END-TO-END LATENCY, HID report to visible in XInput");
            W(new string('-', 74));
            W($"   samples : {latencies.Count}");
            W($"   min     : {latencies[0]:0.00} ms");
            W($"   median  : {P(0.50):0.00} ms");
            W($"   p90     : {P(0.90):0.00} ms");
            W($"   p99     : {P(0.99):0.00} ms");
            W($"   max     : {latencies[^1]:0.00} ms");
            W();
            W("   For reference: this pad reports at about 100 Hz, so one report period is");
            W("   10 ms, and a 60 fps game samples input every 16.7 ms.");
            W($"   Pairing window: {PairWindowMs:0} ms, nearest-in-time among same-mask observations.");
            W("   A negative value means the poller saw the change before the engine finished");
            W("   recording its own timestamp, i.e. the submit was already visible.");
        }
        W();

        // fastest presses the operator actually achieved
        if (e.Count > 2)
        {
            var gaps = new List<double>();
            for (int i = 1; i < e.Count; i++) gaps.Add(ms(e[i].Ticks - e[i - 1].Ticks));
            gaps.Sort();
            W(" HOW FAST WERE THE PRESSES?");
            W(new string('-', 74));
            W($"   gap between consecutive state changes: min {gaps[0]:0.0} ms, " +
              $"median {gaps[gaps.Count / 2]:0.0} ms");
            W($"   fastest 10 gaps: {string.Join(", ", gaps.Take(10).Select(g => g.ToString("0.0")))} ms");
            W();
            int faster = gaps.Count(g => g < 250);
            W($"   {faster} of {gaps.Count} changes happened within 250 ms of the previous one.");
            W("   The console view in 'run' samples every 250 ms, so those are exactly the");
            W("   changes it can miss while the engine still handles them.");
        }
        W();

        var pk = x.Select(y => y.packet).ToList();
        bool contiguous = pk.Count > 1 && Enumerable.Range(0, pk.Count - 1).All(i => pk[i + 1] - pk[i] == 1);
        int submittedCount = e.Count(y => y.Submitted);
        W(" WAS ANY STATE CHANGE DROPPED BY THE DRIVER?");
        W(new string('-', 74));
        W($"   XInput packet numbers contiguous : {contiguous}");
        W($"   packet range                     : {(pk.Count > 0 ? $"{pk[0]}..{pk[^1]}" : "n/a")}");
        W("   A contiguous packet sequence means XInput accepted and exposed every state");
        W("   change in order, with none coalesced away. This is the airtight loss check;");
        W("   the pairing above only measures timing.");
        W();

        bool lossless = contiguous && submittedCount == e.Count;
        W(new string('=', 74));
        W(lossless
            ? " VERDICT: the input path is keeping up. Rapid presses reach XInput."
            : " VERDICT: some edges did not reach XInput. Investigate before Stage 4 sign-off.");
        W(new string('=', 74));

        var payload = new
        {
            test = "rapid input loss and latency",
            capturedLocal = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            seconds,
            timerFrequency = Stopwatch.Frequency,
            hidReports,
            pollCount,
            hidEdges = e.Count,
            submitted = e.Count(y => y.Submitted),
            xinputChanges = Math.Max(0, x.Count - 1),
            matched,
            unmatched,
            latencyMs = latencies.Count == 0 ? null : new
            {
                samples = latencies.Count,
                min = latencies[0],
                median = latencies[latencies.Count / 2],
                max = latencies[^1],
            },
            lossless,
            pairWindowMs = PairWindowMs,
            packetsContiguous = contiguous,
            rawHidEdges = e.Select(y => new { ms = ms(y.Ticks - t0), y.Tag, mask = y.Mask, y.Names, y.Submitted }),
            rawXInputChanges = x.Select(y => new { ms = ms(y.ticks - t0), mask = y.mask, y.packet }),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        File.WriteAllLines(Path.ChangeExtension(outPath, ".txt"), transcript);
        Console.WriteLine($" results: {outPath}");

        r13.Dispose();
        return lossless ? 0 : 3;
    }
}
