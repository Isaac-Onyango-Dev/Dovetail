namespace Dovetail.Core;

/// <summary>
/// The Stage 1 guided input sweep, with no console in it.
///
/// This is the detection that Stage 1 was built on and that Stage 2 depends on: prompt for one
/// physical input, watch the raw report bytes rather than any template, and advance only when a
/// real change is seen and then released. It was lifted out of the console wizard unchanged so
/// that the wizard and the calibration screen are running the same code rather than two copies
/// that drift. <see cref="Dovetail.Diagnostics.Sweep"/> is now a front end onto this, and so is
/// the WPF calibration window.
///
/// Nothing here writes to the console or blocks a UI thread by itself. Progress arrives through
/// <see cref="SweepEvents"/>, and <see cref="CaptureStep"/> is a single blocking call that the
/// caller is expected to run on a worker thread with a cancellation token.
/// </summary>
public sealed class InputSweep : IDisposable
{
    /// <summary>One prompted input.</summary>
    /// <param name="Id">Stable identifier, used by --only and by the diagram hotspot map.</param>
    /// <param name="Prompt">Console phrasing: names position and printed label together.</param>
    /// <param name="Short">Screen phrasing for the calibration window, which also shows a diagram.</param>
    /// <param name="Kind">button, hat, axis or trigger.</param>
    /// <param name="Alt">
    /// What the same control is printed as on other pads, shown under the prompt on screen.
    ///
    /// The script names controls by POSITION, never by the letter moulded into the plastic,
    /// because the sweep exists to discover an unknown layout and position is the only thing
    /// every pad shares. A prompt reading "Press Y" invites somebody holding a PlayStation pad
    /// to press whatever looks closest. Recording that would file one input under another's
    /// name, and the input it displaced would then be refused as already claimed and end up
    /// with no mapping at all: one control swapped, one control dead, and no warning.
    /// </param>
    /// <param name="Xbox">What this control is printed as on an Xbox pad, or "" if it has no
    /// distinct Xbox name. Xbox 360 nomenclature, because that is the device Dovetail emits.</param>
    /// <param name="Sony">The same control's PlayStation name, or "".</param>
    public sealed record Step(string Id, string Prompt, string Short, string Kind, string Alt = "",
                              string Xbox = "", string Sony = "")
    {
        /// <summary>
        /// Both printed names at once, "B / Circle", or "" when the control is named the same
        /// everywhere and a dual label would be noise.
        ///
        /// **This is shown beside the prompt, never as the prompt.** <see cref="Short"/> and
        /// <see cref="Prompt"/> stay positional for the reason set out on <see cref="Alt"/>:
        /// the sweep exists to discover an unknown layout, and a prompt that leads with a
        /// letter invites somebody holding the other kind of pad to press whatever looks
        /// closest. The dual label resolves "which one did you mean" for a person who already
        /// knows their own pad, which is the common case, without ever being the instruction.
        /// </summary>
        public string DualLabel =>
            Xbox.Length > 0 && Sony.Length > 0 ? $"{Xbox} / {Sony}"
            : Xbox.Length > 0 ? Xbox
            : Sony;
    }

