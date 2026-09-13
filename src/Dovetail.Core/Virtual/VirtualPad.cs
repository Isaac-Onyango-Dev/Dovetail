using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Dovetail.Core;

/// <summary>
/// One virtual Xbox 360 controller on the ViGEmBus driver.
///
/// This is the whole reason Dovetail exists. The physical pad speaks only raw HID, and the
/// target games poll XInput, so nothing bridges the two until a real XUSB device exists at
/// the driver level. ViGEmBus creates exactly that: the virtual pad enumerates as
/// VID_045E/PID_028E on the xusb22 service, which every XInput runtime version sees,
/// including the xinput1_3 that Sekiro imports (Findings Log 0.6).
/// </summary>
public sealed class VirtualPad : IDisposable
{
    private readonly IXbox360Controller _pad;
    private XInputReport _last;
    private bool _connected;
    private bool _everSubmitted;

    /// <summary>Rumble from the game, forwarded so the engine can drive the pad's motors.</summary>
    public event Action<byte, byte, byte>? FeedbackReceived;

    /// <summary>Reports submitted since connect. Evidence that the pipeline is live.</summary>
    public long SubmitCount { get; private set; }

    public VirtualPad(ViGEmClient client)
    {
        _pad = client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += (_, e) =>
            FeedbackReceived?.Invoke(e.LargeMotor, e.SmallMotor, e.LedNumber);
    }

    public void Connect()
    {
        if (_connected) return;
        _pad.Connect();
        _connected = true;
    }

    public void Disconnect()
    {
        if (!_connected) return;
        try { _pad.Disconnect(); } catch { /* target may already be gone */ }
        _connected = false;
    }

    /// <summary>
    /// XInput user index the driver assigned, or null while the driver has not reported one
    /// yet. This is the slot a game sees as player 1, 2 and so on.
    /// </summary>
    public int? UserIndex
    {
        get
        {
            try { return _pad.UserIndex; }
            catch { return null; }   // Xbox360UserIndexNotReportedException until the slot lands
        }
    }

    /// <summary>
    /// Pushes a report, but only when something actually changed. Submitting every poll
    /// would put 200 identical reports a second through the driver for no benefit.
    /// Returns true when a report went out.
    /// </summary>
    public bool Submit(XInputReport r)
    {
        if (!_connected) return false;
        if (_everSubmitted && _last.Equals(r)) return false;

        _pad.SetButtonsFull(r.Buttons);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, r.LeftTrigger);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, r.RightTrigger);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, r.ThumbLX);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, r.ThumbLY);
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, r.ThumbRX);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, r.ThumbRY);
        _pad.SubmitReport();

        _last = r;
        _everSubmitted = true;
        SubmitCount++;
        return true;
    }

    /// <summary>
    /// Forgets the last submitted report so the next <see cref="Submit"/> always goes out.
    /// Used after a calibration reload, where the mapped state may be identical but the
    /// scaling behind it has changed.
    /// </summary>
    public void InvalidateLastReport() => _everSubmitted = false;

    public void Dispose() => Disconnect();
}

/// <summary>
/// Owns the ViGEmBus client connection and reports on it clearly when the driver is missing
/// or the wrong version, because that is the single most common reason a tool like this
/// silently does nothing.
/// </summary>
public sealed class VirtualBus : IDisposable
{
    private ViGEmClient? _client;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "not initialised";

    public bool Open()
    {
        try
        {
            _client = new ViGEmClient();
            Available = true;
            Status = "ViGEmBus client connected";
            return true;
        }
        catch (Exception ex)
        {
            Available = false;
            Status = ex.GetType().Name switch
            {
                "VigemBusNotFoundException" =>
                    "ViGEmBus driver is not installed. Run the Dovetail dependency setup.",
                "VigemBusVersionMismatchException" =>
                    "ViGEmBus driver version does not match this client. Update the driver.",
                "VigemBusAccessFailedException" =>
                    "ViGEmBus driver is present but access was denied. Try running as administrator.",
                _ => $"{ex.GetType().Name}: {ex.Message}"
            };
            return false;
        }
    }

    public VirtualPad CreatePad()
    {
        if (_client is null) throw new InvalidOperationException("virtual bus is not open");
        return new VirtualPad(_client);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        Available = false;
        Status = "disposed";
    }
}
