using System.IO;
using Path = System.IO.Path;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dovetail.Core;

namespace Dovetail.App;

/// <summary>
/// What the tray icon is saying right now. Section 5.6 gives each one a colour; the shape
/// never changes, so the icon stays recognisable as the same application.
/// </summary>
public enum TrayState
{
    /// <summary>At least one calibrated pad is bound to a virtual controller.</summary>
    Translating,
    /// <summary>Everything is healthy, nothing is plugged in.</summary>
    Idle,
    /// <summary>A pad is present but has no calibration, or the virtual bus is missing.</summary>
    Attention,
}

/// <summary>
/// The tray icon, its menu, and the four notification states from Section 7.2.
///
/// Notifications use NotifyIcon balloon tips, which Windows 10 renders as Action Center
/// toasts. The alternative, a WinUI toast, needs a packaged identity or a registered
/// AppUserModelID and a Start Menu shortcut before it will show anything at all, and it
/// fails silently when that is missing. A balloon tip works from an unpackaged executable,
/// supports the click-to-act behaviour Section 7.2 requires, and degrades to a visible
/// tooltip rather than to nothing.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly DovetailService _service;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _gameItem;
    private readonly ToolStripMenuItem _controllersRoot;

    /// <summary>What clicking the current balloon should do. Cleared when it is consumed.</summary>
    private Action? _balloonAction;

    public event Action? OpenSettingsRequested;
    public event Action? RunCalibrationRequested;
    public event Action<int>? CalibrateSlotRequested;
    public event Action? RepairDependenciesRequested;
    public event Action? ExitRequested;

    public TrayIcon(DovetailService service)
    {
        _service = service;

        var menu = new ContextMenuStrip { ShowImageMargin = false };

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _gameItem = new ToolStripMenuItem("No game profile active") { Enabled = false };
        _controllersRoot = new ToolStripMenuItem("Controllers");

        menu.Items.Add(_statusItem);
        menu.Items.Add(_gameItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_controllersRoot);
        menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Open Dovetail settings");
        settings.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(settings);

        var recal = new ToolStripMenuItem("Re-run calibration...");
        recal.Click += (_, _) => RunCalibrationRequested?.Invoke();
        menu.Items.Add(recal);

        var repair = new ToolStripMenuItem("Repair dependencies...");
        repair.Click += (_, _) => RepairDependenciesRequested?.Invoke();
        menu.Items.Add(repair);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit Dovetail");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(TrayState.Idle),
            Text = "Dovetail",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Section 7.1: settings are reachable by left-clicking the tray icon.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettingsRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var act = _balloonAction;
            _balloonAction = null;
            act?.Invoke();
        };

        Refresh();
    }

    // ---------------------------------------------------------------- the mark

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Built icons, one per state. Drawing is cheap but the handles are not.</summary>
    private static readonly Dictionary<TrayState, Icon> IconCache = [];

    /// <summary>
    /// Section 5.6, the Dovetail mark: one block split by a single angled cut. Drawn in code
    /// rather than shipped as an .ico, so there is no binary asset to go missing and the
    /// geometry is resolved at whatever size Windows asks for.
    ///
    /// The literal flared dovetail seam was drawn first and rejected. At notification-area
    /// sizes its three kinks collapse into a lightning bolt and the two halves stop reading
    /// as one block. A single angled cut survives the reduction; the flare does not.
    ///
    /// One colour, one transparent cut, no interior detail. That is what lets the same
    /// artwork sit on a dark taskbar and a light one, which two-tone marks cannot do.
    /// </summary>
    private static Icon BuildIcon(TrayState state)
    {
        if (IconCache.TryGetValue(state, out var cached)) return cached;

        Color ink = state switch
        {
            TrayState.Translating => Color.FromArgb(255, 0xD9, 0x48, 0x3B),  // Accent
            TrayState.Attention   => Color.FromArgb(255, 0xD8, 0xA6, 0x57),  // Warn
            _                     => Color.FromArgb(255, 0x8A, 0x90, 0x9C),  // dimmed TextDim
        };

        // Ask Windows what a small icon is here rather than assuming 32. This machine has
        // already produced a mixed-DPI defect once, in the window sizing.
        int size = SystemInformation.SmallIconSize.Width;
        if (size < 16 || size > 256) size = 32;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 32f, size / 32f);

            using var brush = new SolidBrush(ink);
            using var left = MarkHalf(leftOfSeam: true);
            using var right = MarkHalf(leftOfSeam: false);
            g.FillPath(brush, left);
            g.FillPath(brush, right);
        }

        // GetHicon hands back a handle this process owns. Icon.FromHandle does not take
        // ownership, so the handle has to be destroyed by hand or every rebuild leaks one.
        IntPtr h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            var owned = (Icon)temp.Clone();
            IconCache[state] = owned;
            return owned;
        }
        finally { DestroyIcon(h); }
    }

    /// <summary>
    /// One half of the mark, on a 32 x 32 grid. The seam runs from (16,0) to (16,13), cuts
    /// across to (10,17), then drops to (10,32); these vertices are that line offset by half
    /// the 2.4-unit gap, mitred at the corners. Outer corners are a 6-unit radius.
    /// </summary>
    private static GraphicsPath MarkHalf(bool leftOfSeam)
    {
        var p = new GraphicsPath();
        if (leftOfSeam)
        {
            p.AddLine(14.8f, 0f, 14.8f, 12.36f);
            p.AddLine(14.8f, 12.36f, 8.8f, 16.36f);
            p.AddLine(8.8f, 16.36f, 8.8f, 32f);
            p.AddLine(8.8f, 32f, 6f, 32f);
            p.AddArc(0f, 20f, 12f, 12f, 90f, 90f);
            p.AddLine(0f, 26f, 0f, 6f);
            p.AddArc(0f, 0f, 12f, 12f, 180f, 90f);
        }
        else
        {
            p.AddLine(17.2f, 0f, 17.2f, 13.64f);
            p.AddLine(17.2f, 13.64f, 11.2f, 17.64f);
            p.AddLine(11.2f, 17.64f, 11.2f, 32f);
            p.AddLine(11.2f, 32f, 26f, 32f);
            p.AddArc(20f, 20f, 12f, 12f, 90f, -90f);
            p.AddLine(32f, 26f, 32f, 6f);
            p.AddArc(20f, 0f, 12f, 12f, 0f, -90f);
        }
        p.CloseFigure();
        return p;
    }

    /// <summary>What the icon should be saying, from the service's current state.</summary>
    private TrayState CurrentState()
    {
        if (!_service.BusAvailable) return TrayState.Attention;
        if (_service.Channels.Any(c => c.Slot == 0)) return TrayState.Attention;
        return _service.Channels.Any(c => c.Bound && c.Slot > 0)
            ? TrayState.Translating
            : TrayState.Idle;
    }

    public void Refresh()
    {
        var live = _service.Channels.Where(c => c.Bound && c.Slot > 0).ToList();

        // Section 5.6: the icon carries state as colour, so the tray says what is happening
        // without the menu being opened. NotifyIcon.Text is the hover tooltip and Windows
        // truncates it hard, so it stays short and the detail lives in the menu below.
        _icon.Icon = BuildIcon(CurrentState());
        _icon.Text = !_service.BusAvailable
            ? "Dovetail - driver missing"
            : live.Count switch
            {
                0 => "Dovetail - no controller",
                1 => "Dovetail - 1 controller",
                _ => $"Dovetail - {live.Count} controllers",
            };

        _statusItem.Text = _service.BusAvailable
            ? live.Count switch
            {
                0 => "No controller connected",
                1 => $"1 controller connected: {live[0].DisplayName}",
                _ => $"{live.Count} controllers connected",
            }
            : $"Virtual bus unavailable: {_service.BusStatus}";

        _gameItem.Text = _service.ActiveGame is { } g
            ? $"Game profile: {g.Name}"
            : "No game profile active";

        _controllersRoot.DropDownItems.Clear();
        if (_service.Slots.CalibratedCount == 0)
        {
            _controllersRoot.DropDownItems.Add(
                new ToolStripMenuItem("No controllers set up yet") { Enabled = false });
            var setup = new ToolStripMenuItem("Set up a controller...");
            setup.Click += (_, _) => RunCalibrationRequested?.Invoke();
            _controllersRoot.DropDownItems.Add(setup);
        }
        else
        {
            foreach (var (slot, prof) in _service.Slots.Slots.OrderBy(k => k.Key))
            {
                bool connected = live.Any(c => c.Slot == slot);
                var item = new ToolStripMenuItem(
                    $"{prof.EffectiveName}  ({(connected ? "connected" : "not connected")})");

                var recal = new ToolStripMenuItem("Re-calibrate this controller...");
                int captured = slot;
                recal.Click += (_, _) => CalibrateSlotRequested?.Invoke(captured);
                item.DropDownItems.Add(recal);

                // Section 7.4: forget, behind a confirmation, because recalibrating means
                // redoing the whole checkpoint sweep.
                var forget = new ToolStripMenuItem("Forget this controller...");
                forget.Click += (_, _) =>
                {
                    var answer = MessageBox.Show(
                        $"Forget \"{prof.EffectiveName}\"?\n\n" +
                        "Its calibration will be deleted and its virtual controller released.\n" +
                        "If that controller connects again it will be treated as brand new and " +
                        "will have to be set up from scratch, which means redoing the full " +
                        "calibration sweep.",
                        "Dovetail - forget controller",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer == DialogResult.Yes) _service.ForgetSlot(captured);
                };
                item.DropDownItems.Add(forget);

                _controllersRoot.DropDownItems.Add(item);
            }
        }
    }

    // ---------------------------------------------------------------- notifications

    public void NotifyConnected(PadChannel ch)
    {
        if (!_service.Settings.ShowConnectNotifications) return;
        Show($"{ch.DisplayName} connected",
             $"Player {ch.Slot}. Ready to use.",
             ToolTipIcon.Info, null);
    }

    public void NotifyDisconnected(PadChannel ch)
    {
        if (!_service.Settings.ShowDisconnectNotifications) return;
        Show($"{ch.DisplayName} disconnected",
             "Its virtual controller has been released.",
             ToolTipIcon.Info, null);
    }

    /// <summary>
    /// Section 7.2's distinct fourth state. Deliberately a Warning rather than an Info so it
    /// does not look like the routine connect toast, and clicking it goes straight into
    /// calibration for that device.
    /// </summary>
    public void NotifyUnrecognised(PadChannel ch)
    {
        if (!_service.Settings.ShowUnrecognisedNotifications) return;
        int target = _service.Slots.NextFreeSlotNumber();
        Show("New controller detected - not set up in Dovetail",
             "Click to calibrate it. Until then it is left alone and no virtual controller " +
             "is created for it.",
             ToolTipIcon.Warning,
             () => CalibrateSlotRequested?.Invoke(target > 0 ? target : 1));
    }

    public void ShowMessage(string title, string body) =>
        Show(title, body, ToolTipIcon.Info, null);

    private void Show(string title, string body, ToolTipIcon icon, Action? onClick)
    {
        _balloonAction = onClick;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(onClick is null ? 5000 : 15000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in IconCache.Values) icon.Dispose();
        IconCache.Clear();
    }
}
