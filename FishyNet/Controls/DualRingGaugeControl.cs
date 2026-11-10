using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FishyNet.Controls;

/// <summary>
/// High-performance zero-allocation WPF circular dual-ring gauge.
/// Outer ring displays download utilization, inner ring displays upload utilization.
/// Pre-allocates and freezes all Pens and Brushes.
/// </summary>
public sealed class DualRingGaugeControl : FrameworkElement
{
    private static readonly Pen OuterTrackPen;
    private static readonly Pen InnerTrackPen;
    private static readonly Pen OuterProgressPen;
    private static readonly Pen InnerProgressPen;
    private static readonly SolidColorBrush GlyphBrush;
    private static readonly Typeface GlyphTypeface;

    static DualRingGaugeControl()
    {
        var trackBrush = new SolidColorBrush(Color.FromArgb(50, 51, 65, 85));
        trackBrush.Freeze();
        OuterTrackPen = new Pen(trackBrush, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        OuterTrackPen.Freeze();
        InnerTrackPen = new Pen(trackBrush, 3.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        InnerTrackPen.Freeze();

        var skyBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        skyBrush.Freeze();
        OuterProgressPen = new Pen(skyBrush, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        OuterProgressPen.Freeze();

        var violetBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
        violetBrush.Freeze();
        InnerProgressPen = new Pen(violetBrush, 3.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        InnerProgressPen.Freeze();

        GlyphBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        GlyphBrush.Freeze();

        GlyphTypeface = new Typeface(new FontFamily("Segoe UI Emoji, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    public static readonly DependencyProperty DownloadRatioProperty =
        DependencyProperty.Register(
            nameof(DownloadRatio),
            typeof(double),
            typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadRatioProperty =
        DependencyProperty.Register(
            nameof(UploadRatio),
            typeof(double),
            typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterGlyphProperty =
        DependencyProperty.Register(
            nameof(CenterGlyph),
            typeof(string),
            typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public double DownloadRatio
    {
        get => (double)GetValue(DownloadRatioProperty);
        set => SetValue(DownloadRatioProperty, value);
    }

    public double UploadRatio
    {
        get => (double)GetValue(UploadRatioProperty);
        set => SetValue(UploadRatioProperty, value);
    }

    public string CenterGlyph
    {
        get => (string)GetValue(CenterGlyphProperty);
        set => SetValue(CenterGlyphProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 4 || height <= 4) return;

        double center = Math.Min(width, height) / 2.0;
        double outerRadius = center - 4.0;
        double innerRadius = outerRadius - 5.5;

        if (outerRadius <= 2 || innerRadius <= 2) return;

        var centerPoint = new Point(center, center);

        // 1. Draw track rings (full 360)
        dc.DrawEllipse(null, OuterTrackPen, centerPoint, outerRadius, outerRadius);
        dc.DrawEllipse(null, InnerTrackPen, centerPoint, innerRadius, innerRadius);

        // 2. Draw outer progress arc (Download)
        double downRatio = Math.Clamp(DownloadRatio, 0.0, 1.0);
        if (downRatio > 0.005)
        {
            DrawArc(dc, centerPoint, outerRadius, downRatio, OuterProgressPen);
        }

        // 3. Draw inner progress arc (Upload)
        double upRatio = Math.Clamp(UploadRatio, 0.0, 1.0);
        if (upRatio > 0.005)
        {
            DrawArc(dc, centerPoint, innerRadius, upRatio, InnerProgressPen);
        }

        // 4. Draw Center Glyph
        string glyph = CenterGlyph;
        if (!string.IsNullOrEmpty(glyph))
        {
            FormattedText ft = new(
                glyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                GlyphTypeface,
                Math.Max(10.0, innerRadius * 0.95),
                GlyphBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            Point textPos = new(center - (ft.Width / 2.0), center - (ft.Height / 2.0));
            dc.DrawText(ft, textPos);
        }
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double ratio, Pen pen)
    {
        if (ratio >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        // Angle in radians (-PI/2 is top)
        double startAngle = -Math.PI / 2.0;
        double sweepAngle = ratio * 2.0 * Math.PI;
        double endAngle = startAngle + sweepAngle;

        Point startPt = new(
            center.X + (radius * Math.Cos(startAngle)),
            center.Y + (radius * Math.Sin(startAngle)));

        Point endPt = new(
            center.X + (radius * Math.Cos(endAngle)),
            center.Y + (radius * Math.Sin(endAngle)));

        bool isLargeArc = sweepAngle > Math.PI;

        StreamGeometry geom = new();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(startPt, false, false);
            ctx.ArcTo(endPt, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();

        dc.DrawGeometry(null, pen, geom);
    }
}
