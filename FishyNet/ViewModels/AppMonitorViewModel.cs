using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using FishyNet.Controls;
using FishyNet.Core.Formatting;
using FishyNet.Features.AppMonitor;

namespace FishyNet.ViewModels;

public enum AppMonitorTab
{
    Apps,
    Websites,
    TrafficTypes,
    Alerts
}

public enum TrafficSourceFilter
{
    AllLanDevices,
    ThisPcOnly
}

public enum UsagePeriodPreset
{
    LiveSession,
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    ThisMonth,
    CustomRange
}

public sealed record UsagePeriodOption(UsagePeriodPreset Preset, string Label);

/// <summary>
/// Row presentation model for a tracked local application in the App &amp; Website Monitor.
/// </summary>
public sealed class AppTrafficRowViewModel : ObservableObject
{
    private int _processId;
    private string _appName = string.Empty;
    private string _displayName = string.Empty;
    private string _exePath = string.Empty;
    private string _publisher = string.Empty;
    private BitmapSource? _icon;
    private long _downloadBps;
    private long _uploadBps;
    private long _totalDownloadBytes;
    private long _totalUploadBytes;
    private long _totalBytes;
    private string _downloadRateText = "0 B/s";
    private string _uploadRateText = "0 B/s";
    private string _totalVolumeText = "0 B";
    private string _protocolSummary = "TCP/UDP";
    private string _topHostsText = "—";
    private int _activeConnections;
    private bool _isNewFirstAccess;
    private bool _isFirewallBlocked;
    private double _bandwidthShareRatio;
    private bool _useBitsPerSecond;

    public string AppKey { get; }

    public int ProcessId
    {
        get => _processId;
        private set => SetProperty(ref _processId, value);
    }

