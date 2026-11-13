using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FishyNet.Controls;

/// <summary>
/// High-performance zero-allocation WPF circular dual-ring gauge with bindable palette brushes.
/// Outer ring displays download utilization, inner ring displays upload utilization.
/// Freezes all Pens and Brushes whenever theme properties update.
/// </summary>
public sealed class DualRingGaugeControl : FrameworkElement
{
    private static readonly Brush DefaultTrackBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromArgb(50, 148, 163, 184));
    private static readonly Brush DefaultDownloadBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(56, 189, 248));
    private static readonly Brush DefaultUploadBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(167, 139, 250));
    private static readonly Brush DefaultGlyphBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(248, 250, 252));
    private static readonly Typeface GlyphTypeface = new(new FontFamily("Segoe UI Emoji, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Pen _outerTrackPen = null!;
    private Pen _innerTrackPen = null!;
    private Pen _outerProgressPen = null!;
    private Pen _innerProgressPen = null!;
    private Brush _frozenGlyphBrush = DefaultGlyphBrush;

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

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(DefaultTrackBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty DownloadBrushProperty =
        DependencyProperty.Register(nameof(DownloadBrush), typeof(Brush), typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(DefaultDownloadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty UploadBrushProperty =
        DependencyProperty.Register(nameof(UploadBrush), typeof(Brush), typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(DefaultUploadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty GlyphBrushProperty =
        DependencyProperty.Register(nameof(GlyphBrush), typeof(Brush), typeof(DualRingGaugeControl),
            new FrameworkPropertyMetadata(DefaultGlyphBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

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

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush DownloadBrush
    {
        get => (Brush)GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public Brush UploadBrush
    {
        get => (Brush)GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    public DualRingGaugeControl()
    {
        RebuildRenderResources();
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DualRingGaugeControl control)
        {
            control.RebuildRenderResources();
        }
    }

    private void RebuildRenderResources()
    {
        Brush track = ControlRenderHelper.FreezeBrush(TrackBrush, DefaultTrackBrush);
        Color trackColor = ControlRenderHelper.ExtractColor(track, Color.FromArgb(50, 148, 163, 184));
        if (trackColor.A > 90)
        {
            track = ControlRenderHelper.CreateFrozenSolidBrush(trackColor, 55);
        }

        _outerTrackPen = ControlRenderHelper.CreateFrozenPen(track, 3.5, PenLineJoin.Round, PenLineCap.Round);
        _innerTrackPen = ControlRenderHelper.CreateFrozenPen(track, 3.0, PenLineJoin.Round, PenLineCap.Round);

        _outerProgressPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(DownloadBrush, DefaultDownloadBrush), 3.5, PenLineJoin.Round, PenLineCap.Round);
        _innerProgressPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(UploadBrush, DefaultUploadBrush), 3.0, PenLineJoin.Round, PenLineCap.Round);
        _frozenGlyphBrush = ControlRenderHelper.FreezeBrush(GlyphBrush, DefaultGlyphBrush);
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
        dc.DrawEllipse(null, _outerTrackPen, centerPoint, outerRadius, outerRadius);
        dc.DrawEllipse(null, _innerTrackPen, centerPoint, innerRadius, innerRadius);

        // 2. Draw outer progress arc (Download)
        double downRatio = Math.Clamp(DownloadRatio, 0.0, 1.0);
        if (downRatio > 0.005)
        {
            DrawArc(dc, centerPoint, outerRadius, downRatio, _outerProgressPen);
        }

        // 3. Draw inner progress arc (Upload)
        double upRatio = Math.Clamp(UploadRatio, 0.0, 1.0);
        if (upRatio > 0.005)
        {
            DrawArc(dc, centerPoint, innerRadius, upRatio, _innerProgressPen);
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
                _frozenGlyphBrush,
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
