using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FishyNet.Core.Formatting;

namespace FishyNet.Controls;

/// <summary>
/// Shared zero-allocation brush and pen freezing helpers for custom DrawingContext controls.
/// </summary>
internal static class ControlRenderHelper
{
    public static Brush FreezeBrush(Brush? brush, Brush fallback)
    {
        if (brush is null)
        {
            return fallback;
        }

        if (brush.IsFrozen)
        {
            return brush;
        }

        if (brush.CanFreeze)
        {
            Brush clone = brush.Clone();
            clone.Freeze();
            return clone;
        }

        return brush;
    }

    public static SolidColorBrush CreateFrozenSolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush CreateFrozenSolidBrush(Color baseColor, byte alphaOverride)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alphaOverride, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        return brush;
    }

    public static Pen CreateFrozenPen(
        Brush brush,
        double thickness,
        PenLineJoin lineJoin = PenLineJoin.Round,
        PenLineCap lineCap = PenLineCap.Flat,
        DashStyle? dashStyle = null)
    {
        Brush frozen = FreezeBrush(brush, brush);
        var pen = new Pen(frozen, thickness)
        {
            LineJoin = lineJoin,
            StartLineCap = lineCap,
            EndLineCap = lineCap
        };
        if (dashStyle is not null)
        {
            pen.DashStyle = dashStyle;
        }
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
    }

    public static Color ExtractColor(Brush? brush, Color fallback)
    {
        if (brush is SolidColorBrush scb)
        {
            return scb.Color;
        }

        if (brush is GradientBrush gb && gb.GradientStops.Count > 0)
        {
            return gb.GradientStops[0].Color;
        }

        return fallback;
    }

    public static LinearGradientBrush CreateVerticalAreaFill(Brush? strokeBrush, Color fallbackColor, byte topAlpha, byte bottomAlpha)
    {
        Color c = ExtractColor(strokeBrush, fallbackColor);
        var grad = new LinearGradientBrush(
            Color.FromArgb(topAlpha, c.R, c.G, c.B),
            Color.FromArgb(bottomAlpha, c.R, c.G, c.B),
            new Point(0, 0),
            new Point(0, 1));
        grad.Freeze();
        return grad;
    }
}

public enum TimelineAlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public sealed class TimelineSeriesDescriptor
{
    public string Name { get; init; } = string.Empty;
    public Brush? Brush { get; init; }
}

public sealed class TimelineBucketPoint
{
    public DateTime TimestampUtc { get; init; }
    public string Label { get; init; } = string.Empty;
    public double DownloadBps { get; init; }
    public double UploadBps { get; init; }
    public IReadOnlyList<double>? SliceValues { get; init; }
    public string? TopSummary { get; init; }

    public double TotalBps
    {
        get
        {
            if (SliceValues is { Count: > 0 })
            {
                double sum = 0.0;
                for (int i = 0; i < SliceValues.Count; i++)
                {
                    double v = SliceValues[i];
                    if (double.IsFinite(v) && v > 0.0) sum += v;
                }
                return sum;
            }

            double dl = double.IsFinite(DownloadBps) && DownloadBps > 0.0 ? DownloadBps : 0.0;
            double ul = double.IsFinite(UploadBps) && UploadBps > 0.0 ? UploadBps : 0.0;
            return dl + ul;
        }
    }
}

public sealed class TimelineAlertMarker
{
    public int BucketIndex { get; init; } = -1;
    public DateTime TimestampUtc { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public TimelineAlertSeverity Severity { get; init; } = TimelineAlertSeverity.Info;
}

public sealed class TimelineInspectEventArgs : EventArgs
{
    public int Index { get; }
    public TimelineBucketPoint? Bucket { get; }
    public bool IsLocked { get; }

    public TimelineInspectEventArgs(int index, TimelineBucketPoint? bucket, bool isLocked)
    {
        Index = index;
        Bucket = bucket;
        IsLocked = isLocked;
    }
}

/// <summary>
/// GlassWire-inspired stacked smooth area traffic timeline chart drawn via DrawingContext.
/// Supports multi-series stacked polygons (Download / Upload or Top App slices), dynamic peak scale label,
/// clickable/hover vertical playhead hairline with inspect state, and inline timeline alert pin markers.
/// </summary>
public sealed class StackedTrafficTimelineControl : FrameworkElement
{
    private const int MaxSeriesSlots = 5;

