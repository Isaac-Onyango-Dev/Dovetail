using System.Windows;

namespace Dovetail.App;

/// <summary>
/// Places a window on the primary monitor at a size that matches what WPF actually lays out.
///
/// This exists because of a real defect found in Stage 5 on this machine, which runs a
/// 1920x1080 primary monitor at 125% next to a 1920x1080 second monitor at 100%. With
/// WindowStartupLocation="CenterScreen" and a fixed Width and Height, WPF sized the native
/// frame using the primary monitor's 120 DPI and then the window was placed on the 96 DPI
/// monitor. WPF re-laid the content out at 96 DPI, so a 780x700 window painted 780x700
/// pixels of content inside a 975x875 frame: a quarter of the window was blank.
///
/// The fix is to position the window explicitly in device-independent units, which keeps it
/// on the monitor whose DPI WPF used, and to re-assert the size after the native handle
/// exists so the frame is recomputed for the DPI the window ends up on.
/// </summary>
internal static class WindowPlacement
{
    public static void CentreAndFit(Window window, double width, double height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = width;
        window.Height = height;

        // SourceInitialized fires once the HWND exists but before it is shown, which is the
        // first point at which a size change reaches the native frame. Loaded fires after
        // layout, and catches the case where the DPI context only settles on display.
        window.SourceInitialized += (_, _) => Apply(window, width, height);
        window.Loaded += (_, _) => Apply(window, width, height);
    }

    private static void Apply(Window window, double width, double height)
    {
        var area = SystemParameters.WorkArea;   // primary monitor, in DIPs

        // Never taller or wider than the space available: a 700 DIP window does not fit a
        // 1080p screen at 150%, and an off-screen title bar cannot be dragged back.
        double w = Math.Min(width, area.Width);
        double h = Math.Min(height, area.Height);

        if (Math.Abs(window.Width - w) > 0.5) window.Width = w;
        if (Math.Abs(window.Height - h) > 0.5) window.Height = h;

        window.Left = area.Left + Math.Max(0, (area.Width - w) / 2);
        window.Top = area.Top + Math.Max(0, (area.Height - h) / 2);
    }
}
