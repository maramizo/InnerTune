using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace InnerTune;

public static class ReactiveDjIconRenderer
{
    public const int Size = 128;

    private static readonly SolidColorBrush Ink = FrozenBrush(255, 24, 22, 34);
    private static readonly SolidColorBrush InkLight = FrozenBrush(255, 42, 38, 58);
    private static readonly SolidColorBrush Purple = FrozenBrush(255, 151, 91, 255);
    private static readonly SolidColorBrush PurpleLight = FrozenBrush(255, 205, 164, 255);
    private static readonly SolidColorBrush White = FrozenBrush(255, 255, 249, 239);
    private static readonly SolidColorBrush Eye = FrozenBrush(255, 20, 17, 30);
    private static readonly SolidColorBrush Deck = FrozenBrush(255, 36, 34, 48);
    private static readonly SolidColorBrush DeckEdge = FrozenBrush(255, 73, 66, 92);

    public static BitmapSource[] CreateFrames()
    {
        var frames = new BitmapSource[AudioIconAnimator.FrameCount];
        frames[0] = Render(0, 0);
        for (var level = 1; level < AudioIconAnimator.LevelCount; level++)
            for (var phase = 0; phase < AudioIconAnimator.PhaseCount; phase++)
                frames[AudioIconAnimator.Encode(level, phase)] = Render(level / (double)(AudioIconAnimator.LevelCount - 1), phase);
        return frames;
    }

    public static BitmapSource Render(double amplitude, int phase)
    {
        amplitude = Math.Clamp(amplitude, 0, 1);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var angle = phase * Math.PI * 2 / AudioIconAnimator.PhaseCount;
            DrawEqualizer(drawing, amplitude, angle);
            DrawCat(drawing, amplitude, angle);
        }
        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawEqualizer(DrawingContext drawing, double amplitude, double angle)
    {
        for (var index = 0; index < 9; index++)
        {
            var wave = .5 + .5 * Math.Sin(angle + index * .92);
            var counterWave = .5 + .5 * Math.Sin(angle * 1.7 - index * .61);
            var energy = .28 + .48 * wave + .24 * counterWave;
            var height = 5 + amplitude * (20 + 68 * energy);
            var alpha = (byte)Math.Round(22 + amplitude * 42);
            var brush = index % 2 == 0
                ? FrozenBrush(alpha, 174, 104, 255)
                : FrozenBrush(alpha, 255, 86, 173);
            drawing.DrawRoundedRectangle(brush, null, new Rect(3 + index * 14, 119 - height, 9, height), 4.5, 4.5);
        }
    }