    /// <summary>
    /// The script, in the order a person can work through it without moving their hands around.
    ///
    /// The centre cluster on this pad is five buttons: two icon buttons on the top row, SELECT
    /// and START beneath them, and HOME in the middle. The icon pair is the Android back / menu
    /// pair, intended for phone use. Console prompts name position and printed label together,
    /// because "Select" alone is ambiguous on this hardware. The screen prompts can be shorter
    /// because the diagram is pointing at the button.
    /// </summary>
    public static readonly Step[] Script =
    [
        new("face_up",     "the UPPER face button  (Triangle / Y)",                 "Press the upper face button", "button", "Y on an Xbox pad, Triangle on a PlayStation one", "Y", "Triangle"),
        new("face_right",  "the RIGHT face button  (Circle / B)",                   "Press the right face button", "button", "B on an Xbox pad, Circle on a PlayStation one", "B", "Circle"),
        new("face_down",   "the LOWER face button  (Cross / A)",                    "Press the lower face button", "button", "A on an Xbox pad, Cross on a PlayStation one", "A", "Cross"),
        new("face_left",   "the LEFT face button   (Square / X)",                   "Press the left face button", "button", "X on an Xbox pad, Square on a PlayStation one", "X", "Square"),
        new("dpad_up",     "D-pad UP",                                              "Press D-pad up",         "hat"),
        new("dpad_right",  "D-pad RIGHT",                                           "Press D-pad right",      "hat"),
        new("dpad_down",   "D-pad DOWN",                                            "Press D-pad down",       "hat"),
        new("dpad_left",   "D-pad LEFT",                                            "Press D-pad left",       "hat"),
        new("l1",          "L1 - the UPPER button on the LEFT rear edge",            "Press the left shoulder", "button", "Printed L1, or LB on an Xbox pad", "LB", "L1"),
        new("r1",          "R1 - the UPPER button on the RIGHT rear edge",           "Press the right shoulder", "button", "Printed R1, or RB on an Xbox pad", "RB", "R1"),
        new("l2",          "L2 - the LOWER button on the LEFT rear edge, pressed all the way",  "Press the left trigger all the way", "trigger", "Printed L2, or LT on an Xbox pad", "LT", "L2"),
        new("r2",          "R2 - the LOWER button on the RIGHT rear edge, pressed all the way", "Press the right trigger all the way", "trigger", "Printed R2, or RT on an Xbox pad", "RT", "R2"),
        new("ls_left",     "the LEFT stick fully LEFT",                             "Push the left stick left",   "axis"),
        new("ls_right",    "the LEFT stick fully RIGHT",                            "Push the left stick right",  "axis"),
        new("ls_up",       "the LEFT stick fully UP",                               "Push the left stick up",     "axis"),
        new("ls_down",     "the LEFT stick fully DOWN",                             "Push the left stick down",   "axis"),
        new("rs_left",     "the RIGHT stick fully LEFT",                            "Push the right stick left",  "axis"),
        new("rs_right",    "the RIGHT stick fully RIGHT",                           "Push the right stick right", "axis"),
        new("rs_up",       "the RIGHT stick fully UP",                              "Push the right stick up",    "axis"),
        new("rs_down",     "the RIGHT stick fully DOWN",                            "Push the right stick down",  "axis"),
        new("l3",          "CLICK THE LEFT STICK IN  (L3) - press straight down",   "Click the left stick in", "button", "L3, or LS on an Xbox pad. Press straight down", "LS", "L3"),
        new("r3",          "CLICK THE RIGHT STICK IN (R3) - press straight down",   "Click the right stick in", "button", "R3, or RS on an Xbox pad. Press straight down", "RS", "R3"),
        new("select",      "the button printed SELECT - LOWER-LEFT of the centre cluster",   "Press SELECT", "button", "Lower-left of the centre cluster", "Back", "Select"),
        new("start",       "the button printed START - LOWER-RIGHT of the centre cluster",   "Press START", "button", "Lower-right of the centre cluster", "Start", "Options"),
        new("icon_back",   "the small icon button ABOVE SELECT - curved back arrow, top-left of the cluster", "Press the back arrow button", "button", "Top-left of the centre cluster"),
        new("icon_menu",   "the small icon button ABOVE START - the lines/menu icon, top-right of the cluster", "Press the menu button", "button", "Top-right of the centre cluster"),
        new("home",        "the HOME button - the house icon in the MIDDLE of the cluster",  "Press HOME", "button", "The house icon in the middle of the cluster", "Guide", "PS"),
    ];

    public sealed class ByteChange
    {
        public int ByteIndex;
        public byte RestValue;
        public byte ActiveValue;
        public byte XorMask;
        public List<int> BitsSet = [];
        public List<int> BitsCleared = [];
        public int MinObserved;
        public int MaxObserved;
        public bool LooksAnalog;
    }

    public sealed class Capture
    {
        public string Id = "";
        public string Prompt = "";
        public string Kind = "";
        public bool Detected;
        public bool Skipped;
        public string Collection = "";
        public string RestHex = "";
        public string ActiveHex = "";
        public List<ByteChange> Changes = [];
        public string Note = "";
    }

    /// <summary>
    /// Callbacks a front end can hook. All are optional, and all are raised on whatever thread
    /// called <see cref="CaptureStep"/>, so a UI front end has to marshal them itself.
    /// </summary>
    public sealed class SweepEvents
    {
        /// <summary>A line of running commentary, for a console or a log pane.</summary>
        public Action<string>? Log;

