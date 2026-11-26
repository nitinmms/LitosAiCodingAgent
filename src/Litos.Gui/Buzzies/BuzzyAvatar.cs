using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace Litos.Gui.Buzzies;

/// <summary>
/// A Buzzy's avatar: a glossy candy-coloured bean with a honey-bee character (two dark stripes,
/// see-through wings, eyes and antennae), drawn from plain shapes on a 56×48 canvas and scaled to
/// any size. Idle, it bobs gently and its wings flutter slowly; while <see cref="IsWorking"/> it
/// buzzes, with fast wingbeats and a small wobble. Every avatar on screen is driven by one shared
/// frame timer, which stops while none is attached, and each gets its own phase so a sidebar of
/// them never moves in lockstep.
/// </summary>
public sealed class BuzzyAvatar : Decorator
{
    private const double CanvasWidth = 56;
    private const double CanvasHeight = 48;

    private static readonly List<BuzzyAvatar> Live = [];
    private static readonly DispatcherTimer FrameTimer = new(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => TickAll());
    private static readonly IBrush InkBrush = new SolidColorBrush(Color.Parse("#1D1A18"));
    private static readonly IBrush StripeBrush = new SolidColorBrush(Color.Parse("#2A2320"));
    private static readonly IBrush WingBrush = new SolidColorBrush(Color.Parse("#EAF6FF"));
    // Light, so the antennae stay visible on the dark sidebar.
    private static readonly IBrush AntennaBrush = new SolidColorBrush(Color.Parse("#E8DCC4"));
    private static int _nextPhase;

    private readonly Canvas _bee = new() { Width = CanvasWidth, Height = CanvasHeight - 4 };
    private readonly Ellipse _shade = new();
    private readonly Ellipse _body = new();
    private readonly Path _stinger = PathShape("M8 25 L1.5 27 L8 30 Z");
    private readonly (Ellipse Wing, ScaleTransform Scale, RotateTransform Rotate)[] _wings;
    private readonly TranslateTransform _bob = new();
    private readonly RotateTransform _wobble = new();
    private readonly double _phase;

