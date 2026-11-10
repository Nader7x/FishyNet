using System.Windows;
using System.Windows.Media;

namespace SelfishNetv3.Controls;

/// <summary>
/// Lightweight hardware-accelerated WPF sparkline / area chart control using DrawingContext.
/// Supports dual-series (Download + Upload) or single-series rendering with zero per-frame brush allocations.
/// </summary>
public sealed class SparklineControl : FrameworkElement
{
    private static readonly SolidColorBrush GridLineBrush;
    private static readonly Pen GridLinePen;
    private static readonly Pen PrimaryStrokePen;
    private static readonly LinearGradientBrush PrimaryFillBrush;
    private static readonly Pen SecondaryStrokePen;
    private static readonly LinearGradientBrush SecondaryFillBrush;

    static SparklineControl()
    {
        GridLineBrush = new SolidColorBrush(Color.FromArgb(32, 148, 163, 184));
        GridLineBrush.Freeze();
        GridLinePen = new Pen(GridLineBrush, 1.0);
        GridLinePen.Freeze();

        // Sky (#38BDF8) for Download
        var skyColor = Color.FromRgb(56, 189, 248);
        var skyBrush = new SolidColorBrush(skyColor);
        skyBrush.Freeze();
        PrimaryStrokePen = new Pen(skyBrush, 1.8) { LineJoin = PenLineJoin.Round };
        PrimaryStrokePen.Freeze();

        PrimaryFillBrush = new LinearGradientBrush(
            Color.FromArgb(95, 56, 189, 248),
            Color.FromArgb(4, 56, 189, 248),
            new Point(0, 0),
            new Point(0, 1));
        PrimaryFillBrush.Freeze();

        // Violet (#A855F7) for Upload
        var violetColor = Color.FromRgb(168, 85, 247);
        var violetBrush = new SolidColorBrush(violetColor);
        violetBrush.Freeze();
        SecondaryStrokePen = new Pen(violetBrush, 1.5) { LineJoin = PenLineJoin.Round };
        SecondaryStrokePen.Freeze();

        SecondaryFillBrush = new LinearGradientBrush(
            Color.FromArgb(75, 168, 85, 247),
            Color.FromArgb(4, 168, 85, 247),
            new Point(0, 0),
            new Point(0, 1));
        SecondaryFillBrush.Freeze();
    }

    public static readonly DependencyProperty PrimarySeriesProperty =
        DependencyProperty.Register(
            nameof(PrimarySeries),
            typeof(IReadOnlyList<double>),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondarySeriesProperty =
        DependencyProperty.Register(
            nameof(SecondarySeries),
            typeof(IReadOnlyList<double>),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridLinesProperty =
        DependencyProperty.Register(
            nameof(ShowGridLines),
            typeof(bool),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? PrimarySeries
    {
        get => (IReadOnlyList<double>?)GetValue(PrimarySeriesProperty);
        set => SetValue(PrimarySeriesProperty, value);
    }

    public IReadOnlyList<double>? SecondarySeries
    {
        get => (IReadOnlyList<double>?)GetValue(SecondarySeriesProperty);
        set => SetValue(SecondarySeriesProperty, value);
    }

    public bool ShowGridLines
    {
        get => (bool)GetValue(ShowGridLinesProperty);
        set => SetValue(ShowGridLinesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 4 || height < 4) return;

        if (ShowGridLines)
        {
            dc.DrawLine(GridLinePen, new Point(0, height * 0.25), new Point(width, height * 0.25));
            dc.DrawLine(GridLinePen, new Point(0, height * 0.50), new Point(width, height * 0.50));
            dc.DrawLine(GridLinePen, new Point(0, height * 0.75), new Point(width, height * 0.75));
        }

        IReadOnlyList<double>? primary = PrimarySeries;
        IReadOnlyList<double>? secondary = SecondarySeries;

        if ((primary is null || primary.Count < 2) && (secondary is null || secondary.Count < 2))
        {
            dc.DrawLine(GridLinePen, new Point(0, height - 2), new Point(width, height - 2));
            return;
        }

        double maxVal = 1024.0; // Minimum 1 KB/s ceiling so near-zero noise stays flat at bottom
        if (primary is not null)
        {
            for (int i = 0; i < primary.Count; i++)
            {
                if (primary[i] > maxVal) maxVal = primary[i];
            }
        }
        if (secondary is not null)
        {
            for (int i = 0; i < secondary.Count; i++)
            {
                if (secondary[i] > maxVal) maxVal = secondary[i];
            }
        }

        maxVal *= 1.12; // 12% headroom

        if (secondary is not null && secondary.Count >= 2)
        {
            DrawAreaSeries(dc, secondary, width, height, maxVal, SecondaryStrokePen, SecondaryFillBrush);
        }

        if (primary is not null && primary.Count >= 2)
        {
            DrawAreaSeries(dc, primary, width, height, maxVal, PrimaryStrokePen, PrimaryFillBrush);
        }
    }

    private static void DrawAreaSeries(
        DrawingContext dc,
        IReadOnlyList<double> values,
        double width,
        double height,
        double maxVal,
        Pen strokePen,
        Brush fillBrush)
    {
        int count = values.Count;
        double stepX = width / (count - 1);
        double usableHeight = Math.Max(2.0, height - 4.0);

        var fillGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeometry.Open())
        {
            double firstY = height - 2.0 - Math.Clamp(values[0] / maxVal, 0.0, 1.0) * usableHeight;
            ctx.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(0, firstY), isStroked: false, isSmoothJoin: false);

            for (int i = 1; i < count; i++)
            {
                double x = i * stepX;
                double y = height - 2.0 - Math.Clamp(values[i] / maxVal, 0.0, 1.0) * usableHeight;
                ctx.LineTo(new Point(x, y), isStroked: false, isSmoothJoin: true);
            }

            ctx.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
        }
        fillGeometry.Freeze();
        dc.DrawGeometry(fillBrush, null, fillGeometry);

        var strokeGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = strokeGeometry.Open())
        {
            double firstY = height - 2.0 - Math.Clamp(values[0] / maxVal, 0.0, 1.0) * usableHeight;
            ctx.BeginFigure(new Point(0, firstY), isFilled: false, isClosed: false);

            for (int i = 1; i < count; i++)
            {
                double x = i * stepX;
                double y = height - 2.0 - Math.Clamp(values[i] / maxVal, 0.0, 1.0) * usableHeight;
                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
        }
        strokeGeometry.Freeze();
        dc.DrawGeometry(null, strokePen, strokeGeometry);
    }
}
