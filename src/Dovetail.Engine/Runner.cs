using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Runs the translation engine and prints what it is doing. The pump loop lives here rather
/// than in the engine so the Stage 5 tray app can own its own loop without inheriting a
/// console-shaped one.
/// </summary>
internal static class Runner
{
    /// <summary>
    /// Drives the engine on a background thread and returns a handle that stops it. Shared
    /// by 'run' and 'validate', because validation has to read XInput back while the engine
    /// is actively translating.
    /// </summary>
    internal sealed class Pump : IDisposable
    {
        private readonly DovetailEngine _engine;
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly string? _profilePath;
        private DateTime _profileStamp;

        public long Submitted;
        public long Loops;
        public long Reloads;
        public Exception? Fault;

        public Pump(DovetailEngine engine, string? profilePath = null)
        {
            _engine = engine;
            _profilePath = profilePath;
            if (profilePath is not null && File.Exists(profilePath))
            {
                var dir = Path.GetDirectoryName(profilePath)!;
                _profileStamp = Directory.GetFiles(dir, "*.calibration.json")
                                         .Select(File.GetLastWriteTimeUtc)
                                         .DefaultIfEmpty(DateTime.MinValue).Max();
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "dovetail-pump", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            var nextRescan = DateTime.UtcNow;
            var nextProfileCheck = DateTime.UtcNow.AddSeconds(1);
            try
            {
                while (!_stop)
                {
                    if (DateTime.UtcNow >= nextRescan)
                    {
                        _engine.Rescan();
                        nextRescan = DateTime.UtcNow.AddSeconds(2);
                    }

                    // Hot-reload the calibration when the file changes on disk, so stick
                    // feel can be tuned without quitting the game.
                    if (_profilePath is not null && DateTime.UtcNow >= nextProfileCheck)
                    {
                        nextProfileCheck = DateTime.UtcNow.AddSeconds(1);
                        try
                        {
                            // Watch the whole profile directory, since each port has its
                            // own file and any of them can change.
                            var dir = Path.GetDirectoryName(_profilePath)!;
                            var stamp = Directory.GetFiles(dir, "*.calibration.json")
                                                 .Select(File.GetLastWriteTimeUtc)
                                                 .DefaultIfEmpty(DateTime.MinValue)
                                                 .Max();
                            if (stamp != _profileStamp)
                            {
                                _profileStamp = stamp;
                                Thread.Sleep(120);   // let the writer finish
                                _engine.ReloadProfiles(SlotRegistry.Load(Path.GetDirectoryName(_profilePath)!));
                                Reloads++;
                            }
                        }
                        catch { /* a half-written file is retried on the next tick */ }
                    }

                    Submitted += _engine.Pump();
                    Loops++;
                }
            }
            catch (Exception ex) { Fault = ex; }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1500);
        }
    }

    internal static int Run(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        int seconds = int.TryParse(Program.ArgValue(args, "--seconds"), out int s) ? s : 0;

        void Log(string m) { if (!quiet) Console.WriteLine("  " + m); }

        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - TRANSLATION ENGINE");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        string profilePath = Program.ArgValue(args, "--profile") ?? Program.DefaultProfilePath();
        var slots = Program.LoadSlots(args, Log);
        Console.WriteLine();

        using var engine = new DovetailEngine(slots, Log);
        if (!engine.Start())
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($" cannot create a virtual controller: {engine.Bus.Status}");
            return 1;
        }

        engine.Rescan();
        Console.WriteLine();
        Console.WriteLine(" Translating. A virtual Xbox 360 controller appears for each pad as soon as");
        Console.WriteLine(" that pad produces input, so the pad you pick up becomes player 1.");
        Console.WriteLine();
        Console.WriteLine(" The view below is a SAMPLER: it refreshes every 250ms, so a press and release");
        Console.WriteLine(" faster than that can be missed here even though the engine handled it. Run");
        Console.WriteLine(" \"dovetail-engine burst\" to measure what actually reaches the game.");
        if (seconds > 0) Console.WriteLine($" Running for {seconds}s.");
        else Console.WriteLine(" Press Ctrl+C to stop.");
        Console.WriteLine();

        using var pump = new Pump(engine, profilePath);

        var deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        var nextPrint = DateTime.UtcNow.AddSeconds(1);
        // Per channel, because one shared value compares COL01 against COL02 and so
        // never suppresses anything, filling the log with duplicate idle lines.
        var lastLine = new Dictionary<string, string>();

        while (DateTime.UtcNow < deadline)
        {
            if (pump.Fault is not null)
            {
                Console.Error.WriteLine($" pump thread faulted: {pump.Fault.Message}");
                return 2;
            }

            if (DateTime.UtcNow >= nextPrint)
            {
                nextPrint = DateTime.UtcNow.AddMilliseconds(250);
                foreach (var ch in engine.Channels)
                {
                    if (ch.Last is null) continue;
                    string slot = ch.Virtual?.UserIndex is int u ? $"slot {u}" : ch.Bound ? "slot pending" : "unbound";
                    string line = $" {ch.Tag} rid{ch.ReportId} {slot,-12} {ch.Last}  " +
                                  $"-> XInput [{XInputButtons.Describe(ch.LastReport.Buttons)}] " +
                                  $"LT={ch.LastReport.LeftTrigger,3} RT={ch.LastReport.RightTrigger,3} " +
                                  $"L({ch.LastReport.ThumbLX,6},{ch.LastReport.ThumbLY,6}) " +
                                  $"R({ch.LastReport.ThumbRX,6},{ch.LastReport.ThumbRY,6})";
                    if (lastLine.TryGetValue(ch.Tag, out var prev) && prev == line) continue;
                    lastLine[ch.Tag] = line;
                    Console.WriteLine(line);
                }
            }
            Thread.Sleep(10);
        }

        Console.WriteLine();
        Console.WriteLine($" pump loops {pump.Loops}, reports submitted to virtual devices {pump.Submitted}"
                          + (pump.Reloads > 0 ? $", calibration reloads {pump.Reloads}" : ""));
        foreach (var ch in engine.Channels)
            Console.WriteLine($" {ch.Tag}: {ch.ReportsRead} reports read, {ch.IdleReports} idle, " +
                              $"bound={ch.Bound}, virtual submits={ch.Virtual?.SubmitCount ?? 0}");
        return 0;
    }
}
