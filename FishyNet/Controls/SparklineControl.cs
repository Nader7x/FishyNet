using System.Windows;
using System.Windows.Media;

namespace FishyNet.Controls;

/// <summary>
/// Lightweight hardware-accelerated WPF sparkline / area chart control using DrawingContext.
/// Supports dual-series (Download + Upload) or single-series rendering with bindable palette brushes
/// and zero per-frame brush allocations.
/// </summary>
public sealed class SparklineControl : FrameworkElement
{
    private static readonly Color DefaultPrimaryColor = Color.FromRgb(56, 189, 248);
    private static readonly Color DefaultSecondaryColor = Color.FromRgb(167, 139, 250);
    private static readonly Color DefaultGridColor = Color.FromArgb(32, 148, 163, 184);

    private static readonly Brush DefaultPrimaryBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultPrimaryColor);
    private static readonly Brush DefaultSecondaryBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultSecondaryColor);
    private static readonly Brush DefaultGridBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultGridColor);

    private Pen _gridLinePen = null!;
    private Pen _primaryStrokePen = null!;
    private LinearGradientBrush _primaryFillBrush = null!;
    private Pen _secondaryStrokePen = null!;
    private LinearGradientBrush _secondaryFillBrush = null!;

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

    public static readonly DependencyProperty PrimaryBrushProperty =
        DependencyProperty.Register(
            nameof(PrimaryBrush),
            typeof(Brush),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(DefaultPrimaryBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty SecondaryBrushProperty =
        DependencyProperty.Register(
            nameof(SecondaryBrush),
            typeof(Brush),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(DefaultSecondaryBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty GridLineBrushProperty =
        DependencyProperty.Register(
            nameof(GridLineBrush),
            typeof(Brush),
            typeof(SparklineControl),
            new FrameworkPropertyMetadata(DefaultGridBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

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

    public Brush PrimaryBrush
    {
        get => (Brush)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Brush SecondaryBrush
    {
        get => (Brush)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public SparklineControl()
    {
        RebuildRenderResources();
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineControl control)
        {
            control.RebuildRenderResources();
        }
    }

    private void RebuildRenderResources()
    {
        Brush grid = ControlRenderHelper.FreezeBrush(GridLineBrush, DefaultGridBrush);
        _gridLinePen = ControlRenderHelper.CreateFrozenPen(grid, 1.0);

        Brush primary = ControlRenderHelper.FreezeBrush(PrimaryBrush, DefaultPrimaryBrush);
        _primaryStrokePen = ControlRenderHelper.CreateFrozenPen(primary, 1.8);
        _primaryFillBrush = ControlRenderHelper.CreateVerticalAreaFill(primary, DefaultPrimaryColor, 95, 4);

        Brush secondary = ControlRenderHelper.FreezeBrush(SecondaryBrush, DefaultSecondaryBrush);
        _secondaryStrokePen = ControlRenderHelper.CreateFrozenPen(secondary, 1.5);
        _secondaryFillBrush = ControlRenderHelper.CreateVerticalAreaFill(secondary, DefaultSecondaryColor, 75, 4);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 4 || height < 4) return;

        if (ShowGridLines)
        {
            dc.DrawLine(_gridLinePen, new Point(0, height * 0.25), new Point(width, height * 0.25));
            dc.DrawLine(_gridLinePen, new Point(0, height * 0.50), new Point(width, height * 0.50));
            dc.DrawLine(_gridLinePen, new Point(0, height * 0.75), new Point(width, height * 0.75));
        }

        IReadOnlyList<double>? primary = PrimarySeries;
        IReadOnlyList<double>? secondary = SecondarySeries;

        if ((primary is null || primary.Count < 2) && (secondary is null || secondary.Count < 2))
        {
            dc.DrawLine(_gridLinePen, new Point(0, height - 2), new Point(width, height - 2));
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
            DrawAreaSeries(dc, secondary, width, height, maxVal, _secondaryStrokePen, _secondaryFillBrush);
        }

        if (primary is not null && primary.Count >= 2)
        {
            DrawAreaSeries(dc, primary, width, height, maxVal, _primaryStrokePen, _primaryFillBrush);
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
