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
/// Renders the Gateway at center with LAN devices orbiting around it, using bindable palette brushes
/// and zero heap allocations during the 60fps rendering loop.
/// </summary>
public sealed class NetworkTopologyControl : FrameworkElement
{
    private static readonly Color DefaultRadarBgColor = Color.FromArgb(166, 13, 16, 23);
    private static readonly Color DefaultGridColor = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color DefaultGatewayColor = Color.FromRgb(52, 211, 153);
    private static readonly Color DefaultDeviceFillColor = Color.FromArgb(225, 20, 24, 34);
    private static readonly Color DefaultDeviceSelectedColor = Color.FromArgb(245, 29, 35, 50);
    private static readonly Color DefaultDownloadColor = Color.FromRgb(56, 189, 248);
    private static readonly Color DefaultUploadColor = Color.FromRgb(167, 139, 250);
    private static readonly Color DefaultBlockedColor = Color.FromRgb(248, 113, 113);
    private static readonly Color DefaultThrottledColor = Color.FromRgb(251, 191, 36);
    private static readonly Color DefaultBypassedColor = Color.FromArgb(90, 132, 147, 168);
    private static readonly Color DefaultPrimaryTextColor = Color.FromRgb(248, 250, 252);
    private static readonly Color DefaultSecondaryTextColor = Color.FromRgb(176, 189, 206);