        /// <summary>
        /// The operator pressed something that an earlier step already claimed. Raised once per
        /// offending input per step. Without this feedback a wrong press looks like nothing
        /// happening at all.
        /// </summary>
        public Action<string>? WrongInput;
    }

    private readonly List<(HidCollectionInfo Info, HidReader Reader)> _readers = [];
    private Dictionary<string, byte[]?> _rest = [];

    /// <summary>Bits already claimed in this run, so a repeated press cannot be mis-recorded.</summary>
    private readonly Dictionary<(string coll, int by, int bit), string> _claimed = [];

    public IReadOnlyList<HidCollectionInfo> Collections => _readers.Select(r => r.Info).ToList();
    public IReadOnlyDictionary<string, byte[]?> RestStates => _rest;

    /// <summary>Collections that produced at least one report while resting.</summary>
    public IReadOnlyList<string> LiveCollections =>
        _rest.Where(kv => kv.Value is { Length: > 0 }).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Opens every HID collection for the device. Returns the ones that could not be opened, so
    /// the caller can say so; an empty <see cref="Collections"/> afterwards means none could.
    /// </summary>
    public List<string> Open(ushort vid, ushort pid)
    {
        var failures = new List<string>();
        var devices = HidScan.Enumerate(vid, pid)
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var d in devices)
        {
            try { _readers.Add((d, new HidReader(d.DevicePath, d.Caps.InputReportByteLength))); }
            catch (Exception ex) { failures.Add($"{d.CollectionTag}: cannot open - {ex.Message}"); }
        }
        return failures;
    }

    /// <summary>
    /// Samples the resting report on every open collection. Everything after this is measured
    /// as a difference from what this saw, so it has to run before any step.
    /// </summary>
    public void SampleRest(int ms = 3000)
    {
        var result = new Dictionary<string, byte[]?>();
        foreach (var (info, _) in _readers) result[info.CollectionTag] = null;

        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (info, reader) in _readers)
            {
                var rpt = reader.Read(20);
                if (rpt is { Length: > 0 }) result[info.CollectionTag] = rpt;
            }
        }
        _rest = result;
    }

    /// <summary>
    /// Settles the pad before a step, so a button still held from the previous step cannot
    /// instantly satisfy the next one. This is why the sweep works at all: the first version
    /// used fixed time windows and the trigger step failed because its window opened while the
    /// operator was still recentring the stick.
    /// </summary>
    /// <param name="rebase">
    /// Adopt whatever the pad has settled at as the new baseline for the next step.
    ///
    /// Without this the baseline is whatever <see cref="SampleRest"/> saw once at the start, and
    /// if the pad's resting report changes afterwards then every report differs from the
    /// baseline for ever. Every step then sees a change the instant it opens, waits out the
    /// release window because nothing changes again, and records a detection. The whole script
    /// runs itself through in a few seconds with nobody touching anything.
    ///
    /// That is not hypothetical. A wireless pad that is asleep when the run starts leaves the
    /// receiver streaming its own constant idle report; the operator then wakes the pad, its
    /// real idle report is a different constant, and the run bolts. Re-reading the baseline at
    /// each step costs nothing and makes a step measure against what the pad is doing now.
    ///
    /// Off by default so the console wizard's proven behaviour is byte for byte unchanged.
    /// </param>
    public void Settle(int quietMs = 200, int timeoutMs = 4000, bool rebase = false)
    {
        foreach (var (info, rd) in _readers)
        {
            var settled = rd.WaitForRest(quietMs, timeoutMs);
            if (rebase && settled is { Length: > 0 }) _rest[info.CollectionTag] = settled;
        }
    }

    /// <summary>
    /// Waits for the pad to do anything at all, and reports what it settles back to.
    ///
    /// Used as a gate before the script starts, to prove the pad is awake and linked rather
    /// than assuming it. A receiver with no pad linked still streams reports, so "data is
    /// arriving" is not evidence of a controller; only a change is.
    /// </summary>
    /// <returns>True if activity was seen before the timeout.</returns>
    public bool WaitForActivity(int timeoutMs, CancellationToken cancel = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            foreach (var (info, reader) in _readers)
            {
                var rpt = reader.Read(15);
                if (rpt is null || rpt.Length == 0) continue;

                var baseline = _rest.GetValueOrDefault(info.CollectionTag);
                if (baseline is null || baseline.Length == 0) continue;
                if (!rpt.AsSpan().SequenceEqual(baseline)) return true;
            }
        }
        return false;
    }

    /// <summary>Records a step's bits as claimed, so no later step can be satisfied by them.</summary>
    public void Claim(Capture cap)
    {
        foreach (var ch in cap.Changes)
            foreach (var b in ch.BitsSet.Concat(ch.BitsCleared))
                _claimed.TryAdd((cap.Collection, ch.ByteIndex, b), cap.Id);
    }

    /// <summary>
    /// Releases every bit a step claimed, so it can be captured again.
    ///
    /// Re-testing one input needs this and would be broken without it. The claim exists to
    /// refuse a press that an earlier step already recorded; on a re-test the step being
    /// re-run is exactly that earlier step, so its own press would be rejected as "already
    /// recorded" and the re-test could never succeed. Dropping only this step's bits keeps
    /// every other step's protection intact.
    /// </summary>
    public int Unclaim(string stepId)
    {
        var gone = _claimed.Where(kv => kv.Value.Equals(stepId, StringComparison.OrdinalIgnoreCase))
                           .Select(kv => kv.Key).ToList();
        foreach (var k in gone) _claimed.Remove(k);
        return gone.Count;
    }

    // ------------------------------------------------------------------ port pinning

    /// <summary>The collection a run is pinned to, and why. Null until one is chosen.</summary>
    public sealed record PortChoice(string CollectionTag, byte ReportId, string Reason);

    public PortChoice? Pinned { get; private set; }

    /// <summary>
    /// Restricts this sweep to one collection, closing the readers for the others.
    ///
    /// **Why the graphical check needs this at all.** Stage 1 finding 1.8: the adapter exposes
    /// two joystick collections from one USB endpoint, and both stream idle reports at roughly
    /// 100 Hz whether or not a pad is on that port. A sweep reading every collection therefore
    /// accepts a press from either pad. With two controllers connected, player 2 pressing
    /// anything satisfies player 1's step and the press is recorded into player 1's profile,
    /// under the right name, from the wrong hardware. Nothing about that looks wrong on screen.
    /// The console wizard has always pinned the port before measuring; this is the same rule,
    /// in the same place, so the two front ends cannot disagree.
    /// </summary>
    private PortChoice Pin(string tag, byte reportId, string reason)
    {
        for (int i = _readers.Count - 1; i >= 0; i--)
        {
            if (_readers[i].Info.CollectionTag.Equals(tag, StringComparison.OrdinalIgnoreCase)) continue;
            _readers[i].Reader.Dispose();
            _readers.RemoveAt(i);
        }
        _rest = _rest.Where(kv => kv.Key.Equals(tag, StringComparison.OrdinalIgnoreCase))
                     .ToDictionary(kv => kv.Key, kv => kv.Value);
        Pinned = new PortChoice(tag, reportId, reason);
        return Pinned;
    }

    /// <summary>
    /// Pins the collection whose report id matches a saved profile. Needs no user action, so a
    /// re-check of an already-calibrated controller never asks for a press it does not need.
    /// Returns null when no open collection carries that id, and the caller should then fall
    /// back to <see cref="PinByActivity"/>.
    /// </summary>
    public PortChoice? PinByReportId(byte reportId, int probeMs = 600)
    {
        if (reportId == 0 || _readers.Count == 0) return null;

        var deadline = DateTime.UtcNow.AddMilliseconds(probeMs);
        while (DateTime.UtcNow < deadline)
            foreach (var (info, reader) in _readers)
            {
                var rpt = reader.Read(20);
                if (rpt is { Length: > 0 } && rpt[0] == reportId)
                    return Pin(info.CollectionTag, reportId, $"report id {reportId}, from the saved profile");
            }
        return null;
    }

    /// <summary>
    /// Pins the collection that moves, which needs one press from the operator.
    ///
    /// This is the same rule as <c>DeviceSelector.SelectByActivity</c>, run against the readers
    /// this sweep already has open rather than opening a second set of handles on the same
    /// device. It doubles as the wake gate: a receiver with no pad linked still streams reports,
    /// so only a change proves a controller is there, and the press that proves it is the same
    /// press that identifies the port.
    /// </summary>
    /// <param name="minimumScore">
    /// Accumulated absolute byte delta before a collection is accepted. A single clean press
    /// scores far more than this; the threshold is here so that one noisy analogue count on the
    /// wrong port cannot win a race against a real press on the right one.
    /// </param>
    public PortChoice? PinByActivity(int timeoutMs, CancellationToken cancel = default,
                                     int minimumScore = 40)
    {
        if (_readers.Count == 0) return null;

        if (_readers.Count == 1)
        {
            var only = _readers[0];
            var rest = _rest.GetValueOrDefault(only.Info.CollectionTag);
            return Pin(only.Info.CollectionTag,
                       rest is { Length: > 0 } ? rest[0] : (byte)0,
                       "only one collection is open");
        }

        var moved = new Dictionary<string, int>();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            foreach (var (info, reader) in _readers)
            {
                var rpt = reader.Read(15);
                if (rpt is null || rpt.Length == 0) continue;

                var baseline = _rest.GetValueOrDefault(info.CollectionTag);
                if (baseline is null || baseline.Length == 0) { _rest[info.CollectionTag] = rpt; continue; }

                int delta = 0;
                for (int k = 0; k < Math.Min(rpt.Length, baseline.Length); k++)
                    delta += Math.Abs(rpt[k] - baseline[k]);
                if (delta > 0)
                    moved[info.CollectionTag] = moved.GetValueOrDefault(info.CollectionTag) + delta;
            }
            if (moved.Count > 0 && moved.Values.Max() > minimumScore) break;
        }

        if (moved.Count == 0) return null;

        var (winner, score) = moved.OrderByDescending(kv => kv.Value).First();
        var winnerRest = _rest.GetValueOrDefault(winner);
        return Pin(winner, winnerRest is { Length: > 0 } ? winnerRest[0] : (byte)0,
                   $"only collection that moved (activity score {score})");
    }

    /// <summary>
    /// Waits for one prompted input. Blocks until the input is seen and released, until the
    /// timeout expires, or until <paramref name="cancel"/> is signalled, whichever comes first.
    /// Run this on a worker thread.
    /// </summary>
    public Capture CaptureStep(Step step, int timeoutSeconds, SweepEvents? events = null,
                               CancellationToken cancel = default)
    {
        var cap = new Capture { Id = step.Id, Prompt = step.Prompt, Kind = step.Kind };

        // accumulate the extreme excursion per byte so a trigger or stick records its range
        var minSeen = new Dictionary<(string, int), int>();
        var maxSeen = new Dictionary<(string, int), int>();
        byte[]? bestActive = null;
        string bestColl = "";
        int bestScore = 0;

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        bool sawChange = false;
        DateTime lastChange = DateTime.UtcNow;
        var rejected = new HashSet<string>();   // warn once per already-claimed input

        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            foreach (var (info, reader) in _readers)
            {
                var rpt = reader.Read(15);
                if (rpt is null || rpt.Length == 0) continue;

                var baseline = _rest.GetValueOrDefault(info.CollectionTag);
                if (baseline is null || baseline.Length == 0) continue;

                int score = 0;
                for (int k = 0; k < Math.Min(rpt.Length, baseline.Length); k++)
                {
                    if (rpt[k] == baseline[k]) continue;
                    score += Math.Abs(rpt[k] - baseline[k]);
                    var key = (info.CollectionTag, k);
                    minSeen[key] = minSeen.TryGetValue(key, out int lo) ? Math.Min(lo, rpt[k]) : rpt[k];
                    maxSeen[key] = maxSeen.TryGetValue(key, out int hi) ? Math.Max(hi, rpt[k]) : rpt[k];
                }

                if (score > 0)
                {
                    // Reject a press that belongs to a step already completed in this run.
                    // Without this, pressing the wrong button records it under the current
                    // step's name and the operator never finds out.
                    if (ClaimedOwner(info.CollectionTag, rpt, baseline) is { } owner)
                    {
                        if (!rejected.Add(owner)) continue;
                        events?.WrongInput?.Invoke(owner);
                        continue;
                    }

                    sawChange = true;
                    lastChange = DateTime.UtcNow;
                    if (score > bestScore) { bestScore = score; bestActive = rpt; bestColl = info.CollectionTag; }
                }
            }

            // advance once the input has been released and stayed at rest briefly
            if (sawChange && (DateTime.UtcNow - lastChange).TotalMilliseconds > 250)
                break;
        }

        if (cancel.IsCancellationRequested && !sawChange)
        {
            cap.Note = "cancelled";
            return cap;
        }

        if (!sawChange || bestActive is null)
        {
            cap.Note = "no change from rest state observed";
            return cap;
        }

        var restBytes = _rest.GetValueOrDefault(bestColl)!;
        cap.Detected = true;
        cap.Collection = bestColl;
        cap.RestHex = Hex(restBytes);
        cap.ActiveHex = Hex(bestActive);

        for (int k = 0; k < Math.Min(bestActive.Length, restBytes.Length); k++)
        {
            if (bestActive[k] == restBytes[k]) continue;
            byte xor = (byte)(bestActive[k] ^ restBytes[k]);
            var ch = new ByteChange
            {
                ByteIndex = k,
                RestValue = restBytes[k],
                ActiveValue = bestActive[k],
                XorMask = xor,
                MinObserved = minSeen.GetValueOrDefault((bestColl, k), bestActive[k]),
                MaxObserved = maxSeen.GetValueOrDefault((bestColl, k), bestActive[k]),
            };
            for (int b = 0; b < 8; b++)
            {
                if ((xor & (1 << b)) == 0) continue;
                if ((bestActive[k] & (1 << b)) != 0) ch.BitsSet.Add(b);
                else ch.BitsCleared.Add(b);
            }
            // more than two bits moving in one byte, or a wide observed span, reads as analog
            int span = ch.MaxObserved - ch.MinObserved;
            ch.LooksAnalog = (ch.BitsSet.Count + ch.BitsCleared.Count) > 2 || span > 8;
            cap.Changes.Add(ch);
        }

        if (cap.Changes.Count == 0)
        {
            cap.Detected = false;
            cap.Note = "change detected but no byte differed from rest on the strongest sample";
        }
        return cap;
    }

    /// <summary>Every bit that differs between a report and the baseline, as (byte, bit) pairs.</summary>
    public static List<(int Byte, int Bit)> ChangedBits(byte[] report, byte[] baseline)
    {
        var bits = new List<(int, int)>();
        for (int k = 0; k < Math.Min(report.Length, baseline.Length); k++)
        {
            byte x = (byte)(report[k] ^ baseline[k]);
            for (int b = 0; b < 8; b++)
                if ((x & (1 << b)) != 0) bits.Add((k, b));
        }
        return bits;
    }

    /// <summary>
    /// The step that already owns this press, or null if it is a new input.
    ///
    /// A press is only refused when *every* bit it moved is already spoken for. That matters on
    /// this hardware: the four D-pad directions share the hat nibble, so up clears a superset of
    /// the bits every other direction clears, and a rule that rejected on any overlap would
    /// refuse right, down and left for ever after up was recorded. Each direction also sets its
    /// own bit in the vendor byte, and that unclaimed bit is what keeps them distinct.
    ///
    /// Split out of <see cref="CaptureStep"/> so the rule can be exercised without a
    /// controller. It is the one branch of the detection that a person cannot reach by simply
    /// following the prompts, because it only fires when they press the wrong thing.
    /// </summary>
    public string? ClaimedOwner(string collection, byte[] report, byte[] baseline)
    {
        if (_claimed.Count == 0) return null;

        var changed = ChangedBits(report, baseline);
        if (changed.Count == 0) return null;
        if (!changed.All(cb => _claimed.ContainsKey((collection, cb.Byte, cb.Bit)))) return null;

        return _claimed[(collection, changed[0].Byte, changed[0].Bit)];
    }

    /// <summary>Records a step's bits as claimed, without needing a full capture. For tests.</summary>
    public void ClaimBits(string collection, string stepId, params (int Byte, int Bit)[] bits)
    {
        foreach (var (by, bit) in bits) _claimed.TryAdd((collection, by, bit), stepId);
    }

    public static string Hex(byte[]? b) =>
        b is null || b.Length == 0 ? "(no report)" : string.Join(" ", b.Select(x => x.ToString("X2")));

    /// <summary>Resolves --only style selections, keeping the caller's order rather than script order.</summary>
    public static Step[] Select(string? only, Action<string>? onUnknown = null)
    {
        if (only is null) return Script;

        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var byId = Script.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var picked = new List<Step>();
        foreach (var id in wanted)
        {
            if (byId.TryGetValue(id, out var found)) picked.Add(found);
            else onUnknown?.Invoke(id);
        }
        return picked.ToArray();
    }

    public void Dispose()
    {
        foreach (var (_, r) in _readers) r.Dispose();
        _readers.Clear();
    }
}
