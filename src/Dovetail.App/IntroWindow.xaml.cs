using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

// WinForms comes in for the tray icon, so these names are ambiguous project-wide.
using Image = System.Windows.Controls.Image;

namespace Dovetail.App;

/// <summary>
/// The first-launch intro: the exploded controller collapses into an assembled one, then hands
/// over to the first-run setup window. It plays once, on the very first launch, and never on a
/// tray-mode start. <see cref="DovetailSettings.IntroPlayed"/> is what makes that true, and it is
/// written before the animation starts rather than after, so a crash or a kill mid-animation
/// cannot turn it into something that plays every time.
///
/// The two source images are in different projections. The exploded view is drawn front-on and
/// the assembled render is a three-quarter view, rotated roughly twenty degrees and tilted, so
/// no part occupies a corresponding position in both and a straight tween between them reads as
/// a glitch. The cut used here is the one assembly animations actually use: collapse the parts
/// in the exploded view's own projection, land on the closed shell, then carry that into the
/// assembled render with a short cross-dissolve plus a small rotate and scale.
/// </summary>
public partial class IntroWindow : Window
{
    /// <summary>Everything is timed off these, so the whole sequence can be retimed in one place.</summary>
    private static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(0.30);
    private static readonly TimeSpan Collapse = TimeSpan.FromSeconds(1.15);
    private static readonly TimeSpan Dissolve = TimeSpan.FromSeconds(0.55);
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(0.70);

    private readonly Dictionary<string, TranslateTransform> _groups = [];
    private readonly Dictionary<string, List<Image>> _groupImages = [];
    private bool _finished;

    /// <summary>Raised once, when the animation has finished or been skipped.</summary>
    public event Action? Finished;

    public IntroWindow()
    {
        InitializeComponent();
        BuildSheet();
        Hero.Source = PadArtwork.Load("pad-hero.jpg");

        // Any key or click ends it. An intro nobody can skip is a nuisance the second time
        // they see it, and this one is reachable again from the settings window.
        PreviewKeyDown += (_, _) => Finish();
        PreviewMouseDown += (_, _) => Finish();

        Loaded += (_, _) => Play();
    }

    /// <summary>
    /// Lays the exploded parts onto the sheet at their source positions, and gives every group
    /// one shared transform so the parts in a group move as a rigid body.
    /// </summary>
    private void BuildSheet()
    {
        foreach (var layer in PadArtwork.Layers)
        {
            if (!_groups.TryGetValue(layer.Group, out var xf))
            {
                xf = new TranslateTransform(0, 0);
                _groups[layer.Group] = xf;
            }

            var img = new Image
            {
                Source = PadArtwork.Load(layer.File),
                Width = layer.W,
                Height = layer.H,
                Stretch = Stretch.Fill,
                RenderTransform = xf,
            };
            Canvas.SetLeft(img, layer.X);
            Canvas.SetTop(img, layer.Y);
            Sheet.Children.Add(img);

            if (!_groupImages.TryGetValue(layer.Group, out var list))
                _groupImages[layer.Group] = list = [];
            list.Add(img);
        }
    }

