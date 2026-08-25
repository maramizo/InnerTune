using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace InnerTune;

/// <summary>
/// A low-cost, non-interactive equalizer backdrop for the active saved queue.
/// It draws directly into WPF's retained scene and creates no per-bar controls.
/// </summary>
public sealed class QueueEqualizerVisual : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(QueueEqualizerVisual),
        new FrameworkPropertyMetadata(0d, OnMotionChanged));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(QueueEqualizerVisual),
        new FrameworkPropertyMetadata(0d, OnMotionChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(QueueEqualizerVisual),
        new FrameworkPropertyMetadata(false, OnActiveChanged));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public QueueEqualizerVisual()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (!IsActive || ActualWidth < 20 || ActualHeight < 12) return;

        var loudness = Math.Clamp(Level, 0, 1);
        var energy = Math.Clamp(Math.Sqrt(loudness / .42), 0, 1);
        if (energy < .025) return;

        var barCount = Math.Clamp((int)(ActualWidth / 15), 18, 72);
        var slot = ActualWidth / barCount;
        var barWidth = Math.Clamp(slot * .48, 2, 7);
        var maximumHeight = Math.Min(ActualHeight * .76, 92);
        var centerY = ActualHeight / 2;
        var alpha = (byte)Math.Round(7 + 16 * energy);
        var red = (byte)Math.Round(61 + 24 * energy);
        var green = (byte)Math.Round(47 + 9 * energy);
        var blue = (byte)Math.Round(78 + 30 * energy);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();

        for (var index = 0; index < barCount; index++)
        {
            var wave = .5 + .5 * Math.Sin(Phase + index * .71);
            var echo = .5 + .5 * Math.Sin(Phase * .63 - index * .39);
            var shape = .18 + .52 * wave + .30 * echo;
            var height = 3 + maximumHeight * energy * shape;
            var left = index * slot + (slot - barWidth) / 2;
            drawing.DrawRoundedRectangle(
                brush,
                null,
                new Rect(left, centerY - height / 2, barWidth, height),
                barWidth / 2,
                barWidth / 2);
        }
    }

    private static void OnMotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is QueueEqualizerVisual { IsActive: true, IsVisible: true } visual)
            visual.InvalidateVisual();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is QueueEqualizerVisual visual) visual.InvalidateVisual();
    }
}
