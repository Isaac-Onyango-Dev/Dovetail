using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dovetail.App;

/// <summary>
/// Where every piece of the controller artwork is, in one place.
///
/// Two coordinate systems live here and they are deliberately different.
///
/// The exploded layers are placed in a fixed design space, 1833 x 1400 units, which is the
/// content box of the source render translated so its top-left corner is the origin. Layer
/// positions are absolute in that space, so the intro animation can move them without knowing
/// anything about window size; a Viewbox does the scaling.
///
/// The hotspots on the assembled render are fractions of the cropped diagram instead, so the
/// calibration window can draw the pad at whatever size the window gives it and still put a
/// highlight on the right button. They were read off the render at native resolution and
/// verified by drawing them back over the source image, not estimated.
/// </summary>
internal static class PadArtwork
{
    /// <summary>The design space the exploded layers are positioned in.</summary>
    internal const double SheetWidth = 1833;
    internal const double SheetHeight = 1400;

    /// <summary>Offset from source-image coordinates into sheet coordinates.</summary>
    private const double OriginX = 626;
    private const double OriginY = 36;

    /// <summary>One exploded part: where it starts, how big it is, and where it ends up.</summary>
    /// <param name="File">Resource file name.</param>
    /// <param name="X">Left edge in sheet space, exploded.</param>
    /// <param name="Y">Top edge in sheet space, exploded.</param>
    /// <param name="W">Width in sheet space.</param>
    /// <param name="H">Height in sheet space.</param>
    /// <param name="Group">Parts sharing a group name move as one rigid body.</param>
    internal sealed record Layer(string File, double X, double Y, double W, double H, string Group);

    /// <summary>
    /// The eight parts the segmentation recovered, back to front. Drawing order matters only
    /// at the end of the animation, where the top shell has to cover everything that has just
    /// collapsed underneath it; that is what makes the assembly read as closing.
    /// </summary>
    internal static readonly Layer[] Layers =
    [
        new("part08.png",  867 - OriginX, 1151 - OriginY, 1385,  225, "shell_bottom"),
        new("part06.png",  719 - OriginX, 1011 - OriginY,  211,  300, "trigger_left"),
        new("part04.png",  949 - OriginX,  976 - OriginY,  137,  141, "trigger_left"),
        new("part07.png", 2188 - OriginX, 1012 - OriginY,  211,  299, "trigger_right"),
        new("part05.png", 2032 - OriginX,  976 - OriginY,  137,  141, "trigger_right"),
        new("part02.png",  935 - OriginX,  492 - OriginY, 1347,  570, "board"),
        new("part03.png",  809 - OriginX,  706 - OriginY,  253,  242, "board"),
        new("part01.png",  686 - OriginX,   96 - OriginY, 1743,  636, "shell_top"),
    ];

    /// <summary>
    /// How far each group travels as the assembly closes, in sheet units.
    ///
    /// The top shell is the anchor and does not move; everything else converges on it. The
    /// triggers come up and inward from the bottom corners, which is where they sit on the
    /// real pad, and the board and bottom shell rise into the shell and disappear behind it.
    /// <c>Delay</c> staggers the starts so the parts arrive in assembly order rather than all
    /// at once, which is the difference between an animation and a transition.
    /// </summary>
    /// <param name="Vanish">
    /// True for the parts that end up inside the case. The shell is a U in this front-on view,
    /// open between the grips, and the board is taller than its solid area, so a part that only
    /// translates stays visible through the gap and the assembly never looks closed. Fading over
    /// the last of the travel is what reads as going inside.
    /// </param>
    internal sealed record Move(string Group, double Dx, double Dy, double Delay, bool Vanish = false);

    internal static readonly Move[] Moves =
    [
        new("trigger_left",   147, -903, 0.00),
        new("trigger_right", -150, -903, 0.00),
        new("shell_bottom",    -2, -763, 0.10, Vanish: true),
        new("board",           12, -347, 0.22, Vanish: true),
        new("shell_top",        0,    0, 0.00),
    ];

    // ------------------------------------------------------------------ assembled diagram

    /// <summary>
    /// A highlightable region on the assembled render, as fractions of the cropped image.
    /// Ellipses rather than rectangles because every control on this pad is round, and rotated
    /// because the render is a three-quarter view and the face plane is not axis-aligned.
    /// </summary>
    internal sealed record Hotspot(string Id, double Cx, double Cy, double Rx, double Ry);

    /// <summary>The face plane's tilt in the render, in degrees. Measured, not guessed.</summary>
    internal const double FaceRotation = -20;

