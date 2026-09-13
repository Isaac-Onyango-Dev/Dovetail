namespace Dovetail.Core;

internal static class HidUsages
{
    internal static string PageName(ushort page) => page switch
    {
        0x00 => "Undefined",
        0x01 => "Generic Desktop",
        0x02 => "Simulation Controls",
        0x03 => "VR Controls",
        0x04 => "Sport Controls",
        0x05 => "Game Controls",
        0x06 => "Generic Device Controls",
        0x07 => "Keyboard/Keypad",
        0x08 => "LED",
        0x09 => "Button",
        0x0A => "Ordinal",
        0x0B => "Telephony",
        0x0C => "Consumer",
        0x0D => "Digitizer",
        0x0F => "PID (force feedback)",
        0x10 => "Unicode",
        _ => page >= 0xFF00 ? $"Vendor-defined 0x{page:X4}" : $"page 0x{page:X4}"
    };

    /// <summary>Generic Desktop (page 0x01) usage names, the page this pad reports axes on.</summary>
    internal static string GenericDesktop(ushort usage) => usage switch
    {
        0x00 => "Undefined",
        0x01 => "Pointer",
        0x02 => "Mouse",
        0x04 => "Joystick",
        0x05 => "Game Pad",
        0x06 => "Keyboard",
        0x07 => "Keypad",
        0x08 => "Multi-axis Controller",
        0x30 => "X",
        0x31 => "Y",
        0x32 => "Z",
        0x33 => "Rx",
        0x34 => "Ry",
        0x35 => "Rz",
        0x36 => "Slider",
        0x37 => "Dial",
        0x38 => "Wheel",
        0x39 => "Hat switch",
        0x3A => "Counted Buffer",
        0x3B => "Byte Count",
        0x3C => "Motion Wakeup",
        0x3D => "Start",
        0x3E => "Select",
        0x40 => "Vx",
        0x41 => "Vy",
        0x42 => "Vz",
        0x43 => "Vbrx",
        0x44 => "Vbry",
        0x45 => "Vbrz",
        0x46 => "Vno",
        0x80 => "System Control",
        _ => $"usage 0x{usage:X2}"
    };

    internal static string Describe(ushort page, ushort usage) => page switch
    {
        0x01 => $"{GenericDesktop(usage)}",
        0x09 => $"Button {usage}",
        _ => $"{PageName(page)} usage 0x{usage:X4}"
    };

    internal static string CollectionTypeName(byte t) => t switch
    {
        0x00 => "Physical",
        0x01 => "Application",
        0x02 => "Logical",
        0x03 => "Report",
        0x04 => "Named Array",
        0x05 => "Usage Switch",
        0x06 => "Usage Modifier",
        _ => $"0x{t:X2}"
    };
}
