using Dovetail.Core;

namespace Dovetail.Diagnostics;

/// <summary>
/// Names whatever the operator presses, live. Built because "L1" and "L2" mean different
/// things on different pads and the wizard prompts were relying on the operator matching a
/// label to a physical button. This inverts that: press anything, and the tool reports the
/// byte, the bit, and the name Stage 1 assigned to it. Labels then come from the hardware
/// rather than from the prompt wording.
/// </summary>
internal static class Identify
{
    private const int ByRightX = 1, ByRightY = 2, ByLeftX = 3, ByLeftY = 4;
    private const int ByHatBtn = 5, ByButtons = 6, ByVendor = 7;

    /// <summary>Names for every bit Stage 1 mapped. Keyed by (byte, bit).</summary>
    private static readonly Dictionary<(int, int), string> BitNames = new()
    {
        [(ByHatBtn, 4)] = "Triangle / upper face    (HID button 1)",
        [(ByHatBtn, 5)] = "Circle / right face      (HID button 2)",
        [(ByHatBtn, 6)] = "Cross / lower face       (HID button 3)",
        [(ByHatBtn, 7)] = "Square / left face       (HID button 4)",

        [(ByButtons, 0)] = "L1  upper-left shoulder  (HID button 5)",
        [(ByButtons, 1)] = "R1  upper-right shoulder (HID button 6)",
        [(ByButtons, 2)] = "L2  lower-left trigger   (HID button 7)",
        [(ByButtons, 3)] = "R2  lower-right trigger  (HID button 8)",
        // Corrected 2026-09-12 by the targeted centre-cluster sweep. See Findings Log 2.7:
        // SELECT and START are the standard HID buttons 9 and 10, and the byte 7 bits are
        // the pad's three Android buttons, not Select/Start/Home.
        [(ByButtons, 4)] = "SELECT  printed label    (HID button 9)",
        [(ByButtons, 5)] = "START   printed label    (HID button 10)",
        [(ByButtons, 6)] = "L3  left stick click     (HID button 11)",
        [(ByButtons, 7)] = "R3  right stick click    (HID button 12)",

        [(ByVendor, 0)] = "D-pad up     (vendor byte, duplicate of hat)",
        [(ByVendor, 1)] = "D-pad down   (vendor byte, duplicate of hat)",
        [(ByVendor, 2)] = "D-pad left   (vendor byte, duplicate of hat)",
        [(ByVendor, 3)] = "D-pad right  (vendor byte, duplicate of hat)",
        [(ByVendor, 4)] = "HOME  house icon         (vendor byte) -> XInput Guide",
        [(ByVendor, 5)] = "Android MENU icon        (vendor byte) -> unassigned",
        [(ByVendor, 6)] = "Android BACK arrow icon  (vendor byte) -> unassigned",
        [(ByVendor, 7)] = "vendor byte bit 7  <-- never observed changing",
    };

    private static readonly Dictionary<int, string> HatNames = new()
    {
        [0] = "hat UP", [1] = "hat UP-RIGHT", [2] = "hat RIGHT", [3] = "hat DOWN-RIGHT",
        [4] = "hat DOWN", [5] = "hat DOWN-LEFT", [6] = "hat LEFT", [7] = "hat UP-LEFT",
        [15] = "hat centred",
    };

    internal static int Run(string[] args, ushort vid, ushort pid)
    {
        int seconds = int.TryParse(Program.ArgValue(args, "--seconds"), out int s) ? s : 90;
        string? profilePath = Program.ArgValue(args, "--profile");
        string? outPath = Program.ArgValue(args, "--out");

        var candidates = HidScan.Enumerate(vid, pid)
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($" No HID interface for VID 0x{vid:X4} / PID 0x{pid:X4}.");
            return 1;
        }

        HidCollectionInfo? live = null;
        if (profilePath is not null && File.Exists(profilePath))
        {
            var prof = CalibrationProfile.Load(profilePath);
            if (prof.Device.ReportId != 0)
            {
                live = DeviceSelector.SelectByReportId(candidates, prof.Device.ReportId);
                if (live is not null)
                    Console.WriteLine($" using {live.CollectionTag}, report id {prof.Device.ReportId}, from the saved profile");
            }
        }
        live ??= candidates.FirstOrDefault(c => c.CollectionTag == "COL02") ?? candidates[0];

        var log = new List<string>();
        void W(string t) { Console.WriteLine(t); log.Add(t); }

