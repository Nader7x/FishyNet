using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FishyNet.Core.Formatting;
using FishyNet.Features;

namespace FishyNet.Controls;

/// <summary>
/// Hardware-accelerated stacked daily usage bar chart for rendering Today, 7-Day Week, and 30-Day Month
/// Download + Upload byte volumes with bindable palette brushes, Y-axis labels, and hover tooltips.
/// </summary>
public sealed class UsageBarChartControl : FrameworkElement
{
    private static readonly Brush DefaultDownloadBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(56, 189, 248));
    private static readonly Brush DefaultUploadBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(167, 139, 250));
    private static readonly Brush DefaultHoverBandBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly Brush DefaultLabelBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(176, 189, 206));
    private static readonly Brush DefaultTooltipBgBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromArgb(235, 11, 15, 23));
    private static readonly Brush DefaultTooltipBorderBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromArgb(36, 255, 255, 255));
    private static readonly Brush DefaultTooltipFgBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromRgb(248, 250, 252));
    private static readonly Brush DefaultGridBrush = ControlRenderHelper.CreateFrozenSolidBrush(Color.FromArgb(24, 255, 255, 255));

    private static readonly Typeface MonoTypeface = new("Cascadia Mono, Consolas, Segoe UI");

    private Brush _frozenDownloadBrush = DefaultDownloadBrush;
    private Brush _frozenUploadBrush = DefaultUploadBrush;
    private Brush _frozenHoverBandBrush = DefaultHoverBandBrush;
    private Brush _frozenLabelBrush = DefaultLabelBrush;
    private Brush _frozenTooltipBgBrush = DefaultTooltipBgBrush;
    private Brush _frozenTooltipFgBrush = DefaultTooltipFgBrush;
    private Pen _gridPen = null!;
    private Pen _tooltipBorderPen = null!;

    private int _hoveredIndex = -1;

    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(
            nameof(Series),
            typeof(IReadOnlyList<DailyUsagePoint>),
            typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadBrushProperty =
        DependencyProperty.Register(nameof(DownloadBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultDownloadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty UploadBrushProperty =
        DependencyProperty.Register(nameof(UploadBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultUploadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty HoverBandBrushProperty =
        DependencyProperty.Register(nameof(HoverBandBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultHoverBandBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultLabelBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty TooltipBackgroundBrushProperty =
        DependencyProperty.Register(nameof(TooltipBackgroundBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultTooltipBgBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty TooltipBorderBrushProperty =
        DependencyProperty.Register(nameof(TooltipBorderBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultTooltipBorderBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty TooltipForegroundBrushProperty =
        DependencyProperty.Register(nameof(TooltipForegroundBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultTooltipFgBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty GridLineBrushProperty =
        DependencyProperty.Register(nameof(GridLineBrush), typeof(Brush), typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(DefaultGridBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public IReadOnlyList<DailyUsagePoint>? Series
    {
        get => (IReadOnlyList<DailyUsagePoint>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
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

    public Brush HoverBandBrush
    {
        get => (Brush)GetValue(HoverBandBrushProperty);
        set => SetValue(HoverBandBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush TooltipBackgroundBrush
    {
        get => (Brush)GetValue(TooltipBackgroundBrushProperty);
        set => SetValue(TooltipBackgroundBrushProperty, value);
    }

    public Brush TooltipBorderBrush
    {
        get => (Brush)GetValue(TooltipBorderBrushProperty);
        set => SetValue(TooltipBorderBrushProperty, value);
    }

    public Brush TooltipForegroundBrush
    {
        get => (Brush)GetValue(TooltipForegroundBrushProperty);
        set => SetValue(TooltipForegroundBrushProperty, value);
    }

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public UsageBarChartControl()
    {
        RebuildRenderResources();
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UsageBarChartControl control)
        {
            control.RebuildRenderResources();
        }
    }

    private void RebuildRenderResources()
    {
        _frozenDownloadBrush = ControlRenderHelper.FreezeBrush(DownloadBrush, DefaultDownloadBrush);
        _frozenUploadBrush = ControlRenderHelper.FreezeBrush(UploadBrush, DefaultUploadBrush);
        _frozenHoverBandBrush = ControlRenderHelper.FreezeBrush(HoverBandBrush, DefaultHoverBandBrush);
        _frozenLabelBrush = ControlRenderHelper.FreezeBrush(LabelBrush, DefaultLabelBrush);
        _frozenTooltipBgBrush = ControlRenderHelper.FreezeBrush(TooltipBackgroundBrush, DefaultTooltipBgBrush);
        _frozenTooltipFgBrush = ControlRenderHelper.FreezeBrush(TooltipForegroundBrush, DefaultTooltipFgBrush);

        _gridPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(GridLineBrush, DefaultGridBrush), 1.0);
        _tooltipBorderPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(TooltipBorderBrush, DefaultTooltipBorderBrush), 1.0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        IReadOnlyList<DailyUsagePoint>? points = Series;
        if (points is null || points.Count == 0)
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                InvalidateVisual();
            }
            return;
        }

        double leftMargin = 68.0;
        double rightMargin = 12.0;
        double plotWidth = Math.Max(10.0, ActualWidth - leftMargin - rightMargin);
        Point pos = e.GetPosition(this);

        if (pos.X < leftMargin || pos.X > leftMargin + plotWidth)
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                InvalidateVisual();
            }
            return;
        }

        double slotWidth = plotWidth / points.Count;
        int idx = Math.Clamp((int)((pos.X - leftMargin) / slotWidth), 0, points.Count - 1);
        if (idx != _hoveredIndex)
        {
            _hoveredIndex = idx;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 100 || height < 50) return;

        double leftMargin = 68.0;
        double rightMargin = 12.0;
        double topMargin = 12.0;
        double bottomMargin = 26.0;

        double plotWidth = Math.Max(10.0, width - leftMargin - rightMargin);
        double plotHeight = Math.Max(10.0, height - topMargin - bottomMargin);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        IReadOnlyList<DailyUsagePoint>? points = Series;
        long maxTotal = 1024L * 1024L; // 1 MB minimum ceiling
        if (points is not null)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].TotalBytes > maxTotal)
                {
                    maxTotal = points[i].TotalBytes;
                }
            }
        }
        maxTotal = (long)(maxTotal * 1.15);

        // Draw 3 horizontal grid lines + friendly Y-axis byte labels
        ReadOnlySpan<double> fractions = [0.0, 0.5, 1.0];
        foreach (double frac in fractions)
        {
            double y = topMargin + plotHeight * (1.0 - frac);
            dc.DrawLine(_gridPen, new Point(leftMargin, y), new Point(leftMargin + plotWidth, y));

            long labelBytes = (long)(maxTotal * frac);
            var ft = new FormattedText(
                BandwidthFormatter.FormatBytes(labelBytes),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                10.5,
                _frozenLabelBrush,
                dpi);
            dc.DrawText(ft, new Point(Math.Max(0, leftMargin - ft.Width - 8), y - (ft.Height / 2)));
        }

        if (points is null || points.Count == 0)
        {
            return;
        }

        int count = points.Count;
        double slotWidth = plotWidth / count;
        double barWidth = Math.Clamp(slotWidth * 0.58, 4.0, 38.0);
        int labelStride = count <= 10 ? 1 : (count <= 20 ? 2 : 4);

        for (int i = 0; i < count; i++)
        {
            DailyUsagePoint pt = points[i];
            double slotX = leftMargin + (i * slotWidth);
            double barX = slotX + ((slotWidth - barWidth) / 2.0);

            if (i == _hoveredIndex)
            {
                dc.DrawRoundedRectangle(_frozenHoverBandBrush, null, new Rect(slotX, topMargin, slotWidth, plotHeight), 4.0, 4.0);
            }

            double downHeight = (pt.DownloadBytes / (double)maxTotal) * plotHeight;
            double upHeight = (pt.UploadBytes / (double)maxTotal) * plotHeight;

            double bottomY = topMargin + plotHeight;
            if (downHeight >= 0.5)
            {
                dc.DrawRoundedRectangle(
                    _frozenDownloadBrush,
                    null,
                    new Rect(barX, bottomY - downHeight, barWidth, downHeight),
                    3.0,
                    3.0);
            }

            if (upHeight >= 0.5)
            {
                dc.DrawRoundedRectangle(
                    _frozenUploadBrush,
                    null,
                    new Rect(barX, bottomY - downHeight - upHeight, barWidth, upHeight),
                    3.0,
                    3.0);
            }

            if (i % labelStride == 0 || i == count - 1)
            {
                var dateText = new FormattedText(
                    pt.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    10.0,
                    _frozenLabelBrush,
                    dpi);
                double textX = slotX + (slotWidth / 2.0) - (dateText.Width / 2.0);
                dc.DrawText(dateText, new Point(textX, topMargin + plotHeight + 6));
            }
        }

        // Draw floating hover tooltip if a bar is hovered
        if (_hoveredIndex >= 0 && _hoveredIndex < count)
        {
            DailyUsagePoint hovered = points[_hoveredIndex];
            string tooltipStr = $"{hovered.Date:yyyy-MM-dd}\nDown:  {hovered.DownloadFormatted}\nUp:    {hovered.UploadFormatted}\nTotal: {hovered.TotalFormatted}";
            var tipText = new FormattedText(
                tooltipStr,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                11.0,
                _frozenTooltipFgBrush,
                dpi);

            double boxW = tipText.Width + 18;
            double boxH = tipText.Height + 14;
            double slotCenterX = leftMargin + (_hoveredIndex + 0.5) * slotWidth;
            double boxX = Math.Clamp(slotCenterX - (boxW / 2), leftMargin + 4, width - boxW - 4);
            double boxY = topMargin + 6;

            dc.DrawRoundedRectangle(_frozenTooltipBgBrush, _tooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 8, 8);
            dc.DrawText(tipText, new Point(boxX + 9, boxY + 7));
        }
    }
}