    internal static readonly Hotspot[] Hotspots =
    [
        // Measured off the render key by key, not derived from the cluster centre.
        //
        // The first version put these at north, east, south and west of the middle of the
        // D-pad, which is where the arms of a cross would be. This pad's D-pad is four separate
        // chevron keys and the whole face plane is rotated, so the keys actually sit on the
        // diagonals: up is the upper-RIGHT key, left the upper-left, down the lower-left, right
        // the lower-right. Every ring landed in a gap between two keys and the operator had to
        // read the caption to know which one was meant.
        new("dpad_up",     0.4240, 0.0890, 0.0310, 0.0255),
        new("dpad_right",  0.4270, 0.1550, 0.0310, 0.0255),
        new("dpad_down",   0.3500, 0.1600, 0.0310, 0.0255),
        new("dpad_left",   0.3500, 0.0950, 0.0310, 0.0255),
        new("left_stick",  0.3934, 0.2655, 0.0709, 0.0710),
        new("right_stick", 0.5961, 0.4128, 0.0709, 0.0710),
        new("face_left",   0.7436, 0.3897, 0.0298, 0.0302),
        new("face_up",     0.8243, 0.3844, 0.0298, 0.0302),
        new("face_down",   0.7464, 0.4625, 0.0298, 0.0302),
        new("face_right",  0.8314, 0.4572, 0.0298, 0.0302),
        new("icon_back",   0.5735, 0.1643, 0.0198, 0.0160),
        new("select",      0.5494, 0.2123, 0.0213, 0.0177),
        new("home",        0.6089, 0.2602, 0.0241, 0.0213),
        new("start",       0.6755, 0.3028, 0.0213, 0.0177),
        new("icon_menu",   0.7436, 0.2708, 0.0227, 0.0177),
    ];

    /// <summary>
    /// Which hotspot a sweep step points at. Several steps share one: all four left-stick
    /// directions and the L3 click are the same piece of hardware, so they light the same ring.
    ///
    /// L1, L2, R1 and R2 map to nothing on purpose. The render is a three-quarter view from
    /// above and the rear edge is fully occluded, so there is no geometry to highlight. Those
    /// four get the callout panel instead, which is cut from the exploded view where both
    /// trigger assemblies are drawn as separate labelled parts.
    /// </summary>
    internal static string? HotspotFor(string stepId) => stepId switch
    {
        "face_up" or "face_right" or "face_down" or "face_left" => stepId,
        "dpad_up" or "dpad_right" or "dpad_down" or "dpad_left" => stepId,
        "select" or "start" or "home" or "icon_back" or "icon_menu" => stepId,
        "ls_left" or "ls_right" or "ls_up" or "ls_down" or "l3" => "left_stick",
        "rs_left" or "rs_right" or "rs_up" or "rs_down" or "r3" => "right_stick",
        _ => null,
    };

    /// <summary>
    /// Which way a direction points on the pad's face, as seen in this render.
    ///
    /// The face plane is rotated and foreshortened, so "up" on the hardware is up and to the
    /// right on screen and the four directions are not at right angles to each other in the
    /// picture. These vectors were derived from the measured D-pad key positions relative to
    /// the centre of the cluster, so an arrow drawn along one of them points where the hand
    /// actually has to push.
    /// </summary>
    internal static (double X, double Y)? DirectionOf(string stepId) => stepId switch
    {
        "ls_up" or "rs_up" or "dpad_up" => (0.777, -0.629),
        "ls_down" or "rs_down" or "dpad_down" => (-0.777, 0.629),
        "ls_left" or "rs_left" or "dpad_left" => (-0.849, -0.529),
        "ls_right" or "rs_right" or "dpad_right" => (0.849, 0.529),
        _ => null,
    };

    /// <summary>
    /// True for the steps that ask for a stick to be pushed rather than clicked. Without a
    /// direction marker these look identical to the L3 and R3 steps, which put the same ring on
    /// the same stick and mean something completely different.
    /// </summary>
    internal static bool IsStickPush(string stepId) =>
        stepId.StartsWith("ls_") || stepId.StartsWith("rs_");

    /// <summary>True for the two stick-click steps, which get a press-down marker instead.</summary>
    internal static bool IsStickClick(string stepId) => stepId is "l3" or "r3";

    /// <summary>Which callout a step needs when it has no hotspot on the diagram.</summary>
    internal static string? CalloutFor(string stepId) => stepId switch
    {
        "l1" or "l2" => "part06.png",
        "r1" or "r2" => "part07.png",
        _ => null,
    };

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Loads one artwork resource. Frozen so it can be shared across threads and so WPF stops
    /// tracking it for changes, which matters when eight of these are on a Canvas being
    /// animated at once.
    /// </summary>
    internal static BitmapImage Load(string file)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri($"pack://application:,,,/assets/intro/{file}", UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>True when the artwork is compiled in and loadable. Guards the intro animation.</summary>
    internal static bool Available()
    {
        try { _ = Load("part01.png"); return true; }
        catch { return false; }
    }
}