    private static void DrawCat(DrawingContext drawing, double amplitude, double angle)
    {
        var bob = -4 * amplitude * (.5 + .5 * Math.Sin(angle * 2));
        drawing.DrawEllipse(FrozenBrush(58, 0, 0, 0), null, new Point(64, 119), 48, 7);

        var tailPen = FrozenPen(Ink, 12);
        var tail = new StreamGeometry();
        using (var geometry = tail.Open())
        {
            geometry.BeginFigure(new Point(43, 83 + bob), false, false);
            geometry.BezierTo(new Point(17, 88), new Point(17, 59), new Point(31, 57 + bob), true, false);
        }
        tail.Freeze();
        drawing.DrawGeometry(null, tailPen, tail);
        drawing.DrawEllipse(White, null, new Point(30, 56 + bob), 8, 7);

        drawing.DrawEllipse(Ink, null, new Point(64, 78 + bob), 29, 31);
        DrawPolygon(drawing, Ink, [new Point(37, 38 + bob), new Point(39, 11 + bob), new Point(56, 29 + bob)]);
        DrawPolygon(drawing, Ink, [new Point(72, 29 + bob), new Point(90, 11 + bob), new Point(91, 39 + bob)]);
        DrawPolygon(drawing, Purple, [new Point(42, 31 + bob), new Point(43, 19 + bob), new Point(51, 31 + bob)]);
        DrawPolygon(drawing, Purple, [new Point(77, 31 + bob), new Point(87, 19 + bob), new Point(86, 32 + bob)]);
        drawing.DrawEllipse(Ink, null, new Point(64, 48 + bob), 33, 31);

        var headphoneArc = new StreamGeometry();
        using (var geometry = headphoneArc.Open())
        {
            geometry.BeginFigure(new Point(38, 38 + bob), false, false);
            geometry.BezierTo(new Point(40, 8 + bob), new Point(88, 8 + bob), new Point(91, 38 + bob), true, false);
        }
        headphoneArc.Freeze();
        drawing.DrawGeometry(null, FrozenPen(Purple, 7), headphoneArc);
        drawing.DrawRoundedRectangle(InkLight, null, new Rect(27, 37 + bob, 14, 28), 7, 7);
        drawing.DrawRoundedRectangle(InkLight, null, new Rect(87, 37 + bob, 14, 28), 7, 7);
        drawing.DrawRoundedRectangle(Purple, null, new Rect(30, 42 + bob, 7, 18), 3.5, 3.5);
        drawing.DrawRoundedRectangle(Purple, null, new Rect(91, 42 + bob, 7, 18), 3.5, 3.5);

        drawing.DrawEllipse(White, null, new Point(52, 49 + bob), 8, 11);
        drawing.DrawEllipse(White, null, new Point(76, 49 + bob), 8, 11);
        drawing.DrawEllipse(Eye, null, new Point(53, 51 + bob), 4.2, 6.5);
        drawing.DrawEllipse(Eye, null, new Point(75, 51 + bob), 4.2, 6.5);
        drawing.DrawEllipse(Purple, null, new Point(54, 54 + bob), 2.4, 3.2);
        drawing.DrawEllipse(Purple, null, new Point(74, 54 + bob), 2.4, 3.2);
        drawing.DrawEllipse(White, null, new Point(51.5, 47 + bob), 1.5, 1.8);
        drawing.DrawEllipse(White, null, new Point(73.5, 47 + bob), 1.5, 1.8);
        drawing.DrawEllipse(White, null, new Point(64, 65 + bob), 16, 10);
        DrawPolygon(drawing, Purple, [new Point(60, 61 + bob), new Point(68, 61 + bob), new Point(64, 65 + bob)]);
        var mouth = new StreamGeometry();
        using (var geometry = mouth.Open())
        {
            geometry.BeginFigure(new Point(64, 65 + bob), false, false);
            geometry.BezierTo(new Point(61, 70 + bob), new Point(57, 68 + bob), new Point(57, 67 + bob), true, false);
            geometry.BeginFigure(new Point(64, 65 + bob), false, false);
            geometry.BezierTo(new Point(67, 70 + bob), new Point(71, 68 + bob), new Point(71, 67 + bob), true, false);
        }
        mouth.Freeze();
        drawing.DrawGeometry(null, FrozenPen(Eye, 1.8), mouth);

        var leftLift = amplitude * (.25 + .75 * (.5 + .5 * Math.Sin(angle)));
        var rightLift = amplitude * (.25 + .75 * (.5 - .5 * Math.Sin(angle)));
        var leftHand = new Point(43 - 12 * leftLift, 89 + bob - 67 * leftLift);
        var rightHand = new Point(85 + 12 * rightLift, 89 + bob - 67 * rightLift);
        DrawArm(drawing, new Point(44, 72 + bob), leftHand);
        DrawArm(drawing, new Point(84, 72 + bob), rightHand);

        drawing.DrawRoundedRectangle(DeckEdge, null, new Rect(16, 91, 96, 31), 11, 11);
        drawing.DrawRoundedRectangle(Deck, null, new Rect(18, 88, 92, 31), 10, 10);
        drawing.DrawEllipse(Ink, null, new Point(57, 102), 30, 14);
        drawing.DrawEllipse(FrozenBrush(255, 81, 74, 105), null, new Point(57, 102), 25, 10);
        drawing.DrawEllipse(Purple, null, new Point(57, 102), 12, 6);
        drawing.DrawEllipse(Eye, null, new Point(57, 102), 3, 3);
        drawing.DrawRoundedRectangle(Purple, null, new Rect(91, 95, 8, 8), 4, 4);
        drawing.DrawRoundedRectangle(PurpleLight, null, new Rect(94, 108, 10, 3), 1.5, 1.5);

        DrawPaw(drawing, leftHand);
        DrawPaw(drawing, rightHand);
    }

    private static void DrawArm(DrawingContext drawing, Point shoulder, Point hand)
    {
        drawing.DrawLine(FrozenPen(Purple, 15), shoulder, hand);
        drawing.DrawLine(FrozenPen(InkLight, 11), shoulder, hand);
    }

    private static void DrawPaw(DrawingContext drawing, Point center)
    {
        drawing.DrawEllipse(Purple, null, center, 9, 8.5);
        drawing.DrawEllipse(White, null, center, 7.4, 7);
        drawing.DrawEllipse(FrozenBrush(255, 226, 217, 211), null, new Point(center.X, center.Y + 2), 3.7, 2.4);
    }

    private static void DrawPolygon(DrawingContext drawing, Brush brush, IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        drawing.DrawGeometry(brush, null, geometry);
    }

    private static SolidColorBrush FrozenBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