    private static readonly Color DefaultDownloadColor = Color.FromRgb(56, 189, 248);
    private static readonly Color DefaultUploadColor = Color.FromRgb(167, 139, 250);
    private static readonly Color DefaultAppSliceColor = Color.FromRgb(45, 212, 191);
    private static readonly Color DefaultWebSliceColor = Color.FromRgb(96, 165, 250);
    private static readonly Color DefaultExtraSliceColor = Color.FromRgb(251, 191, 36);
    private static readonly Color DefaultGridColor = Color.FromArgb(24, 255, 255, 255);
    private static readonly Color DefaultLabelColor = Color.FromRgb(132, 147, 168);
    private static readonly Color DefaultPrimaryTextColor = Color.FromRgb(248, 250, 252);
    private static readonly Color DefaultPlayheadColor = Color.FromRgb(34, 211, 238);
    private static readonly Color DefaultWarnColor = Color.FromRgb(251, 191, 36);
    private static readonly Color DefaultCriticalColor = Color.FromRgb(248, 113, 113);
    private static readonly Color DefaultInfoColor = Color.FromRgb(56, 189, 248);
    private static readonly Color DefaultTooltipBgColor = Color.FromArgb(235, 11, 15, 23);
    private static readonly Color DefaultTooltipBorderColor = Color.FromArgb(42, 255, 255, 255);

