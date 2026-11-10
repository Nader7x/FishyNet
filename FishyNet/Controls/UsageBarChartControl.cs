using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FishyNet.Core.Formatting;
using FishyNet.Features;

namespace FishyNet.Controls;

/// <summary>
/// Hardware-accelerated stacked daily usage bar chart for rendering Today, 7-Day Week, and 30-Day Month
/// Download + Upload byte volumes with friendly Y-axis labels and interactive hover tooltips.
/// </summary>
public sealed class UsageBarChartControl : FrameworkElement
{
    private static readonly SolidColorBrush DownloadBrush;
    private static readonly SolidColorBrush UploadBrush;
    private static readonly SolidColorBrush HoverBandBrush;
    private static readonly SolidColorBrush LabelBrush;
    private static readonly SolidColorBrush TooltipBgBrush;
    private static readonly Pen GridPen;
    private static readonly Pen TooltipBorderPen;
    private static readonly Typeface MonoTypeface = new("Cascadia Mono, Consolas, Segoe UI");

    private int _hoveredIndex = -1;

    static UsageBarChartControl()
    {
        DownloadBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Sky #38BDF8
        DownloadBrush.Freeze();

        UploadBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247)); // Violet #A855F7
        UploadBrush.Freeze();

        HoverBandBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        HoverBandBrush.Freeze();

        LabelBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        LabelBrush.Freeze();

        TooltipBgBrush = new SolidColorBrush(Color.FromArgb(240, 15, 23, 42));
        TooltipBgBrush.Freeze();

        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 148, 163, 184));
        gridBrush.Freeze();
        GridPen = new Pen(gridBrush, 1.0);
        GridPen.Freeze();

        var borderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
        borderBrush.Freeze();
        TooltipBorderPen = new Pen(borderBrush, 1.0);
        TooltipBorderPen.Freeze();
    }

    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(
            nameof(Series),
            typeof(IReadOnlyList<DailyUsagePoint>),
            typeof(UsageBarChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DailyUsagePoint>? Series
    {
        get => (IReadOnlyList<DailyUsagePoint>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
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
            dc.DrawLine(GridPen, new Point(leftMargin, y), new Point(leftMargin + plotWidth, y));

            long labelBytes = (long)(maxTotal * frac);
            var ft = new FormattedText(
                BandwidthFormatter.FormatBytes(labelBytes),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                10.5,
                LabelBrush,
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
                dc.DrawRectangle(HoverBandBrush, null, new Rect(slotX, topMargin, slotWidth, plotHeight));
            }

            double downHeight = (pt.DownloadBytes / (double)maxTotal) * plotHeight;
            double upHeight = (pt.UploadBytes / (double)maxTotal) * plotHeight;

            double bottomY = topMargin + plotHeight;
            if (downHeight >= 0.5)
            {
                dc.DrawRoundedRectangle(
                    DownloadBrush,
                    null,
                    new Rect(barX, bottomY - downHeight, barWidth, downHeight),
                    2.0,
                    2.0);
            }

            if (upHeight >= 0.5)
            {
                dc.DrawRoundedRectangle(
                    UploadBrush,
                    null,
                    new Rect(barX, bottomY - downHeight - upHeight, barWidth, upHeight),
                    2.0,
                    2.0);
            }

            if (i % labelStride == 0 || i == count - 1)
            {
                var dateText = new FormattedText(
                    pt.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    10.0,
                    LabelBrush,
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
                Brushes.White,
                dpi);

            double boxW = tipText.Width + 18;
            double boxH = tipText.Height + 14;
            double slotCenterX = leftMargin + (_hoveredIndex + 0.5) * slotWidth;
            double boxX = Math.Clamp(slotCenterX - (boxW / 2), leftMargin + 4, width - boxW - 4);
            double boxY = topMargin + 6;

            dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 5, 5);
            dc.DrawText(tipText, new Point(boxX + 9, boxY + 7));
        }
    }
}