        W(new string('=', 74));
        W(" DOVETAIL - INPUT IDENTIFIER");
        W(new string('=', 74));
        W($" collection {live.CollectionTag}   {seconds}s   started {DateTime.Now:HH:mm:ss}");
        W("");
        W(" Press any control. Each press is named as soon as it is seen.");
        W(" Nothing is saved to the calibration profile by this command.");
        W("");
        W(" To settle the shoulder labelling, press these four in order and read back");
        W(" what the tool calls each one:");
        W("   1. the UPPER button on the LEFT rear edge");
        W("   2. the LOWER button on the LEFT rear edge, directly beneath it");
        W("   3. the UPPER button on the RIGHT rear edge");
        W("   4. the LOWER button on the RIGHT rear edge");
        W("");
        W(new string('-', 74));

        using var reader = new HidReader(live.DevicePath, live.Caps.InputReportByteLength);
        var rest = reader.WaitForRest(300, 6000);
        if (rest is null || rest.Length == 0)
        {
            Console.Error.WriteLine(" no reports from the device");
            return 1;
        }
        W($" rest state: {Hex(rest)}");
        W("");

        byte[] prev = rest;
        int events = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        // Heartbeat. This adapter streams idle reports whether or not a pad is linked to the
        // port, so silence from the pad looks identical to silence from the tool. The
        // heartbeat separates the two: it keeps printing while nothing is arriving, and says
        // plainly that reports are flowing but none carry input.
        var nextBeat = DateTime.UtcNow.AddSeconds(12);
        int reportsSeen = 0, idleReports = 0;
        byte[] lastReport = rest;

        while (DateTime.UtcNow < deadline)
        {
            var r = reader.Read(30);

            if (DateTime.UtcNow >= nextBeat)
            {
                nextBeat = DateTime.UtcNow.AddSeconds(12);
                int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                if (events == 0 && idleReports == reportsSeen && reportsSeen > 0)
                {
                    W($" ... listening. {reportsSeen} reports received, all idle ({Hex(lastReport)}).");
                    W($"     The receiver is alive but the pad is sending nothing. Wake or re-pair it.");
                    W($"     {left}s remaining.");
                }
                else
                {
                    W($" ... listening. {events} input events so far, {reportsSeen} reports. {left}s remaining.");
                }
            }

            if (r is null || r.Length < rest.Length) continue;
            reportsSeen++;
            lastReport = r;
            if (r.AsSpan().SequenceEqual(rest)) idleReports++;
            if (r.AsSpan().SequenceEqual(prev)) continue;

            // button bits going high
            foreach (int by in new[] { ByHatBtn, ByButtons, ByVendor })
            {
                if (by >= r.Length) continue;
                int lowMask = by == ByHatBtn ? 0xF0 : 0xFF;   // hat nibble handled separately
                int rose = (r[by] & ~prev[by]) & lowMask;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((rose & (1 << bit)) == 0) continue;
                    events++;
                    string name = BitNames.GetValueOrDefault((by, bit), "unmapped bit");
                    W($" [{events,2}] PRESS   byte {by} bit {bit}   {name}");
                    W($"        report {Hex(r)}");
                }
            }

            // hat
            if (ByHatBtn < r.Length)
            {
                int hatNow = r[ByHatBtn] & 0x0F, hatWas = prev[ByHatBtn] & 0x0F;
                if (hatNow != hatWas && hatNow != 15)
                {
                    events++;
                    W($" [{events,2}] HAT     byte {ByHatBtn} low nibble = {hatNow}   " +
                      $"{HatNames.GetValueOrDefault(hatNow, "unknown hat value")}");
                    W($"        report {Hex(r)}");
                }
            }

            // axes, only when they leave the deadzone, to keep the log readable
            foreach (var (by, label) in new[]
                     { (ByLeftX, "left stick X"), (ByLeftY, "left stick Y"),
                       (ByRightX, "right stick X"), (ByRightY, "right stick Y") })
            {
                if (by >= r.Length) continue;
                int centre = rest[by];
                int devNow = Math.Abs(r[by] - centre), devWas = Math.Abs(prev[by] - centre);
                if (devNow > 40 && devWas <= 40)
                {
                    events++;
                    string dir = r[by] > centre
                        ? (by is ByLeftY or ByRightY ? "DOWN" : "RIGHT")
                        : (by is ByLeftY or ByRightY ? "UP" : "LEFT");
                    W($" [{events,2}] AXIS    byte {by} = {r[by],3}   {label} moved {dir}");
                }
            }

            prev = r;
        }

        W("");
        W(new string('=', 74));
        W($" {events} input events recorded in {seconds}s");
        W(new string('=', 74));

        if (outPath is not null)
        {
            File.WriteAllLines(outPath, log);
            Console.WriteLine($" (written to {outPath})");
        }
        return 0;
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
}
