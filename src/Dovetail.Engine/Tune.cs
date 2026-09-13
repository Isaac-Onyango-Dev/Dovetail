using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Stick feel tuning, command-line front end. Writes deadzone, anti-deadzone, saturation and
/// response curve into the calibration profile, which a running engine picks up within a
/// second, so a change can be felt in-game without quitting.
///
/// The presets, the legal ranges and the write itself live in <see cref="StickFeel"/> in
/// Dovetail.Core, because the settings window sets the same four numbers from sliders. Two
/// implementations would drift, and the drift would be invisible: a slider that allowed a value
/// the command line clamps would produce a profile the engine reads back differently from the
/// one the user thought they saved. This file is prompting, printing and argument parsing.
/// </summary>
internal static class Tune
{
    internal static int Run(string[] args)
    {
        string dir = Program.ProfileDirectory(args);
        string? portArg = Program.ArgValue(args, "--slot") ?? Program.ArgValue(args, "--port");

        // Stick feel is one preference belonging to the operator, not to a unit, so a tune
        // applies to every profile unless a single slot is named.
        var targets = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.calibration.json").OrderBy(x => x).ToList()
            : [];
        if (portArg is not null && int.TryParse(portArg, out int onlySlot))
            targets = targets.Where(x =>
                Path.GetFileName(x).Equals(SlotRegistry.FileNameForSlot(onlySlot), StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"no calibration profile found in {dir}");
            return 1;
        }

        string? presetId = Program.ArgValue(args, "--preset");
        string? floorArg = Program.ArgValue(args, "--anti-deadzone");
        string? curveArg = Program.ArgValue(args, "--curve");
        string? dzArg = Program.ArgValue(args, "--deadzone");
        string? satArg = Program.ArgValue(args, "--saturation");
        bool show = args.Contains("--show") ||
                   (presetId is null && floorArg is null && curveArg is null && dzArg is null && satArg is null);

        if (show) return Show(targets);

        StickFeel.Settings settings;
        if (presetId is not null)
        {
            var p = StickFeel.FindPreset(presetId);
            if (p is null)
            {
                Console.Error.WriteLine($"unknown preset '{presetId}'. Valid: " +
                                        string.Join(", ", StickFeel.Presets.Select(x => x.Id)));
                return 1;
            }
            settings = StickFeel.Settings.FromPreset(p);
            Console.WriteLine($" preset '{p.Id}': {p.Summary}");
            Console.WriteLine();
        }
        else settings = new StickFeel.Settings();

        // explicit flags win over the preset
        if (floorArg is not null && int.TryParse(floorArg, out int f)) settings.AntiDeadzone = f;
        if (curveArg is not null && double.TryParse(curveArg, out double c)) settings.ResponseCurve = c;
        if (dzArg is not null && int.TryParse(dzArg, out int d)) settings.Deadzone = d;
        if (satArg is not null && int.TryParse(satArg, out int sa)) settings.Saturation = sa;

        // Apply the same settings to every target, so both pads feel identical.
        CalibrationProfile? last = null;
        foreach (var path in targets)
        {
            var target = CalibrationProfile.Load(path);
            StickFeel.Apply(target, settings);
            StickFeel.Note(target, presetId, "tune");
            target.Save(path);
            Console.WriteLine($" written to {Path.GetFileName(path)}");
            last = target;
        }

        Console.WriteLine();
        if (last is not null) ShowCurrent(last);
        Console.WriteLine();
        Console.WriteLine(" A running engine will pick this up within a second. No need to restart the game.");
        return 0;
    }

    private static int Show(List<string> targets)
    {
        Console.WriteLine($" profiles found: {string.Join(", ", targets.Select(Path.GetFileName))}");
        Console.WriteLine();
        foreach (var path in targets)
        {
            Console.WriteLine($" --- {Path.GetFileName(path)} ---");
            ShowCurrent(CalibrationProfile.Load(path));
            Console.WriteLine();
        }
        Console.WriteLine(" Presets:");
        foreach (var p in StickFeel.Presets)
            Console.WriteLine($"   {p.Id,-11} {p.Summary}");
        Console.WriteLine();
        Console.WriteLine(" Apply one:      dovetail-engine tune --preset standard      (all pads)");
        Console.WriteLine("                 dovetail-engine tune --preset standard --slot 1   (one pad)");
        Console.WriteLine(" Or set by hand: dovetail-engine tune --anti-deadzone 8049 --curve 1.2 --deadzone 4 --saturation 88");
        Console.WriteLine();
        Console.WriteLine(" The same four numbers are on the controller cards in the settings window,");
        Console.WriteLine(" which writes them through the same code as this command.");
        Console.WriteLine();
        Console.WriteLine(" saturation is the deviation, out of 127, at which the stick reports full");
        Console.WriteLine(" deflection. Free-play measurement on this pad: median push 61, p90 push 88.");
        Console.WriteLine();
        Console.WriteLine(" A running engine reloads the profile within a second, so you can change");
        Console.WriteLine(" this while the game is open and feel the difference immediately.");
        return 0;
    }

    private static void ShowCurrent(CalibrationProfile profile)
    {
        Console.WriteLine(" Current settings:");
        Console.WriteLine($"   {"axis",-13} {"centre",6} {"range",9} {"deadzone",8} {"anti-dz",8} {"curve",6} {"saturate",9}");
        Console.WriteLine($"   {new string('-', 12)} {new string('-', 6)} {new string('-', 9)} {new string('-', 8)} {new string('-', 8)} {new string('-', 6)} {new string('-', 9)}");
        foreach (var (name, a) in profile.Axes)
        {
            string sat = a.Saturation > 0 ? $"{a.Saturation} ({a.Saturation * 100 / 127}%)" : "mechanical";
            Console.WriteLine($"   {name,-13} {a.Centre,6} {a.Min + ".." + a.Max,9} {a.Deadzone,8} {a.AntiDeadzone,8} {a.ResponseCurve,6:0.##} {sat,9}");
        }

        if (!profile.Axes.TryGetValue("leftStickX", out var ax)) return;

        Console.WriteLine();
        Console.WriteLine($" Usable travel, left stick X: {StickFeel.UsableTravelPercent(ax, false)}% of the push reaches the game.");
        Console.WriteLine();
        Console.WriteLine(" What a stick push now produces, left stick X, against the 7849 the game discards:");
        Console.WriteLine($"   {"physical travel",16} {"raw",5} {"XInput",8}  reaches the game?");
        Console.WriteLine($"   {new string('-', 16)} {new string('-', 5)} {new string('-', 8)}  -----------------");
        foreach (double frac in new[] { 0.0, 0.05, 0.10, 0.20, 0.30, 0.50, 0.75, 1.0 })
        {
            int rawv = (int)Math.Round(ax.Centre + frac * (ax.Max - ax.Centre));
            short outv = XInputMapper.Scale(rawv, ax);
            bool reaches = Math.Abs(outv) > XInputMapper.XInputLeftThumbDeadzone;
            Console.WriteLine($"   {frac * 100,14:0}%  {rawv,5} {outv,8}  {(reaches ? "yes" : "no, discarded")}");
        }
    }
}