    private void Play()
    {
        if (SystemParameters.ClientAreaAnimation == false || IsReducedMotion())
        {
            // Honour the operator's own setting rather than overriding it. Straight to the
            // assembled pad, no movement, short hold, then on to setup.
            foreach (var (group, xf) in _groups)
            {
                var move = PadArtwork.Moves.First(m => m.Group == group);
                xf.X = move.Dx;
                xf.Y = move.Dy;
                if (move.Vanish && _groupImages.TryGetValue(group, out var still))
                    foreach (var im in still) im.Opacity = 0;
            }
            Hero.Opacity = 1;
            Sheet.Opacity = 0;
            HeroScale.ScaleX = HeroScale.ScaleY = 1;
            HeroSpin.Angle = 0;
            Lockup.Opacity = 1;
            Caption.Opacity = 1;
            var wait = new DispatcherTimerLite(TimeSpan.FromSeconds(1.2), Finish);
            wait.Start();
            return;
        }

        var sb = new Storyboard();
        // Quadratic rather than cubic: a cubic ease-in-out spends so long accelerating that at
        // this duration the parts look stationary for the first third of the move.
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        // wordmark and caption fade up first
        foreach (var el in new FrameworkElement[] { Lockup, Caption })
        {
            var fade = new DoubleAnimation(0, 1, FadeIn) { BeginTime = TimeSpan.Zero };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            sb.Children.Add(fade);
        }

        TimeSpan collapseStart = FadeIn + Hold;
        TimeSpan lastArrival = collapseStart;

        // The part moves are applied with BeginAnimation on each transform rather than through
        // the storyboard. A Storyboard targeting a TranslateTransform by object reference does
        // not animate it and does not complain either: the opacity animations below ran and the
        // parts sat still. Animating the transform directly is the mechanism that works, and it
        // still honours BeginTime, which is all the storyboard was providing here.
        foreach (var move in PadArtwork.Moves)
        {
            if (!_groups.TryGetValue(move.Group, out var xf)) continue;
            if (move.Dx == 0 && move.Dy == 0) continue;

            var begin = collapseStart + TimeSpan.FromSeconds(move.Delay);
            var arrival = begin + Collapse;
            if (arrival > lastArrival) lastArrival = arrival;

            xf.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, move.Dx, Collapse) { BeginTime = begin, EasingFunction = ease });
            xf.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, move.Dy, Collapse) { BeginTime = begin, EasingFunction = ease });

            if (!move.Vanish || !_groupImages.TryGetValue(move.Group, out var imgs)) continue;

            // Over the last quarter of the travel, so it reads as the part going inside the
            // case rather than as the part being deleted.
            var vanishFor = TimeSpan.FromSeconds(Collapse.TotalSeconds * 0.25);
            var vanishAt = begin + (Collapse - vanishFor);
            foreach (var im in imgs)
                im.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, vanishFor) { BeginTime = vanishAt });
        }

        // cross-dissolve into the assembled render, which also unwinds the small rotate and
        // scale so the projection change reads as the camera moving rather than as a cut
        var dissolveAt = lastArrival;
        var heroFade = new DoubleAnimation(0, 1, Dissolve) { BeginTime = dissolveAt };
        Storyboard.SetTarget(heroFade, Hero);
        Storyboard.SetTargetProperty(heroFade, new PropertyPath(OpacityProperty));
        sb.Children.Add(heroFade);

        // The exploded sheet has to go as the assembled render arrives. Without this the
        // closed shell stays underneath and its grips and triggers show around the edges of
        // the hero, which reads as two pictures stacked rather than as one pad.
        var sheetOut = new DoubleAnimation(1, 0, Dissolve) { BeginTime = dissolveAt };
        Storyboard.SetTarget(sheetOut, Sheet);
        Storyboard.SetTargetProperty(sheetOut, new PropertyPath(OpacityProperty));
        sb.Children.Add(sheetOut);

        foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var grow = new DoubleAnimation(0.94, 1.0, Dissolve)
            {
                BeginTime = dissolveAt,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, HeroScale);
            Storyboard.SetTargetProperty(grow, new PropertyPath(prop));
            sb.Children.Add(grow);
        }

        var spin = new DoubleAnimation(-3, 0, Dissolve)
        {
            BeginTime = dissolveAt,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(spin, HeroSpin);
        Storyboard.SetTargetProperty(spin, new PropertyPath(RotateTransform.AngleProperty));
        sb.Children.Add(spin);

        // the whole card fades out at the end, so setup does not appear with a bang
        var cardOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3))
        {
            BeginTime = dissolveAt + Dissolve + Linger,
        };
        Storyboard.SetTarget(cardOut, Card);
        Storyboard.SetTargetProperty(cardOut, new PropertyPath(OpacityProperty));
        sb.Children.Add(cardOut);

        sb.Completed += (_, _) => Finish();
        sb.Begin(this, true);
    }

    /// <summary>
    /// Respects the Windows "show animations" setting. WPF has no direct equivalent of the web's
    /// reduced-motion query; this is the system flag that setting actually drives.
    /// </summary>
    private static bool IsReducedMotion()
    {
        try { return !SystemParameters.ClientAreaAnimation; }
        catch { return false; }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke();
        Close();
    }

    /// <summary>
    /// A one-shot timer that does not need a field to stay alive. DispatcherTimer holds itself
    /// through its Tick subscription only while it is running, so the reduced-motion path would
    /// otherwise be at the mercy of a collection between Start and Tick.
    /// </summary>
    private sealed class DispatcherTimerLite
    {
        private readonly System.Windows.Threading.DispatcherTimer _t;
        private readonly Action _onTick;

        public DispatcherTimerLite(TimeSpan after, Action onTick)
        {
            _onTick = onTick;
            _t = new System.Windows.Threading.DispatcherTimer { Interval = after };
            _t.Tick += (_, _) => { _t.Stop(); _onTick(); };
        }

        public void Start() => _t.Start();
    }
}