    private static readonly Brush DefaultRadarBgBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultRadarBgColor);
    private static readonly Brush DefaultGridBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultGridColor);
    private static readonly Brush DefaultGatewayBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultGatewayColor);
    private static readonly Brush DefaultDeviceFillBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultDeviceFillColor);
    private static readonly Brush DefaultDeviceSelectedBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultDeviceSelectedColor);
    private static readonly Brush DefaultDownloadBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultDownloadColor);
    private static readonly Brush DefaultUploadBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultUploadColor);
    private static readonly Brush DefaultBlockedBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultBlockedColor);
    private static readonly Brush DefaultThrottledBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultThrottledColor);
    private static readonly Brush DefaultBypassedBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultBypassedColor);
    private static readonly Brush DefaultPrimaryTextBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultPrimaryTextColor);
    private static readonly Brush DefaultSecondaryTextBrush = ControlRenderHelper.CreateFrozenSolidBrush(DefaultSecondaryTextColor);

    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Arial"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface GlyphTypeface = new(new FontFamily("Segoe UI Emoji, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush _frozenRadarBgBrush = DefaultRadarBgBrush;
    private Pen _radarGridPen = null!;
    private Pen _radarCrosshairPen = null!;
    private Brush _frozenGatewayFillBrush = DefaultGatewayBrush;
    private Pen _gatewayGlowPen = null!;
    private Pen _radarPulsePen = null!;
    private Brush _frozenDeviceFillBrush = DefaultDeviceFillBrush;
    private Brush _frozenDeviceSelectedBrush = DefaultDeviceSelectedBrush;
    private Pen _deviceNormalPen = null!;
    private Pen _deviceSelectedPen = null!;
    private Pen _deviceBlockedPen = null!;
    private Pen _deviceThrottledPen = null!;
    private Brush _frozenPrimaryTextBrush = DefaultPrimaryTextBrush;
    private Brush _frozenSecondaryTextBrush = DefaultSecondaryTextBrush;
    private Brush _skyParticleBrush = DefaultDownloadBrush;
    private Brush _violetParticleBrush = DefaultUploadBrush;
    private Brush _nodeHaloBrush = null!;
    private Pen _streamBlockedPen = null!;
    private Pen _streamThrottledPen = null!;
    private Pen _streamBypassedPen = null!;
    private readonly Pen[] _streamActivePens = new Pen[5];

    private double _cachedDpi = -1;
    private FormattedText? _cachedGwGlyph;
    private FormattedText? _cachedGwLabel;
    private readonly Dictionary<string, FormattedText> _glyphCache = [];

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

    public static readonly DependencyProperty RadarBackgroundBrushProperty =
        DependencyProperty.Register(nameof(RadarBackgroundBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultRadarBgBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty GridLineBrushProperty =
        DependencyProperty.Register(nameof(GridLineBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultGridBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty GatewayBrushProperty =
        DependencyProperty.Register(nameof(GatewayBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultGatewayBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty DeviceFillBrushProperty =
        DependencyProperty.Register(nameof(DeviceFillBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultDeviceFillBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty DeviceSelectedBrushProperty =
        DependencyProperty.Register(nameof(DeviceSelectedBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultDeviceSelectedBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty DownloadBrushProperty =
        DependencyProperty.Register(nameof(DownloadBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultDownloadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty UploadBrushProperty =
        DependencyProperty.Register(nameof(UploadBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultUploadBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty BlockedBrushProperty =
        DependencyProperty.Register(nameof(BlockedBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultBlockedBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty ThrottledBrushProperty =
        DependencyProperty.Register(nameof(ThrottledBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultThrottledBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty BypassedBrushProperty =
        DependencyProperty.Register(nameof(BypassedBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultBypassedBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty PrimaryTextBrushProperty =
        DependencyProperty.Register(nameof(PrimaryTextBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultPrimaryTextBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public static readonly DependencyProperty SecondaryTextBrushProperty =
        DependencyProperty.Register(nameof(SecondaryTextBrush), typeof(Brush), typeof(NetworkTopologyControl),
            new FrameworkPropertyMetadata(DefaultSecondaryTextBrush, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

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

    public Brush RadarBackgroundBrush
    {
        get => (Brush)GetValue(RadarBackgroundBrushProperty);
        set => SetValue(RadarBackgroundBrushProperty, value);
    }

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public Brush GatewayBrush
    {
        get => (Brush)GetValue(GatewayBrushProperty);
        set => SetValue(GatewayBrushProperty, value);
    }

    public Brush DeviceFillBrush
    {
        get => (Brush)GetValue(DeviceFillBrushProperty);
        set => SetValue(DeviceFillBrushProperty, value);
    }

    public Brush DeviceSelectedBrush
    {
        get => (Brush)GetValue(DeviceSelectedBrushProperty);
        set => SetValue(DeviceSelectedBrushProperty, value);
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

    public Brush BlockedBrush
    {
        get => (Brush)GetValue(BlockedBrushProperty);
        set => SetValue(BlockedBrushProperty, value);
    }

    public Brush ThrottledBrush
    {
        get => (Brush)GetValue(ThrottledBrushProperty);
        set => SetValue(ThrottledBrushProperty, value);
    }

    public Brush BypassedBrush
    {
        get => (Brush)GetValue(BypassedBrushProperty);
        set => SetValue(BypassedBrushProperty, value);
    }

    public Brush PrimaryTextBrush
    {
        get => (Brush)GetValue(PrimaryTextBrushProperty);
        set => SetValue(PrimaryTextBrushProperty, value);
    }

    public Brush SecondaryTextBrush
    {
        get => (Brush)GetValue(SecondaryTextBrushProperty);
        set => SetValue(SecondaryTextBrushProperty, value);
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NetworkTopologyControl control)
        {
            control.RebuildRenderResources();
            control.InvalidateTextCaches();
        }
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
        RebuildRenderResources();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void RebuildRenderResources()
    {
        _frozenRadarBgBrush = ControlRenderHelper.FreezeBrush(RadarBackgroundBrush, DefaultRadarBgBrush);

        Color gridColor = ControlRenderHelper.ExtractColor(GridLineBrush, DefaultGridColor);
        _radarGridPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(gridColor, 36), 1.0, dashStyle: new DashStyle([4, 4], 0));
        _radarCrosshairPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(gridColor, 20), 1.0);

        Color gwColor = ControlRenderHelper.ExtractColor(GatewayBrush, DefaultGatewayColor);
        _frozenGatewayFillBrush = ControlRenderHelper.CreateFrozenSolidBrush(gwColor, 235);
        _gatewayGlowPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(gwColor, 90), 5.0);
        _radarPulsePen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(gwColor, 85), 1.5);

        _frozenDeviceFillBrush = ControlRenderHelper.FreezeBrush(DeviceFillBrush, DefaultDeviceFillBrush);
        _frozenDeviceSelectedBrush = ControlRenderHelper.FreezeBrush(DeviceSelectedBrush, DefaultDeviceSelectedBrush);

        Color dlColor = ControlRenderHelper.ExtractColor(DownloadBrush, DefaultDownloadColor);
        Color ulColor = ControlRenderHelper.ExtractColor(UploadBrush, DefaultUploadColor);
        Color blkColor = ControlRenderHelper.ExtractColor(BlockedBrush, DefaultBlockedColor);
        Color thrColor = ControlRenderHelper.ExtractColor(ThrottledBrush, DefaultThrottledColor);

        _deviceNormalPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(dlColor, 185), 1.8);
        _deviceSelectedPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(dlColor, 255), 3.0);
        _deviceBlockedPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(blkColor, 255), 2.0);
        _deviceThrottledPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(thrColor, 255), 2.0);

        _frozenPrimaryTextBrush = ControlRenderHelper.FreezeBrush(PrimaryTextBrush, DefaultPrimaryTextBrush);
        _frozenSecondaryTextBrush = ControlRenderHelper.FreezeBrush(SecondaryTextBrush, DefaultSecondaryTextBrush);

        _skyParticleBrush = ControlRenderHelper.CreateFrozenSolidBrush(dlColor, 235);
        _violetParticleBrush = ControlRenderHelper.CreateFrozenSolidBrush(ulColor, 235);
        _nodeHaloBrush = ControlRenderHelper.CreateFrozenSolidBrush(dlColor, 50);

        _streamBlockedPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(blkColor, 230), 1.5, dashStyle: new DashStyle([3, 3], 0));
        _streamThrottledPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(thrColor, 145), 2.0);

        Color bypColor = ControlRenderHelper.ExtractColor(BypassedBrush, DefaultBypassedColor);
        _streamBypassedPen = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(bypColor, 80), 1.0);

        for (int i = 0; i < _streamActivePens.Length; i++)
        {
            double th = 1.2 + (i * 0.95);
            byte a = (byte)(80 + (i * 35));
            _streamActivePens[i] = ControlRenderHelper.CreateFrozenPen(ControlRenderHelper.CreateFrozenSolidBrush(dlColor, a), th);
        }
    }

    private void InvalidateTextCaches()
    {
        _cachedDpi = -1;
        _cachedGwGlyph = null;
        _cachedGwLabel = null;
        _glyphCache.Clear();

        for (int i = 0; i < _nodeLayouts.Count; i++)
        {
            _nodeLayouts[i].NameFt = null;
            _nodeLayouts[i].SpeedFt = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateRenderSubscription();

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopRendering();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRenderSubscription();

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
        SelectedDevice = hit?.ViewModel;
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

        // 1. Draw Background radar surface & concentric range rings
        dc.DrawRoundedRectangle(_frozenRadarBgBrush, null, new Rect(0, 0, width, height), 12.0, 12.0);

        dc.DrawLine(_radarCrosshairPen, new Point(center.X, 0), new Point(center.X, height));
        dc.DrawLine(_radarCrosshairPen, new Point(0, center.Y), new Point(width, center.Y));

        dc.DrawEllipse(null, _radarGridPen, center, maxRadius * 0.35, maxRadius * 0.35);
        dc.DrawEllipse(null, _radarGridPen, center, maxRadius * 0.70, maxRadius * 0.70);
        dc.DrawEllipse(null, _radarGridPen, center, maxRadius, maxRadius);

        double pulseRatio = (_animationTime * 0.3) % 1.0;
        double pulseRadius = maxRadius * pulseRatio;
        dc.PushOpacity(1.0 - pulseRatio);
        dc.DrawEllipse(null, _radarPulsePen, center, pulseRadius, pulseRadius);
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

        // 3. Layout Orbiting Nodes
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
        EnsureTextCache(dpi);

        // 4. Draw Connection Streams & Animated Particles
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
                streamPen = _streamBlockedPen;
            }
            else if (dev.IsThrottled)
            {
                streamPen = _streamThrottledPen;
            }
            else if (!dev.IsSpoofed)
            {
                streamPen = _streamBypassedPen;
            }
            else
            {
                int penIdx = Math.Clamp((int)(totalBps / (100.0 * 1024.0)), 0, 4);
                streamPen = _streamActivePens[penIdx];
            }

            dc.DrawLine(streamPen, center, node.Position);

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

                    Brush pBrush = downBps >= upBps ? _skyParticleBrush : _violetParticleBrush;
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

            Brush fill = isSelected ? _frozenDeviceSelectedBrush : _frozenDeviceFillBrush;
            Pen pen = dev.IsBlocked
                ? _deviceBlockedPen
                : (dev.IsThrottled ? _deviceThrottledPen : (isSelected ? _deviceSelectedPen : _deviceNormalPen));

            if (isSelected || isHovered)
            {
                dc.DrawEllipse(_nodeHaloBrush, null, node.Position, node.Radius + 7, node.Radius + 7);
            }

            dc.DrawEllipse(fill, pen, node.Position, node.Radius, node.Radius);

            FormattedText ftGlyph = GetOrCreateGlyph(dev.CategoryIconGlyph, dpi);
            dc.DrawText(ftGlyph, new Point(node.Position.X - (ftGlyph.Width / 2.0), node.Position.Y - (ftGlyph.Height / 2.0)));

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
                    isSelected ? _frozenPrimaryTextBrush : _frozenSecondaryTextBrush,
                    dpi);
            }

            dc.DrawText(node.NameFt, new Point(node.Position.X - (node.NameFt.Width / 2.0), node.Position.Y + node.Radius + 3.0));

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
                    _skyParticleBrush,
                    dpi);
            }

            dc.DrawText(node.SpeedFt, new Point(node.Position.X - (node.SpeedFt.Width / 2.0), node.Position.Y + node.Radius + 15.0));
        }

        // 6. Draw Center Gateway Node
        dc.DrawEllipse(null, _gatewayGlowPen, center, 28, 28);
        dc.DrawEllipse(_frozenGatewayFillBrush, null, center, 24, 24);

        if (_cachedGwGlyph != null)
        {
            dc.DrawText(_cachedGwGlyph, new Point(center.X - (_cachedGwGlyph.Width / 2.0), center.Y - (_cachedGwGlyph.Height / 2.0)));
        }

        if (_cachedGwLabel != null)
        {
            dc.DrawText(_cachedGwLabel, new Point(center.X - (_cachedGwLabel.Width / 2.0), center.Y + 28.0));
        }
    }

    private void EnsureTextCache(double dpi)
    {
        if (Math.Abs(_cachedDpi - dpi) > 0.001 || _cachedGwGlyph is null || _cachedGwLabel is null)
        {
            _cachedDpi = dpi;
            _glyphCache.Clear();

            _cachedGwGlyph = new FormattedText(
                "📡",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                GlyphTypeface,
                16.0,
                _frozenPrimaryTextBrush,
                dpi);

            _cachedGwLabel = new FormattedText(
                "GATEWAY",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10.0,
                _frozenPrimaryTextBrush,
                dpi);
        }
    }

    private FormattedText GetOrCreateGlyph(string glyph, double dpi)
    {
        if (_glyphCache.TryGetValue(glyph, out FormattedText? cached))
        {
            return cached;
        }

        FormattedText ft = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GlyphTypeface,
            14.0,
            _frozenPrimaryTextBrush,
            dpi);

        if (_glyphCache.Count < 128)
        {
            _glyphCache[glyph] = ft;
        }

        return ft;
    }
}
