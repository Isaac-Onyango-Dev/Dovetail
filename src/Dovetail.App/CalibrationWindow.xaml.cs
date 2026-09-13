using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

// WinForms comes in for the tray icon, so these names are ambiguous project-wide.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

using Dovetail.Core;

namespace Dovetail.App;

/// <summary>
/// The controller check, styled after a console's own controller setup screen: a picture of the
/// pad, one named input at a time, the right part of the picture lit up, and nothing on screen
/// to click to move on. It advances when the pad says so.
///
/// This is a front end, not a reimplementation. Every press is judged by
/// <see cref="InputSweep"/>, which is the Stage 1 detection lifted into Dovetail.Core unchanged:
/// it watches the raw report bytes against a sampled rest state, refuses a press that an earlier
/// step already claimed, and advances only once the input has been released and settled. The
/// console wizard drives the same object. What is new here is the diagram, the highlight and
/// the Skip control.
///
/// Stage 2, the stick and trigger measurement, is deliberately not moved into this window. That
/// wizard produced every calibration in this project and has been through five rounds of defect
/// fixes against real hardware. This screen hands over to it once the check is done.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly InputSweep _sweep = new();

    /// <summary>Cancels the whole run, when the window closes.</summary>
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>
    /// Cancels just the step in progress. Skip needs this: <see cref="InputSweep.CaptureStep"/>
    /// blocks on the pad until it sees something or times out, so there is no way to leave a
    /// step early other than to cancel the wait. A fresh one is made per step, which is why
    /// this is not simply the run-level token.
    /// </summary>
    private CancellationTokenSource? _step;

    /// <summary>
    /// Results keyed by step id rather than a flat list, because a re-test has to replace one
    /// entry in place. A list plus an append would show the same input twice in the summary,
    /// once with the result the operator has just corrected.
    /// </summary>
    private readonly Dictionary<string, InputSweep.Capture> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Script order, so the summary reads in the order the inputs were asked for.</summary>
    private IEnumerable<InputSweep.Capture> Results =>
        InputSweep.Script.Select(s => _results.GetValueOrDefault(s.Id)).OfType<InputSweep.Capture>();

    private readonly Dictionary<string, ImageSource> _calloutCache = [];

    /// <summary>The slot's saved profile, when there is one. Used to pin the port silently.</summary>
    private readonly CalibrationProfile? _profile;

    /// <summary>The steps this run covers. The whole script, or a --only subset, or one re-test.</summary>
    private InputSweep.Step[] _steps;

    private Task? _run;
    private int _index = -1;
    private volatile bool _skipRequested;
    private bool _closing;
    private bool _opened;

    /// <summary>How long one input waits before the check records it as not seen and moves on.</summary>
    private const int StepTimeoutSeconds = 30;

    /// <summary>Raised when the check finished and the operator asked to go on to measurement.</summary>
    public event Action? ContinueToMeasurement;

    /// <summary>One row of the re-test picker.</summary>
    public sealed record RetestRow(string Id, string Label);

    /// <param name="slot">Player slot being checked, for the heading.</param>
    /// <param name="profile">
    /// The slot's saved calibration, when it has one. Its recorded report id pins the receiver
    /// port without asking the operator for anything, which is what the console wizard does on
    /// a re-run. Null for a brand-new controller, which has to be identified by a press.
    /// </param>
    /// <param name="only">
    /// Comma-separated step ids to run instead of the whole script, the same selection the
    /// console sweep's --only takes. Null runs all twenty-seven.
    /// </param>
    public CalibrationWindow(int slot, CalibrationProfile? profile = null, string? only = null)
    {
        InitializeComponent();
        // Sized here rather than in XAML so it is clamped to the work area and centred on the
        // monitor whose DPI WPF laid it out for. The summary grew a re-test row, and a fixed
        // height that fitted before now pushes the buttons off the bottom edge on a 1080p
        // screen at 125%. Same defect WindowPlacement was written for in Stage 5.
        WindowPlacement.CentreAndFit(this, 880, 800);

        _profile = profile;
        _steps = InputSweep.Select(only);
        if (_steps.Length == 0) _steps = InputSweep.Script;   // an --only that matched nothing

        SlotLine.Text = _steps.Length == InputSweep.Script.Length
            ? $"Player {slot} - controller check"
            : $"Player {slot} - re-testing {_steps.Length} of {InputSweep.Script.Length}";
        Pad.Source = PadArtwork.Load("pad-hero.jpg");

        // The hotspots are fractions of the image, so they cannot become pixels until the
        // image has actually been laid out. Re-place on every size change.
        DiagramHost.SizeChanged += (_, _) => PlaceHighlight();

        Loaded += (_, _) => _run = Task.Run(() => RunSweep(_steps));
        Closing += (_, _) =>
        {
            _closing = true;
            _cancel.Cancel();
            _step?.Cancel();
            // Dispose the readers only once the worker has actually left them, or it can be
            // reading from a handle that has just been closed.
            var run = _run;
            if (run is null) _sweep.Dispose();
            else run.ContinueWith(_ => _sweep.Dispose(), TaskScheduler.Default);
        };
    }

    // ------------------------------------------------------------------ the run

    private void RunSweep(InputSweep.Step[] steps)
    {
        // A re-test reuses the open, already-pinned readers. Re-opening would drop the pin and
        // re-ask for the identifying press, which is the opposite of what "just this one" is for.
        if (_opened) { RunScript(steps); return; }

        var failures = _sweep.Open(DovetailEngine.DefaultVid, DovetailEngine.DefaultPid);

        if (_sweep.Collections.Count == 0)
        {
            // Distinguish "not plugged in" from "hidden from us". They produce the same empty
            // list, and the second one is the confusing case: the pad is connected, Windows can
            // see it, and every open comes back as access denied because HidHide is cloaking it
            // and this installation is not on its allow list. Reporting that as "no controller
            // found" sends the operator to check a cable that is already fine.
            if (HidHideAccess.BlockingUs())
            {
                Report("The controller is hidden from Dovetail",
                       "HidHide is hiding this pad from everything except the programs on its list, " +
                       "and this copy of Dovetail is not on it yet. Use Repair dependencies from " +
                       "the tray menu, then start the check again.",
                       "hidhide: cloaking, " + HidHideAccess.MissingRegistrations().Count + " of ours not allowed");
                return;
            }

            Report("No controller found",
                   "Connect the controller, close this window, and start the check again.",
                   failures.Count > 0 ? string.Join("; ", failures) : "");
            return;
        }

        Post(() =>
        {
            Status.Text = $"open: {string.Join(", ", _sweep.Collections.Select(c => c.CollectionTag))}";
            Prompt.Text = "Reading the controller";
            SkipBtn.IsEnabled = false;
            Hint.Text = "A moment, while the pad is checked for signs of life.";
        });
        _sweep.SampleRest(1500);

        if (_sweep.LiveCollections.Count == 0)
        {
            Report("The pad is not sending anything",
                   "Not even an idle report is arriving. Check the receiver is plugged in, then " +
                   "close this window and start the check again.", "");
            return;
        }

        // ---- pin the receiver port before anything is measured --------------------
        //
        // This is the step the graphical check was missing, and its absence was not visible on
        // screen. Stage 1 finding 1.8: the adapter exposes two joystick collections from one
        // endpoint and both stream idle reports whether or not a pad is on that port. A check
        // reading every collection accepts a press from either pad, so with two controllers
        // connected, player 2 pressing anything satisfies player 1's step and is recorded into
        // player 1's profile under the right name from the wrong hardware. The console wizard
        // has pinned the port since Stage 2; this is the same rule, from the same object.
        //
        // Pinning also subsumes the wake gate. A receiver with no pad linked still streams, so
        // only a change proves a controller is there, and the press that proves it is the same
        // press that says which port it is on.
        if (_profile is { Device.ReportId: > 0 } saved &&
            _sweep.PinByReportId(saved.Device.ReportId) is { } byId)
        {
            Post(() => Status.Text = $"port {byId.CollectionTag}, {byId.Reason}");
        }
        else
        {
            Post(() =>
            {
                Prompt.Text = "Press any button to begin";
                SetDualLabel("");
                Hint.Text = _sweep.Collections.Count > 1
                    ? "This wakes the controller and shows which adapter port it is on. Both " +
                      "ports look identical until something moves. Nothing is recorded yet."
                    : "This wakes the controller and proves it is linked to the receiver. " +
                      "Nothing is recorded yet.";
                Status.Text = "waiting for the pad";
                // Nothing to skip yet. Leaving it live lets the gate report "skipped" for a
                // step that has not started.
                SkipBtn.IsEnabled = false;
            });

            var choice = _sweep.PinByActivity(120000, _cancel.Token);
            if (_cancel.IsCancellationRequested) return;
            if (choice is null)
            {
                Report("The controller is not responding",
                       "The receiver is alive and sending data, but the pad itself is silent, so it " +
                       "is probably asleep or unpaired. Wake it or re-pair it, then start the check " +
                       "again.",
                       "receiver streaming, no pad activity in 120s");
                return;
            }
            Post(() => Status.Text = $"port {choice.CollectionTag}, {choice.Reason}");
        }

        // Now take the baseline, from a pad that is demonstrably awake and back at rest, on the
        // one collection this run will read from.
        Post(() =>
        {
            Prompt.Text = "Hands off the controller";
            SetDualLabel("");
            SkipBtn.IsEnabled = false;
            Hint.Text = "Measuring what the pad looks like when nothing is being pressed.";
        });
        // Settle with rebase rather than SampleRest. SampleRest keeps whatever report happened
        // to arrive last in its window, so a hand still on the pad during those two seconds
        // becomes the definition of "at rest" and every step afterwards detects instantly.
        // A rebasing settle only adopts a value that held still for 600 ms.
        _sweep.Settle(600, 8000, rebase: true);

        _opened = true;
        RunScript(steps);
    }

    /// <summary>
    /// Walks a list of steps. Separated from the open-and-pin phase above so a re-test can run
    /// again over the same readers without re-identifying the port.
    /// </summary>
    private void RunScript(InputSweep.Step[] steps)
    {
        var events = new InputSweep.SweepEvents
        {
            WrongInput = owner => Post(() =>
                SetNudge($"That is {Pretty(owner)}, which is already recorded. Still waiting for this one.")),
        };

        for (int i = 0; i < steps.Length && !_cancel.IsCancellationRequested; i++)
        {
            var step = steps[i];
            _index = Array.IndexOf(InputSweep.Script, step);
            _skipRequested = false;

            // A step being re-tested still owns the bits it claimed last time, so its own press
            // would be refused as "already recorded" and the re-test could never pass. Release
            // this step's claim only; every other step keeps its protection.
            _sweep.Unclaim(step.Id);

            _step?.Dispose();
            _step = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);

            Post(() => ShowStep(i, steps.Length, step));

            // Settle first, so a button still held from the previous step cannot instantly
            // satisfy this one. This is the fix that made the original sweep work at all.
            //
            // rebase: whatever the pad is resting at now becomes this step's baseline. A pad
            // whose idle report drifts, or which woke up after the first sample, would otherwise
            // make every step detect the moment it opens.
            _sweep.Settle(rebase: true);

            var cap = _sweep.CaptureStep(step, StepTimeoutSeconds, events, _step.Token);

            if (_cancel.IsCancellationRequested) return;

            if (_skipRequested && !cap.Detected)
            {
                cap = new InputSweep.Capture
                {
                    Id = step.Id,
                    Prompt = step.Prompt,
                    Kind = step.Kind,
                    Skipped = true,
                    Note = "skipped by the operator",
                };
            }

            _results[cap.Id] = cap;          // replaces, so a re-test corrects rather than duplicates
            if (cap.Detected) _sweep.Claim(cap);

            Post(() => FlashResult(cap));

            // Long enough to read the green ring as an acknowledgement, short enough not to
            // feel like waiting. At 280 ms the confirmation was gone before the eye landed on
            // it, and the next prompt was already up.
            if (cap.Detected) Thread.Sleep(420);
        }

        if (!_cancel.IsCancellationRequested) Post(Summarise);
    }

    // ------------------------------------------------------------------ presentation

    private void ShowStep(int i, int total, InputSweep.Step step)
    {
        Counter.Text = $"{i + 1} / {total}";
        Prompt.Text = step.Short;
        // Both printed names, when the control has two. The prompt above stays positional; this
        // says which button that position is on the pad in the operator's hands.
        SetDualLabel(step.DualLabel);
        // The gloss wins when there is one. Naming a control by position and then saying what it
        // is printed as on an Xbox or PlayStation pad is what stops somebody pressing whatever
        // looks closest, which would record one input under another's name.
        Hint.Text = step.Alt.Length > 0
            ? step.Alt
            : step.Kind switch
            {
                "axis" => "Push it all the way over, then let it centre.",
                "trigger" => "Press it all the way down, then let go.",
                "hat" => "One direction only.",
                _ => "Press it once, then let go.",
            };
        SetNudge("");
        // The port stays on screen beside the step. Pinning is otherwise invisible, and "which
        // pad is this actually reading" is the first question when a two-pad setup misbehaves.
        Status.Text = _sweep.Pinned is { } pin ? $"{pin.CollectionTag}  {step.Id}" : step.Id;
        ProgressFill.Width = Track.ActualWidth * i / total;

        RetestPanel.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Visible;
        SkipBtn.IsEnabled = true;
        DoneBtn.Visibility = Visibility.Collapsed;
        PlaceHighlight();
    }

    private void SetDualLabel(string label)
    {
        DualLabelText.Text = label;
        DualLabelChip.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the ring over the part of the pad the current step is asking for, or opens the
    /// callout when the render cannot show it. The ring is an ellipse rotated onto the face
    /// plane, because this is a three-quarter view and an axis-aligned circle sits visibly off
    /// the button.
    /// </summary>
    private void PlaceHighlight()
    {
        Overlay.Children.Clear();
        Callout.Visibility = Visibility.Collapsed;

        if (_index < 0 || _index >= InputSweep.Script.Length) return;
        var step = InputSweep.Script[_index];

        if (PadArtwork.CalloutFor(step.Id) is { } calloutFile)
        {
            CalloutTitle.Text = step.Id.StartsWith('l') ? "Left rear edge" : "Right rear edge";
            if (!_calloutCache.TryGetValue(calloutFile, out var src))
                _calloutCache[calloutFile] = src = PadArtwork.Load(calloutFile);
            CalloutImage.Source = src;
            Callout.Visibility = Visibility.Visible;
            return;
        }

        string? id = PadArtwork.HotspotFor(step.Id);
        if (id is null) return;

        var spot = PadArtwork.Hotspots.FirstOrDefault(h => h.Id == id);
        if (spot is null) return;

        var (ox, oy, iw, ih) = ImageBox();
        if (iw <= 0 || ih <= 0) return;

        double cx = ox + spot.Cx * iw;
        double cy = oy + spot.Cy * ih;
        double rx = spot.Rx * iw;
        double ry = spot.Ry * ih;

        // A soft disc under a hard ring. The disc alone washes out against the pad's own
        // specular highlights; the ring alone is easy to miss at a glance on a busy render.
        var glow = new Ellipse
        {
            Width = rx * 2.9,
            Height = ry * 2.9,
            Fill = new RadialGradientBrush(
                Color.FromArgb(125, 0xD9, 0x48, 0x3B),
                Color.FromArgb(0, 0xD9, 0x48, 0x3B)),
            IsHitTestVisible = false,
        };
        Place(glow, cx, cy);

        var ring = new Ellipse
        {
            Width = rx * 2,
            Height = ry * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x3B)),
            StrokeThickness = Math.Max(2, rx * 0.14),
            Fill = new SolidColorBrush(Color.FromArgb(55, 0xD9, 0x48, 0x3B)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(PadArtwork.FaceRotation),
            IsHitTestVisible = false,
        };
        Place(ring, cx, cy);

        Overlay.Children.Add(glow);
        Overlay.Children.Add(ring);

        // A stick that has to be pushed and a stick that has to be clicked get the same ring in
        // the same place, which is ambiguous the moment the run reaches L3. Mark which one this
        // is: an arrow along the face plane for a push, a pair of closing chevrons for a click.
        if (PadArtwork.IsStickPush(step.Id) && PadArtwork.DirectionOf(step.Id) is { } dir)
            Overlay.Children.Add(DirectionArrow(cx, cy, rx, ry, dir));
        else if (PadArtwork.IsStickClick(step.Id))
            Overlay.Children.Add(PressMarker(cx, cy, rx, ry));

        // A slow pulse, so the eye finds it without the screen feeling urgent.
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1.0, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>
    /// An arrow leaving the stick along the direction the operator has to push. It starts at the
    /// edge of the ring rather than at the centre, so it does not sit on top of the stick itself,
    /// and it is drawn with a pale outline underneath because the render behind it is a light
    /// grey stick cap on a dark shell and a single-colour stroke disappears over one of them.
    /// </summary>
    private static Canvas DirectionArrow(double cx, double cy, double rx, double ry,
                                         (double X, double Y) dir)
    {
        double len = Math.Max(rx, ry) * 2.0;
        double head = Math.Max(8, len * 0.42);

        double sx = cx + dir.X * rx * 1.05;
        double sy = cy + dir.Y * ry * 1.05;
        double ex = cx + dir.X * (rx * 1.05 + len);
        double ey = cy + dir.Y * (ry * 1.05 + len);

        // perpendicular, for the arrowhead's shoulders
        double px = -dir.Y, py = dir.X;

        var geom = new StreamGeometry();
        using (var c = geom.Open())
        {
            c.BeginFigure(new Point(sx, sy), false, false);
            c.LineTo(new Point(ex, ey), true, true);
            c.BeginFigure(new Point(ex, ey), true, true);
            c.LineTo(new Point(ex - dir.X * head + px * head * 0.5,
                               ey - dir.Y * head + py * head * 0.5), true, true);
            c.LineTo(new Point(ex - dir.X * head - px * head * 0.5,
                               ey - dir.Y * head - py * head * 0.5), true, true);
        }
        geom.Freeze();

        var host = new Canvas { IsHitTestVisible = false };
        host.Children.Add(new Path
        {
            Data = geom,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            StrokeThickness = Math.Max(7, len * 0.30),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
        });
        host.Children.Add(new Path
        {
            Data = geom,
            Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x3B)),
            StrokeThickness = Math.Max(3, len * 0.16),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x3B)),
        });
        return host;
    }

    /// <summary>
    /// Two chevrons closing on the stick, for the click-it-in steps. Deliberately not an arrow:
    /// the gesture is downward, into the pad, which has no direction on a flat picture, so it is
    /// shown as pressure from both sides instead.
    /// </summary>
    private static Canvas PressMarker(double cx, double cy, double rx, double ry)
    {
        var host = new Canvas { IsHitTestVisible = false };
        double r = Math.Max(rx, ry);

        foreach (int sign in new[] { -1, 1 })
        {
            var geom = new StreamGeometry();
            using (var c = geom.Open())
            {
                double tip = cx + sign * r * 1.15;
                double back = cx + sign * r * 1.75;
                c.BeginFigure(new Point(back, cy - r * 0.55), false, false);
                c.LineTo(new Point(tip, cy), true, true);
                c.LineTo(new Point(back, cy + r * 0.55), true, true);
            }
            geom.Freeze();

            host.Children.Add(new Path
            {
                Data = geom,
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                StrokeThickness = Math.Max(7, r * 0.34),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
            host.Children.Add(new Path
            {
                Data = geom,
                Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x3B)),
                StrokeThickness = Math.Max(3, r * 0.18),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
        return host;
    }

    private static void Place(FrameworkElement el, double cx, double cy)
    {
        Canvas.SetLeft(el, cx - el.Width / 2);
        Canvas.SetTop(el, cy - el.Height / 2);
    }

    /// <summary>
    /// Where the pad image actually sits, in the overlay canvas's own coordinates.
    ///
    /// The offset has to be asked for rather than calculated. An Image with Stretch=Uniform
    /// returns its letterboxed content size from ArrangeOverride, so ActualWidth is the width
    /// of the picture and not of the slot it was given, and the element is then centred inside
    /// that slot by its alignment. Deriving the offset from ActualWidth therefore yields zero,
    /// and every highlight lands exactly one letterbox margin to the left of the button it is
    /// supposed to be pointing at. TranslatePoint asks the layout system where the image really
    /// ended up, which is correct whatever the window is doing.
    ///
    /// The uniform fit is still applied afterwards, because it costs nothing and keeps this
    /// right if the image is ever given a fixed size or a different Stretch.
    /// </summary>
    private (double X, double Y, double W, double H) ImageBox()
    {
        if (Pad.Source is not { } src || Pad.ActualWidth <= 0 || src.Height <= 0) return (0, 0, 0, 0);

        Point origin;
        try { origin = Pad.TranslatePoint(new Point(0, 0), Overlay); }
        catch { return (0, 0, 0, 0); }

        double hostW = Pad.ActualWidth, hostH = Pad.ActualHeight;
        double ar = src.Width / src.Height;
        double w = hostW, h = hostW / ar;
        if (h > hostH) { h = hostH; w = hostH * ar; }

        return (origin.X + (hostW - w) / 2, origin.Y + (hostH - h) / 2, w, h);
    }

    private void FlashResult(InputSweep.Capture cap)
    {
        if (cap.Skipped) { Status.Text = $"{cap.Id}  skipped"; return; }

        if (!cap.Detected)
        {
            Status.Text = $"{cap.Id}  nothing seen in {StepTimeoutSeconds}s";
            return;
        }

        var first = cap.Changes.Count > 0 ? cap.Changes[0] : null;
        Status.Text = first is null
            ? $"{cap.Id}  ok"
            : $"{cap.Id}  byte {first.ByteIndex} " +
              (first.LooksAnalog
                  ? $"range {first.MinObserved}..{first.MaxObserved}"
                  : $"bit {string.Join(",", first.BitsSet.Concat(first.BitsCleared))}");

        // Turn the whole marker green where it is, rather than moving it. Seeing the thing you
        // just pressed acknowledge the press is the point of the highlight. The direction arrow
        // and the press chevrons are Paths inside a Canvas, so recolouring only the ellipses
        // would leave a red arrow attached to a green ring.
        var good = new SolidColorBrush(Color.FromRgb(0x6F, 0xA3, 0x6B));
        var goodFill = new SolidColorBrush(Color.FromArgb(85, 0x6F, 0xA3, 0x6B));

        foreach (var el in Overlay.Children.OfType<Ellipse>())
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.Stroke = good;
            el.Fill = goodFill;
        }

        foreach (var marker in Overlay.Children.OfType<Canvas>())
            foreach (var path in marker.Children.OfType<Path>())
            {
                // the pale outline underneath stays white; only the coloured pass changes
                if (path.Stroke is SolidColorBrush { Color.A: 220, Color.R: 255 }) continue;
                path.Stroke = good;
                if (path.Fill is not null) path.Fill = good;
            }
    }

    private void Summarise()
    {
        Overlay.Children.Clear();
        Callout.Visibility = Visibility.Collapsed;
        Counter.Text = "";
        ProgressFill.Width = Track.ActualWidth;
        _index = -1;

        var all = Results.ToList();
        int ok = all.Count(r => r.Detected);
        int skipped = all.Count(r => r.Skipped);
        int missed = all.Count - ok - skipped;

        Prompt.Text = missed == 0 && skipped == 0
            ? "Every input registered"
            : $"{ok} of {all.Count} inputs registered";
        SetDualLabel("");

        Hint.Text = (missed, skipped) switch
        {
            (0, 0) => "Next comes the measuring: resting position, how far the sticks travel, and the triggers.",
            (0, _) => $"{skipped} skipped. Next comes the measuring: rest, stick travel and triggers.",
            (_, 0) => $"Not seen: {Missing()}. Re-test them below, or carry on and come back to it.",
            _ => $"Not seen: {Missing()}. {skipped} skipped. Re-test below, or carry on and come back to it.",
        };

        SetNudge("");
        Status.Text = _sweep.Pinned is { } pin ? $"port {pin.CollectionTag}" : "";
        SkipBtn.Visibility = Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Visible;
        DoneBtn.Content = "Measure sticks and triggers";
        CancelBtn.Content = "Close";

        BuildRetestList();
    }

    /// <summary>
    /// Fills the re-test picker with every input and how it went, so one press can be corrected
    /// without sitting through the other twenty-six. Inputs that were not seen sort to the top,
    /// because those are what somebody reaching for this control is almost always after.
    /// </summary>
    private void BuildRetestList()
    {
        string? previous = RetestPick.SelectedValue as string;

        var rows = InputSweep.Script.Select(s =>
        {
            var cap = _results.GetValueOrDefault(s.Id);
            string state = cap is null ? "not run"
                : cap.Skipped ? "skipped"
                : cap.Detected ? "ok"
                : "not seen";
            string dual = s.DualLabel.Length > 0 ? $"  [{s.DualLabel}]" : "";
            return (Row: new RetestRow(s.Id, $"{Pretty(s.Id)}{dual}  -  {state}"),
                    Rank: state switch { "not seen" => 0, "skipped" => 1, "not run" => 2, _ => 3 });
        })
        .OrderBy(x => x.Rank)
        .Select(x => x.Row)
        .ToList();

        RetestPick.ItemsSource = rows;
        RetestPick.SelectedValue = previous is not null && rows.Any(r => r.Id == previous)
            ? previous
            : rows.FirstOrDefault()?.Id;

        RetestMissedBtn.IsEnabled = MissingIds().Length > 0;
        RetestPanel.Visibility = Visibility.Visible;
    }

    private string[] MissingIds() =>
        Results.Where(r => !r.Detected && !r.Skipped).Select(r => r.Id).ToArray();

    private string Missing() =>
        string.Join(", ", MissingIds().Select(Pretty));

    /// <summary>
    /// Starts another pass over just these steps. The window keeps its open, pinned readers and
    /// every result it already has; only the named steps are asked for again.
    /// </summary>
    private void Retest(InputSweep.Step[] steps)
    {
        if (steps.Length == 0 || _run is { IsCompleted: false }) return;

        RetestPanel.Visibility = Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Visible;
        SkipBtn.IsEnabled = true;
        CancelBtn.Content = "Cancel";
        SetNudge("");

        _steps = steps;
        _run = Task.Run(() => RunSweep(steps));
    }

    /// <summary>The screen phrasing for a step, with its verb stripped, for use mid-sentence.</summary>
    private static string Pretty(string id)
    {
        var step = InputSweep.Script.FirstOrDefault(s => s.Id == id);
        if (step is null) return id;
        return step.Short
            .Replace("Press the ", "the ").Replace("Press ", "")
            .Replace("Push the ", "the ").Replace("Click the ", "the ")
            .Replace(" all the way", "");
    }

    private void Report(string title, string detail, string status) => Post(() =>
    {
        Prompt.Text = title;
        SetDualLabel("");
        Hint.Text = detail;
        Status.Text = status;
        Counter.Text = "";
        Overlay.Children.Clear();
        SkipBtn.Visibility = Visibility.Collapsed;
        // Nothing was opened or pinned, so there is nothing to re-test against.
        RetestPanel.Visibility = Visibility.Collapsed;
        CancelBtn.Content = "Close";
    });

    private void SetNudge(string text)
    {
        NudgeText.Text = text;
        NudgeText.Opacity = text.Length > 0 ? 1 : 0;
    }

    private void Post(Action a)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(a);
    }

    // ------------------------------------------------------------------ controls

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _skipRequested = true;
        SkipBtn.IsEnabled = false;
        Status.Text = "skipped";
        _step?.Cancel();
    }

    private void RetestOne_Click(object sender, RoutedEventArgs e)
    {
        if (RetestPick.SelectedValue is not string id) return;
        Retest(InputSweep.Select(id));
    }

    private void RetestMissed_Click(object sender, RoutedEventArgs e) =>
        Retest(InputSweep.Select(string.Join(',', MissingIds())));

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        ContinueToMeasurement?.Invoke();
        Close();
    }
}
