using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FishyNet.ViewModels;

namespace FishyNet.Controls;

/// <summary>
/// Hardware-accelerated interactive Network Topology Radar & Node Graph canvas.
/// Renders the Gateway at center with LAN devices orbiting around it.
/// Renders animated particle streams whose thickness and velocity scale with live bandwidth flow.
/// Zero heap allocations during the 60fps rendering loop.
/// </summary>
public sealed class NetworkTopologyControl : FrameworkElement
{
    private static readonly SolidColorBrush RadarBgBrush;
    private static readonly Pen RadarGridPen;
    private static readonly Pen RadarCrosshairPen;
    private static readonly SolidColorBrush GatewayFillBrush;
    private static readonly Pen GatewayGlowPen;
    private static readonly SolidColorBrush DeviceFillBrush;
    private static readonly SolidColorBrush DeviceSelectedBrush;
    private static readonly Pen DeviceNormalPen;
    private static readonly Pen DeviceSelectedPen;
    private static readonly Pen DeviceBlockedPen;
    private static readonly Pen DeviceThrottledPen;
    private static readonly SolidColorBrush TextPrimaryBrush;
    private static readonly SolidColorBrush TextSecondaryBrush;
    private static readonly SolidColorBrush SkyParticleBrush;
    private static readonly SolidColorBrush VioletParticleBrush;
    private static readonly SolidColorBrush CrimsonParticleBrush;
    private static readonly SolidColorBrush NodeHaloBrush;
    private static readonly Pen RadarPulsePen;
    private static readonly Pen StreamBlockedPen;
    private static readonly Pen StreamThrottledPen;
    private static readonly Pen StreamBypassedPen;
    private static readonly Pen[] StreamActivePens;
    private static readonly Typeface LabelTypeface;
    private static readonly Typeface GlyphTypeface;

    private static double _cachedDpi = -1;
    private static FormattedText? _cachedGwGlyph;
    private static FormattedText? _cachedGwLabel;
    private static readonly Dictionary<string, FormattedText> GlyphCache = [];

    private readonly List<NodeLayoutInfo> _nodeLayouts = [];
    private readonly List<DeviceViewModel> _orbitingDevices = [];
    private DeviceViewModel? _gatewayVm;
    private bool _isGwHovered;
    private double _animationTime;
    private long _lastFrameTicks;
    private bool _isRenderingSubscribed;
    private NodeLayoutInfo? _hoveredNode;

    private sealed class NodeLayoutInfo
    {
        public DeviceViewModel ViewModel { get; set; }
        public Point Position { get; set; }
        public double Radius { get; set; }
        public string? LastName { get; set; }
        public FormattedText? NameFt { get; set; }
        public string? LastSpeed { get; set; }
        public FormattedText? SpeedFt { get; set; }
        public bool LastSelected { get; set; }

        public NodeLayoutInfo(DeviceViewModel viewModel, Point position, double radius)
        {
            ViewModel = viewModel;
            Position = position;
            Radius = radius;
        }
    }