    public string AppName
    {
        get => _appName;
        private set => SetProperty(ref _appName, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string ExePath
    {
        get => _exePath;
        private set
        {
            if (SetProperty(ref _exePath, value))
            {
                OnPropertyChanged(nameof(CanBlockFirewall));
            }
        }
    }

    public string Publisher
    {
        get => _publisher;
        private set => SetProperty(ref _publisher, value);
    }

    public BitmapSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public long DownloadBps
    {
        get => _downloadBps;
        private set => SetProperty(ref _downloadBps, value);
    }

    public long UploadBps
    {
        get => _uploadBps;
        private set => SetProperty(ref _uploadBps, value);
    }

    public long TotalDownloadBytes
    {
        get => _totalDownloadBytes;
        private set => SetProperty(ref _totalDownloadBytes, value);
    }

    public long TotalUploadBytes
    {
        get => _totalUploadBytes;
        private set => SetProperty(ref _totalUploadBytes, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value);
    }

    public string DownloadRateText
    {
        get => _downloadRateText;
        private set => SetProperty(ref _downloadRateText, value);
    }

    public string UploadRateText
    {
        get => _uploadRateText;
        private set => SetProperty(ref _uploadRateText, value);
    }

    public string TotalVolumeText
    {
        get => _totalVolumeText;
        private set => SetProperty(ref _totalVolumeText, value);
    }

    public string ProtocolSummary
    {
        get => _protocolSummary;
        private set => SetProperty(ref _protocolSummary, value);
    }

    public string TopHostsText
    {
        get => _topHostsText;
        private set => SetProperty(ref _topHostsText, value);
    }

    public int ActiveConnections
    {
        get => _activeConnections;
        private set => SetProperty(ref _activeConnections, value);
    }

    public bool IsNewFirstAccess
    {
        get => _isNewFirstAccess;
        internal set => SetProperty(ref _isNewFirstAccess, value);
    }

    public bool IsFirewallBlocked
    {
        get => _isFirewallBlocked;
        private set
        {
            if (SetProperty(ref _isFirewallBlocked, value))
            {
                OnPropertyChanged(nameof(BlockButtonText));
            }
        }
    }

    public string BlockButtonText => IsFirewallBlocked ? "Unblock" : "Block";

    public bool CanBlockFirewall =>
        !string.IsNullOrWhiteSpace(ExePath) &&
        ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public double BandwidthShareRatio
    {
        get => _bandwidthShareRatio;
        private set => SetProperty(ref _bandwidthShareRatio, value);
    }

    public AppTrafficRowViewModel(AppTrafficEntry entry, double bandwidthShareRatio, bool useBitsPerSecond)
    {
        AppKey = entry.AppKey;
        UpdateFrom(entry, bandwidthShareRatio, useBitsPerSecond);
    }

    internal void UpdateFrom(AppTrafficEntry entry, double bandwidthShareRatio, bool useBitsPerSecond)
    {
        _useBitsPerSecond = useBitsPerSecond;
        ProcessId = entry.ProcessId;
        AppName = entry.ProcessName;
        DisplayName = entry.DisplayName;
        ExePath = entry.ExePath;
        Publisher = entry.Publisher;
        Icon = entry.Icon;
        DownloadBps = entry.DownloadBps;
        UploadBps = entry.UploadBps;
        TotalDownloadBytes = entry.TotalDownloadBytes;
        TotalUploadBytes = entry.TotalUploadBytes;
        TotalBytes = entry.TotalBytes;
        ProtocolSummary = entry.ProtocolsSummary;
        TopHostsText = entry.TopHostsSummary;
        ActiveConnections = entry.ActiveConnections;
        IsNewFirstAccess = entry.IsNewFirstAccess;
        IsFirewallBlocked = entry.IsFirewallBlocked;
        BandwidthShareRatio = Math.Clamp(bandwidthShareRatio, 0.0, 1.0);

        RefreshFormattedStrings();
    }

    internal void SetFirewallBlocked(bool isBlocked) => IsFirewallBlocked = isBlocked;

    internal void SetUnitMode(bool useBitsPerSecond)
    {
        if (_useBitsPerSecond != useBitsPerSecond)
        {
            _useBitsPerSecond = useBitsPerSecond;
            RefreshFormattedStrings();
        }
    }

    private void RefreshFormattedStrings()
    {
        DownloadRateText = BandwidthFormatter.FormatRate(DownloadBps, _useBitsPerSecond);
        UploadRateText = BandwidthFormatter.FormatRate(UploadBps, _useBitsPerSecond);
        TotalVolumeText = BandwidthFormatter.FormatBytes(TotalBytes);
    }
}

/// <summary>
/// Row presentation model for a contacted website/domain in the App &amp; Website Monitor.
/// </summary>
public sealed class WebsiteTrafficRowViewModel : ObservableObject
{
    private string _domain = string.Empty;
    private string _resolvedIp = string.Empty;
    private string _protocolPort = string.Empty;
    private string _sourceHost = string.Empty;
    private string _sourceHostIp = string.Empty;
    private bool _isLocalHost;
    private string _appName = string.Empty;
    private long _downloadBytes;
    private long _uploadBytes;
    private long _totalBytes;
    private string _totalVolumeText = "0 B";
    private long _hitCount;
    private string _lastSeenText = string.Empty;
    private bool _isSinkholed;

    public string EntryKey { get; }

    public string Domain
    {
        get => _domain;
        private set => SetProperty(ref _domain, value);
    }

    public string ResolvedIp
    {
        get => _resolvedIp;
        private set => SetProperty(ref _resolvedIp, value);
    }

    public string ProtocolPort
    {
        get => _protocolPort;
        private set => SetProperty(ref _protocolPort, value);
    }

    public string SourceHost
    {
        get => _sourceHost;
        private set => SetProperty(ref _sourceHost, value);
    }

    public string SourceHostIp
    {
        get => _sourceHostIp;
        private set => SetProperty(ref _sourceHostIp, value);
    }

    public bool IsLocalHost
    {
        get => _isLocalHost;
        private set => SetProperty(ref _isLocalHost, value);
    }

    public string AppName
    {
        get => _appName;
        private set => SetProperty(ref _appName, value);
    }

    public long DownloadBytes
    {
        get => _downloadBytes;
        private set => SetProperty(ref _downloadBytes, value);
    }

    public long UploadBytes
    {
        get => _uploadBytes;
        private set => SetProperty(ref _uploadBytes, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value);
    }

    public string TotalVolumeText
    {
        get => _totalVolumeText;
        private set => SetProperty(ref _totalVolumeText, value);
    }

    public long HitCount
    {
        get => _hitCount;
        private set => SetProperty(ref _hitCount, value);
    }

    public string LastSeenText
    {
        get => _lastSeenText;
        private set => SetProperty(ref _lastSeenText, value);
    }

    public bool IsSinkholed
    {
        get => _isSinkholed;
        internal set
        {
            if (SetProperty(ref _isSinkholed, value))
            {
                OnPropertyChanged(nameof(SinkholeButtonText));
            }
        }
    }

    public string SinkholeButtonText => IsSinkholed ? "Unsinkhole" : "Sinkhole";

    public WebsiteTrafficRowViewModel(WebsiteTrafficEntry entry, bool? liveSinkholedOverride = null)
    {
        EntryKey = entry.EntryKey;
        UpdateFrom(entry, liveSinkholedOverride);
    }

    internal void UpdateFrom(WebsiteTrafficEntry entry, bool? liveSinkholedOverride = null)
    {
        Domain = entry.Domain;
        ResolvedIp = entry.ResolvedIp;
        ProtocolPort = entry.ProtocolPort;
        SourceHost = entry.SourceHost;
        SourceHostIp = entry.SourceHostIp;
        IsLocalHost = entry.IsLocalHost;
        AppName = entry.AppName;
        DownloadBytes = entry.DownloadBytes;
        UploadBytes = entry.UploadBytes;
        TotalBytes = entry.TotalBytes;
        TotalVolumeText = entry.TotalBytesText;
        HitCount = entry.HitCount;
        LastSeenText = entry.LastSeenLocalText;
        IsSinkholed = liveSinkholedOverride ?? entry.IsSinkholed;
    }
}

/// <summary>
/// Aggregated protocol traffic breakdown row for the Traffic Types sub-tab.
/// </summary>
public sealed class TrafficTypeBreakdownRow : ObservableObject
{
    private long _downloadBytes;
    private long _uploadBytes;
    private long _totalBytes;
    private string _downloadVolumeText = "0 B";
    private string _uploadVolumeText = "0 B";
    private string _totalVolumeText = "0 B";
    private double _shareRatio;
    private string _sharePercentText = "0%";
    private int _entryCount;

    public string TypeName { get; }

    public long DownloadBytes
    {
        get => _downloadBytes;
        private set => SetProperty(ref _downloadBytes, value);
    }

    public long UploadBytes
    {
        get => _uploadBytes;
        private set => SetProperty(ref _uploadBytes, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value);
    }

    public string DownloadVolumeText
    {
        get => _downloadVolumeText;
        private set => SetProperty(ref _downloadVolumeText, value);
    }

    public string UploadVolumeText
    {
        get => _uploadVolumeText;
        private set => SetProperty(ref _uploadVolumeText, value);
    }

    public string TotalVolumeText
    {
        get => _totalVolumeText;
        private set => SetProperty(ref _totalVolumeText, value);
    }

    public double ShareRatio
    {
        get => _shareRatio;
        private set => SetProperty(ref _shareRatio, value);
    }

    public string SharePercentText
    {
        get => _sharePercentText;
        private set => SetProperty(ref _sharePercentText, value);
    }

    public int EntryCount
    {
        get => _entryCount;
        private set => SetProperty(ref _entryCount, value);
    }

    public TrafficTypeBreakdownRow(string typeName)
    {
        TypeName = typeName;
    }

    internal void Update(long downloadBytes, long uploadBytes, double shareRatio, int entryCount)
    {
        DownloadBytes = downloadBytes;
        UploadBytes = uploadBytes;
        TotalBytes = downloadBytes + uploadBytes;
        DownloadVolumeText = BandwidthFormatter.FormatBytes(downloadBytes);
        UploadVolumeText = BandwidthFormatter.FormatBytes(uploadBytes);
        TotalVolumeText = BandwidthFormatter.FormatBytes(TotalBytes);
        ShareRatio = Math.Clamp(shareRatio, 0.0, 1.0);
        SharePercentText = $"{(ShareRatio * 100.0):F1}%";
        EntryCount = entryCount;
    }
}

/// <summary>
/// GlassWire-inspired Application, Website, Protocol Breakdown, Time-Machine, and Alert ViewModel.
/// Headless-safe (works cleanly when Application.Current is null) and AOT-compatible.
/// </summary>
public sealed class AppMonitorViewModel : ObservableObject
{
    private const int MaxDisplayedWebsites = 500;
    private const int MaxDisplayedAlerts = 200;

    private static readonly string[] CanonicalTrafficTypes =
    [
        "TLS / HTTPS",
        "QUIC / UDP",
        "HTTP",
        "DNS",
        "TCP",
        "UDP",
        "Other"
    ];

    private static readonly IReadOnlyList<UsagePeriodOption> UsagePeriodOptions =
    [
        new UsagePeriodOption(UsagePeriodPreset.LiveSession, "Live Session"),
        new UsagePeriodOption(UsagePeriodPreset.Today, "Today"),
        new UsagePeriodOption(UsagePeriodPreset.Yesterday, "Yesterday"),
        new UsagePeriodOption(UsagePeriodPreset.Last7Days, "Last 7 Days"),
        new UsagePeriodOption(UsagePeriodPreset.Last30Days, "Last 30 Days"),
        new UsagePeriodOption(UsagePeriodPreset.ThisMonth, "This Month"),
        new UsagePeriodOption(UsagePeriodPreset.CustomRange, "Custom Range...")
    ];

    private readonly AppTrafficTracker _appTracker;
    private readonly WebsiteTrafficTracker _websiteTracker;
    private readonly TrafficTimelineStore _timelineStore;
    private readonly AppMonitorHistoryStore? _historyStore;
    private readonly IAppFirewallController? _firewallController;
    private readonly Func<WebsiteTrafficRowViewModel, (bool Success, bool IsNowSinkholed, string Message)>? _toggleSinkholeHandler;
    private readonly Func<string, string, bool?>? _liveSinkholeLookup;
    private readonly Action<string>? _statusReporter;

    private AppMonitorTab _selectedTab = AppMonitorTab.Apps;
    private TrafficSourceFilter _sourceFilter = TrafficSourceFilter.AllLanDevices;
    private TimeMachineWindow _selectedTimeWindow = TimeMachineWindow.Live;
    private UsagePeriodOption _selectedUsagePeriod = UsagePeriodOptions[0];
    private DateTime _customFromDate = DateTime.Today.AddDays(-6);
    private DateTime _customToDate = DateTime.Today;
    private bool _isClampingDates;
    private string _activeDateRangeSummary = "Live Session";
    private string _historicalTotalRxText = "0 B";
    private string _historicalTotalTxText = "0 B";
    private string _searchQuery = string.Empty;
    private bool _useBitsPerSecond;

    private IReadOnlyList<TimelineBucketPoint> _timelineBuckets = [];
    private IReadOnlyList<TimelineSeriesDescriptor> _timelineSeries = [];
    private IReadOnlyList<TimelineAlertMarker> _timelineAlerts = [];
    private IReadOnlyList<TimelineBucketSnapshot> _rawTimelineBuckets = [];

    private int _inspectedIndex = -1;
    private int _hoveredIndex = -1;
    private bool _isInspectLocked;
    private string _inspectedSummary = "Waiting for network traffic...";
    private string _peakTrafficText = "0 B/s";
    private string _windowTotalRxText = "0 B";
    private string _windowTotalTxText = "0 B";

    private AppTrafficRowViewModel? _selectedApplication;
    private WebsiteTrafficRowViewModel? _selectedWebsite;
    private MonitorAlertRecord? _latestBannerAlert;
    private int _unacknowledgedAlertCount;
    private int _activeAppCount;
    private int _uniqueDomainCount;
    private string _totalAppDownloadRateText = "0 B/s";
    private string _totalAppUploadRateText = "0 B/s";
    private string _statusMessage = "App & Website Monitor ready.";

    private AppTrafficSnapshot? _lastAppSnapshot;
    private WebsiteTrafficSnapshot? _lastAllWebSnapshot;
    private AppTrafficSnapshot? _lastHistoricalAppSnapshot;
    private WebsiteTrafficSnapshot? _lastHistoricalWebSnapshot;
    private DateTime? _lastRefreshUtc;

    public ObservableCollection<AppTrafficRowViewModel> Applications { get; } = [];
    public ObservableCollection<WebsiteTrafficRowViewModel> Websites { get; } = [];
    public ObservableCollection<TrafficTypeBreakdownRow> TrafficTypes { get; } = [];
    public ObservableCollection<MonitorAlertRecord> Alerts { get; } = [];

    public RelayCommand SelectTabCommand { get; }
    public RelayCommand SetSourceFilterCommand { get; }
    public RelayCommand SelectTimeWindowCommand { get; }
    public RelayCommand UnlockInspectCommand { get; }
    public RelayCommand ToggleBlockAppCommand { get; }
    public RelayCommand AcknowledgeAppCommand { get; }
    public RelayCommand ToggleSinkholeDomainCommand { get; }
    public RelayCommand AcknowledgeAlertCommand { get; }
    public RelayCommand AcknowledgeAllAlertsCommand { get; }
    public RelayCommand ClearMonitorHistoryCommand { get; }
    public RelayCommand ApplyCustomDateRangeCommand { get; }

    public AppMonitorViewModel(
        AppTrafficTracker appTracker,
        WebsiteTrafficTracker websiteTracker,
        TrafficTimelineStore timelineStore,
        IAppFirewallController? firewallController = null,
        Func<WebsiteTrafficRowViewModel, (bool Success, bool IsNowSinkholed, string Message)>? toggleSinkholeHandler = null,
        Func<string, string, bool?>? liveSinkholeLookup = null,
        Action<string>? statusReporter = null,
        bool useBitsPerSecond = false,
        AppMonitorHistoryStore? historyStore = null)
    {
        _appTracker = appTracker ?? throw new ArgumentNullException(nameof(appTracker));
        _websiteTracker = websiteTracker ?? throw new ArgumentNullException(nameof(websiteTracker));
        _timelineStore = timelineStore ?? throw new ArgumentNullException(nameof(timelineStore));
        _historyStore = historyStore;
        _firewallController = firewallController;
        _toggleSinkholeHandler = toggleSinkholeHandler;
        _liveSinkholeLookup = liveSinkholeLookup;
        _statusReporter = statusReporter;
        _useBitsPerSecond = useBitsPerSecond;

        for (int i = 0; i < CanonicalTrafficTypes.Length; i++)
        {
            TrafficTypes.Add(new TrafficTypeBreakdownRow(CanonicalTrafficTypes[i]));
        }

        SelectTabCommand = new RelayCommand(ExecuteSelectTab);
        SetSourceFilterCommand = new RelayCommand(ExecuteSetSourceFilter);
        SelectTimeWindowCommand = new RelayCommand(ExecuteSelectTimeWindow);
        UnlockInspectCommand = new RelayCommand(UnlockInspect);
        ToggleBlockAppCommand = new RelayCommand(param => _ = ToggleBlockAppAsync(ResolveAppRow(param)));
        AcknowledgeAppCommand = new RelayCommand(param => ExecuteAcknowledgeApp(ResolveAppRow(param)));
        ToggleSinkholeDomainCommand = new RelayCommand(param => ExecuteToggleSinkholeDomain(ResolveWebsiteRow(param)));
        AcknowledgeAlertCommand = new RelayCommand(ExecuteAcknowledgeAlert);
        AcknowledgeAllAlertsCommand = new RelayCommand(ExecuteAcknowledgeAllAlerts);
        ClearMonitorHistoryCommand = new RelayCommand(ExecuteClearMonitorHistory);
        ApplyCustomDateRangeCommand = new RelayCommand(ExecuteApplyCustomDateRange);

        RefreshUnitTexts();
    }

    public AppMonitorTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAppsTab));
                OnPropertyChanged(nameof(IsWebsitesTab));
                OnPropertyChanged(nameof(IsTrafficTypesTab));
                OnPropertyChanged(nameof(IsAlertsTab));
            }
        }
    }

    public bool IsAppsTab => _selectedTab == AppMonitorTab.Apps;
    public bool IsWebsitesTab => _selectedTab == AppMonitorTab.Websites;
    public bool IsTrafficTypesTab => _selectedTab == AppMonitorTab.TrafficTypes;
    public bool IsAlertsTab => _selectedTab == AppMonitorTab.Alerts;

    public TrafficSourceFilter SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (SetProperty(ref _sourceFilter, value))
            {
                OnPropertyChanged(nameof(IsAllLanSource));
                OnPropertyChanged(nameof(IsThisPcSource));
                RefreshFromCurrentSnapshots();
            }
        }
    }

    public bool IsAllLanSource => _sourceFilter == TrafficSourceFilter.AllLanDevices;
    public bool IsThisPcSource => _sourceFilter == TrafficSourceFilter.ThisPcOnly;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref _searchQuery, normalized))
            {
                ApplyAppAndWebsiteFilters();
            }
        }
    }

    public IReadOnlyList<UsagePeriodOption> AvailableUsagePeriods => UsagePeriodOptions;

    public UsagePeriodOption SelectedUsagePeriod
    {
        get => _selectedUsagePeriod;
        set
        {
            UsagePeriodOption next = value ?? UsagePeriodOptions[0];
            if (SetProperty(ref _selectedUsagePeriod, next))
            {
                OnPropertyChanged(nameof(IsCustomDateRange));
                OnPropertyChanged(nameof(IsHistoricalMode));
                RefreshForUsagePeriodChange();
            }
        }
    }

    public bool IsCustomDateRange => _selectedUsagePeriod.Preset == UsagePeriodPreset.CustomRange;
    public bool IsHistoricalMode => _selectedUsagePeriod.Preset != UsagePeriodPreset.LiveSession;

    public DateTime CustomFromDate
    {
        get => _customFromDate;
        set
        {
            DateTime normalized = value.Date;
            if (SetProperty(ref _customFromDate, normalized) && !_isClampingDates)
            {
                if (_customFromDate > _customToDate)
                {
                    _isClampingDates = true;
                    try
                    {
                        CustomToDate = _customFromDate;
                    }
                    finally
                    {
                        _isClampingDates = false;
                    }
                }

                if (IsCustomDateRange)
                {
                    RefreshForUsagePeriodChange();
                }
            }
        }
    }

    public DateTime CustomToDate
    {
        get => _customToDate;
        set
        {
            DateTime normalized = value.Date;
            if (SetProperty(ref _customToDate, normalized) && !_isClampingDates)
            {
                if (_customToDate < _customFromDate)
                {
                    _isClampingDates = true;
                    try
                    {
                        CustomFromDate = _customToDate;
                    }
                    finally
                    {
                        _isClampingDates = false;
                    }
                }

                if (IsCustomDateRange)
                {
                    RefreshForUsagePeriodChange();
                }
            }
        }
    }

    public string ActiveDateRangeSummary
    {
        get => _activeDateRangeSummary;
        private set => SetProperty(ref _activeDateRangeSummary, value);
    }

    public string HistoricalTotalRxText
    {
        get => _historicalTotalRxText;
        private set => SetProperty(ref _historicalTotalRxText, value);
    }

    public string HistoricalTotalTxText
    {
        get => _historicalTotalTxText;
        private set => SetProperty(ref _historicalTotalTxText, value);
    }

    public TimeMachineWindow SelectedTimeWindow
    {
        get => _selectedTimeWindow;
        set
        {
            if (SetProperty(ref _selectedTimeWindow, value))
            {
                OnPropertyChanged(nameof(IsLiveWindow));
                OnPropertyChanged(nameof(IsFiveMinutesWindow));
                OnPropertyChanged(nameof(IsOneHourWindow));
                OnPropertyChanged(nameof(IsTwentyFourHoursWindow));
                OnPropertyChanged(nameof(IsSevenDaysWindow));

                _isInspectLocked = false;
                _inspectedIndex = -1;
                _hoveredIndex = -1;
                OnPropertyChanged(nameof(IsInspectLocked));
                OnPropertyChanged(nameof(InspectedIndex));
                OnPropertyChanged(nameof(HoveredIndex));

                RefreshTimeline();
            }
        }
    }

    public bool IsLiveWindow => _selectedTimeWindow == TimeMachineWindow.Live;
    public bool IsFiveMinutesWindow => _selectedTimeWindow == TimeMachineWindow.FiveMinutes;
    public bool IsOneHourWindow => _selectedTimeWindow == TimeMachineWindow.OneHour;
    public bool IsTwentyFourHoursWindow => _selectedTimeWindow == TimeMachineWindow.TwentyFourHours;
    public bool IsSevenDaysWindow => _selectedTimeWindow == TimeMachineWindow.SevenDays;

    public IReadOnlyList<TimelineBucketPoint> TimelineBuckets
    {
        get => _timelineBuckets;
        private set => SetProperty(ref _timelineBuckets, value);
    }

    public IReadOnlyList<TimelineSeriesDescriptor> TimelineSeries
    {
        get => _timelineSeries;
        private set => SetProperty(ref _timelineSeries, value);
    }

    public IReadOnlyList<TimelineAlertMarker> TimelineAlerts
    {
        get => _timelineAlerts;
        private set => SetProperty(ref _timelineAlerts, value);
    }

    public int InspectedIndex
    {
        get => _inspectedIndex;
        set
        {
            if (SetProperty(ref _inspectedIndex, value))
            {
                UpdateInspectedSummary();
            }
        }
    }

    public int HoveredIndex
    {
        get => _hoveredIndex;
        set
        {
            if (SetProperty(ref _hoveredIndex, value))
            {
                UpdateInspectedSummary();
            }
        }
    }

    public bool IsInspectLocked
    {
        get => _isInspectLocked;
        set
        {
            if (SetProperty(ref _isInspectLocked, value))
            {
                UpdateInspectedSummary();
            }
        }
    }

    public string InspectedSummary
    {
        get => _inspectedSummary;
        private set => SetProperty(ref _inspectedSummary, value);
    }

    public string PeakTrafficText
    {
        get => _peakTrafficText;
        private set => SetProperty(ref _peakTrafficText, value);
    }

    public string WindowTotalRxText
    {
        get => _windowTotalRxText;
        private set => SetProperty(ref _windowTotalRxText, value);
    }

    public string WindowTotalTxText
    {
        get => _windowTotalTxText;
        private set => SetProperty(ref _windowTotalTxText, value);
    }

    public AppTrafficRowViewModel? SelectedApplication
    {
        get => _selectedApplication;
        set => SetProperty(ref _selectedApplication, value);
    }

    public WebsiteTrafficRowViewModel? SelectedWebsite
    {
        get => _selectedWebsite;
        set => SetProperty(ref _selectedWebsite, value);
    }

    public int ActiveAppCount
    {
        get => _activeAppCount;
        private set => SetProperty(ref _activeAppCount, value);
    }

    public string TotalAppDownloadRateText
    {
        get => _totalAppDownloadRateText;
        private set => SetProperty(ref _totalAppDownloadRateText, value);
    }

    public string TotalAppUploadRateText
    {
        get => _totalAppUploadRateText;
        private set => SetProperty(ref _totalAppUploadRateText, value);
    }

    public int UniqueDomainCount
    {
        get => _uniqueDomainCount;
        private set => SetProperty(ref _uniqueDomainCount, value);
    }

    public MonitorAlertRecord? LatestBannerAlert
    {
        get => _latestBannerAlert;
        private set
        {
            if (SetProperty(ref _latestBannerAlert, value))
            {
                OnPropertyChanged(nameof(HasBannerAlert));
            }
        }
    }

    public bool HasBannerAlert => _latestBannerAlert is not null;

    public int UnacknowledgedAlertCount
    {
        get => _unacknowledgedAlertCount;
        private set
        {
            if (SetProperty(ref _unacknowledgedAlertCount, value))
            {
                OnPropertyChanged(nameof(HasUnacknowledgedAlerts));
            }
        }
    }

    public bool HasUnacknowledgedAlerts => _unacknowledgedAlertCount > 0;

    public bool UseBitsPerSecond
    {
        get => _useBitsPerSecond;
        set => SetUnitMode(value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                _statusReporter?.Invoke(value);
            }
        }
    }

    public void SetUnitMode(bool useBitsPerSecond)
    {
        if (!SetProperty(ref _useBitsPerSecond, useBitsPerSecond, nameof(UseBitsPerSecond)))
        {
            return;
        }

        for (int i = 0; i < Applications.Count; i++)
        {
            Applications[i].SetUnitMode(useBitsPerSecond);
        }

        RefreshUnitTexts();
        RefreshTimeline();
    }

    /// <summary>
    /// Refreshes applications, websites, protocol breakdown, timeline series/buckets/markers, and alerts
    /// from the underlying trackers without resetting AppTrafficTracker live rate deltas.
    /// </summary>
    public void RefreshFromTrackers(DateTime? utcNow = null)
    {
        _lastRefreshUtc = utcNow;
        _lastAppSnapshot = _appTracker.GetSnapshot(utcNow);
        _lastAllWebSnapshot = _websiteTracker.GetSnapshot(localOnlyFilter: null, timestampUtc: utcNow);

        RefreshFromCurrentSnapshots();
        RefreshTimeline();
        RefreshAlertsOnly();
    }

    /// <summary>
    /// Lightweight alert-only refresh used when App Monitor is not the active tab so header badges stay live.
    /// </summary>
    public void RefreshAlertsOnly()
    {
        IReadOnlyList<MonitorAlertRecord> latest = _timelineStore.GetAlerts(MaxDisplayedAlerts);
        SyncAlertsCollection(latest);

        int unack = 0;
        MonitorAlertRecord? firstUnack = null;
        for (int i = 0; i < latest.Count; i++)
        {
            if (!latest[i].IsAcknowledged)
            {
                unack++;
                firstUnack ??= latest[i];
            }
        }

        UnacknowledgedAlertCount = unack;
        LatestBannerAlert = firstUnack;
    }

    /// <summary>
    /// Toggles Windows Firewall block/unblock state for the specified application row asynchronously.
    /// </summary>
    public async Task ToggleBlockAppAsync(AppTrafficRowViewModel? row, CancellationToken ct = default)
    {
        if (row is null)
        {
            return;
        }

        if (_firewallController is null)
        {
            ReportStatus("Windows Firewall controller is unavailable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(row.ExePath))
        {
            ReportStatus($"Cannot block {row.DisplayName}: executable path is unavailable.");
            return;
        }

        bool wasBlocked = row.IsFirewallBlocked;
        AppFirewallResult result;
        try
        {
            result = wasBlocked
                ? await _firewallController.UnblockAppAsync(row.ExePath, ct)
                : await _firewallController.BlockAppAsync(row.ExePath, row.DisplayName, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ReportStatus($"Firewall update failed for {row.DisplayName}: {ex.Message}");
            return;
        }

        DispatchToUiOrInline(() =>
        {
            if (result.Success)
            {
                bool isNowBlocked = !wasBlocked;
                row.SetFirewallBlocked(isNowBlocked);
                for (int i = 0; i < Applications.Count; i++)
                {
                    if (string.Equals(Applications[i].ExePath, row.ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Applications[i].SetFirewallBlocked(isNowBlocked);
                    }
                }

                _timelineStore.RecordAlert(new MonitorAlertRecord(
                    0,
                    DateTime.UtcNow,
                    MonitorAlertKind.FirewallAction,
                    MonitorAlertSeverity.Info,
                    isNowBlocked ? $"Firewall Blocked: {row.AppName}" : $"Firewall Unblocked: {row.AppName}",
                    result.Message,
                    row.AppName,
                    row.ExePath,
                    IsAcknowledged: true));

                RefreshAlertsOnly();
                RefreshTimeline();
                ReportStatus(string.IsNullOrWhiteSpace(result.Message)
                    ? $"{(isNowBlocked ? "Blocked" : "Unblocked")} {row.DisplayName}."
                    : result.Message);
            }
            else
            {
                ReportStatus(string.IsNullOrWhiteSpace(result.Message)
                    ? $"Firewall update failed for {row.DisplayName}."
                    : result.Message);
            }
        });
    }

    private void RefreshFromCurrentSnapshots()
    {
        if (IsHistoricalMode)
        {
            RefreshHistoricalMode();
            return;
        }

        AppTrafficSnapshot appSnap = _lastAppSnapshot ?? _appTracker.GetSnapshot(_lastRefreshUtc);
        WebsiteTrafficSnapshot allWebSnap = _lastAllWebSnapshot ?? _websiteTracker.GetSnapshot(localOnlyFilter: null, _lastRefreshUtc);

        _lastAppSnapshot = appSnap;
        _lastAllWebSnapshot = allWebSnap;

        ActiveAppCount = appSnap.Applications.Count;
        TotalAppDownloadRateText = BandwidthFormatter.FormatRate(appSnap.TotalDownloadBps, _useBitsPerSecond);
        TotalAppUploadRateText = BandwidthFormatter.FormatRate(appSnap.TotalUploadBps, _useBitsPerSecond);

        ApplyAppAndWebsiteFilters(appSnap, allWebSnap);
        RefreshTrafficTypesBreakdown(appSnap, allWebSnap);
    }

    private void ApplyAppAndWebsiteFilters()
    {
        if (IsHistoricalMode)
        {
            if (_lastHistoricalAppSnapshot is null || _lastHistoricalWebSnapshot is null)
            {
                RefreshHistoricalMode();
                return;
            }

            ApplyAppAndWebsiteFilters(_lastHistoricalAppSnapshot, _lastHistoricalWebSnapshot);
            return;
        }

        AppTrafficSnapshot appSnap = _lastAppSnapshot ?? _appTracker.GetSnapshot(_lastRefreshUtc);
        WebsiteTrafficSnapshot allWebSnap = _lastAllWebSnapshot ?? _websiteTracker.GetSnapshot(localOnlyFilter: null, _lastRefreshUtc);
        ApplyAppAndWebsiteFilters(appSnap, allWebSnap);
    }

    private void RefreshForUsagePeriodChange()
    {
        if (IsHistoricalMode)
        {
            RefreshHistoricalMode();
            return;
        }

        ActiveDateRangeSummary = "Live Session";
        HistoricalTotalRxText = "0 B";
        HistoricalTotalTxText = "0 B";
        _lastHistoricalAppSnapshot = null;
        _lastHistoricalWebSnapshot = null;

        RefreshFromCurrentSnapshots();
        RefreshTimeline();
    }

    private void RefreshHistoricalMode()
    {
        (DateOnly fromDate, DateOnly toDate) = ResolveActiveDateRange();
        ActiveDateRangeSummary = fromDate == toDate
            ? fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : $"{fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} → {toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

        (AppTrafficSnapshot appSnap, WebsiteTrafficSnapshot webSnap) = BuildHistoricalSnapshots(fromDate, toDate);
        _lastHistoricalAppSnapshot = appSnap;
        _lastHistoricalWebSnapshot = webSnap;

        long totalRx = Math.Max(appSnap.TotalDownloadBytes, webSnap.TotalDownloadBytes);
        long totalTx = Math.Max(appSnap.TotalUploadBytes, webSnap.TotalUploadBytes);
        HistoricalTotalRxText = BandwidthFormatter.FormatBytes(totalRx);
        HistoricalTotalTxText = BandwidthFormatter.FormatBytes(totalTx);
        WindowTotalRxText = HistoricalTotalRxText;
        WindowTotalTxText = HistoricalTotalTxText;

        ActiveAppCount = appSnap.Applications.Count;
        TotalAppDownloadRateText = BandwidthFormatter.FormatRate(0, _useBitsPerSecond);
        TotalAppUploadRateText = BandwidthFormatter.FormatRate(0, _useBitsPerSecond);

        ApplyAppAndWebsiteFilters(appSnap, webSnap);
        RefreshTrafficTypesBreakdown(appSnap, webSnap);
    }

    private (DateOnly FromDate, DateOnly ToDate) ResolveActiveDateRange()
    {
        DateOnly todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        return _selectedUsagePeriod.Preset switch
        {
            UsagePeriodPreset.Today => (todayUtc, todayUtc),
            UsagePeriodPreset.Yesterday => (todayUtc.AddDays(-1), todayUtc.AddDays(-1)),
            UsagePeriodPreset.Last7Days => (todayUtc.AddDays(-6), todayUtc),
            UsagePeriodPreset.Last30Days => (todayUtc.AddDays(-29), todayUtc),
            UsagePeriodPreset.ThisMonth => (new DateOnly(todayUtc.Year, todayUtc.Month, 1), todayUtc),
            UsagePeriodPreset.CustomRange => (DateOnly.FromDateTime(_customFromDate), DateOnly.FromDateTime(_customToDate)),
            _ => (todayUtc, todayUtc)
        };
    }

    private (AppTrafficSnapshot AppSnap, WebsiteTrafficSnapshot WebSnap) BuildHistoricalSnapshots(DateOnly fromDate, DateOnly toDate)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (_historyStore is null)
        {
            return (
                new AppTrafficSnapshot(nowUtc, [], 0, 0, 0, 0, 0),
                new WebsiteTrafficSnapshot(nowUtc, [], 0, 0, 0));
        }

        IReadOnlyList<AppTrafficEntry> apps = _historyStore.QueryApplications(fromDate, toDate);
        long appRx = 0;
        long appTx = 0;
        for (int i = 0; i < apps.Count; i++)
        {
            appRx += apps[i].TotalDownloadBytes;
            appTx += apps[i].TotalUploadBytes;
        }

        bool? localOnlyFilter = _sourceFilter == TrafficSourceFilter.ThisPcOnly ? true : null;
        IReadOnlyList<WebsiteTrafficEntry> websites = _historyStore.QueryWebsites(fromDate, toDate, localOnlyFilter);
        long webRx = 0;
        long webTx = 0;
        var uniqueDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < websites.Count; i++)
        {
            webRx += websites[i].DownloadBytes;
            webTx += websites[i].UploadBytes;
            uniqueDomains.Add(websites[i].Domain);
        }

        return (
            new AppTrafficSnapshot(nowUtc, apps, 0, 0, appRx, appTx, 0),
            new WebsiteTrafficSnapshot(nowUtc, websites, webRx, webTx, uniqueDomains.Count));
    }

    private void ApplyAppAndWebsiteFilters(AppTrafficSnapshot appSnap, WebsiteTrafficSnapshot allWebSnap)
    {
        string query = _searchQuery.Trim();

        // 1. Applications
        var matchingApps = new List<AppTrafficEntry>(appSnap.Applications.Count);
        for (int i = 0; i < appSnap.Applications.Count; i++)
        {
            AppTrafficEntry entry = appSnap.Applications[i];
            if (MatchesAppSearch(entry, query))
            {
                matchingApps.Add(entry);
            }
        }

        double denomBps = appSnap.TotalDownloadBps + appSnap.TotalUploadBps;
        double denomBytes = appSnap.TotalDownloadBytes + appSnap.TotalUploadBytes;
        SyncApplicationsCollection(matchingApps, denomBps, denomBytes);

        // 2. Websites (respecting SourceFilter + SearchQuery)
        bool localOnly = _sourceFilter == TrafficSourceFilter.ThisPcOnly;
        var matchingWebsites = new List<WebsiteTrafficEntry>(Math.Min(allWebSnap.Websites.Count, MaxDisplayedWebsites));
        var uniqueDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < allWebSnap.Websites.Count; i++)
        {
            WebsiteTrafficEntry entry = allWebSnap.Websites[i];
            if (localOnly && !entry.IsLocalHost)
            {
                continue;
            }

            if (!MatchesWebsiteSearch(entry, query))
            {
                continue;
            }

            uniqueDomains.Add(entry.Domain);
            if (matchingWebsites.Count < MaxDisplayedWebsites)
            {
                matchingWebsites.Add(entry);
            }
        }

        UniqueDomainCount = uniqueDomains.Count;
        SyncWebsitesCollection(matchingWebsites);
    }

    private void RefreshTrafficTypesBreakdown(AppTrafficSnapshot appSnap, WebsiteTrafficSnapshot allWebSnap)
    {
        bool localOnly = _sourceFilter == TrafficSourceFilter.ThisPcOnly;

        Span<long> rxByType = stackalloc long[CanonicalTrafficTypes.Length];
        Span<long> txByType = stackalloc long[CanonicalTrafficTypes.Length];
        Span<int> countByType = stackalloc int[CanonicalTrafficTypes.Length];

        long localWebRx = 0;
        long localWebTx = 0;

        for (int i = 0; i < allWebSnap.Websites.Count; i++)
        {
            WebsiteTrafficEntry entry = allWebSnap.Websites[i];
            if (entry.IsLocalHost)
            {
                localWebRx += entry.DownloadBytes;
                localWebTx += entry.UploadBytes;
            }

            if (localOnly && !entry.IsLocalHost)
            {
                continue;
            }

            int bucketIdx = ClassifyProtocolBucketIndex(entry.ProtocolPort);
            rxByType[bucketIdx] += entry.DownloadBytes;
            txByType[bucketIdx] += entry.UploadBytes;
            countByType[bucketIdx]++;
        }

        // Attribute any remaining local app traffic that did not match a domain entry (e.g., direct-IP sockets)
        long remLocalRx = Math.Max(0L, appSnap.TotalDownloadBytes - localWebRx);
        long remLocalTx = Math.Max(0L, appSnap.TotalUploadBytes - localWebTx);
        if (remLocalRx > 0 || remLocalTx > 0)
        {
            int otherIdx = CanonicalTrafficTypes.Length - 1;
            rxByType[otherIdx] += remLocalRx;
            txByType[otherIdx] += remLocalTx;
            countByType[otherIdx] += Math.Max(1, appSnap.Applications.Count);
        }

        long grandTotal = 0;
        for (int i = 0; i < CanonicalTrafficTypes.Length; i++)
        {
            grandTotal += rxByType[i] + txByType[i];
        }

        var ordered = new List<(int Index, long Rx, long Tx, long Total, int Count)>(CanonicalTrafficTypes.Length);
        for (int i = 0; i < CanonicalTrafficTypes.Length; i++)
        {
            ordered.Add((i, rxByType[i], txByType[i], rxByType[i] + txByType[i], countByType[i]));
        }

        ordered.Sort(static (a, b) =>
        {
            int cmp = b.Total.CompareTo(a.Total);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        for (int targetIdx = 0; targetIdx < ordered.Count; targetIdx++)
        {
            (int idx, long rx, long tx, long total, int count) = ordered[targetIdx];
            string typeName = CanonicalTrafficTypes[idx];
            double ratio = grandTotal > 0 ? (double)total / grandTotal : 0.0;

            int existingIdx = FindTrafficTypeIndex(typeName);
            if (existingIdx >= 0)
            {
                TrafficTypes[existingIdx].Update(rx, tx, ratio, count);
                if (existingIdx != targetIdx)
                {
                    TrafficTypes.Move(existingIdx, targetIdx);
                }
            }
        }
    }

    private void RefreshTimeline()
    {
        TrafficTimelineSnapshot snapshot = _timelineStore.GetSnapshot(_selectedTimeWindow, topSliceCount: 4);
        _rawTimelineBuckets = snapshot.Buckets;

        PeakTrafficText = BandwidthFormatter.FormatRate(snapshot.PeakTotalBps, _useBitsPerSecond);
        if (!IsHistoricalMode)
        {
            WindowTotalRxText = BandwidthFormatter.FormatBytes(snapshot.TotalDownloadBytes);
            WindowTotalTxText = BandwidthFormatter.FormatBytes(snapshot.TotalUploadBytes);
        }

        // 1. Series descriptors (max 5 slots aligned with StackedTrafficTimelineControl; Brush = null picks theme DynamicResources)
        if (snapshot.SeriesLabels.Count > 0)
        {
            int seriesCount = Math.Min(5, snapshot.SeriesLabels.Count);
            var descriptors = new TimelineSeriesDescriptor[seriesCount];
            for (int i = 0; i < seriesCount; i++)
            {
                descriptors[i] = new TimelineSeriesDescriptor { Name = snapshot.SeriesLabels[i] };
            }
            TimelineSeries = descriptors;
        }
        else
        {
            TimelineSeries =
            [
                new TimelineSeriesDescriptor { Name = "Download" },
                new TimelineSeriesDescriptor { Name = "Upload" }
            ];
        }

        // 2. Bucket points + Alert markers
        int count = snapshot.Buckets.Count;
        var bucketPoints = new TimelineBucketPoint[count];
        var markers = new List<TimelineAlertMarker>(snapshot.AlertsInWindow.Count);
        bool hasSeriesSlices = snapshot.SeriesLabels.Count > 0;
        int sliceLen = hasSeriesSlices ? Math.Min(5, snapshot.SeriesLabels.Count) : 0;

        for (int i = 0; i < count; i++)
        {
            TimelineBucketSnapshot b = snapshot.Buckets[i];
            double bucketSeconds = Math.Max(1.0, (b.BucketEndUtc - b.BucketStartUtc).TotalSeconds);

            double[]? sliceValues = null;
            string? topSummary = null;

            if (hasSeriesSlices)
            {
                sliceValues = new double[sliceLen];
                string? topSliceName = null;
                double topSliceBps = 0.0;

                for (int s = 0; s < sliceLen; s++)
                {
                    string label = snapshot.SeriesLabels[s];
                    double bps = FindSliceBps(b.Slices, label, bucketSeconds);
                    sliceValues[s] = bps;

                    if (bps > topSliceBps)
                    {
                        topSliceBps = bps;
                        topSliceName = label;
                    }
                }

                if (!string.IsNullOrWhiteSpace(topSliceName) && topSliceBps > 0.0)
                {
                    topSummary = $"Top: {topSliceName} ({BandwidthFormatter.FormatRate(topSliceBps, _useBitsPerSecond)})";
                }
            }

            bucketPoints[i] = new TimelineBucketPoint
            {
                TimestampUtc = b.BucketStartUtc,
                Label = b.Label,
                DownloadBps = b.DownloadBps,
                UploadBps = b.UploadBps,
                SliceValues = sliceValues,
                TopSummary = topSummary
            };

            for (int a = 0; a < b.Alerts.Count; a++)
            {
                MonitorAlertRecord alert = b.Alerts[a];
                markers.Add(new TimelineAlertMarker
                {
                    BucketIndex = i,
                    TimestampUtc = alert.TimestampUtc,
                    Title = alert.Title,
                    Detail = string.IsNullOrWhiteSpace(alert.TargetDetail)
                        ? alert.Description
                        : $"{alert.SubjectName} — {alert.TargetDetail}",
                    Severity = MapSeverity(alert.Severity)
                });
            }
        }

        TimelineBuckets = bucketPoints;
        TimelineAlerts = markers;

        if (_inspectedIndex >= count)
        {
            _inspectedIndex = -1;
            _isInspectLocked = false;
            OnPropertyChanged(nameof(InspectedIndex));
            OnPropertyChanged(nameof(IsInspectLocked));
        }

        if (_hoveredIndex >= count)
        {
            _hoveredIndex = -1;
            OnPropertyChanged(nameof(HoveredIndex));
        }

        UpdateInspectedSummary();
    }

    private void UpdateInspectedSummary()
    {
        int count = _timelineBuckets.Count;
        if (count == 0)
        {
            InspectedSummary = "Waiting for network traffic...";
            return;
        }

        int activeIdx;
        if (_isInspectLocked && _inspectedIndex >= 0 && _inspectedIndex < count)
        {
            activeIdx = _inspectedIndex;
        }
        else if (_hoveredIndex >= 0 && _hoveredIndex < count)
        {
            activeIdx = _hoveredIndex;
        }
        else if (_inspectedIndex >= 0 && _inspectedIndex < count)
        {
            activeIdx = _inspectedIndex;
        }
        else
        {
            TimelineBucketPoint latest = _timelineBuckets[^1];
            string dl = BandwidthFormatter.FormatRate(latest.DownloadBps, _useBitsPerSecond);
            string ul = BandwidthFormatter.FormatRate(latest.UploadBps, _useBitsPerSecond);
            InspectedSummary = $"{FormatWindowDisplayName(_selectedTimeWindow)} · Latest {latest.Label}: ↓ {dl} · ↑ {ul} · Click chart to lock slice";
            return;
        }

        TimelineBucketPoint bucket = _timelineBuckets[activeIdx];
        string lockPrefix = _isInspectLocked ? "[LOCKED] " : string.Empty;
        string dlRate = BandwidthFormatter.FormatRate(bucket.DownloadBps, _useBitsPerSecond);
        string ulRate = BandwidthFormatter.FormatRate(bucket.UploadBps, _useBitsPerSecond);

        string summary = $"{lockPrefix}{bucket.Label} · ↓ {dlRate} · ↑ {ulRate}";

        if (activeIdx < _rawTimelineBuckets.Count)
        {
            TimelineBucketSnapshot raw = _rawTimelineBuckets[activeIdx];
            summary += $" · Vol {BandwidthFormatter.FormatBytes(raw.TotalBytes)}";

            if (!string.IsNullOrWhiteSpace(bucket.TopSummary))
            {
                summary += $" · {bucket.TopSummary}";
            }

            if (raw.Alerts.Count > 0)
            {
                summary += $" · Alert: {raw.Alerts[0].Title}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(bucket.TopSummary))
        {
            summary += $" · {bucket.TopSummary}";
        }

        InspectedSummary = summary;
    }

    private void SyncApplicationsCollection(List<AppTrafficEntry> entries, double denomBps, double denomBytes)
    {
        var desiredKeys = new HashSet<string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            desiredKeys.Add(entries[i].AppKey);
        }

        for (int i = Applications.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(Applications[i].AppKey))
            {
                if (ReferenceEquals(SelectedApplication, Applications[i]))
                {
                    SelectedApplication = null;
                }
                Applications.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < entries.Count; targetIdx++)
        {
            AppTrafficEntry entry = entries[targetIdx];
            double ratio = denomBps > 0.0
                ? entry.TotalBps / denomBps
                : (denomBytes > 0.0 ? entry.TotalBytes / denomBytes : 0.0);

            int existingIdx = FindAppIndex(entry.AppKey);
            if (existingIdx >= 0)
            {
                Applications[existingIdx].UpdateFrom(entry, ratio, _useBitsPerSecond);
                if (existingIdx != targetIdx)
                {
                    Applications.Move(existingIdx, targetIdx);
                }
            }
            else
            {
                Applications.Insert(targetIdx, new AppTrafficRowViewModel(entry, ratio, _useBitsPerSecond));
            }
        }
    }

    private void SyncWebsitesCollection(List<WebsiteTrafficEntry> entries)
    {
        var desiredKeys = new HashSet<string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            desiredKeys.Add(entries[i].EntryKey);
        }

        for (int i = Websites.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(Websites[i].EntryKey))
            {
                if (ReferenceEquals(SelectedWebsite, Websites[i]))
                {
                    SelectedWebsite = null;
                }
                Websites.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < entries.Count; targetIdx++)
        {
            WebsiteTrafficEntry entry = entries[targetIdx];
            bool? liveSinkholed = _liveSinkholeLookup?.Invoke(entry.SourceHostIp, entry.Domain);

            int existingIdx = FindWebsiteIndex(entry.EntryKey);
            if (existingIdx >= 0)
            {
                Websites[existingIdx].UpdateFrom(entry, liveSinkholed);
                if (existingIdx != targetIdx)
                {
                    Websites.Move(existingIdx, targetIdx);
                }
            }
            else
            {
                Websites.Insert(targetIdx, new WebsiteTrafficRowViewModel(entry, liveSinkholed));
            }
        }
    }

    private void SyncAlertsCollection(IReadOnlyList<MonitorAlertRecord> latest)
    {
        bool identical = Alerts.Count == latest.Count;
        if (identical)
        {
            for (int i = 0; i < latest.Count; i++)
            {
                if (Alerts[i] != latest[i])
                {
                    identical = false;
                    break;
                }
            }
        }

        if (identical)
        {
            return;
        }

        Alerts.Clear();
        for (int i = 0; i < latest.Count; i++)
        {
            Alerts.Add(latest[i]);
        }
    }

    private void ExecuteSelectTab(object? parameter)
    {
        if (parameter is AppMonitorTab tab)
        {
            SelectedTab = tab;
            return;
        }

        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            string trimmed = text.Trim();
            if (Enum.TryParse(trimmed, ignoreCase: true, out AppMonitorTab parsed))
            {
                SelectedTab = parsed;
            }
            else if (string.Equals(trimmed, "Traffic", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(trimmed, "Types", StringComparison.OrdinalIgnoreCase))
            {
                SelectedTab = AppMonitorTab.TrafficTypes;
            }
        }
    }

    private void ExecuteSetSourceFilter(object? parameter)
    {
        if (parameter is TrafficSourceFilter filter)
        {
            SourceFilter = filter;
            return;
        }

        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            string trimmed = text.Trim();
            if (Enum.TryParse(trimmed, ignoreCase: true, out TrafficSourceFilter parsed))
            {
                SourceFilter = parsed;
            }
            else if (string.Equals(trimmed, "All", StringComparison.OrdinalIgnoreCase))
            {
                SourceFilter = TrafficSourceFilter.AllLanDevices;
            }
            else if (string.Equals(trimmed, "ThisPC", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(trimmed, "Local", StringComparison.OrdinalIgnoreCase))
            {
                SourceFilter = TrafficSourceFilter.ThisPcOnly;
            }
        }
    }

    private void ExecuteSelectTimeWindow(object? parameter)
    {
        if (parameter is TimeMachineWindow window)
        {
            SelectedTimeWindow = window;
            return;
        }

        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            string trimmed = text.Trim();
            if (Enum.TryParse(trimmed, ignoreCase: true, out TimeMachineWindow parsed))
            {
                SelectedTimeWindow = parsed;
                return;
            }

            TimeMachineWindow? mapped = trimmed.ToUpperInvariant() switch
            {
                "1M" or "LIVE" => TimeMachineWindow.Live,
                "5M" or "5MIN" => TimeMachineWindow.FiveMinutes,
                "1H" or "1HR" => TimeMachineWindow.OneHour,
                "24H" or "1D" => TimeMachineWindow.TwentyFourHours,
                "7D" or "7DAYS" => TimeMachineWindow.SevenDays,
                _ => null
            };

            if (mapped.HasValue)
            {
                SelectedTimeWindow = mapped.Value;
            }
        }
    }

    private void UnlockInspect()
    {
        _isInspectLocked = false;
        _inspectedIndex = -1;
        _hoveredIndex = -1;
        OnPropertyChanged(nameof(IsInspectLocked));
        OnPropertyChanged(nameof(InspectedIndex));
        OnPropertyChanged(nameof(HoveredIndex));
        UpdateInspectedSummary();
    }

    private void ExecuteAcknowledgeApp(AppTrafficRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _appTracker.AcknowledgeFirstAccess(row.AppKey);
        row.IsNewFirstAccess = false;

        for (int i = 0; i < Alerts.Count; i++)
        {
            MonitorAlertRecord alert = Alerts[i];
            if (!alert.IsAcknowledged &&
                alert.Kind == MonitorAlertKind.FirstNetworkAccess &&
                (string.Equals(alert.TargetDetail, row.AppKey, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(alert.SubjectName, row.AppName, StringComparison.OrdinalIgnoreCase)))
            {
                _timelineStore.AcknowledgeAlert(alert.Id);
            }
        }

        RefreshAlertsOnly();
        ReportStatus($"Acknowledged first network access for {row.DisplayName}.");
    }

    private void ExecuteToggleSinkholeDomain(WebsiteTrafficRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (_toggleSinkholeHandler is null)
        {
            ReportStatus(row.IsLocalHost
                ? $"DNS sinkhole applies to monitored LAN devices only ({row.Domain})."
                : $"No DNS sinkhole handler configured for {row.Domain}.");
            return;
        }

        (bool success, bool isNowSinkholed, string message) = _toggleSinkholeHandler(row);
        if (success)
        {
            row.IsSinkholed = isNowSinkholed;
            for (int i = 0; i < Websites.Count; i++)
            {
                WebsiteTrafficRowViewModel w = Websites[i];
                if (!w.IsLocalHost &&
                    string.Equals(w.Domain, row.Domain, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.SourceHostIp, row.SourceHostIp, StringComparison.OrdinalIgnoreCase))
                {
                    w.IsSinkholed = isNowSinkholed;
                }
            }
        }

        ReportStatus(message);
    }

    private void ExecuteAcknowledgeAlert(object? parameter)
    {
        long? targetId = parameter switch
        {
            MonitorAlertRecord rec => rec.Id,
            long id => id,
            null => _latestBannerAlert?.Id,
            _ => null
        };

        if (!targetId.HasValue)
        {
            return;
        }

        MonitorAlertRecord? target = null;
        for (int i = 0; i < Alerts.Count; i++)
        {
            if (Alerts[i].Id == targetId.Value)
            {
                target = Alerts[i];
                break;
            }
        }

        if (_timelineStore.AcknowledgeAlert(targetId.Value))
        {
            if (target is not null && target.Kind == MonitorAlertKind.FirstNetworkAccess)
            {
                if (!string.IsNullOrWhiteSpace(target.TargetDetail))
                {
                    _appTracker.AcknowledgeFirstAccess(target.TargetDetail);
                }

                for (int i = 0; i < Applications.Count; i++)
                {
                    AppTrafficRowViewModel app = Applications[i];
                    if (string.Equals(app.AppKey, target.TargetDetail, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(app.AppName, target.SubjectName, StringComparison.OrdinalIgnoreCase))
                    {
                        _appTracker.AcknowledgeFirstAccess(app.AppKey);
                        app.IsNewFirstAccess = false;
                    }
                }
            }

            RefreshAlertsOnly();
        }
    }

    private void ExecuteAcknowledgeAllAlerts()
    {
        _timelineStore.AcknowledgeAllAlerts();
        for (int i = 0; i < Applications.Count; i++)
        {
            if (Applications[i].IsNewFirstAccess)
            {
                _appTracker.AcknowledgeFirstAccess(Applications[i].AppKey);
                Applications[i].IsNewFirstAccess = false;
            }
        }

        RefreshAlertsOnly();
        ReportStatus("Acknowledged all App & Security alerts.");
    }

    private void ExecuteApplyCustomDateRange()
    {
        if (!IsCustomDateRange)
        {
            SelectedUsagePeriod = UsagePeriodOptions[^1];
            return;
        }

        RefreshForUsagePeriodChange();
    }

    private void ExecuteClearMonitorHistory()
    {
        _timelineStore.Clear();
        _appTracker.Clear();
        _websiteTracker.Clear();
        _historyStore?.Clear();

        _lastHistoricalAppSnapshot = null;
        _lastHistoricalWebSnapshot = null;

        _isInspectLocked = false;
        _inspectedIndex = -1;
        _hoveredIndex = -1;
        OnPropertyChanged(nameof(IsInspectLocked));
        OnPropertyChanged(nameof(InspectedIndex));
        OnPropertyChanged(nameof(HoveredIndex));

        RefreshFromTrackers();
        ReportStatus("Cleared App & Website monitor history.");
    }

    private AppTrafficRowViewModel? ResolveAppRow(object? parameter)
    {
        if (parameter is AppTrafficRowViewModel row)
        {
            return row;
        }

        if (parameter is string key && !string.IsNullOrWhiteSpace(key))
        {
            for (int i = 0; i < Applications.Count; i++)
            {
                if (string.Equals(Applications[i].AppKey, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Applications[i].ExePath, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Applications[i];
                }
            }
        }

        return SelectedApplication;
    }

    private WebsiteTrafficRowViewModel? ResolveWebsiteRow(object? parameter)
    {
        if (parameter is WebsiteTrafficRowViewModel row)
        {
            return row;
        }

        if (parameter is string key && !string.IsNullOrWhiteSpace(key))
        {
            for (int i = 0; i < Websites.Count; i++)
            {
                if (string.Equals(Websites[i].EntryKey, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Websites[i].Domain, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Websites[i];
                }
            }
        }

        return SelectedWebsite;
    }

    private int FindAppIndex(string appKey)
    {
        for (int i = 0; i < Applications.Count; i++)
        {
            if (string.Equals(Applications[i].AppKey, appKey, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private int FindWebsiteIndex(string entryKey)
    {
        for (int i = 0; i < Websites.Count; i++)
        {
            if (string.Equals(Websites[i].EntryKey, entryKey, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private int FindTrafficTypeIndex(string typeName)
    {
        for (int i = 0; i < TrafficTypes.Count; i++)
        {
            if (string.Equals(TrafficTypes[i].TypeName, typeName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private void RefreshUnitTexts()
    {
        AppTrafficSnapshot? snap = _lastAppSnapshot;
        TotalAppDownloadRateText = BandwidthFormatter.FormatRate(snap?.TotalDownloadBps ?? 0, _useBitsPerSecond);
        TotalAppUploadRateText = BandwidthFormatter.FormatRate(snap?.TotalUploadBps ?? 0, _useBitsPerSecond);
    }

    private void ReportStatus(string message)
    {
        StatusMessage = message;
    }

    private static void DispatchToUiOrInline(Action action)
    {
        if (Application.Current is { Dispatcher: { } dispatcher } && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static bool MatchesAppSearch(AppTrafficEntry entry, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return entry.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.ExePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.ProtocolsSummary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.TopHostsSummary.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWebsiteSearch(WebsiteTrafficEntry entry, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return entry.Domain.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.ResolvedIp.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.ProtocolPort.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.SourceHost.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.SourceHostIp.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.AppName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int ClassifyProtocolBucketIndex(string protocolPort)
    {
        if (string.IsNullOrWhiteSpace(protocolPort))
        {
            return 6; // Other
        }

        if (protocolPort.Contains("TLS", StringComparison.OrdinalIgnoreCase)) return 0;  // TLS / HTTPS
        if (protocolPort.Contains("QUIC", StringComparison.OrdinalIgnoreCase)) return 1; // QUIC / UDP
        if (protocolPort.Contains("HTTP", StringComparison.OrdinalIgnoreCase)) return 2; // HTTP
        if (protocolPort.Contains("DNS", StringComparison.OrdinalIgnoreCase)) return 3;  // DNS
        if (protocolPort.Contains("TCP", StringComparison.OrdinalIgnoreCase)) return 4;  // TCP
        if (protocolPort.Contains("UDP", StringComparison.OrdinalIgnoreCase)) return 5;  // UDP
        return 6; // Other
    }

    private static double FindSliceBps(IReadOnlyList<TimelineSeriesSlice> slices, string label, double bucketSeconds)
    {
        for (int i = 0; i < slices.Count; i++)
        {
            if (string.Equals(slices[i].Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return slices[i].TotalBytes / bucketSeconds;
            }
        }
        return 0.0;
    }

    private static TimelineAlertSeverity MapSeverity(MonitorAlertSeverity severity) => severity switch
    {
        MonitorAlertSeverity.Critical => TimelineAlertSeverity.Critical,
        MonitorAlertSeverity.Warning => TimelineAlertSeverity.Warning,
        _ => TimelineAlertSeverity.Info
    };

    private static string FormatWindowDisplayName(TimeMachineWindow window) => window switch
    {
        TimeMachineWindow.Live => "Live (1s)",
        TimeMachineWindow.FiveMinutes => "5 Minutes",
        TimeMachineWindow.OneHour => "1 Hour",
        TimeMachineWindow.TwentyFourHours => "24 Hours",
        TimeMachineWindow.SevenDays => "7 Days",
        _ => window.ToString()
    };
}
