using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Proves the end state of the Section 5.7 first-run setup: not merely that a driver
/// installed, but that a virtual controller can be created and that XInput reports it.
///
/// This exists because "the installer exited with code 0" is not the same claim as "a game
/// would now see a controller". The operator's sign-off condition is explicitly
/// fresh state, one click, verified install, working device, and this is the last of those
/// four. It needs no physical pad, so it can run inside a sandbox where no hardware exists.
/// </summary>
internal static class VerifyDevice
{
    internal static int Run(string[] args)
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - VIRTUAL DEVICE VERIFICATION (no physical pad needed)");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($" {DateTime.Now:yyyy-MM-dd HH:mm:ss}   machine {Environment.MachineName}");
        Console.WriteLine();

        var reader = XInputReader.OpenFirstAvailable(out var tried);
        foreach (var t in tried) Console.WriteLine("   " + t);
        if (reader is null)
        {
            Console.WriteLine(" FAIL: no XInput runtime could be loaded, so nothing could read a controller.");
            return 1;
        }
        Console.WriteLine($"   using {reader.DllName}");
        Console.WriteLine();

        var before = reader.ConnectedSlots();
        Console.WriteLine($" slots occupied before : {(before.Count == 0 ? "none" : string.Join(", ", before))}");

        using var bus = new VirtualBus();
        if (!bus.Open())
        {
            Console.WriteLine($" FAIL: {bus.Status}");
            reader.Dispose();
            return 3;
        }
        Console.WriteLine($" virtual bus           : {bus.Status}");

        using var pad = bus.CreatePad();
        pad.Connect();
        Console.WriteLine(" virtual pad           : connected");

        // The driver takes a moment to assign a slot and publish the device.
        List<int> after = [];
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            after = reader.ConnectedSlots();
            if (after.Except(before).Any()) break;
            Thread.Sleep(150);
        }

        var appeared = after.Except(before).ToList();
        Console.WriteLine($" slots occupied after  : {(after.Count == 0 ? "none" : string.Join(", ", after))}");
        Console.WriteLine($" new slot              : {(appeared.Count == 0 ? "NONE" : string.Join(", ", appeared))}");
        Console.WriteLine($" driver-reported index : {pad.UserIndex?.ToString() ?? "not reported"}");
        Console.WriteLine();

        if (appeared.Count == 0)
        {
            Console.WriteLine(" FAIL: the virtual pad connected but XInput never reported a new controller.");
            reader.Dispose();
            return 3;
        }

        // Drive a value through and read it back, so the device is proven functional rather
        // than merely present.
        int slot = appeared[0];
        var probe = new XInputReport
        {
            Buttons = XInputButtons.A | XInputButtons.LeftThumb,
            LeftTrigger = 255,
            ThumbLX = 20000,
            ThumbRY = -20000,
        };
        pad.Submit(probe);
        Thread.Sleep(250);

        var got = reader.Read(slot);
        Console.WriteLine(" round-trip test, writing a known state and reading it back:");
        Console.WriteLine($"   sent     buttons=0x{probe.Buttons:X4} [{XInputButtons.Describe(probe.Buttons)}] " +
                          $"LT={probe.LeftTrigger} L({probe.ThumbLX},{probe.ThumbLY}) R({probe.ThumbRX},{probe.ThumbRY})");
        if (got is null)
        {
            Console.WriteLine("   received nothing; the slot stopped responding");
            reader.Dispose();
            return 3;
        }
        Console.WriteLine($"   received buttons=0x{got.Value.Buttons:X4} [{XInputButtons.Describe(got.Value.Buttons)}] " +
                          $"LT={got.Value.LeftTrigger} L({got.Value.ThumbLX},{got.Value.ThumbLY}) " +
                          $"R({got.Value.ThumbRX},{got.Value.ThumbRY})");

        bool buttonsOk = (got.Value.Buttons & probe.Buttons) == probe.Buttons;
        bool triggerOk = got.Value.LeftTrigger >= 200;
        bool axisOk = got.Value.ThumbLX > 15000 && got.Value.ThumbRY < -15000;
        Console.WriteLine();
        Console.WriteLine($"   buttons round-tripped : {buttonsOk}");
        Console.WriteLine($"   trigger round-tripped : {triggerOk}");
        Console.WriteLine($"   axes round-tripped    : {axisOk}");
        Console.WriteLine();

        bool pass = buttonsOk && triggerOk && axisOk;
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(pass
            ? " PASS: a virtual Xbox 360 controller exists and XInput reads back what was\n" +
              " written to it. A game on this machine would see a working controller."
            : " FAIL: the device exists but values did not survive the round trip.");
        Console.WriteLine(new string('=', 74));

        reader.Dispose();
        return pass ? 0 : 3;
    }
}