    static NetworkTopologyControl()
    {
        RadarBgBrush = new SolidColorBrush(Color.FromArgb(140, 16, 17, 22));
        RadarBgBrush.Freeze();

        var gridBrush = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184));
        gridBrush.Freeze();
        RadarGridPen = new Pen(gridBrush, 1.0) { DashStyle = new DashStyle([4, 4], 0) };
        RadarGridPen.Freeze();

        var crossBrush = new SolidColorBrush(Color.FromArgb(18, 148, 163, 184));
        crossBrush.Freeze();
        RadarCrosshairPen = new Pen(crossBrush, 1.0);
        RadarCrosshairPen.Freeze();

        var gwFill = new SolidColorBrush(Color.FromArgb(235, 16, 185, 129));
        gwFill.Freeze();
        GatewayFillBrush = gwFill;

        var gwGlow = new SolidColorBrush(Color.FromArgb(90, 16, 185, 129));
        gwGlow.Freeze();
        GatewayGlowPen = new Pen(gwGlow, 5.0);
        GatewayGlowPen.Freeze();

        var devFill = new SolidColorBrush(Color.FromArgb(235, 25, 27, 34));
        devFill.Freeze();
        DeviceFillBrush = devFill;

        var devSel = new SolidColorBrush(Color.FromArgb(250, 38, 41, 52));
        devSel.Freeze();
        DeviceSelectedBrush = devSel;

        var devPen = new SolidColorBrush(Color.FromArgb(180, 56, 189, 248));
        devPen.Freeze();
        DeviceNormalPen = new Pen(devPen, 1.8);
        DeviceNormalPen.Freeze();

        var selPen = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
        selPen.Freeze();
        DeviceSelectedPen = new Pen(selPen, 3.0);
        DeviceSelectedPen.Freeze();

        var blkPen = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
        blkPen.Freeze();
        DeviceBlockedPen = new Pen(blkPen, 2.0);
        DeviceBlockedPen.Freeze();

        var thrPen = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
        thrPen.Freeze();
        DeviceThrottledPen = new Pen(thrPen, 2.0);
        DeviceThrottledPen.Freeze();

        TextPrimaryBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        TextPrimaryBrush.Freeze();

        TextSecondaryBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        TextSecondaryBrush.Freeze();

        SkyParticleBrush = new SolidColorBrush(Color.FromArgb(230, 56, 189, 248));
        SkyParticleBrush.Freeze();

        VioletParticleBrush = new SolidColorBrush(Color.FromArgb(230, 168, 85, 247));
        VioletParticleBrush.Freeze();

        CrimsonParticleBrush = new SolidColorBrush(Color.FromArgb(230, 239, 68, 68));
        CrimsonParticleBrush.Freeze();

        var haloBrush = new SolidColorBrush(Color.FromArgb(50, 56, 189, 248));
        haloBrush.Freeze();
        NodeHaloBrush = haloBrush;

        var pulseBrush = new SolidColorBrush(Color.FromArgb(80, 16, 185, 129));
        pulseBrush.Freeze();
        RadarPulsePen = new Pen(pulseBrush, 1.5);
        RadarPulsePen.Freeze();

        StreamBlockedPen = new Pen(CrimsonParticleBrush, 1.5) { DashStyle = new DashStyle([3, 3], 0) };
        StreamBlockedPen.Freeze();

        var thrStreamBrush = new SolidColorBrush(Color.FromArgb(140, 245, 158, 11));
        thrStreamBrush.Freeze();
        StreamThrottledPen = new Pen(thrStreamBrush, 2.0);
        StreamThrottledPen.Freeze();

        var slateStreamBrush = new SolidColorBrush(Color.FromArgb(70, 100, 116, 139));
        slateStreamBrush.Freeze();
        StreamBypassedPen = new Pen(slateStreamBrush, 1.0);
        StreamBypassedPen.Freeze();

        StreamActivePens = new Pen[5];
        for (int i = 0; i < 5; i++)
        {
            double th = 1.2 + (i * 0.95);
            byte a = (byte)(80 + (i * 35));
            var activeBrush = new SolidColorBrush(Color.FromArgb(a, 56, 189, 248));
            activeBrush.Freeze();
            var p = new Pen(activeBrush, th);
            p.Freeze();
            StreamActivePens[i] = p;
        }

        LabelTypeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        GlyphTypeface = new Typeface(new FontFamily("Segoe UI Emoji, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    public static readonly DependencyProperty DevicesProperty =
        DependencyProperty.Register(
            nameof(Devices),
            typeof(IEnumerable),
            typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDevicesChanged));

    public static readonly DependencyProperty SelectedDeviceProperty =
        DependencyProperty.Register(
            nameof(SelectedDevice),
            typeof(DeviceViewModel),
            typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Devices
    {
        get => (IEnumerable?)GetValue(DevicesProperty);
        set => SetValue(DevicesProperty, value);
    }

    public DeviceViewModel? SelectedDevice
    {
        get => (DeviceViewModel?)GetValue(SelectedDeviceProperty);
        set => SetValue(SelectedDeviceProperty, value);
    }

    private static void OnDevicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NetworkTopologyControl control)
        {
            if (e.OldValue is INotifyCollectionChanged oldNcc)
            {
                oldNcc.CollectionChanged -= control.OnDevicesCollectionChanged;
            }

            if (e.NewValue is INotifyCollectionChanged newNcc)
            {
                newNcc.CollectionChanged += control.OnDevicesCollectionChanged;
            }

            control.InvalidateVisual();
        }
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
    }

    public NetworkTopologyControl()
    {
        ClipToBounds = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateRenderSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderSubscription();
    }

    private void UpdateRenderSubscription()
    {
        if (IsLoaded && IsVisible)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (!_isRenderingSubscribed)
        {
            _lastFrameTicks = Environment.TickCount64;
            CompositionTarget.Rendering += OnCompositionRendering;
            _isRenderingSubscribed = true;
        }
    }

    private void StopRendering()
    {
        if (_isRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _isRenderingSubscribed = false;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        long delta = now - _lastFrameTicks;
        _lastFrameTicks = now;

        if (delta > 0)
        {
            // Advance animation time based on elapsed delta
            _animationTime += delta / 1000.0;
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pos = e.GetPosition(this);
        Point center = new(ActualWidth / 2.0, ActualHeight / 2.0);
        double dx = pos.X - center.X;
        double dy = pos.Y - center.Y;
        bool isGwHovered = (dx * dx) + (dy * dy) <= (28.0 * 28.0);

        NodeLayoutInfo? hit = HitTestNode(pos);

        if (hit != _hoveredNode || isGwHovered != _isGwHovered)
        {
            _hoveredNode = hit;
            _isGwHovered = isGwHovered;
            Cursor = (hit != null || isGwHovered) ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point pos = e.GetPosition(this);
        Point center = new(ActualWidth / 2.0, ActualHeight / 2.0);
        double dx = pos.X - center.X;
        double dy = pos.Y - center.Y;

        if ((dx * dx) + (dy * dy) <= (28.0 * 28.0))
        {
            if (_gatewayVm != null)
            {
                SelectedDevice = _gatewayVm;
            }
            e.Handled = true;
            return;
        }

        NodeLayoutInfo? hit = HitTestNode(pos);
        if (hit != null)
        {
            SelectedDevice = hit.ViewModel;
        }
        else
        {
            SelectedDevice = null;
        }
        e.Handled = true;
    }

    private NodeLayoutInfo? HitTestNode(Point pt)
    {
        for (int i = _nodeLayouts.Count - 1; i >= 0; i--)
        {
            NodeLayoutInfo node = _nodeLayouts[i];
            double dx = pt.X - node.Position.X;
            double dy = pt.Y - node.Position.Y;
            if ((dx * dx) + (dy * dy) <= (node.Radius * node.Radius))
            {
                return node;
            }
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 50 || height < 50) return;

        Point center = new(width / 2.0, height / 2.0);
        double maxRadius = Math.Min(width, height) / 2.0 - 45.0;
        if (maxRadius < 30) return;

        // 1. Draw Background radar gradient & concentric range rings
        dc.DrawRectangle(RadarBgBrush, null, new Rect(0, 0, width, height));

        // Crosshairs
        dc.DrawLine(RadarCrosshairPen, new Point(center.X, 0), new Point(center.X, height));
        dc.DrawLine(RadarCrosshairPen, new Point(0, center.Y), new Point(width, center.Y));

        // Concentric rings
        dc.DrawEllipse(null, RadarGridPen, center, maxRadius * 0.35, maxRadius * 0.35);
        dc.DrawEllipse(null, RadarGridPen, center, maxRadius * 0.70, maxRadius * 0.70);
        dc.DrawEllipse(null, RadarGridPen, center, maxRadius, maxRadius);

        // Animated Radar Sweep Pulse (using PushOpacity with pre-frozen pen for zero allocation)
        double pulseRatio = (_animationTime * 0.3) % 1.0;
        double pulseRadius = maxRadius * pulseRatio;
        dc.PushOpacity(1.0 - pulseRatio);
        dc.DrawEllipse(null, RadarPulsePen, center, pulseRadius, pulseRadius);
        dc.Pop();

        // 2. Identify Gateway and Orbiting Devices
        _orbitingDevices.Clear();
        _gatewayVm = null;

        if (Devices != null)
        {
            foreach (object item in Devices)
            {
                if (item is DeviceViewModel vm)
                {
                    if (vm.IsGateway)
                    {
                        _gatewayVm = vm;
                    }
                    else
                    {
                        _orbitingDevices.Add(vm);
                    }
                }
            }
        }

        // 3. Layout Orbiting Nodes in concentric or radial distribution
        int count = _orbitingDevices.Count;
        double baseRadius = maxRadius * 0.78;

        while (_nodeLayouts.Count > count)
        {
            _nodeLayouts.RemoveAt(_nodeLayouts.Count - 1);
        }

        for (int i = 0; i < count; i++)
        {
            DeviceViewModel dev = _orbitingDevices[i];

            double ringDist = count > 10
                ? (i % 2 == 0 ? maxRadius * 0.58 : maxRadius * 0.90)
                : baseRadius;

            double angle = (2.0 * Math.PI * i / count) - (Math.PI / 2.0);
            Point nodePos = new(
                center.X + (ringDist * Math.Cos(angle)),
                center.Y + (ringDist * Math.Sin(angle)));

            if (i < _nodeLayouts.Count)
            {
                NodeLayoutInfo existing = _nodeLayouts[i];
                existing.ViewModel = dev;
                existing.Position = nodePos;
                existing.Radius = 22.0;
            }
            else
            {
                _nodeLayouts.Add(new NodeLayoutInfo(dev, nodePos, 22.0));
            }
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureStaticTextCache(dpi);

        // 4. Draw Connection Streams & Animated Particles between Gateway and Nodes
        for (int i = 0; i < _nodeLayouts.Count; i++)
        {
            NodeLayoutInfo node = _nodeLayouts[i];
            DeviceViewModel dev = node.ViewModel;

            double downBps = dev.CurrentDownloadBps;
            double upBps = dev.CurrentUploadBps;
            double totalBps = downBps + upBps;

            Pen streamPen;
            if (dev.IsBlocked)
            {
                streamPen = StreamBlockedPen;
            }
            else if (dev.IsThrottled)
            {
                streamPen = StreamThrottledPen;
            }
            else if (!dev.IsSpoofed)
            {
                streamPen = StreamBypassedPen;
            }
            else
            {
                int penIdx = Math.Clamp((int)(totalBps / (100.0 * 1024.0)), 0, 4);
                streamPen = StreamActivePens[penIdx];
            }

            dc.DrawLine(streamPen, center, node.Position);

            // Draw Animated Particle Flow along stream
            if (!dev.IsBlocked && totalBps > 100)
            {
                double flowSpeed = 0.5 + Math.Min(3.0, totalBps / 300000.0);
                int particleCount = Math.Max(2, Math.Min(6, (int)(streamPen.Thickness * 1.5)));

                for (int p = 0; p < particleCount; p++)
                {
                    double particleOffset = ((_animationTime * flowSpeed) + ((double)p / particleCount)) % 1.0;

                    Point pPt = new(
                        center.X + ((node.Position.X - center.X) * particleOffset),
                        center.Y + ((node.Position.Y - center.Y) * particleOffset));

                    Brush pBrush = downBps >= upBps ? SkyParticleBrush : VioletParticleBrush;
                    dc.DrawEllipse(pBrush, null, pPt, 2.5, 2.5);
                }
            }
        }

        // 5. Draw Orbiting Device Nodes
        for (int i = 0; i < _nodeLayouts.Count; i++)
        {
            NodeLayoutInfo node = _nodeLayouts[i];
            DeviceViewModel dev = node.ViewModel;
            bool isSelected = dev == SelectedDevice;
            bool isHovered = node == _hoveredNode;

            Brush fill = isSelected ? DeviceSelectedBrush : DeviceFillBrush;
            Pen pen = dev.IsBlocked
                ? DeviceBlockedPen
                : (dev.IsThrottled ? DeviceThrottledPen : (isSelected ? DeviceSelectedPen : DeviceNormalPen));

            // Outer halo when hovered or selected
            if (isSelected || isHovered)
            {
                dc.DrawEllipse(NodeHaloBrush, null, node.Position, node.Radius + 7, node.Radius + 7);
            }

            // Main node circle
            dc.DrawEllipse(fill, pen, node.Position, node.Radius, node.Radius);

            // Category Icon Glyph in node center (cached)
            FormattedText ftGlyph = GetOrCreateGlyph(dev.CategoryIconGlyph, dpi);
            dc.DrawText(ftGlyph, new Point(node.Position.X - (ftGlyph.Width / 2.0), node.Position.Y - (ftGlyph.Height / 2.0)));

            // Device Name label beneath node (cached per node unless text or selection changes)
            string displayName = dev.DisplayName;
            if (displayName.Length > 16) displayName = displayName[..15] + "…";

            if (node.NameFt == null || node.LastName != displayName || node.LastSelected != isSelected)
            {
                node.LastName = displayName;
                node.LastSelected = isSelected;
                node.NameFt = new FormattedText(
                    displayName,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    10.0,
                    isSelected ? TextPrimaryBrush : TextSecondaryBrush,
                    dpi);
            }

            dc.DrawText(node.NameFt, new Point(node.Position.X - (node.NameFt.Width / 2.0), node.Position.Y + node.Radius + 3.0));

            // Live rate label beneath name (cached per node unless text changes)
            string speedText = dev.DownloadSpeedText;
            if (node.SpeedFt == null || node.LastSpeed != speedText)
            {
                node.LastSpeed = speedText;
                node.SpeedFt = new FormattedText(
                    speedText,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    9.0,
                    SkyParticleBrush,
                    dpi);
            }

            dc.DrawText(node.SpeedFt, new Point(node.Position.X - (node.SpeedFt.Width / 2.0), node.Position.Y + node.Radius + 15.0));
        }

        // 6. Draw Center Gateway Node
        dc.DrawEllipse(null, GatewayGlowPen, center, 28, 28);
        dc.DrawEllipse(GatewayFillBrush, null, center, 24, 24);

        if (_cachedGwGlyph != null)
        {
            dc.DrawText(_cachedGwGlyph, new Point(center.X - (_cachedGwGlyph.Width / 2.0), center.Y - (_cachedGwGlyph.Height / 2.0)));
        }

        if (_cachedGwLabel != null)
        {
            dc.DrawText(_cachedGwLabel, new Point(center.X - (_cachedGwLabel.Width / 2.0), center.Y + 28.0));
        }
    }

    private static void EnsureStaticTextCache(double dpi)
    {
        if (Math.Abs(_cachedDpi - dpi) > 0.001)
        {
            _cachedDpi = dpi;
            GlyphCache.Clear();

            _cachedGwGlyph = new FormattedText(
                "📡",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                GlyphTypeface,
                16.0,
                TextPrimaryBrush,
                dpi);

            _cachedGwLabel = new FormattedText(
                "GATEWAY",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10.0,
                TextPrimaryBrush,
                dpi);
        }
    }

    private static FormattedText GetOrCreateGlyph(string glyph, double dpi)
    {
        if (GlyphCache.TryGetValue(glyph, out FormattedText? cached))
        {
            return cached;
        }

        FormattedText ft = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GlyphTypeface,
            14.0,
            TextPrimaryBrush,
            dpi);

        GlyphCache[glyph] = ft;
        return ft;
    }
}