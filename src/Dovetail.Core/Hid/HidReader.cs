using System.Runtime.InteropServices;

namespace Dovetail.Core;

/// <summary>
/// Reads raw input reports from one HID collection with a timeout, using overlapped I/O so
/// a silent device never blocks the interactive wizards.
///
/// Two details matter for correctness and were wrong in the first version:
///
/// 1. The receive buffer is allocated once with unmanaged memory and reused. With
///    overlapped I/O the driver writes into the buffer after ReadFile has returned, so a
///    managed byte[] is unsafe: the P/Invoke marshaller pins it only for the duration of
///    the call, leaving the GC free to move it while the write is still outstanding.
///
/// 2. A read that times out stays pending against that same buffer. Allocating a fresh
///    buffer per call, as the first version did, meant the completed read had filled the
///    previous buffer while the caller was handed the new, untouched one - silently
///    returning zeroed reports whenever a read had previously timed out.
/// </summary>
public sealed class HidReader : IDisposable
{
    private IntPtr _handle = new(-1);
    private IntPtr _event = IntPtr.Zero;
    private IntPtr _overlapped = IntPtr.Zero;
    private IntPtr _buffer = IntPtr.Zero;
    private readonly int _reportLength;
    private bool _pending;
    private bool _disposed;

    public string DevicePath { get; }
    public int ReportLength => _reportLength;

    public HidReader(string devicePath, int inputReportByteLength)
    {
        DevicePath = devicePath;
        _reportLength = inputReportByteLength > 0 ? inputReportByteLength : 64;

        _handle = Native.CreateFile(devicePath,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        if (_handle == new IntPtr(-1))
            throw new InvalidOperationException(
                $"CreateFile failed for {devicePath}, Win32 error {Marshal.GetLastWin32Error()}");

        // A deeper queue stops fast stick movement from dropping reports between polls.
        Native.HidD_SetNumInputBuffers(_handle, 64);

        _event = Native.CreateEvent(IntPtr.Zero, true, false, null);
        if (_event == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateEvent failed, Win32 error {Marshal.GetLastWin32Error()}");

        _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.OVERLAPPED>());
        _buffer = Marshal.AllocHGlobal(_reportLength);
        ResetOverlapped();
    }

    private void ResetOverlapped()
    {
        var ov = new Native.OVERLAPPED { hEvent = _event };
        Marshal.StructureToPtr(ov, _overlapped, false);
    }

    /// <summary>
    /// Returns the next input report, or null if none arrived within the timeout. The
    /// report id byte is included at index 0 exactly as hidclass delivers it. A timed-out
    /// read stays pending and is picked up by the following call.
    /// </summary>
    public byte[]? Read(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_pending)
        {
            ResetOverlapped();
            for (int i = 0; i < _reportLength; i++) Marshal.WriteByte(_buffer, i, 0);

            if (Native.ReadFilePtr(_handle, _buffer, _reportLength, IntPtr.Zero, _overlapped))
            {
                Native.GetOverlappedResult(_handle, _overlapped, out int doneNow, false);
                return Copy(doneNow);
            }
            int err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_IO_PENDING)
                throw new InvalidOperationException($"ReadFile failed, Win32 error {err}");
            _pending = true;
        }

        uint wait = Native.WaitForSingleObject(_event, (uint)timeoutMs);
        if (wait == Native.WAIT_TIMEOUT)
            return null;                      // read stays pending against the same buffer

        _pending = false;
        if (!Native.GetOverlappedResult(_handle, _overlapped, out int transferred, false))
            throw new InvalidOperationException(
                $"GetOverlappedResult failed, Win32 error {Marshal.GetLastWin32Error()}");

        return Copy(transferred);
    }

    private byte[] Copy(int n)
    {
        if (n <= 0) return [];
        int len = Math.Min(n, _reportLength);
        var r = new byte[len];
        Marshal.Copy(_buffer, r, 0, len);
        return r;
    }

    /// <summary>Drains queued reports and returns the most recent one, or null if silent.</summary>
    public byte[]? Flush(int quietMs = 60)
    {
        byte[]? last = null;
        while (true)
        {
            var rpt = Read(quietMs);
            if (rpt is null) return last;
            last = rpt;
        }
    }

    /// <summary>
    /// Waits until the report stops changing for <paramref name="quietMs"/> and returns
    /// that settled report. Used to establish a true rest state before a timed step, so a
    /// step never starts its clock while the operator is still moving something.
    /// </summary>
    public byte[]? WaitForRest(int quietMs, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        byte[]? stable = null;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var rpt = Read(20);
            if (rpt is null || rpt.Length == 0) continue;

            if (stable is not null && rpt.AsSpan().SequenceEqual(stable))
            {
                if ((DateTime.UtcNow - stableSince).TotalMilliseconds >= quietMs)
                    return stable;
            }
            else
            {
                stable = rpt;
                stableSince = DateTime.UtcNow;
            }
        }
        return stable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pending && _handle != new IntPtr(-1))
        {
            Native.CancelIo(_handle);
            Native.WaitForSingleObject(_event, 200);
            _pending = false;
        }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        if (_overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_overlapped); _overlapped = IntPtr.Zero; }
        if (_event != IntPtr.Zero) { Native.CloseHandle(_event); _event = IntPtr.Zero; }
        if (_handle != new IntPtr(-1)) { Native.CloseHandle(_handle); _handle = new IntPtr(-1); }
    }
}