    private static readonly Brush DefaultDownloadBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultDownloadColor);
    private static readonly Brush DefaultUploadBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultUploadColor);
    private static readonly Brush DefaultAppSliceBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultAppSliceColor);
    private static readonly Brush DefaultWebSliceBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultWebSliceColor);
    private static readonly Brush DefaultExtraSliceBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultExtraSliceColor);
    private static readonly Brush DefaultGridBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultGridColor);
    private static readonly Brush DefaultLabelBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultLabelColor);
    private static readonly Brush DefaultPrimaryTextBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultPrimaryTextColor);
    private static readonly Brush DefaultPlayheadBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultPlayheadColor);
    private static readonly Brush DefaultWarnBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultWarnColor);
    private static readonly Brush DefaultCriticalBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultCriticalColor);
    private static readonly Brush DefaultInfoBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultInfoColor);
    private static readonly Brush DefaultTooltipBgBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultTooltipBgColor);
    private static readonly Brush DefaultTooltipBorderBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultTooltipBorderColor);

    private static readonly Typeface SansTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Arial"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Pre-allocated scratch buffers so steady-state OnRender performs zero array allocations.
    private double[] _prevCumulative = [];
    private double[] _nextCumulative = [];
    private readonly List<Point> _topPoints = [];
    private readonly List<Point> _bottomPoints = [];

    private readonly Pen[] _seriesStrokePens = new Pen[MaxSeriesSlots];
    private readonly LinearGradientBrush[] _seriesFillBrushes = new LinearGradientBrush[MaxSeriesSlots];
    private readonly Brush[] _seriesDotBrushes = new Brush[MaxSeriesSlots];

    private Pen _gridPen = null!;
    private Pen _playheadLockedPen = null!;
    private Pen _playheadHoverPen = null!;
    private Pen _markerLinePen = null!;
    private Pen _tooltipBorderPen = null!;
    private Brush _frozenLabelBrush = DefaultLabelBrush;
    private Brush _frozenPrimaryTextBrush = DefaultPrimaryTextBrush;
    private Brush _frozenTooltipBgBrush = DefaultTooltipBgBrush;
    private Brush _frozenPlayheadBrush = DefaultPlayheadBrush;
    private Brush _frozenWarnBrush = DefaultWarnBrush;
    private Brush _frozenCriticalBrush = DefaultCriticalBrush;
    private Brush _frozenInfoBrush = DefaultInfoBrush;
    private Brush _peakBadgeBgBrush = null!;

    public event EventHandler<TimelineInspectEventArgs>? BucketInspected;

    public static readonly DependencyProperty BucketsProperty =
        DependencyProperty.Register(
            nameof(Buckets),
            typeof(IReadOnlyList<TimelineBucketPoint>),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionPropertyChanged));

    public static readonly DependencyProperty SeriesDescriptorsProperty =
        DependencyProperty.Register(
            nameof(SeriesDescriptors),
            typeof(IReadOnlyList<TimelineSeriesDescriptor>),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty AlertMarkersProperty =
        DependencyProperty.Register(
            nameof(AlertMarkers),
            typeof(IReadOnlyList<TimelineAlertMarker>),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionPropertyChanged));

    public static readonly DependencyProperty InspectedIndexProperty =
        DependencyProperty.Register(
            nameof(InspectedIndex),
            typeof(int),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredIndexProperty =
        DependencyProperty.Register(
            nameof(HoveredIndex),
            typeof(int),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInspectLockedProperty =
        DependencyProperty.Register(
            nameof(IsInspectLocked),
            typeof(bool),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UseBitsPerSecondProperty =
        DependencyProperty.Register(
            nameof(UseBitsPerSecond),
            typeof(bool),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(
            nameof(ShowLegend),
            typeof(bool),
            typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadBrushProperty =
        DependencyProperty.Register(nameof(DownloadBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultDownloadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty UploadBrushProperty =
        DependencyProperty.Register(nameof(UploadBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultUploadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty AppSliceBrushProperty =
        DependencyProperty.Register(nameof(AppSliceBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultAppSliceBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty WebSliceBrushProperty =
        DependencyProperty.Register(nameof(WebSliceBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultWebSliceBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty ExtraSliceBrushProperty =
        DependencyProperty.Register(nameof(ExtraSliceBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultExtraSliceBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty GridLineBrushProperty =
        DependencyProperty.Register(nameof(GridLineBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultGridBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultLabelBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty PrimaryTextBrushProperty =
        DependencyProperty.Register(nameof(PrimaryTextBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultPrimaryTextBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty PlayheadBrushProperty =
        DependencyProperty.Register(nameof(PlayheadBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultPlayheadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty WarnMarkerBrushProperty =
        DependencyProperty.Register(nameof(WarnMarkerBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultWarnBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty CriticalMarkerBrushProperty =
        DependencyProperty.Register(nameof(CriticalMarkerBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultCriticalBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty InfoMarkerBrushProperty =
        DependencyProperty.Register(nameof(InfoMarkerBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultInfoBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty TooltipBackgroundBrushProperty =
        DependencyProperty.Register(nameof(TooltipBackgroundBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultTooltipBgBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public static readonly DependencyProperty TooltipBorderBrushProperty =
        DependencyProperty.Register(nameof(TooltipBorderBrush), typeof(Brush), typeof(StackedTrafficTimelineControl),
            new FrameworkPropertyMetadata(DefaultTooltipBorderBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnDescriptorsOrBrushesChanged));

    public IReadOnlyList<TimelineBucketPoint>? Buckets
    {
        get => (IReadOnlyList<TimelineBucketPoint>?)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public IReadOnlyList<TimelineSeriesDescriptor>? SeriesDescriptors
    {
        get => (IReadOnlyList<TimelineSeriesDescriptor>?)GetValue(SeriesDescriptorsProperty);
        set => SetValue(SeriesDescriptorsProperty, value);
    }

    public IReadOnlyList<TimelineAlertMarker>? AlertMarkers
    {
        get => (IReadOnlyList<TimelineAlertMarker>?)GetValue(AlertMarkersProperty);
        set => SetValue(AlertMarkersProperty, value);
    }

    public int InspectedIndex
    {
        get => (int)GetValue(InspectedIndexProperty);
        set => SetValue(InspectedIndexProperty, value);
    }

    public int HoveredIndex
    {
        get => (int)GetValue(HoveredIndexProperty);
        set => SetValue(HoveredIndexProperty, value);
    }

    public bool IsInspectLocked
    {
        get => (bool)GetValue(IsInspectLockedProperty);
        set => SetValue(IsInspectLockedProperty, value);
    }

    public bool UseBitsPerSecond
    {
        get => (bool)GetValue(UseBitsPerSecondProperty);
        set => SetValue(UseBitsPerSecondProperty, value);
    }

    public bool ShowLegend
    {
        get => (bool)GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
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

    public Brush AppSliceBrush
    {
        get => (Brush)GetValue(AppSliceBrushProperty);
        set => SetValue(AppSliceBrushProperty, value);
    }

    public Brush WebSliceBrush
    {
        get => (Brush)GetValue(WebSliceBrushProperty);
        set => SetValue(WebSliceBrushProperty, value);
    }

    public Brush ExtraSliceBrush
    {
        get => (Brush)GetValue(ExtraSliceBrushProperty);
        set => SetValue(ExtraSliceBrushProperty, value);
    }

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush PrimaryTextBrush
    {
        get => (Brush)GetValue(PrimaryTextBrushProperty);
        set => SetValue(PrimaryTextBrushProperty, value);
    }

    public Brush PlayheadBrush
    {
        get => (Brush)GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public Brush WarnMarkerBrush
    {
        get => (Brush)GetValue(WarnMarkerBrushProperty);
        set => SetValue(WarnMarkerBrushProperty, value);
    }

    public Brush CriticalMarkerBrush
    {
        get => (Brush)GetValue(CriticalMarkerBrushProperty);
        set => SetValue(CriticalMarkerBrushProperty, value);
    }

    public Brush InfoMarkerBrush
    {
        get => (Brush)GetValue(InfoMarkerBrushProperty);
        set => SetValue(InfoMarkerBrushProperty, value);
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

    public StackedTrafficTimelineControl()
    {
        ClipToBounds = true;
        RebuildRenderResources();
    }

    private static void OnCollectionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackedTrafficTimelineControl control)
        {
            return;
        }

        if (e.OldValue is INotifyCollectionChanged oldNcc)
        {
            oldNcc.CollectionChanged -= control.OnObservedCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newNcc)
        {
            newNcc.CollectionChanged += control.OnObservedCollectionChanged;
        }

        control.InvalidateVisual();
    }

    private void OnObservedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnDescriptorsOrBrushesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackedTrafficTimelineControl control)
        {
            control.RebuildRenderResources();
        }
    }

    private void RebuildRenderResources()
    {
        _frozenLabelBrush = ControlRenderHelper.FreezeBrush(LabelBrush, DefaultLabelBrush);
        _frozenPrimaryTextBrush = ControlRenderHelper.FreezeBrush(PrimaryTextBrush, DefaultPrimaryTextBrush);
        _frozenTooltipBgBrush = ControlRenderHelper.FreezeBrush(TooltipBackgroundBrush, DefaultTooltipBgBrush);
        _frozenPlayheadBrush = ControlRenderHelper.FreezeBrush(PlayheadBrush, DefaultPlayheadBrush);
        _frozenWarnBrush = ControlRenderHelper.FreezeBrush(WarnMarkerBrush, DefaultWarnBrush);
        _frozenCriticalBrush = ControlRenderHelper.FreezeBrush(CriticalMarkerBrush, DefaultCriticalBrush);
        _frozenInfoBrush = ControlRenderHelper.FreezeBrush(InfoMarkerBrush, DefaultInfoBrush);

        Color tooltipColor = ControlRenderHelper.ExtractColor(_frozenTooltipBgBrush, DefaultTooltipBgColor);
        _peakBadgeBgBrush = ControlRenderHelper.CreateFrozenSolidBrush(tooltipColor, 200);

        _gridPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(GridLineBrush, DefaultGridBrush), 1.0);
        _tooltipBorderPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.FreezeBrush(TooltipBorderBrush, DefaultTooltipBorderBrush), 1.0);
        _playheadLockedPen = ControlRenderHelper.CreateFrozenPen(_frozenPlayheadBrush, 1.5);
        _playheadHoverPen = ControlRenderHelper.CreateFrozenPen(
            ControlRenderHelper.CreateFrozenSolidBrush(ControlRenderHelper.ExtractColor(_frozenPlayheadBrush, DefaultPlayheadColor), 145),
            1.0,
            dashStyle: new DashStyle([4, 3], 0));
        _markerLinePen = ControlRenderHelper.CreateFrozenPen(
            ControlRenderHelper.CreateFrozenSolidBrush(ControlRenderHelper.ExtractColor(_frozenWarnBrush, DefaultWarnColor), 120),
            1.0,
            dashStyle: new DashStyle([2, 3], 0));

        IReadOnlyList<TimelineSeriesDescriptor>? descriptors = SeriesDescriptors;
        for (int i = 0; i < MaxSeriesSlots; i++)
        {
            Brush? custom = (descriptors is not null && i < descriptors.Count) ? descriptors[i].Brush : null;
            Brush slotBrush = custom ?? GetDefaultSlotBrush(i);
            Color fallbackColor = GetDefaultSlotColor(i);

            Brush frozenSlot = ControlRenderHelper.FreezeBrush(slotBrush, GetDefaultSlotBrush(i));
            _seriesDotBrushes[i] = frozenSlot;
            _seriesStrokePens[i] = ControlRenderHelper.CreateFrozenPen(frozenSlot, 1.8);
            _seriesFillBrushes[i] = ControlRenderHelper.CreateVerticalAreaFill(frozenSlot, fallbackColor, 128, 16);
        }
    }

    private Brush GetDefaultSlotBrush(int slotIndex) => slotIndex switch
    {
        0 => DownloadBrush ?? DefaultDownloadBrush,
        1 => UploadBrush ?? DefaultUploadBrush,
        2 => AppSliceBrush ?? DefaultAppSliceBrush,
        3 => WebSliceBrush ?? DefaultWebSliceBrush,
        _ => ExtraSliceBrush ?? DefaultExtraSliceBrush
    };

    private static Color GetDefaultSlotColor(int slotIndex) => slotIndex switch
    {
        0 => DefaultDownloadColor,
        1 => DefaultUploadColor,
        2 => DefaultAppSliceColor,
        3 => DefaultWebSliceColor,
        _ => DefaultExtraSliceColor
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = HitTestBucketIndex(e.GetPosition(this));
        if (idx != HoveredIndex)
        {
            HoveredIndex = idx;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoveredIndex != -1)
        {
            HoveredIndex = -1;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int idx = HitTestBucketIndex(e.GetPosition(this));
        if (idx < 0)
        {
            return;
        }

        IReadOnlyList<TimelineBucketPoint>? buckets = Buckets;
        if (buckets is null || idx >= buckets.Count)
        {
            return;
        }

        if (IsInspectLocked && InspectedIndex == idx)
        {
            IsInspectLocked = false;
            InspectedIndex = -1;
            BucketInspected?.Invoke(this, new TimelineInspectEventArgs(-1, null, false));
        }
        else
        {
            InspectedIndex = idx;
            IsInspectLocked = true;
            BucketInspected?.Invoke(this, new TimelineInspectEventArgs(idx, buckets[idx], true));
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsInspectLocked || InspectedIndex != -1)
        {
            IsInspectLocked = false;
            InspectedIndex = -1;
            BucketInspected?.Invoke(this, new TimelineInspectEventArgs(-1, null, false));
            e.Handled = true;
        }
    }

    private int HitTestBucketIndex(Point pos)
    {
        IReadOnlyList<TimelineBucketPoint>? buckets = Buckets;
        if (buckets is null || buckets.Count < 2)
        {
            return -1;
        }

        GetPlotRect(out double left, out double top, out double plotWidth, out double plotHeight);
        if (pos.X < left || pos.X > left + plotWidth || pos.Y < top || pos.Y > top + plotHeight)
        {
            return -1;
        }

        double ratio = (pos.X - left) / plotWidth;
        return Math.Clamp((int)Math.Round(ratio * (buckets.Count - 1)), 0, buckets.Count - 1);
    }

    private void GetPlotRect(out double left, out double top, out double plotWidth, out double plotHeight)
    {
        left = 12.0;
        top = 26.0;
        plotWidth = Math.Max(20.0, ActualWidth - 24.0);
        plotHeight = Math.Max(20.0, ActualHeight - 48.0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 80 || height < 60)
        {
            return;
        }

        GetPlotRect(out double plotLeft, out double plotTop, out double plotWidth, out double plotHeight);
        double plotBottom = plotTop + plotHeight;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Horizontal reference grid lines (0%, 50%, 100%)
        dc.DrawLine(_gridPen, new Point(plotLeft, plotTop), new Point(plotLeft + plotWidth, plotTop));
        dc.DrawLine(_gridPen, new Point(plotLeft, plotTop + plotHeight * 0.5), new Point(plotLeft + plotWidth, plotTop + plotHeight * 0.5));
        dc.DrawLine(_gridPen, new Point(plotLeft, plotBottom), new Point(plotLeft + plotWidth, plotBottom));

        IReadOnlyList<TimelineBucketPoint>? buckets = Buckets;
        if (buckets is null || buckets.Count < 2)
        {
            var emptyText = new FormattedText(
                "Collecting live traffic timeline...",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                SansTypeface,
                11.0,
                _frozenLabelBrush,
                dpi);
            dc.DrawText(emptyText, new Point(plotLeft + (plotWidth - emptyText.Width) / 2.0, plotTop + (plotHeight - emptyText.Height) / 2.0));
            return;
        }

        int count = buckets.Count;
        EnsureScratchCapacity(count);

        // Determine stacked series count (SliceValues if provided, else Download + Upload)
        int seriesCount = 2;
        bool useCustomSlices = false;
        for (int i = 0; i < count; i++)
        {
            if (buckets[i].SliceValues is { Count: > 0 } slices)
            {
                useCustomSlices = true;
                seriesCount = Math.Clamp(Math.Max(seriesCount, slices.Count), 1, MaxSeriesSlots);
            }
        }

        double maxVal = 1024.0; // 1 KB/s minimum ceiling
        for (int i = 0; i < count; i++)
        {
            double total = buckets[i].TotalBps;
            if (total > maxVal)
            {
                maxVal = total;
            }
        }
        maxVal *= 1.15;

        // Dynamic Peak Scale Badge (top-left) + 50% Midpoint Label
        bool useBits = UseBitsPerSecond;
        string peakLabel = $"PEAK {BandwidthFormatter.FormatRate(maxVal, useBits)}";
        var peakFt = new FormattedText(peakLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.0, _frozenPrimaryTextBrush, dpi);
        double badgeW = peakFt.Width + 12.0;
        double badgeH = peakFt.Height + 4.0;
        dc.DrawRoundedRectangle(_peakBadgeBgBrush, _tooltipBorderPen, new Rect(plotLeft, 4.0, badgeW, badgeH), 6.0, 6.0);
        dc.DrawText(peakFt, new Point(plotLeft + 6.0, 6.0));

        string midLabel = BandwidthFormatter.FormatRate(maxVal * 0.5, useBits);
        var midFt = new FormattedText(midLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 9.5, _frozenLabelBrush, dpi);
        dc.DrawText(midFt, new Point(plotLeft + 4.0, plotTop + (plotHeight * 0.5) - midFt.Height - 2.0));

        // Top-right inline series legend
        if (ShowLegend)
        {
            DrawLegend(dc, plotLeft + plotWidth, useCustomSlices, seriesCount, dpi);
        }

        // Render stacked smooth area layers bottom-to-top using cumulative scratch buffers
        Array.Clear(_prevCumulative, 0, count);
        double stepX = plotWidth / (count - 1);

        for (int s = 0; s < seriesCount; s++)
        {
            bool anyNonZero = false;
            _topPoints.Clear();
            _bottomPoints.Clear();

            for (int i = 0; i < count; i++)
            {
                double val = GetSeriesValue(buckets[i], s, useCustomSlices);
                if (val > 0.5) anyNonZero = true;

                double prevVal = _prevCumulative[i];
                double nextVal = prevVal + val;
                _nextCumulative[i] = nextVal;

                double x = plotLeft + (i * stepX);
                double topY = plotBottom - Math.Clamp(nextVal / maxVal, 0.0, 1.0) * plotHeight;
                double botY = plotBottom - Math.Clamp(prevVal / maxVal, 0.0, 1.0) * plotHeight;

                _topPoints.Add(new Point(x, topY));
                _bottomPoints.Add(new Point(x, botY));
            }

            if (anyNonZero)
            {
                DrawSmoothStackedLayer(dc, _topPoints, _bottomPoints, _seriesFillBrushes[s], _seriesStrokePens[s]);
            }

            Array.Copy(_nextCumulative, _prevCumulative, count);
        }

        // X-Axis time labels
        int tickCount = Math.Min(5, count);
        if (tickCount >= 2)
        {
            for (int t = 0; t < tickCount; t++)
            {
                int idx = (int)Math.Round(t * (count - 1) / (double)(tickCount - 1));
                string label = FormatBucketLabel(buckets[idx]);
                var tickFt = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 9.5, _frozenLabelBrush, dpi);
                double x = plotLeft + (idx * stepX);
                double textX = Math.Clamp(x - (tickFt.Width / 2.0), plotLeft, plotLeft + plotWidth - tickFt.Width);
                dc.DrawText(tickFt, new Point(textX, plotBottom + 5.0));
            }
        }

        // Timeline Alert Pin Markers
        IReadOnlyList<TimelineAlertMarker>? alerts = AlertMarkers;
        if (alerts is { Count: > 0 })
        {
            for (int a = 0; a < alerts.Count; a++)
            {
                TimelineAlertMarker marker = alerts[a];
                int idx = ResolveMarkerBucketIndex(marker, buckets);
                if (idx < 0 || idx >= count)
                {
                    continue;
                }

                double x = plotLeft + (idx * stepX);
                Brush markerBrush = marker.Severity switch
                {
                    TimelineAlertSeverity.Critical => _frozenCriticalBrush,
                    TimelineAlertSeverity.Warning => _frozenWarnBrush,
                    _ => _frozenInfoBrush
                };

                double totalY = plotBottom - Math.Clamp(buckets[idx].TotalBps / maxVal, 0.0, 1.0) * plotHeight;
                dc.DrawLine(_markerLinePen, new Point(x, plotTop + 6.0), new Point(x, totalY));

                // Crisp diamond pin head at top of plot
                DrawAlertDiamondPin(dc, new Point(x, plotTop + 4.0), markerBrush);
            }
        }

        // Hover / Locked Inspect Playhead Hairline + Floating Glass Tooltip
        int lockedIdx = (IsInspectLocked && InspectedIndex >= 0 && InspectedIndex < count) ? InspectedIndex : -1;
        int hoverIdx = (HoveredIndex >= 0 && HoveredIndex < count) ? HoveredIndex : -1;

        if (hoverIdx >= 0 && hoverIdx != lockedIdx)
        {
            double hx = plotLeft + (hoverIdx * stepX);
            dc.DrawLine(_playheadHoverPen, new Point(hx, plotTop), new Point(hx, plotBottom));
        }

        int activeIdx = hoverIdx >= 0 ? hoverIdx : lockedIdx;
        if (lockedIdx >= 0)
        {
            double lx = plotLeft + (lockedIdx * stepX);
            dc.DrawLine(_playheadLockedPen, new Point(lx, plotTop), new Point(lx, plotBottom));
            DrawPlayheadDots(dc, buckets[lockedIdx], lx, plotBottom, plotHeight, maxVal, useCustomSlices, seriesCount);
        }
        else if (hoverIdx >= 0)
        {
            double hx = plotLeft + (hoverIdx * stepX);
            DrawPlayheadDots(dc, buckets[hoverIdx], hx, plotBottom, plotHeight, maxVal, useCustomSlices, seriesCount);
        }

        if (activeIdx >= 0)
        {
            DrawInspectTooltip(
                dc,
                buckets[activeIdx],
                activeIdx,
                alerts,
                buckets,
                plotLeft + (activeIdx * stepX),
                plotLeft,
                plotTop,
                plotWidth,
                useCustomSlices,
                seriesCount,
                useBits,
                lockedIdx == activeIdx,
                dpi);
        }
    }

    private void DrawLegend(DrawingContext dc, double rightEdge, bool useCustomSlices, int seriesCount, double dpi)
    {
        IReadOnlyList<TimelineSeriesDescriptor>? descriptors = SeriesDescriptors;
        double cursorX = rightEdge;
        for (int s = seriesCount - 1; s >= 0; s--)
        {
            string name = (descriptors is not null && s < descriptors.Count && !string.IsNullOrWhiteSpace(descriptors[s].Name))
                ? descriptors[s].Name
                : (useCustomSlices ? $"Slice {s + 1}" : (s == 0 ? "Download" : "Upload"));

            var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SansTypeface, 10.0, _frozenLabelBrush, dpi);
            cursorX -= ft.Width;
            dc.DrawText(ft, new Point(cursorX, 6.0));
            cursorX -= 11.0;
            dc.DrawEllipse(_seriesDotBrushes[s], null, new Point(cursorX + 3.5, 6.0 + (ft.Height / 2.0)), 3.5, 3.5);
            cursorX -= 10.0;
        }
    }

    private static void DrawSmoothStackedLayer(
        DrawingContext dc,
        List<Point> topPts,
        List<Point> bottomPts,
        Brush fillBrush,
        Pen strokePen)
    {
        int n = topPts.Count;
        if (n < 2) return;

        var fillGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeom.Open())
        {
            ctx.BeginFigure(bottomPts[0], isFilled: true, isClosed: true);
            ctx.LineTo(topPts[0], isStroked: false, isSmoothJoin: false);

            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = topPts[i];
                Point p1 = topPts[i + 1];
                double dx = (p1.X - p0.X) * 0.42;
                ctx.BezierTo(new Point(p0.X + dx, p0.Y), new Point(p1.X - dx, p1.Y), p1, isStroked: false, isSmoothJoin: true);
            }

            ctx.LineTo(bottomPts[n - 1], isStroked: false, isSmoothJoin: false);

            for (int i = n - 1; i > 0; i--)
            {
                Point p0 = bottomPts[i];
                Point p1 = bottomPts[i - 1];
                double dx = (p0.X - p1.X) * 0.42;
                ctx.BezierTo(new Point(p0.X - dx, p0.Y), new Point(p1.X + dx, p1.Y), p1, isStroked: false, isSmoothJoin: true);
            }
        }
        fillGeom.Freeze();
        dc.DrawGeometry(fillBrush, null, fillGeom);

        var strokeGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = strokeGeom.Open())
        {
            ctx.BeginFigure(topPts[0], isFilled: false, isClosed: false);
            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = topPts[i];
                Point p1 = topPts[i + 1];
                double dx = (p1.X - p0.X) * 0.42;
                ctx.BezierTo(new Point(p0.X + dx, p0.Y), new Point(p1.X - dx, p1.Y), p1, isStroked: true, isSmoothJoin: true);
            }
        }
        strokeGeom.Freeze();
        dc.DrawGeometry(null, strokePen, strokeGeom);
    }

    private void DrawPlayheadDots(
        DrawingContext dc,
        TimelineBucketPoint bucket,
        double x,
        double plotBottom,
        double plotHeight,
        double maxVal,
        bool useCustomSlices,
        int seriesCount)
    {
        double cumulative = 0.0;
        for (int s = 0; s < seriesCount; s++)
        {
            double val = GetSeriesValue(bucket, s, useCustomSlices);
            cumulative += val;
            if (val <= 0.1) continue;

            double y = plotBottom - Math.Clamp(cumulative / maxVal, 0.0, 1.0) * plotHeight;
            dc.DrawEllipse(_frozenTooltipBgBrush, _seriesStrokePens[s], new Point(x, y), 4.0, 4.0);
        }
    }

    private void DrawAlertDiamondPin(DrawingContext dc, Point center, Brush fillBrush)
    {
        var geom = new StreamGeometry();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - 4.5), true, true);
            ctx.LineTo(new Point(center.X + 4.5, center.Y), false, false);
            ctx.LineTo(new Point(center.X, center.Y + 4.5), false, false);
            ctx.LineTo(new Point(center.X - 4.5, center.Y), false, false);
        }
        geom.Freeze();
        dc.DrawGeometry(fillBrush, _tooltipBorderPen, geom);
    }

    private void DrawInspectTooltip(
        DrawingContext dc,
        TimelineBucketPoint bucket,
        int bucketIndex,
        IReadOnlyList<TimelineAlertMarker>? alerts,
        IReadOnlyList<TimelineBucketPoint> buckets,
        double playheadX,
        double plotLeft,
        double plotTop,
        double plotWidth,
        bool useCustomSlices,
        int seriesCount,
        bool useBits,
        bool isLocked,
        double dpi)
    {
        string header = $"{FormatBucketLabel(bucket)}{(isLocked ? "  [LOCKED]" : string.Empty)}";
        string totalLine = $"Total: {BandwidthFormatter.FormatRate(bucket.TotalBps, useBits)}";

        string breakdown;
        if (!useCustomSlices)
        {
            breakdown = $"Down:  {BandwidthFormatter.FormatRate(bucket.DownloadBps, useBits)}\nUp:    {BandwidthFormatter.FormatRate(bucket.UploadBps, useBits)}";
        }
        else
        {
            IReadOnlyList<TimelineSeriesDescriptor>? descriptors = SeriesDescriptors;
            var lines = new List<string>(seriesCount);
            for (int s = 0; s < seriesCount; s++)
            {
                double val = GetSeriesValue(bucket, s, true);
                if (val <= 0.0) continue;
                string name = (descriptors is not null && s < descriptors.Count && !string.IsNullOrWhiteSpace(descriptors[s].Name))
                    ? descriptors[s].Name
                    : $"Slice {s + 1}";
                lines.Add($"{name}: {BandwidthFormatter.FormatRate(val, useBits)}");
            }
            breakdown = lines.Count > 0 ? string.Join("\n", lines) : totalLine;
        }

        string? alertText = null;
        if (alerts is not null)
        {
            for (int i = 0; i < alerts.Count; i++)
            {
                if (ResolveMarkerBucketIndex(alerts[i], buckets) == bucketIndex)
                {
                    alertText = string.IsNullOrWhiteSpace(alerts[i].Detail)
                        ? $"Alert: {alerts[i].Title}"
                        : $"Alert: {alerts[i].Title} — {alerts[i].Detail}";
                    break;
                }
            }
        }

        string text = $"{header}\n{totalLine}\n{breakdown}";
        if (!string.IsNullOrWhiteSpace(bucket.TopSummary))
        {
            text += $"\n{bucket.TopSummary}";
        }
        if (!string.IsNullOrWhiteSpace(alertText))
        {
            text += $"\n{alertText}";
        }

        var tipFt = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.5, _frozenPrimaryTextBrush, dpi);
        double boxW = tipFt.Width + 18.0;
        double boxH = tipFt.Height + 14.0;
        double boxX = (playheadX + 12.0 + boxW <= plotLeft + plotWidth)
            ? playheadX + 12.0
            : Math.Max(plotLeft + 4.0, playheadX - boxW - 12.0);
        double boxY = plotTop + 6.0;

        dc.DrawRoundedRectangle(_frozenTooltipBgBrush, _tooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 8.0, 8.0);
        dc.DrawText(tipFt, new Point(boxX + 9.0, boxY + 7.0));
    }

    private void EnsureScratchCapacity(int count)
    {
        if (_prevCumulative.Length < count)
        {
            _prevCumulative = new double[count];
            _nextCumulative = new double[count];
        }
    }

    private static double GetSeriesValue(TimelineBucketPoint bucket, int seriesIndex, bool useCustomSlices)
    {
        if (useCustomSlices && bucket.SliceValues is { } slices)
        {
            if (seriesIndex >= slices.Count) return 0.0;
            double v = slices[seriesIndex];
            return double.IsFinite(v) && v > 0.0 ? v : 0.0;
        }

        return seriesIndex switch
        {
            0 => double.IsFinite(bucket.DownloadBps) && bucket.DownloadBps > 0.0 ? bucket.DownloadBps : 0.0,
            1 => double.IsFinite(bucket.UploadBps) && bucket.UploadBps > 0.0 ? bucket.UploadBps : 0.0,
            _ => 0.0
        };
    }

    private static string FormatBucketLabel(TimelineBucketPoint bucket)
    {
        if (!string.IsNullOrWhiteSpace(bucket.Label))
        {
            return bucket.Label;
        }

        return bucket.TimestampUtc == default
            ? "--:--:--"
            : bucket.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static int ResolveMarkerBucketIndex(TimelineAlertMarker marker, IReadOnlyList<TimelineBucketPoint> buckets)
    {
        if (marker.BucketIndex >= 0 && marker.BucketIndex < buckets.Count)
        {
            return marker.BucketIndex;
        }

        if (marker.TimestampUtc == default || buckets.Count == 0)
        {
            return -1;
        }

        int bestIdx = -1;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].TimestampUtc == default) continue;
            long diff = Math.Abs((buckets[i].TimestampUtc - marker.TimestampUtc).Ticks);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
