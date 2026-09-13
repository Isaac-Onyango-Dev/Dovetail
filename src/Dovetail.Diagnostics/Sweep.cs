using System.Text.Json;

using Dovetail.Core;

namespace Dovetail.Diagnostics;

/// <summary>
/// Stage 1 guided input sweep, console front end.
///
/// The detection itself lives in <see cref="InputSweep"/> in Dovetail.Core, so that this wizard
/// and the calibration window in the tray application run the same code rather than two copies
/// of it. This file is now only prompting, printing and the JSON report; the behaviour that was
/// verified in Stage 1 is unchanged, and the regression guards in the engine self-test still
/// cover it.
/// </summary>
internal static class Sweep
{
    internal static int Run(string[] args, ushort vid, ushort pid)
    {
        int stepTimeout = int.TryParse(Program.ArgValue(args, "--step-timeout"), out int st) ? st : 30;
        string outPath = Program.ArgValue(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "stage1-sweep.json");
        string? only = Program.ArgValue(args, "--only");

        using var sweep = new InputSweep();
        var failures = sweep.Open(vid, pid);
        foreach (var f in failures) Console.WriteLine($"  {f}");

        if (sweep.Collections.Count == 0)
        {
            Console.Error.WriteLine(
                $"No HID interface for VID 0x{vid:X4} / PID 0x{pid:X4} could be opened. Connect the controller.");
            return 1;
        }

        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - STAGE 1 GUIDED INPUT SWEEP");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($" collections open: {string.Join(", ", sweep.Collections.Select(c => c.CollectionTag))}");
        Console.WriteLine($" per-step timeout: {stepTimeout}s");
        Console.WriteLine();
        Console.WriteLine(" Work through the prompts in order. Each step advances by itself once it");
        Console.WriteLine(" sees the input move and then return to rest. Take your time.");
        Console.WriteLine();

        // ---- rest state ----
        Console.WriteLine(" Sampling rest state. Do not touch the controller for 3 seconds...");
        sweep.SampleRest(3000);
        foreach (var info in sweep.Collections)
            Console.WriteLine($"   {info.CollectionTag} rest = " +
                              InputSweep.Hex(sweep.RestStates.GetValueOrDefault(info.CollectionTag)));

        if (sweep.LiveCollections.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine(" No collection produced any report during rest sampling.");
            Console.WriteLine(" The pad may be asleep. Press any button once, then run the sweep again.");
            return 1;
        }
        Console.WriteLine();

        // --only runs the steps in the order the caller listed them, not in script order.
        // Getting this wrong once already had the operator pressing the button the prompt did
        // not want.
        var steps = InputSweep.Select(only,
            id => Console.WriteLine($" (--only: no step named '{id}', skipping)"));

        var events = new InputSweep.SweepEvents
        {
            Log = Console.WriteLine,
            WrongInput = owner => Console.WriteLine(
                $"   ignoring that press - it is '{owner}', already recorded. Still waiting for this step."),
        };

        var results = new List<InputSweep.Capture>();
        int idx = 0;

        foreach (var step in steps)
        {
            idx++;
            Console.WriteLine(new string('-', 74));
            Console.WriteLine($" [{idx}/{steps.Length}]  PRESS: {step.Prompt}");
            Console.WriteLine(new string('-', 74));

            // Settle first, so a button still held from the previous step cannot instantly
            // satisfy this one.
            sweep.Settle();

            var cap = sweep.CaptureStep(step, stepTimeout, events);
            results.Add(cap);
            sweep.Claim(cap);

            if (cap.Detected)
            {
                Console.WriteLine($"   DETECTED on {cap.Collection}");
                Console.WriteLine($"     rest   {cap.RestHex}");
                Console.WriteLine($"     active {cap.ActiveHex}");
                foreach (var c in cap.Changes)
                {
                    string bits = c.BitsSet.Count > 0 ? $"bits set {string.Join(",", c.BitsSet)}" : "";
                    if (c.BitsCleared.Count > 0)
                        bits += (bits.Length > 0 ? "; " : "") + $"bits cleared {string.Join(",", c.BitsCleared)}";
                    string analog = c.LooksAnalog
                        ? $"  range {c.MinObserved}..{c.MaxObserved} (analog)"
                        : "";
                    Console.WriteLine($"     byte {c.ByteIndex,2}: 0x{c.RestValue:X2} -> 0x{c.ActiveValue:X2} " +
                                      $"xor 0x{c.XorMask:X2}  {bits}{analog}");
                }
            }
            else
            {
                Console.WriteLine($"   NOT DETECTED within {stepTimeout}s - recorded as no distinct change.");
            }
            Console.WriteLine();
        }

        // ---- summary ----
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" SWEEP SUMMARY");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" {"input",-12} {"coll",-6} {"byte",-5} {"bit/range",-22} status");
        Console.WriteLine($" {new string('-', 11)} {new string('-', 5)} {new string('-', 4)} {new string('-', 21)} ------");
        foreach (var c in results)
        {
            if (!c.Detected) { Console.WriteLine($" {c.Id,-12} {"-",-6} {"-",-5} {"-",-22} NOT DETECTED"); continue; }
            var first = c.Changes[0];
            string where = first.LooksAnalog
                ? $"analog {first.MinObserved}..{first.MaxObserved}"
                : (first.BitsSet.Count > 0 ? $"bit {string.Join(",", first.BitsSet)} set"
                                           : $"bit {string.Join(",", first.BitsCleared)} cleared");
            Console.WriteLine($" {c.Id,-12} {c.Collection,-6} {first.ByteIndex,-5} {where,-22} ok" +
                              (c.Changes.Count > 1 ? $" (+{c.Changes.Count - 1} more bytes)" : ""));
        }
        Console.WriteLine();

        int detected = results.Count(r => r.Detected);
        Console.WriteLine($" {detected} of {results.Count} inputs produced a distinct, recorded change.");
        var missing = results.Where(r => !r.Detected).Select(r => r.Id).ToList();
        if (missing.Count > 0) Console.WriteLine($" not detected: {string.Join(", ", missing)}");

        var payload = new
        {
            stage = 1,
            capturedUtc = DateTime.UtcNow.ToString("u"),
            vid = $"0x{vid:X4}",
            pid = $"0x{pid:X4}",
            collections = sweep.Collections.Select(info => new
            {
                tag = info.CollectionTag,
                instanceId = info.InstanceId,
                devicePath = info.DevicePath,
                inputReportByteLength = info.Caps.InputReportByteLength,
                restState = InputSweep.Hex(sweep.RestStates.GetValueOrDefault(info.CollectionTag))
            }),
            steps = results
        };
        // Capture and ByteChange expose public fields, which System.Text.Json skips unless
        // IncludeFields is set. Without this the steps array serialises as {}.
        File.WriteAllText(outPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true
            }));
        Console.WriteLine($" results written to {outPath}");
        return 0;
    }
}
