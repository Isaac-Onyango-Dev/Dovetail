using System.Runtime.InteropServices;

namespace Dovetail.Core;

/// <summary>
/// Reads XInput back from the operating system. This is the independent check on the whole
/// pipeline: the engine writes to ViGEmBus, and this reads what XInput hands to a game.
/// If a button appears here, a game that polls XInput will see it.
///
/// Both runtime versions can be queried. That matters because Sekiro imports
/// <c>xinput1_3.dll</c>, not 1.4 (Findings Log 0.6), so validating against 1.4 alone would
/// not prove the case that actually has to work.
/// </summary>
public sealed class XInputReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    private delegate int XInputGetStateDelegate(int userIndex, out XINPUT_STATE state);

    private IntPtr _module;
    private XInputGetStateDelegate? _getState;
    private XInputGetStateDelegate? _getStateEx;

    public string DllName { get; }
    public bool Loaded => _getState is not null;
    /// <summary>True when the undocumented ordinal 100 export is present, which reports Guide.</summary>
    public bool HasGuideSupport => _getStateEx is not null;
    public string LoadDiagnostic { get; private set; } = "";

    /// <summary>Runtime DLLs worth trying, newest first. 1.3 is the one Sekiro imports.</summary>
    public static readonly string[] Candidates =
        ["xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"];

    public XInputReader(string dllName)
    {
        DllName = dllName;
        _module = LoadLibrary(dllName);
        if (_module == IntPtr.Zero)
        {
            LoadDiagnostic = $"LoadLibrary failed, Win32 error {Marshal.GetLastWin32Error()}";
            return;
        }

        IntPtr p = GetProcAddress(_module, "XInputGetState");
        if (p == IntPtr.Zero)
        {
            LoadDiagnostic = $"XInputGetState not exported, Win32 error {Marshal.GetLastWin32Error()}";
            return;
        }
        _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(p);

        // XInputGetState masks the Guide button. Ordinal 100, XInputGetStateEx, does not.
        IntPtr pEx = GetProcAddress(_module, new IntPtr(100));
        if (pEx != IntPtr.Zero)
            _getStateEx = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(pEx);

        LoadDiagnostic = $"loaded, XInputGetStateEx {(HasGuideSupport ? "present" : "absent")}";
    }

    /// <summary>The first runtime version that loads, or null if none do.</summary>
    public static XInputReader? OpenFirstAvailable(out List<string> tried)
    {
        tried = [];
        foreach (var name in Candidates)
        {
            var r = new XInputReader(name);
            tried.Add($"{name}: {(r.Loaded ? r.LoadDiagnostic : r.LoadDiagnostic)}");
            if (r.Loaded) return r;
            r.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Packet number from the most recent <see cref="Read"/>. XInput increments this once
    /// per accepted state change, which makes it a non-repeating identity for a change.
    /// A button mask cannot serve that purpose because masks repeat constantly.
    /// </summary>
    public uint LastPacketNumber { get; private set; }

    /// <summary>
    /// Reads one slot. Returns null when nothing is connected there. When
    /// <paramref name="includeGuide"/> is set and the extended export exists, the Guide bit
    /// is reported too.
    /// </summary>
    public XInputReport? Read(int userIndex, bool includeGuide = true)
    {
        var fn = includeGuide && _getStateEx is not null ? _getStateEx : _getState;
        if (fn is null) return null;

        int rc = fn(userIndex, out XINPUT_STATE st);
        if (rc == ERROR_DEVICE_NOT_CONNECTED) return null;
        if (rc != ERROR_SUCCESS) return null;

        LastPacketNumber = st.dwPacketNumber;
        var g = st.Gamepad;
        return new XInputReport
        {
            Buttons = g.wButtons,
            LeftTrigger = g.bLeftTrigger,
            RightTrigger = g.bRightTrigger,
            ThumbLX = g.sThumbLX,
            ThumbLY = g.sThumbLY,
            ThumbRX = g.sThumbRX,
            ThumbRY = g.sThumbRY,
        };
    }

    /// <summary>Slots 0 to 3 that currently report a connected device.</summary>
    public List<int> ConnectedSlots()
    {
        var slots = new List<int>();
        for (int i = 0; i < 4; i++)
            if (Read(i) is not null) slots.Add(i);
        return slots;
    }

    public void Dispose()
    {
        _getState = null;
        _getStateEx = null;
        if (_module != IntPtr.Zero) { FreeLibrary(_module); _module = IntPtr.Zero; }
    }
}
