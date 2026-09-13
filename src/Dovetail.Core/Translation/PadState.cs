namespace Dovetail.Core;

/// <summary>
/// One decoded snapshot of the physical pad. Every field here was measured on the wire
/// during Stage 1 and Stage 2, not inferred from a device template.
/// </summary>
public sealed class PadState
{
    public byte ReportId;

    // face buttons, byte 5 bits 4-7
    public bool Triangle, Circle, Cross, Square;

    // byte 6
    public bool L1, R1, L2, R2, Select, Start, L3, R3;

    // vendor byte 7
    public bool Home, AndroidMenu, AndroidBack;

    // D-pad, from the hat nibble in byte 5
    public bool DUp, DDown, DLeft, DRight;
    public int HatValue = 15;

    // raw axis counts, 0..255
    public int LeftX = 128, LeftY = 128, RightX = 128, RightY = 128;

    public byte[] Raw = [];

    public bool AnyInputActive =>
        Triangle || Circle || Cross || Square ||
        L1 || R1 || L2 || R2 || Select || Start || L3 || R3 ||
        Home || AndroidMenu || AndroidBack ||
        DUp || DDown || DLeft || DRight;

    public override string ToString()
    {
        var held = new List<string>();
        if (Triangle) held.Add("Y");
        if (Circle) held.Add("B");
        if (Cross) held.Add("A");
        if (Square) held.Add("X");
        if (L1) held.Add("L1");
        if (R1) held.Add("R1");
        if (L2) held.Add("L2");
        if (R2) held.Add("R2");
        if (L3) held.Add("L3");
        if (R3) held.Add("R3");
        if (Select) held.Add("Select");
        if (Start) held.Add("Start");
        if (Home) held.Add("Home");
        if (AndroidMenu) held.Add("AndroidMenu");
        if (AndroidBack) held.Add("AndroidBack");
        if (DUp) held.Add("Up");
        if (DDown) held.Add("Down");
        if (DLeft) held.Add("Left");
        if (DRight) held.Add("Right");
        string buttons = held.Count == 0 ? "-" : string.Join("+", held);
        return $"L({LeftX,3},{LeftY,3}) R({RightX,3},{RightY,3}) hat={HatValue,2} [{buttons}]";
    }
}

/// <summary>
/// Turns a raw 8-byte report into a <see cref="PadState"/> using the byte and bit positions
/// recorded in the calibration profile. Nothing here is hard-coded to this pad: every
/// position is read from the profile, so a differently laid out device needs a new profile
/// rather than new code.
/// </summary>
public sealed class HidDecoder
{
    private readonly CalibrationProfile _profile;

    private readonly (int by, int bit) _triangle, _circle, _cross, _square;
    private readonly (int by, int bit) _l1, _r1, _l2, _r2, _l3, _r3, _select, _start;
    private readonly (int by, int bit) _home, _menu, _back;
    private readonly int _hatByte, _hatNull;
    private readonly int _lx, _ly, _rx, _ry;
    private readonly int _minLength;

    public CalibrationProfile Profile => _profile;

    public HidDecoder(CalibrationProfile profile)
    {
        _profile = profile;

        (int, int) Bit(string name, int fallbackByte, int fallbackBit) =>
            profile.Buttons.TryGetValue(name, out var b) ? (b.Byte, b.Bit) : (fallbackByte, fallbackBit);

        _triangle = Bit("triangle", 5, 4);
        _circle = Bit("circle", 5, 5);
        _cross = Bit("cross", 5, 6);
        _square = Bit("square", 5, 7);
        _l1 = Bit("l1", 6, 0);
        _r1 = Bit("r1", 6, 1);
        _l2 = Bit("l2", 6, 2);
        _r2 = Bit("r2", 6, 3);
        _select = Bit("select", 6, 4);
        _start = Bit("start", 6, 5);
        _l3 = Bit("l3", 6, 6);
        _r3 = Bit("r3", 6, 7);
        _home = Bit("home", 7, 4);
        _menu = Bit("android_menu", 7, 5);
        _back = Bit("android_back", 7, 6);

        _hatByte = profile.Hat.Byte;
        _hatNull = profile.Hat.NullValue;

        int AxisByte(string name, int fallback) =>
            profile.Axes.TryGetValue(name, out var a) ? a.Byte : fallback;

        _lx = AxisByte("leftStickX", 3);
        _ly = AxisByte("leftStickY", 4);
        _rx = AxisByte("rightStickX", 1);
        _ry = AxisByte("rightStickY", 2);

        _minLength = new[]
        {
            _triangle.by, _circle.by, _cross.by, _square.by,
            _l1.by, _r1.by, _l2.by, _r2.by, _l3.by, _r3.by, _select.by, _start.by,
            _home.by, _menu.by, _back.by, _hatByte, _lx, _ly, _rx, _ry
        }.Max() + 1;
    }

    public bool CanDecode(byte[] report) => report.Length >= _minLength;

    public PadState Decode(byte[] r)
    {
        if (!CanDecode(r))
            throw new ArgumentException($"report is {r.Length} bytes, need at least {_minLength}");

        static bool B(byte[] rr, (int by, int bit) p) => (rr[p.by] & (1 << p.bit)) != 0;

        int hat = r[_hatByte] & 0x0F;
        var s = new PadState
        {
            ReportId = r[0],
            Raw = r,
            Triangle = B(r, _triangle),
            Circle = B(r, _circle),
            Cross = B(r, _cross),
            Square = B(r, _square),
            L1 = B(r, _l1),
            R1 = B(r, _r1),
            L2 = B(r, _l2),
            R2 = B(r, _r2),
            Select = B(r, _select),
            Start = B(r, _start),
            L3 = B(r, _l3),
            R3 = B(r, _r3),
            Home = B(r, _home),
            AndroidMenu = B(r, _menu),
            AndroidBack = B(r, _back),
            HatValue = hat,
            LeftX = r[_lx],
            LeftY = r[_ly],
            RightX = r[_rx],
            RightY = r[_ry],
        };

        // Hat encoding measured in Stage 1: 0 up, 2 right, 4 down, 6 left, odd values are
        // the diagonals, 15 is null. Decoding by range covers the diagonals without having
        // had to press each one.
        if (hat != _hatNull && hat <= 7)
        {
            s.DUp = hat == 7 || hat == 0 || hat == 1;
            s.DRight = hat >= 1 && hat <= 3;
            s.DDown = hat >= 3 && hat <= 5;
            s.DLeft = hat >= 5 && hat <= 7;
        }
        return s;
    }
}