    public BuzzyAvatar(string color, double width = 44)
    {
        _phase = _nextPhase++ * 0.37;
        Width = width;
        Height = width * CanvasHeight / CanvasWidth;
        IsHitTestVisible = false;

        _wings = [NewWing(19, 9), NewWing(28, 8)];
        foreach (var (wing, _, _) in _wings)
            _bee.Children.Add(wing);

        _bee.Children.Add(_stinger);
        _bee.Children.Add(Place(_shade, 27, 27.5, 19, 14.5));
        _bee.Children.Add(Place(_body, 26, 26, 18, 13.5));
        _bee.Children.Add(PathShape("M18.5 13.8 Q15 26 18.5 38.4", stroke: StripeBrush, thickness: 3.6));
        _bee.Children.Add(PathShape("M26.5 12.6 Q23 26 26.5 39.4", stroke: StripeBrush, thickness: 3.6));
        var highlight = Place(new Ellipse { Fill = Brushes.White, Opacity = 0.55 }, 17, 19, 5.5, 2.8);
        highlight.RenderTransform = new RotateTransform(-20);
        _bee.Children.Add(highlight);
        foreach (var x in new[] { 35.0, 40.5 })
        {
            _bee.Children.Add(Place(new Ellipse { Fill = InkBrush }, x, 23.5, 2.4, 2.4));
            _bee.Children.Add(Place(new Ellipse { Fill = Brushes.White }, x + 0.8, 22.7, 0.8, 0.8));
        }
        _bee.Children.Add(PathShape("M35 29 Q38 31.5 41 29", stroke: InkBrush, thickness: 1.3));
        _bee.Children.Add(PathShape("M35 15 Q34.5 9 32 4.5", stroke: AntennaBrush, thickness: 1.4));
        _bee.Children.Add(PathShape("M40 17.5 Q42.5 11 46 8", stroke: AntennaBrush, thickness: 1.4));
        _bee.Children.Add(Place(new Ellipse { Fill = AntennaBrush }, 32, 4.5, 1.9, 1.9));
        _bee.Children.Add(Place(new Ellipse { Fill = AntennaBrush }, 46, 8, 1.9, 1.9));

        _bee.RenderTransformOrigin = RelativePoint.Center;
        _bee.RenderTransform = new TransformGroup { Children = { _wobble, _bob } };
        Canvas.SetTop(_bee, 3);

        var outer = new Canvas { Width = CanvasWidth, Height = CanvasHeight, Children = { _bee } };
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = outer };
        SetColor(color);
    }

    /// <summary>True while the Buzzy has a turn running: the wings beat fast and it buzzes.</summary>
    public bool IsWorking { get; set; }

    public void SetColor(string hex)
    {
        var color = Color.TryParse(hex, out var parsed) ? parsed : Color.Parse(BuzzyTemplates.Buddy.Color);
        _body.Fill = new SolidColorBrush(color);
        _shade.Fill = new SolidColorBrush(Shade(color, 0.65));
        _stinger.Fill = _shade.Fill;
    }

    /// <summary>The darker rim colour under the body, which gives the candy its glossy depth.</summary>
    internal static Color Shade(Color color, double factor) =>
        Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Live.Add(this);
        if (!FrameTimer.IsEnabled)
            FrameTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Live.Remove(this);
        if (Live.Count == 0)
            FrameTimer.Stop();
    }

    private static void TickAll()
    {
        var seconds = Environment.TickCount64 / 1000.0;
        foreach (var avatar in Live)
            avatar.Tick(seconds);
    }

    private void Tick(double seconds)
    {
        var t = seconds + _phase;
        var motion = AnimationFrame.At(t, IsWorking);
        _bob.X = motion.OffsetX;
        _bob.Y = motion.OffsetY;
        _wobble.Angle = motion.Tilt;
        for (var i = 0; i < _wings.Length; i++)
        {
            var wing = AnimationFrame.Wing(t + i * 0.03, IsWorking);
            _wings[i].Scale.ScaleY = wing.ScaleY;
            _wings[i].Rotate.Angle = wing.Angle;
            _wings[i].Wing.Opacity = IsWorking ? 0.6 : 0.8;
        }
    }

    private static (Ellipse, ScaleTransform, RotateTransform) NewWing(double cx, double cy)
    {
        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var wing = Place(new Ellipse { Fill = WingBrush, Stroke = Brushes.White, StrokeThickness = 0.8 }, cx, cy, 6.5, 8.5);
        wing.RenderTransformOrigin = new RelativePoint(0.5, 1.0, RelativeUnit.Relative);
        wing.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        return (wing, scale, rotate);
    }

    private static Ellipse Place(Ellipse ellipse, double cx, double cy, double rx, double ry)
    {
        ellipse.Width = rx * 2;
        ellipse.Height = ry * 2;
        Canvas.SetLeft(ellipse, cx - rx);
        Canvas.SetTop(ellipse, cy - ry);
        return ellipse;
    }

    private static Path PathShape(string data, IBrush? fill = null, IBrush? stroke = null, double thickness = 0) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round,
    };
}

/// <summary>The avatar's motion as a pure function of time, kept apart from the shapes so it is
/// testable without a running Avalonia app. Units are the avatar's 56×48 drawing units.</summary>
internal static class AnimationFrame
{
    internal readonly record struct Body(double OffsetX, double OffsetY, double Tilt);
    internal readonly record struct WingPose(double ScaleY, double Angle);

    private const double IdleBobSeconds = 2.6;
    private const double BuzzSeconds = 0.45;
    private const double IdleFlapSeconds = 1.4;
    private const double BusyFlapSeconds = 0.12;

    internal static Body At(double t, bool working)
    {
        if (!working)
            return new Body(0, -(1 - Math.Cos(Tau * t / IdleBobSeconds)), 0);

        var p = Tau * t / BuzzSeconds;
        return new Body(Math.Sin(p), -1.5 * Math.Abs(Math.Sin(p)), 3 * Math.Sin(p + 1));
    }

    internal static WingPose Wing(double t, bool working)
    {
        var c = Math.Cos(Tau * t / (working ? BusyFlapSeconds : IdleFlapSeconds));
        return new WingPose(0.75 + 0.25 * c, -1 - 7 * c);
    }

    private const double Tau = Math.PI * 2;
}
