using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using FishyNet.Core;
using FishyNet.Core.Formatting;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;
using FishyNet.Features;
using FishyNet.Features.AppMonitor;
using FishyNet.Features.SmartSchedules;
using FishyNet.Themes;

namespace FishyNet.ViewModels;

public enum DeviceViewMode
{
    Table,
    Bento,
    Topology
}

public enum AppSection
{
    LanControl,
    AppMonitor,
    TopologySecurity,
    HistoryPolicies
}

public sealed record ThemePaletteOption(
    string Id,
    string Name,
    string AccentHex,
    string BackdropHex = "#07090D");

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MainChartPoints = 60;
    private readonly double[] _netDownloadBuffer = new double[MainChartPoints];
    private readonly double[] _netUploadBuffer = new double[MainChartPoints];

    private readonly DeviceRegistry _registry;
    private readonly CaptureEngine _engine;
    private readonly DeviceProfileStore _profileStore;
    private readonly UsageHistoryStore _historyStore;
    private readonly HostResolverService _hostResolver;
    private readonly TrayIconService _trayService;
    private readonly TrafficTimelineStore _timelineStore;
    private readonly AppMonitorHistoryStore _appHistoryStore;
    private readonly IAppFirewallController _appFirewall;
    private readonly AppTrafficTracker _appTracker;
    private readonly WebsiteTrafficTracker _websiteTracker;

    private readonly Dispatcher _uiDispatcher;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly object _adapterRefreshLock = new();
    private CancellationTokenSource? _adapterRefreshCts;
    private bool _isSyncingAdapters;
    private int _ticksSinceHistoryFlush;
    private int _ticksSinceAppMonitorRefresh;
    private double _peakDownloadBps;
    private bool _disposed;

    private NetworkAdapterInfo? _selectedAdapter;
    private DeviceViewModel? _selectedDevice;
    private string _searchQuery = string.Empty;
    private string _statusFilter = "All";
    private int _activeWorkspaceTab;

    private bool _isRedirecting;
    private bool _isScanning;
    private bool _isNpcapMissing;
    private bool _isInstallingNpcap;
    private bool _useBitsPerSecond;
    private bool _closeToTray = true;
    private string _statusMessage = "Initializing packet engine...";
    private string _driverStatusText = "Checking Npcap...";

    private string _totalDownloadText = "0 B/s";
    private string _totalUploadText = "0 B/s";
    private string _peakDownloadText = "0 B/s";
    private string _sessionTotalText = "0 B";
    private string _gatewayLatencyText = "—";
    private string _arpGuardStatusText = "ARP Guard: Protected";
    private bool _isArpAlertActive;

    private int _onlineDevicesCount;
    private int _throttledDevicesCount;
    private int _blockedDevicesCount;

    private IReadOnlyList<double> _networkDownloadSeries = new double[MainChartPoints];
    private IReadOnlyList<double> _networkUploadSeries = new double[MainChartPoints];

    private string _newDomainInput = string.Empty;
    private DnsQueryRecord? _selectedDnsRecord;

    // Historical Usage Tab State
    private UsageTimeRange _selectedHistoryRange = UsageTimeRange.Past7Days;
    private string _historyTotalDownloadText = "0 B";
    private string _historyTotalUploadText = "0 B";
    private string _historyGrandTotalText = "0 B";
    private IReadOnlyList<DailyUsagePoint> _historyDailySeries = Array.Empty<DailyUsagePoint>();
    private DeviceUsageSummary? _selectedHistoryDevice;

    // Protocol Analytics State
    private string _protoHttpTlsSummary = "0 B (0%)";
    private string _protoTcpSummary = "0 B (0%)";
    private string _protoUdpSummary = "0 B (0%)";
    private string _protoDnsSummary = "0 queries";

    // UI Redesign & Smart Scheduling State
    private DeviceViewMode _currentViewMode = DeviceViewMode.Table;
    private DeviceViewMode _lastLanViewMode = DeviceViewMode.Table;
    private AppSection _activeSection = AppSection.LanControl;
    private bool _isSyncingSectionAndViewMode;
    private string _themePaletteId = "obsidian_cyan";

    private bool _isInspectorOpen;
    private bool _isGamingBoostActive;
    private bool _isMiniHudActive;
    private string _totalSpeedTickerText = "▼ 0 B/s   ▲ 0 B/s   ● Ping —";
    private readonly Dictionary<MacAddress, (int DownCap, int UpCap, bool WasSpoofed)> _gamingBoostSavedCaps = new();

    // Throttle All & Batch Selection State
    private readonly List<DeviceViewModel> _subscribedDevices = [];
    private int _throttleAllDownloadCapKBps = 250;
    private int _throttleAllUploadCapKBps = 64;
    private bool _isThrottleAllActive;
    private bool _throttleAllKeepAppliedToNewDevices = true;
    private int _batchDownloadCapKBps = 500;
    private int _batchUploadCapKBps = 120;
    private bool _suppressBatchSelectionNotifications;

    public static IReadOnlyList<ThemePaletteOption> BuiltInThemePalettes { get; } =
    [
        new("obsidian_cyan", "Obsidian Cyan", "#22D3EE", "#07090D"),
        new("graphite_mint", "Graphite Mint", "#10B981", "#08090B"),
        new("carbon_cobalt", "Carbon Cobalt", "#60A5FA", "#06070B"),
        new("smoked_onyx", "Smoked Onyx", "#E4E4E7", "#08080A")
    ];

    public event Action? RequestShowWindow;
    public event Action? RequestExplicitExit;
    public event Action<bool>? RequestMiniHudVisibilityChanged;
    public event Action? RequestMinimize;
    public event Action? RequestMaximizeRestore;
    public event Action? RequestClose;
    public event Action<string>? ThemePaletteRequested;

    internal DeviceRegistry Registry => _registry;
    internal DeviceProfileStore ProfileStore => _profileStore;
    internal CaptureEngine Engine => _engine;
    internal AppTrafficTracker AppTracker => _appTracker;
    internal WebsiteTrafficTracker WebsiteTracker => _websiteTracker;
    internal TrafficTimelineStore TimelineStore => _timelineStore;
    internal AppMonitorHistoryStore AppHistoryStore => _appHistoryStore;

    public AppMonitorViewModel AppMonitor { get; }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ICollectionView FilteredDevices { get; }

    public IReadOnlyList<BandwidthPresetProfile> Presets => DeviceProfileStore.BuiltInPresets;
    public IReadOnlyList<ThemePaletteOption> AvailableThemePalettes => BuiltInThemePalettes;
    public IReadOnlyList<string> Categories { get; } = ["Workstation", "Mobile", "Media/Console", "IoT", "Router", "Virtual", "Device"];

    public ObservableCollection<DeviceUsageSummary> HistoryDeviceSummaries { get; } = [];
    public ObservableCollection<DnsQueryRecord> DnsQueryLogs { get; } = [];
    public ObservableCollection<ArpSecurityAlert> SecurityAlerts { get; } = [];

    public SmartScheduleEngine SmartSchedules { get; } = new();
    public ObservableCollection<SmartScheduleRule> SmartRules => SmartSchedules.Rules;

    public RelayCommand ScanSubnetCommand { get; }
    public RelayCommand ToggleRedirectionCommand { get; }
    public RelayCommand ToggleUnitModeCommand { get; }
    public RelayCommand MonitorAllCommand { get; }
    public RelayCommand UnspoofAllCommand { get; }
    public RelayCommand ApplyPresetToSelectedCommand { get; }
    public RelayCommand ApplyPresetToAllLanCommand { get; }
    public RelayCommand WakeSelectedDeviceCommand { get; }
    public RelayCommand AddBlockedDomainCommand { get; }
    public RelayCommand RemoveBlockedDomainCommand { get; }
    public RelayCommand BlockSelectedDnsDomainCommand { get; }
    public RelayCommand ClearDnsLogsCommand { get; }
    public RelayCommand SelectHistoryRangeCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand ExportHistoryCsvCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ExportDevicesCsvCommand { get; }
    public RelayCommand SetStatusFilterCommand { get; }

    public RelayCommand ToggleGamingBoostCommand { get; }
    public RelayCommand ToggleMiniHudCommand { get; }
    public RelayCommand SetViewModeCommand { get; }
    public RelayCommand NavigateSectionCommand { get; }
    public RelayCommand SelectThemePaletteCommand { get; }
    public RelayCommand ToggleInspectorCommand { get; }
    public RelayCommand CloseInspectorCommand { get; }
    public RelayCommand DeselectDeviceCommand { get; }
    public RelayCommand ToggleSmartRuleCommand { get; }
    public RelayCommand ResetSmartRulesCommand { get; }
    public RelayCommand MinimizeWindowCommand { get; }
    public RelayCommand MaximizeWindowCommand { get; }
    public RelayCommand CloseWindowCommand { get; }

    public RelayCommand ApplyThrottleAllCommand { get; }
    public RelayCommand ClearAllThrottlesCommand { get; }
    public RelayCommand ApplyThrottleAllPresetCommand { get; }

    public RelayCommand ToggleSelectAllFilteredCommand { get; }
    public RelayCommand ApplyBatchThrottleCommand { get; }
    public RelayCommand BatchMonitorCommand { get; }
    public RelayCommand BatchBypassCommand { get; }
    public RelayCommand BatchBlockCommand { get; }
    public RelayCommand BatchUnblockCommand { get; }
    public RelayCommand BatchClearCapsCommand { get; }
    public RelayCommand ClearBatchSelectionCommand { get; }
    public RelayCommand InstallNpcapCommand { get; }

    public MainViewModel() : this(new DeviceProfileStore())
    {
    }

    public MainViewModel(DeviceProfileStore profileStore)
        : this(profileStore, null, null, null)
    {
    }

    internal MainViewModel(
        DeviceProfileStore profileStore,
        IAppFirewallController? firewallController,
        ISocketTableProvider? socketTableProvider,
        IProcessMetadataResolver? metadataResolver,
        string? appHistoryPath = null)
    {
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _registry = new DeviceRegistry();
        _engine = new CaptureEngine(_registry);
        _profileStore = profileStore ?? new DeviceProfileStore();
        _historyStore = new UsageHistoryStore();
        _appHistoryStore = new AppMonitorHistoryStore(appHistoryPath);
        _hostResolver = new HostResolverService(_registry);
        _trayService = new TrayIconService();

        _useBitsPerSecond = _profileStore.Preferences.UseBitsPerSecond;
        _closeToTray = _profileStore.Preferences.CloseToTray;
        _themePaletteId = NormalizePaletteId(_profileStore.Preferences.ThemePaletteId) ?? "obsidian_cyan";

        if (Application.Current is not null)
        {
            ThemeManager.ApplyPalette(_themePaletteId);
        }

        _timelineStore = new TrafficTimelineStore();
        _appFirewall = firewallController ?? new AppFirewallService();
        _appTracker = new AppTrafficTracker(
            socketTableProvider,
            metadataResolver,
            _appFirewall,
            _timelineStore);
        _websiteTracker = new WebsiteTrafficTracker(
            _registry,
            _appTracker,
            IsDomainGloballyOrDeviceSinkholed);

        _engine.RegisterTelemetryObserver(_appTracker);
        _engine.RegisterTelemetryObserver(_websiteTracker);

        AppMonitor = new AppMonitorViewModel(
            _appTracker,
            _websiteTracker,
            _timelineStore,
            _appFirewall,
            HandleWebsiteSinkholeToggle,
            LookupLiveDomainSinkholeState,
            msg => StatusMessage = msg,
            _useBitsPerSecond,
            _appHistoryStore);

        bool hasSavedThrottleAll =
            _profileStore.Preferences.ThrottleAllEnabled ||
            _profileStore.Preferences.ThrottleAllDownloadCapKBps > 0 ||
            _profileStore.Preferences.ThrottleAllUploadCapKBps > 0;
        _isThrottleAllActive = _profileStore.Preferences.ThrottleAllEnabled;
        _throttleAllDownloadCapKBps = hasSavedThrottleAll
            ? Math.Max(0, _profileStore.Preferences.ThrottleAllDownloadCapKBps)
            : 250;
        _throttleAllUploadCapKBps = hasSavedThrottleAll
            ? Math.Max(0, _profileStore.Preferences.ThrottleAllUploadCapKBps)
            : 64;

        Devices.CollectionChanged += OnDevicesCollectionChanged;

        FilteredDevices = CollectionViewSource.GetDefaultView(Devices);
        FilteredDevices.Filter = FilterDevicePredicate;

        _engine.StatusLog += msg => Application.Current?.Dispatcher.BeginInvoke(() => StatusMessage = msg);

        ScanSubnetCommand = new RelayCommand(() => _ = ScanSubnetAsync());
        ToggleRedirectionCommand = new RelayCommand(ToggleRedirection);
        ToggleUnitModeCommand = new RelayCommand(() => UseBitsPerSecond = !UseBitsPerSecond);
        MonitorAllCommand = new RelayCommand(ExecuteMonitorAll);
        UnspoofAllCommand = new RelayCommand(ExecuteUnspoofAll);
        ApplyPresetToSelectedCommand = new RelayCommand(param =>
        {
            if (param is BandwidthPresetProfile preset && SelectedDevice is not null)
            {
                ApplyPresetToDevice(SelectedDevice, preset);
            }
        });
        ApplyPresetToAllLanCommand = new RelayCommand(param =>
        {
            if (param is BandwidthPresetProfile preset)
            {
                ApplyPresetToAllLanDevices(preset);
            }
        });
        WakeSelectedDeviceCommand = new RelayCommand(() => _ = WakeSelectedDeviceAsync());
        AddBlockedDomainCommand = new RelayCommand(ExecuteAddBlockedDomain);
        RemoveBlockedDomainCommand = new RelayCommand(param =>
        {
            if (param is string domain && SelectedDevice is not null)
            {
                SelectedDevice.RemoveBlockedDomain(domain);
            }
        });
        BlockSelectedDnsDomainCommand = new RelayCommand(ExecuteBlockSelectedDnsDomain);
        ClearDnsLogsCommand = new RelayCommand(() => DnsQueryLogs.Clear());

        SelectHistoryRangeCommand = new RelayCommand(param =>
        {
            if (param is string rangeStr && Enum.TryParse(rangeStr, out UsageTimeRange parsed))
            {
                SelectedHistoryRange = parsed;
            }
        });
        RefreshHistoryCommand = new RelayCommand(RefreshHistoryReport);
        ExportHistoryCsvCommand = new RelayCommand(ExecuteExportHistoryCsv);
        ClearHistoryCommand = new RelayCommand(ExecuteClearHistory);
        ExportDevicesCsvCommand = new RelayCommand(ExecuteExportDevicesCsv);
        SetStatusFilterCommand = new RelayCommand(param =>
        {
            if (param is string filter && !string.IsNullOrWhiteSpace(filter))
            {
                StatusFilter = filter;
            }
        });

        ToggleGamingBoostCommand = new RelayCommand(ToggleGamingBoost);
        ToggleMiniHudCommand = new RelayCommand(() => IsMiniHudActive = !IsMiniHudActive);
        SetViewModeCommand = new RelayCommand(param =>
        {
            if (param is string str && Enum.TryParse(str, out DeviceViewMode mode))
            {
                CurrentViewMode = mode;
            }
            else if (param is DeviceViewMode m)
            {
                CurrentViewMode = m;
            }
        });
        NavigateSectionCommand = new RelayCommand(ExecuteNavigateSection);
        SelectThemePaletteCommand = new RelayCommand(ExecuteSelectThemePalette);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        CloseInspectorCommand = new RelayCommand(() => IsInspectorOpen = false);
        DeselectDeviceCommand = new RelayCommand(() => SelectedDevice = null);
        ToggleSmartRuleCommand = new RelayCommand(param =>
        {
            if (param is SmartScheduleRule rule)
            {
                rule.IsEnabled = !rule.IsEnabled;
                StatusMessage = $"Rule '{rule.Name}' is now {(rule.IsEnabled ? "ENABLED" : "DISABLED")}.";
            }
        });
        ResetSmartRulesCommand = new RelayCommand(() =>
        {
            SmartSchedules.PopulateDefaultRules();
            StatusMessage = "Reset smart schedules to built-in presets.";
        });
        MinimizeWindowCommand = new RelayCommand(() => RequestMinimize?.Invoke());
        MaximizeWindowCommand = new RelayCommand(() => RequestMaximizeRestore?.Invoke());
        CloseWindowCommand = new RelayCommand(() => RequestClose?.Invoke());
        InstallNpcapCommand = new RelayCommand(() => _ = InstallNpcapAsync(), () => !IsInstallingNpcap);

        ApplyThrottleAllCommand = new RelayCommand(ApplyThrottleAll);
        ClearAllThrottlesCommand = new RelayCommand(ClearAllThrottles);
        ApplyThrottleAllPresetCommand = new RelayCommand(ExecuteApplyThrottleAllPreset);

        ToggleSelectAllFilteredCommand = new RelayCommand(ToggleSelectAllFiltered);
        ApplyBatchThrottleCommand = new RelayCommand(ExecuteApplyBatchThrottle, () => HasBatchSelection);
        BatchMonitorCommand = new RelayCommand(ExecuteBatchMonitor, () => HasBatchSelection);
        BatchBypassCommand = new RelayCommand(ExecuteBatchBypass, () => HasBatchSelection);
        BatchBlockCommand = new RelayCommand(ExecuteBatchBlock, () => HasBatchSelection);
        BatchUnblockCommand = new RelayCommand(ExecuteBatchUnblock, () => HasBatchSelection);
        BatchClearCapsCommand = new RelayCommand(ExecuteBatchClearCaps, () => HasBatchSelection);
        ClearBatchSelectionCommand = new RelayCommand(ClearBatchSelection, () => HasBatchSelection);

        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += OnTelemetryTick;
    }

    public DeviceViewMode CurrentViewMode
    {
        get => _currentViewMode;
        set
        {
            if (SetProperty(ref _currentViewMode, value))
            {
                OnPropertyChanged(nameof(IsTableView));
                OnPropertyChanged(nameof(IsBentoView));
                OnPropertyChanged(nameof(IsTopologyView));

                if (value == DeviceViewMode.Topology)
                {
                    IsInspectorOpen = false;
                }
                else
                {
                    _lastLanViewMode = value;
                }

                if (!_isSyncingSectionAndViewMode)
                {
                    _isSyncingSectionAndViewMode = true;
                    try
                    {
                        ActiveSection = value == DeviceViewMode.Topology
                            ? AppSection.TopologySecurity
                            : AppSection.LanControl;
                    }
                    finally
                    {
                        _isSyncingSectionAndViewMode = false;
                    }
                }
            }
        }
    }

    public bool IsTableView => CurrentViewMode == DeviceViewMode.Table;
    public bool IsBentoView => CurrentViewMode == DeviceViewMode.Bento;
    public bool IsTopologyView => CurrentViewMode == DeviceViewMode.Topology;

    public AppSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                OnPropertyChanged(nameof(IsLanControlSection));
                OnPropertyChanged(nameof(IsAppMonitorSection));
                OnPropertyChanged(nameof(IsTopologySecuritySection));
                OnPropertyChanged(nameof(IsHistoryPoliciesSection));

                if (!_isSyncingSectionAndViewMode)
                {
                    _isSyncingSectionAndViewMode = true;
                    try
                    {
                        if (value == AppSection.TopologySecurity)
                        {
                            CurrentViewMode = DeviceViewMode.Topology;
                        }
                        else if (value == AppSection.LanControl && CurrentViewMode == DeviceViewMode.Topology)
                        {
                            CurrentViewMode = _lastLanViewMode;
                        }
                        else if (value == AppSection.HistoryPolicies)
                        {
                            RefreshHistoryReport();
                        }
                        else if (value == AppSection.AppMonitor)
                        {
                            AppMonitor.RefreshFromTrackers();
                        }
                    }
                    finally
                    {
                        _isSyncingSectionAndViewMode = false;
                    }
                }
            }
        }
    }

    public bool IsLanControlSection => _activeSection == AppSection.LanControl;
    public bool IsAppMonitorSection => _activeSection == AppSection.AppMonitor;
    public bool IsTopologySecuritySection => _activeSection == AppSection.TopologySecurity;
    public bool IsHistoryPoliciesSection => _activeSection == AppSection.HistoryPolicies;

    public string ThemePaletteId
    {
        get => _themePaletteId;
        set
        {
            string? normalized = NormalizePaletteId(value);
            if (normalized is null) return;

            if (SetProperty(ref _themePaletteId, normalized))
            {
                _profileStore.Preferences.ThemePaletteId = normalized;
                _profileStore.Save();
                ThemeManager.ApplyPalette(normalized);
                OnPropertyChanged(nameof(SelectedThemePalette));
                ThemePaletteRequested?.Invoke(normalized);
            }
        }
    }

    public ThemePaletteOption SelectedThemePalette
    {
        get
        {
            for (int i = 0; i < BuiltInThemePalettes.Count; i++)
            {
                if (string.Equals(BuiltInThemePalettes[i].Id, _themePaletteId, StringComparison.OrdinalIgnoreCase))
                {
                    return BuiltInThemePalettes[i];
                }
            }
            return BuiltInThemePalettes[0];
        }
        set
        {
            if (value is not null)
            {
                ThemePaletteId = value.Id;
            }
        }
    }

    public int ThrottleAllDownloadCapKBps
    {
        get => _throttleAllDownloadCapKBps;
        set => SetProperty(ref _throttleAllDownloadCapKBps, Math.Max(0, value));
    }

    public int ThrottleAllUploadCapKBps
    {
        get => _throttleAllUploadCapKBps;
        set => SetProperty(ref _throttleAllUploadCapKBps, Math.Max(0, value));
    }

    public bool IsThrottleAllActive
    {
        get => _isThrottleAllActive;
        private set => SetProperty(ref _isThrottleAllActive, value);
    }

    public bool ThrottleAllKeepAppliedToNewDevices
    {
        get => _throttleAllKeepAppliedToNewDevices;
        set => SetProperty(ref _throttleAllKeepAppliedToNewDevices, value);
    }

    public int BatchDownloadCapKBps
    {
        get => _batchDownloadCapKBps;
        set => SetProperty(ref _batchDownloadCapKBps, Math.Max(0, value));
    }

    public int BatchUploadCapKBps
    {
        get => _batchUploadCapKBps;
        set => SetProperty(ref _batchUploadCapKBps, Math.Max(0, value));
    }

    public int SelectedBatchCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Devices.Count; i++)
            {
                DeviceViewModel vm = Devices[i];
                if (vm.IsBatchSelected && vm.CanThrottle)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public bool HasBatchSelection => SelectedBatchCount > 0;

    public bool IsAllFilteredSelected
    {
        get
        {
            bool anyThrottleable = false;
            foreach (object? item in FilteredDevices)
            {
                if (item is DeviceViewModel vm && vm.CanThrottle)
                {
                    anyThrottleable = true;
                    if (!vm.IsBatchSelected)
                    {
                        return false;
                    }
                }
            }
            return anyThrottleable;
        }
        set => SetFilteredBatchSelection(value);
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set => SetProperty(ref _isInspectorOpen, value);
    }

    public bool IsGamingBoostActive
    {
        get => _isGamingBoostActive;
        set
        {
            if (SetProperty(ref _isGamingBoostActive, value))
            {
                OnPropertyChanged(nameof(GamingBoostButtonText));
                UpdateTotalSpeedTickerText();
            }
        }
    }

    public string GamingBoostButtonText => IsGamingBoostActive ? "BOOST ACTIVE" : "BOOST MY PC";

    public bool IsMiniHudActive
    {
        get => _isMiniHudActive;
        set
        {
            if (SetProperty(ref _isMiniHudActive, value))
            {
                RequestMiniHudVisibilityChanged?.Invoke(value);
            }
        }
    }

    public string TotalSpeedTickerText
    {
        get => _totalSpeedTickerText;
        private set => SetProperty(ref _totalSpeedTickerText, value);
    }

    private void UpdateTotalSpeedTickerText()
    {
        TotalSpeedTickerText = $"▼ {TotalDownloadText}   ▲ {TotalUploadText}   ● Ping {GatewayLatencyText}{(IsGamingBoostActive ? "  [⚡ BOOST ACTIVE]" : "")}";
    }

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value) && value is not null && !_isSyncingAdapters)
            {
                BindToSelectedAdapter(value);
            }
        }
    }

    public bool HasSelectedDevice => _selectedDevice is not null;

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                if (value is null)
                {
                    IsInspectorOpen = false;
                }
                else if (CurrentViewMode != DeviceViewMode.Topology)
                {
                    IsInspectorOpen = true;
                }
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                FilteredDevices.Refresh();
                OnPropertyChanged(nameof(IsAllFilteredSelected));
            }
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value))
            {
                FilteredDevices.Refresh();
                OnPropertyChanged(nameof(IsAllFilteredSelected));
            }
        }
    }

    public int ActiveWorkspaceTab
    {
        get => _activeWorkspaceTab;
        set
        {
            if (SetProperty(ref _activeWorkspaceTab, value) && value == 1)
            {
                RefreshHistoryReport();
            }
        }
    }

    public bool IsRedirecting
    {
        get => _isRedirecting;
        private set
        {
            if (SetProperty(ref _isRedirecting, value))
            {
                OnPropertyChanged(nameof(RedirectionButtonText));
            }
        }
    }

    public string RedirectionButtonText => _isRedirecting ? "Stop Redirection" : "Start Redirection";

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public bool UseBitsPerSecond
    {
        get => _useBitsPerSecond;
        set
        {
            if (SetProperty(ref _useBitsPerSecond, value))
            {
                _profileStore.Preferences.UseBitsPerSecond = value;
                _profileStore.Save();
                OnPropertyChanged(nameof(UnitModeButtonText));
                foreach (DeviceViewModel vm in Devices)
                {
                    vm.SetUnitMode(value);
                }
                AppMonitor.SetUnitMode(value);
            }
        }
    }

    public string UnitModeButtonText => _useBitsPerSecond ? "Units: Mbps (Bits)" : "Units: MB/s (Bytes)";

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
            {
                _profileStore.Preferences.CloseToTray = value;
                _profileStore.Save();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string DriverStatusText
    {
        get => _driverStatusText;
        private set => SetProperty(ref _driverStatusText, value);
    }

    public bool IsNpcapMissing
    {
        get => _isNpcapMissing;
        private set => SetProperty(ref _isNpcapMissing, value);
    }

    public bool IsInstallingNpcap
    {
        get => _isInstallingNpcap;
        private set
        {
            if (SetProperty(ref _isInstallingNpcap, value))
            {
                InstallNpcapCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TotalDownloadText
    {
        get => _totalDownloadText;
        private set => SetProperty(ref _totalDownloadText, value);
    }

    public string TotalUploadText
    {
        get => _totalUploadText;
        private set => SetProperty(ref _totalUploadText, value);
    }

    public string PeakDownloadText
    {
        get => _peakDownloadText;
        private set => SetProperty(ref _peakDownloadText, value);
    }

    public string SessionTotalText
    {
        get => _sessionTotalText;
        private set => SetProperty(ref _sessionTotalText, value);
    }

    public string GatewayLatencyText
    {
        get => _gatewayLatencyText;
        private set => SetProperty(ref _gatewayLatencyText, value);
    }

    public string ArpGuardStatusText
    {
        get => _arpGuardStatusText;
        private set => SetProperty(ref _arpGuardStatusText, value);
    }

    public bool IsArpAlertActive
    {
        get => _isArpAlertActive;
        private set => SetProperty(ref _isArpAlertActive, value);
    }

    public int OnlineDevicesCount
    {
        get => _onlineDevicesCount;
        private set => SetProperty(ref _onlineDevicesCount, value);
    }

    public int ThrottledDevicesCount
    {
        get => _throttledDevicesCount;
        private set => SetProperty(ref _throttledDevicesCount, value);
    }

    public int BlockedDevicesCount
    {
        get => _blockedDevicesCount;
        private set => SetProperty(ref _blockedDevicesCount, value);
    }

    public IReadOnlyList<double> NetworkDownloadSeries
    {
        get => _networkDownloadSeries;
        private set => SetProperty(ref _networkDownloadSeries, value);
    }

    public IReadOnlyList<double> NetworkUploadSeries
    {
        get => _networkUploadSeries;
        private set => SetProperty(ref _networkUploadSeries, value);
    }

    public string NewDomainInput
    {
        get => _newDomainInput;
        set => SetProperty(ref _newDomainInput, value);
    }

    public DnsQueryRecord? SelectedDnsRecord
    {
        get => _selectedDnsRecord;
        set => SetProperty(ref _selectedDnsRecord, value);
    }

    public UsageTimeRange SelectedHistoryRange
    {
        get => _selectedHistoryRange;
        set
        {
            if (SetProperty(ref _selectedHistoryRange, value))
            {
                OnPropertyChanged(nameof(SelectedHistoryRangeTitle));
                RefreshHistoryReport();
            }
        }
    }

    public string SelectedHistoryRangeTitle => _selectedHistoryRange switch
    {
        UsageTimeRange.Today => "Today (Last 24 Hours)",
        UsageTimeRange.Past7Days => "Past 7 Days (Weekly Rollup)",
        UsageTimeRange.Past30Days => "Past 30 Days (Monthly Rollup)",
        _ => "All-Time Recorded Usage"
    };

    public string HistoryTotalDownloadText
    {
        get => _historyTotalDownloadText;
        private set => SetProperty(ref _historyTotalDownloadText, value);
    }

    public string HistoryTotalUploadText
    {
        get => _historyTotalUploadText;
        private set => SetProperty(ref _historyTotalUploadText, value);
    }

    public string HistoryGrandTotalText
    {
        get => _historyGrandTotalText;
        private set => SetProperty(ref _historyGrandTotalText, value);
    }

    public IReadOnlyList<DailyUsagePoint> HistoryDailySeries
    {
        get => _historyDailySeries;
        private set => SetProperty(ref _historyDailySeries, value);
    }

    public DeviceUsageSummary? SelectedHistoryDevice
    {
        get => _selectedHistoryDevice;
        set
        {
            if (SetProperty(ref _selectedHistoryDevice, value))
            {
                OnPropertyChanged(nameof(SelectedHistoryDeviceSeries));
            }
        }
    }

    public IReadOnlyList<DailyUsagePoint> SelectedHistoryDeviceSeries =>
        _selectedHistoryDevice?.DailySeries ?? _historyDailySeries;

    public string ProtoHttpTlsSummary
    {
        get => _protoHttpTlsSummary;
        private set => SetProperty(ref _protoHttpTlsSummary, value);
    }

    public string ProtoTcpSummary
    {
        get => _protoTcpSummary;
        private set => SetProperty(ref _protoTcpSummary, value);
    }

    public string ProtoUdpSummary
    {
        get => _protoUdpSummary;
        private set => SetProperty(ref _protoUdpSummary, value);
    }

    public string ProtoDnsSummary
    {
        get => _protoDnsSummary;
        private set => SetProperty(ref _protoDnsSummary, value);
    }

    public void Initialize()
    {
        if (WpcapNative.IsDriverAvailable(out string version))
        {
            IsNpcapMissing = false;
            DriverStatusText = version;
        }
        else
        {
            IsNpcapMissing = true;
            DriverStatusText = "Npcap / WinPcap Not Detected";
            StatusMessage = "Npcap driver is missing. Click 'Install Npcap' in the status bar to install it.";

            if (Application.Current is not null)
            {
                MessageBoxResult choice = MessageBox.Show(
                    "Npcap packet capture driver is not installed on this PC.\n\nWould you like FishyNet to download and launch the official Npcap installer now?",
                    "Npcap Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (choice == MessageBoxResult.Yes)
                {
                    _ = InstallNpcapAsync();
                }
            }
        }

        _trayService.Initialize();
        _trayService.RestoreRequested += () => RequestShowWindow?.Invoke();
        _trayService.ToggleRedirectionRequested += ToggleRedirection;
        _trayService.ScanSubnetRequested += () => _ = ScanSubnetAsync();
        _trayService.ApplyPresetRequested += ApplyPresetToAllLanDevices;
        _trayService.MonitorAllRequested += ExecuteMonitorAll;
        _trayService.UnspoofAllRequested += ExecuteUnspoofAll;
        _trayService.ExitRequested += () => RequestExplicitExit?.Invoke();

        _ = Task.Run(() => ProcessRegistryEventsAsync(_backgroundCts.Token));
        _ = Task.Run(() => RunPeriodicLatencySweepAsync(_backgroundCts.Token));

        NetworkChange.NetworkAddressChanged += OnNetworkEnvironmentChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkEnvironmentChanged;

        ApplyDiscoveredAdapters(CaptureEngine.DiscoverSuitableAdapters());

        RefreshHistoryReport();
        _telemetryTimer.Start();
    }

    private async Task InstallNpcapAsync()
    {
        if (IsInstallingNpcap) return;

        IsInstallingNpcap = true;
        try
        {
            NpcapInstallResult result = await NpcapInstallerService.DownloadAndInstallAsync(
                status => DispatchToUiOrInline(() => StatusMessage = status),
                _backgroundCts.Token);

            if (WpcapNative.IsDriverAvailable(out string version))
            {
                IsNpcapMissing = false;
                DriverStatusText = version;
                StatusMessage = "Npcap installed successfully. Binding network adapter...";
                ApplyDiscoveredAdapters(CaptureEngine.DiscoverSuitableAdapters());
            }
            else
            {
                StatusMessage = result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore on shutdown
        }
        finally
        {
            IsInstallingNpcap = false;
        }
    }

    private void OnNetworkEnvironmentChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;

        CancellationToken ct;
        lock (_adapterRefreshLock)
        {
            _adapterRefreshCts?.Cancel();
            _adapterRefreshCts?.Dispose();
            _adapterRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundCts.Token);
            ct = _adapterRefreshCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                IReadOnlyList<NetworkAdapterInfo> foundAdapters = CaptureEngine.DiscoverSuitableAdapters();
                DispatchToUiOrInline(() => ApplyDiscoveredAdapters(foundAdapters));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer network change event or shutdown
            }
        }, ct);
    }

    private void ApplyDiscoveredAdapters(IReadOnlyList<NetworkAdapterInfo> foundAdapters)
    {
        if (_disposed) return;

        NetworkAdapterInfo? previousSelected = _selectedAdapter;
        NetworkAdapterInfo? activeEngineAdapter = _engine.ActiveAdapter;

        _isSyncingAdapters = true;
        try
        {
            var foundIds = new HashSet<string>(foundAdapters.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < foundAdapters.Count; i++)
            {
                foundIds.Add(foundAdapters[i].Id);
            }

            for (int i = Adapters.Count - 1; i >= 0; i--)
            {
                if (!foundIds.Contains(Adapters[i].Id))
                {
                    Adapters.RemoveAt(i);
                }
            }

            for (int i = 0; i < foundAdapters.Count; i++)
            {
                NetworkAdapterInfo target = foundAdapters[i];
                int existingIdx = -1;
                for (int j = i; j < Adapters.Count; j++)
                {
                    if (string.Equals(Adapters[j].Id, target.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx < 0)
                {
                    Adapters.Insert(i, target);
                }
                else
                {
                    if (existingIdx != i)
                    {
                        Adapters.Move(existingIdx, i);
                    }

                    if (Adapters[i] != target)
                    {
                        Adapters[i] = target;
                    }
                }
            }

            if (Adapters.Count == 0)
            {
                SelectedAdapter = null;
            }
            else
            {
                NetworkAdapterInfo? chosen = null;
                if (previousSelected is not null)
                {
                    chosen = Adapters.FirstOrDefault(a => string.Equals(a.Id, previousSelected.Id, StringComparison.OrdinalIgnoreCase));
                }

                if (chosen is null)
                {
                    string? prefId = _profileStore.Preferences.PreferredAdapterId;
                    chosen = Adapters.FirstOrDefault(a => string.Equals(a.Id, prefId, StringComparison.OrdinalIgnoreCase)) ?? Adapters[0];
                }

                SelectedAdapter = chosen;
            }
        }
        finally
        {
            _isSyncingAdapters = false;
        }

        if (_selectedAdapter is null)
        {
            if (activeEngineAdapter is not null)
            {
                _engine.StopAll();
                IsRedirecting = false;
                Devices.Clear();
                SelectedDevice = null;
            }

            StatusMessage = "No active IPv4 network adapter with a default gateway was found.";
            return;
        }

        if (activeEngineAdapter != _selectedAdapter)
        {
            BindToSelectedAdapter(_selectedAdapter);
        }
    }

    public void NotifyMinimizedToTray()
    {
        _trayService.ShowNotification(
            "FishyNet Running in Background",
            "Bandwidth shaping and usage recording continue in the system tray. Double-click the tray icon to restore.");
    }

    private void BindToSelectedAdapter(NetworkAdapterInfo adapter)
    {
        IsRedirecting = false;
        Devices.Clear();
        SelectedDevice = null;

        _profileStore.Preferences.PreferredAdapterId = adapter.Id;
        _profileStore.Save();

        if (_engine.BindAdapter(adapter, out string? error))
        {
            _ = ScanSubnetAsync();
        }
        else
        {
            StatusMessage = error ?? "Failed to open adapter with Npcap.";
        }
    }

    private async Task ScanSubnetAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            await _engine.StartSubnetScanAsync();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ToggleRedirection()
    {
        if (IsRedirecting)
        {
            _engine.StopRedirection();
            IsRedirecting = false;
        }
        else
        {
            // If all throttleable LAN devices are currently bypassed, re-enable monitoring automatically
            bool anyMonitored = Devices.Any(d => d.CanThrottle && d.IsSpoofed);
            if (!anyMonitored)
            {
                foreach (DeviceViewModel vm in Devices)
                {
                    if (vm.CanThrottle)
                    {
                        vm.IsSpoofed = true;
                    }
                }
            }

            if (_engine.StartRedirection(out string? err))
            {
                IsRedirecting = true;
            }
            else
            {
                StatusMessage = err ?? "Could not start packet redirection.";
            }
        }
    }

    private void ApplyPresetToDevice(DeviceViewModel vm, BandwidthPresetProfile preset)
    {
        if (!vm.CanThrottle) return;
        vm.DownloadCapKBps = preset.DownloadCapKBps;
        vm.UploadCapKBps = preset.UploadCapKBps;
        vm.IsBlocked = preset.BlockAll;
        vm.IsSpoofed = true;
        StatusMessage = $"Applied '{preset.Name}' profile to {vm.DisplayName}.";
    }

    private void ApplyPresetToAllLanDevices(BandwidthPresetProfile preset)
    {
        var updatedModels = new List<NetworkDevice>(Devices.Count);
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (!vm.CanThrottle) continue;

            vm.ApplyBatchLimits(
                preset.DownloadCapKBps,
                preset.UploadCapKBps,
                preset.BlockAll,
                isSpoofed: true,
                suppressRuleModified: true);
            _engine.SpoofDevice(vm.Model);
            updatedModels.Add(vm.Model);
        }

        _profileStore.UpdateBatch(updatedModels);
        RefreshDeviceSummaryCounts();
        StatusMessage = $"Applied '{preset.Name}' profile to {updatedModels.Count} LAN devices.";
    }

    private void ExecuteMonitorAll()
    {
        int count = 0;
        foreach (DeviceViewModel vm in Devices)
        {
            if (vm.CanThrottle)
            {
                vm.IsSpoofed = true;
                _engine.SpoofDevice(vm.Model);
                count++;
            }
        }

        StatusMessage = $"Switched {count} LAN devices to MONITORED state.";
    }

    private void ExecuteUnspoofAll()
    {
        var updatedModels = new List<NetworkDevice>(Devices.Count);
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (!vm.CanThrottle) continue;

            vm.ApplyBatchLimits(0, 0, isBlocked: false, isSpoofed: false, suppressRuleModified: true);
            updatedModels.Add(vm.Model);
        }

        IsThrottleAllActive = false;
        _profileStore.Preferences.ThrottleAllEnabled = false;
        _profileStore.UpdateBatch(updatedModels);
        _engine.CompleteUnspoof();
        RefreshDeviceSummaryCounts();
        StatusMessage = "Bypassed all LAN hosts, restored ARP tables, and cleared active caps.";
    }

    private void ExecuteNavigateSection(object? parameter)
    {
        if (parameter is AppSection section)
        {
            ActiveSection = section;
            return;
        }

        if (parameter is string str && Enum.TryParse(str, ignoreCase: true, out AppSection parsed))
        {
            ActiveSection = parsed;
        }
    }

    private void ExecuteSelectThemePalette(object? parameter)
    {
        string? id = parameter switch
        {
            ThemePaletteOption opt => opt.Id,
            string s => s,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(id))
        {
            ThemePaletteId = id;
        }
    }

    private static string? NormalizePaletteId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        ReadOnlySpan<char> trimmed = id.AsSpan().Trim();
        for (int i = 0; i < BuiltInThemePalettes.Count; i++)
        {
            if (BuiltInThemePalettes[i].Id.AsSpan().Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return BuiltInThemePalettes[i].Id;
            }
        }

        return null;
    }

    private void ApplyThrottleAll()
    {
        int dl = Math.Max(0, _throttleAllDownloadCapKBps);
        int ul = Math.Max(0, _throttleAllUploadCapKBps);
        bool isThrottling = dl > 0 || ul > 0;

        var updatedModels = new List<NetworkDevice>(Devices.Count);
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (!vm.CanThrottle) continue;

            bool? targetSpoofed = isThrottling && !vm.IsBlocked ? true : null;
            vm.ApplyBatchLimits(dl, ul, isBlocked: null, isSpoofed: targetSpoofed, suppressRuleModified: true);
            if (isThrottling && vm.IsSpoofed)
            {
                _engine.SpoofDevice(vm.Model);
            }
            updatedModels.Add(vm.Model);
        }

        IsThrottleAllActive = isThrottling;
        _profileStore.Preferences.ThrottleAllEnabled = isThrottling;
        _profileStore.Preferences.ThrottleAllDownloadCapKBps = dl;
        _profileStore.Preferences.ThrottleAllUploadCapKBps = ul;
        _profileStore.UpdateBatch(updatedModels);
        RefreshDeviceSummaryCounts();

        StatusMessage = isThrottling
            ? $"Applied Throttle All ({dl} KB/s down · {ul} KB/s up) to {updatedModels.Count} LAN devices."
            : $"Cleared bandwidth caps across {updatedModels.Count} LAN devices.";
    }

    private void ClearAllThrottles()
    {
        var updatedModels = new List<NetworkDevice>(Devices.Count);
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (!vm.CanThrottle) continue;

            vm.ApplyBatchLimits(0, 0, isBlocked: null, isSpoofed: null, suppressRuleModified: true);
            updatedModels.Add(vm.Model);
        }

        IsThrottleAllActive = false;
        _profileStore.Preferences.ThrottleAllEnabled = false;
        _profileStore.UpdateBatch(updatedModels);
        RefreshDeviceSummaryCounts();

        StatusMessage = $"Cleared bandwidth caps across {updatedModels.Count} LAN devices.";
    }

    private void ExecuteApplyThrottleAllPreset(object? parameter)
    {
        if (parameter is null) return;

        if (parameter is int intCap)
        {
            if (intCap <= 0)
            {
                ClearAllThrottles();
                return;
            }

            ThrottleAllDownloadCapKBps = intCap;
            ApplyThrottleAll();
            return;
        }

        if (parameter is BandwidthPresetProfile preset)
        {
            if (preset.BlockAll)
            {
                ApplyPresetToAllLanDevices(preset);
                return;
            }

            ThrottleAllDownloadCapKBps = preset.DownloadCapKBps;
            ThrottleAllUploadCapKBps = preset.UploadCapKBps;
            ApplyThrottleAll();
            return;
        }

        string? text = parameter as string ?? parameter.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int sepIndex = span.IndexOfAny(':', ',', '/');
        if (sepIndex >= 0)
        {
            ReadOnlySpan<char> dlSpan = span[..sepIndex].Trim();
            ReadOnlySpan<char> ulSpan = span[(sepIndex + 1)..].Trim();
            if (int.TryParse(dlSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dl) &&
                int.TryParse(ulSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ul))
            {
                if (dl < 0 || ul < 0) return;
                ThrottleAllDownloadCapKBps = dl;
                ThrottleAllUploadCapKBps = ul;
                ApplyThrottleAll();
            }
            return;
        }

        if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out int singleDl))
        {
            if (singleDl < 0) return;
            if (singleDl == 0)
            {
                ClearAllThrottles();
                return;
            }

            ThrottleAllDownloadCapKBps = singleDl;
            ApplyThrottleAll();
        }
    }

    private void ToggleSelectAllFiltered()
    {
        SetFilteredBatchSelection(!IsAllFilteredSelected);
    }

    private void SetFilteredBatchSelection(bool select)
    {
        _suppressBatchSelectionNotifications = true;
        try
        {
            foreach (object? item in FilteredDevices)
            {
                if (item is DeviceViewModel vm)
                {
                    if (select)
                    {
                        if (vm.CanThrottle)
                        {
                            vm.IsBatchSelected = true;
                        }
                    }
                    else
                    {
                        vm.IsBatchSelected = false;
                    }
                }
            }
        }
        finally
        {
            _suppressBatchSelectionNotifications = false;
        }

        NotifyBatchSelectionPropertiesChanged();
    }

    private void ClearBatchSelection()
    {
        _suppressBatchSelectionNotifications = true;
        try
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                Devices[i].IsBatchSelected = false;
            }
        }
        finally
        {
            _suppressBatchSelectionNotifications = false;
        }

        NotifyBatchSelectionPropertiesChanged();
    }

    private void ExecuteApplyBatchThrottle()
    {
        int dl = Math.Max(0, _batchDownloadCapKBps);
        int ul = Math.Max(0, _batchUploadCapKBps);
        bool isThrottling = dl > 0 || ul > 0;

        int count = ExecuteBatchDeviceAction(
            dl,
            ul,
            isBlocked: null,
            spoofSelector: vm => isThrottling && !vm.IsBlocked ? true : null,
            spoofWhenMonitored: isThrottling,
            unspoofWhenBypassed: false);

        if (count > 0)
        {
            StatusMessage = isThrottling
                ? $"Applied batch limits ({dl} KB/s down · {ul} KB/s up) to {count} selected devices."
                : $"Cleared bandwidth caps for {count} selected devices.";
        }
    }

    private void ExecuteBatchMonitor()
    {
        int count = ExecuteBatchDeviceAction(
            null,
            null,
            null,
            static _ => true,
            spoofWhenMonitored: true,
            unspoofWhenBypassed: false);

        if (count > 0)
        {
            StatusMessage = $"Switched {count} selected devices to MONITORED state.";
        }
    }

    private void ExecuteBatchBypass()
    {
        int count = ExecuteBatchDeviceAction(
            null,
            null,
            null,
            static _ => false,
            spoofWhenMonitored: false,
            unspoofWhenBypassed: true);

        if (count > 0)
        {
            StatusMessage = $"Switched {count} selected devices to BYPASSED state.";
        }
    }

    private void ExecuteBatchBlock()
    {
        int count = ExecuteBatchDeviceAction(
            null,
            null,
            true,
            static _ => true,
            spoofWhenMonitored: true,
            unspoofWhenBypassed: false);

        if (count > 0)
        {
            StatusMessage = $"Blocked all traffic for {count} selected devices.";
        }
    }

    private void ExecuteBatchUnblock()
    {
        int count = ExecuteBatchDeviceAction(
            null,
            null,
            false,
            static _ => null,
            spoofWhenMonitored: false,
            unspoofWhenBypassed: false);

        if (count > 0)
        {
            StatusMessage = $"Unblocked {count} selected devices.";
        }
    }

    private void ExecuteBatchClearCaps()
    {
        int count = ExecuteBatchDeviceAction(
            0,
            0,
            null,
            static _ => null,
            spoofWhenMonitored: false,
            unspoofWhenBypassed: false);

        if (count > 0)
        {
            StatusMessage = $"Cleared bandwidth caps for {count} selected devices.";
        }
    }

    private int ExecuteBatchDeviceAction(
        int? downloadCapKBps,
        int? uploadCapKBps,
        bool? isBlocked,
        Func<DeviceViewModel, bool?> spoofSelector,
        bool spoofWhenMonitored,
        bool unspoofWhenBypassed)
    {
        var updatedModels = new List<NetworkDevice>(Devices.Count);
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (!vm.IsBatchSelected || !vm.CanThrottle) continue;

            bool? targetSpoofed = spoofSelector(vm);
            vm.ApplyBatchLimits(downloadCapKBps, uploadCapKBps, isBlocked, targetSpoofed, suppressRuleModified: true);

            if (spoofWhenMonitored && vm.IsSpoofed)
            {
                _engine.SpoofDevice(vm.Model);
            }
            else if (unspoofWhenBypassed && !vm.IsSpoofed)
            {
                _engine.UnspoofDevice(vm.Model, burstCount: 5);
            }

            updatedModels.Add(vm.Model);
        }

        if (updatedModels.Count == 0)
        {
            return 0;
        }

        _profileStore.UpdateBatch(updatedModels);
        RefreshDeviceSummaryCounts();
        return updatedModels.Count;
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            for (int i = 0; i < _subscribedDevices.Count; i++)
            {
                _subscribedDevices[i].BatchSelectionChanged -= OnDeviceBatchSelectionChanged;
            }
            _subscribedDevices.Clear();
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (object? item in e.OldItems)
                {
                    if (item is DeviceViewModel oldVm)
                    {
                        oldVm.BatchSelectionChanged -= OnDeviceBatchSelectionChanged;
                        _subscribedDevices.Remove(oldVm);
                    }
                }
            }

            if (e.NewItems is not null)
            {
                foreach (object? item in e.NewItems)
                {
                    if (item is DeviceViewModel newVm)
                    {
                        newVm.BatchSelectionChanged -= OnDeviceBatchSelectionChanged;
                        newVm.BatchSelectionChanged += OnDeviceBatchSelectionChanged;
                        if (!_subscribedDevices.Contains(newVm))
                        {
                            _subscribedDevices.Add(newVm);
                        }
                    }
                }
            }
        }

        RefreshDeviceSummaryCounts();
        NotifyBatchSelectionPropertiesChanged();
    }

    private void OnDeviceBatchSelectionChanged(DeviceViewModel _)
    {
        if (_suppressBatchSelectionNotifications) return;
        NotifyBatchSelectionPropertiesChanged();
    }

    private void NotifyBatchSelectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedBatchCount));
        OnPropertyChanged(nameof(HasBatchSelection));
        OnPropertyChanged(nameof(IsAllFilteredSelected));

        ApplyBatchThrottleCommand.RaiseCanExecuteChanged();
        BatchMonitorCommand.RaiseCanExecuteChanged();
        BatchBypassCommand.RaiseCanExecuteChanged();
        BatchBlockCommand.RaiseCanExecuteChanged();
        BatchUnblockCommand.RaiseCanExecuteChanged();
        BatchClearCapsCommand.RaiseCanExecuteChanged();
        ClearBatchSelectionCommand.RaiseCanExecuteChanged();
    }

    private void RefreshDeviceSummaryCounts()
    {
        int throttled = 0;
        int blocked = 0;
        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (vm.IsBlocked || vm.Model.IsQuotaExceeded)
            {
                blocked++;
            }
            else if (vm.DownloadCapKBps > 0 || vm.UploadCapKBps > 0)
            {
                throttled++;
            }
        }

        OnlineDevicesCount = Devices.Count;
        ThrottledDevicesCount = throttled;
        BlockedDevicesCount = blocked;
    }

    private void ApplyThrottleAllToNewDeviceIfActive(NetworkDevice dev)
    {
        if (!IsThrottleAllActive || !ThrottleAllKeepAppliedToNewDevices) return;
        if (dev.IsLocalHost || dev.IsGateway) return;

        int dl = Math.Max(0, _throttleAllDownloadCapKBps);
        int ul = Math.Max(0, _throttleAllUploadCapKBps);
        dev.DownloadCapKBps = dl;
        dev.UploadCapKBps = ul;
        if ((dl > 0 || ul > 0) && !dev.IsBlocked)
        {
            dev.IsSpoofed = true;
        }
    }

    private void DispatchToUiOrInline(Action action)
    {
        if (Application.Current is { Dispatcher: { } dispatcher })
        {
            dispatcher.BeginInvoke(action);
        }
        else if (_uiDispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _uiDispatcher.BeginInvoke(action);
        }
    }

    private async Task WakeSelectedDeviceAsync()
    {
        if (SelectedDevice is null) return;
        bool ok = await NetworkToolsService.SendWakeOnLanAsync(SelectedDevice.Model.Mac);
        StatusMessage = ok
            ? $"Sent 102-byte Wake-on-LAN Magic Packet to {SelectedDevice.DisplayName} ({SelectedDevice.MacAddressText})."
            : $"Failed to send Wake-on-LAN packet to {SelectedDevice.MacAddressText}.";
    }

    private void ExecuteAddBlockedDomain()
    {
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(NewDomainInput)) return;
        SelectedDevice.AddBlockedDomain(NewDomainInput);
        StatusMessage = $"Blocked domain '{NewDomainInput}' on {SelectedDevice.DisplayName}.";
        NewDomainInput = string.Empty;
    }

    private void ExecuteBlockSelectedDnsDomain()
    {
        if (SelectedDnsRecord is null) return;
        DeviceViewModel? targetVm = Devices.FirstOrDefault(d => d.Model.Mac == SelectedDnsRecord.DeviceMac);
        if (targetVm is not null && targetVm.CanThrottle)
        {
            targetVm.AddBlockedDomain(SelectedDnsRecord.Domain);
            SelectedDevice = targetVm;
            StatusMessage = $"Blocked domain '{SelectedDnsRecord.Domain}' for {targetVm.DisplayName}.";
        }
    }

    private void RefreshHistoryReport()
    {
        UsageHistoryReport report = _historyStore.GetUsageReport(_selectedHistoryRange);
        HistoryTotalDownloadText = report.TotalDownloadFormatted;
        HistoryTotalUploadText = report.TotalUploadFormatted;
        HistoryGrandTotalText = report.GrandTotalFormatted;
        HistoryDailySeries = report.NetworkDailySeries;

        string? prevSelectedMac = _selectedHistoryDevice?.MacAddress;
        HistoryDeviceSummaries.Clear();
        foreach (DeviceUsageSummary row in report.DeviceSummaries)
        {
            HistoryDeviceSummaries.Add(row);
        }

        SelectedHistoryDevice = HistoryDeviceSummaries.FirstOrDefault(d => d.MacAddress == prevSelectedMac)
            ?? HistoryDeviceSummaries.FirstOrDefault();
    }

    private void ExecuteExportHistoryCsv()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Historical Usage Report",
            Filter = "CSV Report (*.csv)|*.csv",
            FileName = $"FishyNet_Usage_{_selectedHistoryRange}_{DateTime.Now:yyyyMMdd}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            _historyStore.ExportCsv(_selectedHistoryRange, dlg.FileName);
            StatusMessage = $"Exported {_selectedHistoryRange} usage report to {dlg.FileName}.";
        }
    }

    private void ExecuteClearHistory()
    {
        MessageBoxResult res = MessageBox.Show(
            "Clear all recorded historical usage data for all devices?",
            "Confirm History Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            _historyStore.ClearHistory();
            RefreshHistoryReport();
            StatusMessage = "Historical usage ledger cleared.";
        }
    }

    private void ExecuteExportDevicesCsv()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Discovered LAN Devices",
            Filter = "CSV Inventory (*.csv)|*.csv",
            FileName = $"FishyNet_Devices_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Display Name,IP Address,MAC Address,Vendor,Category,Download Speed,Upload Speed,Session Total,7-Day Total,30-Day Total,Status");
            foreach (DeviceViewModel d in Devices)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"\"{d.DisplayName.Replace("\"", "\"\"")}\",\"{d.IpAddressText}\",\"{d.MacAddressText}\",\"{d.Vendor}\",\"{d.Category}\",\"{d.DownloadSpeedText}\",\"{d.UploadSpeedText}\",\"{d.SessionTotalText}\",\"{d.WeekUsageText}\",\"{d.MonthUsageText}\",\"{d.StatusBadgeText}\"");
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Exported {Devices.Count} devices to {dlg.FileName}.";
        }
    }

    private bool FilterDevicePredicate(object obj)
    {
        if (obj is not DeviceViewModel vm) return false;

        if (_statusFilter == "Throttled" && vm.DownloadCapKBps == 0 && vm.UploadCapKBps == 0) return false;
        if (_statusFilter == "Blocked" && !vm.IsBlocked && !vm.Model.IsQuotaExceeded) return false;
        if (_statusFilter == "Bypassed" && (vm.IsSpoofed || !vm.CanThrottle)) return false;
        if (_statusFilter == "Active" && vm.CurrentDownloadBps < 10 && vm.CurrentUploadBps < 10) return false;

        if (string.IsNullOrWhiteSpace(_searchQuery)) return true;

        string q = _searchQuery.Trim();
        return vm.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.IpAddressText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.MacAddressText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    internal int ProcessPendingRegistryEvents()
    {
        int processed = 0;
        while (_registry.Events.TryRead(out NetworkEvent? evt))
        {
            HandleRegistryEvent(evt, CancellationToken.None);
            processed++;
        }
        return processed;
    }

    internal async Task ProcessRegistryEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (NetworkEvent evt in _registry.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                HandleRegistryEvent(evt, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void HandleRegistryEvent(NetworkEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case NetworkEventKind.DeviceDiscovered when evt.Device is not null:
            {
                NetworkDevice dev = evt.Device;
                _profileStore.ApplySavedProfileToDevice(dev);
                ApplyThrottleAllToNewDeviceIfActive(dev);
                _ = _hostResolver.ResolveDeviceHostnameAsync(dev, ct);

                DispatchToUiOrInline(() =>
                {
                    if (Devices.Any(d => d.Model.Mac == dev.Mac)) return;

                    var vm = new DeviceViewModel(dev, _useBitsPerSecond);
                    vm.RuleModified += OnDeviceRuleModified;
                    Devices.Add(vm);
                    SelectedDevice ??= vm;
                    RefreshDeviceSummaryCounts();

                    if (!dev.IsLocalHost && !dev.IsGateway && dev.IsSpoofed)
                    {
                        _engine.SpoofDevice(dev);
                    }

                    if (!dev.IsLocalHost && !dev.IsGateway)
                    {
                        _timelineStore.RecordNewLanDeviceAlert(dev);
                        AppMonitor.RefreshAlertsOnly();
                    }

                    if (!dev.IsLocalHost && !dev.IsGateway && _profileStore.Preferences.NotifyOnNewDevice && Devices.Count > 3)
                    {
                        _trayService.ShowNotification(
                            "New Device Connected",
                            $"{vm.DisplayName} ({vm.IpAddressText} · {vm.Vendor}) joined the network.");
                    }
                });
                break;
            }

            case NetworkEventKind.DeviceUpdated when evt.Device is not null:
            {
                NetworkDevice dev = evt.Device;
                DispatchToUiOrInline(() =>
                {
                    DeviceViewModel? vm = Devices.FirstOrDefault(d => d.Model.Mac == dev.Mac);
                    vm?.SyncMetadataFromModel();
                });
                break;
            }

            case NetworkEventKind.DnsQueryObserved when evt.DnsRecord is not null:
            {
                DnsQueryRecord dns = evt.DnsRecord;
                DispatchToUiOrInline(() =>
                {
                    DnsQueryLogs.Insert(0, dns);
                    if (DnsQueryLogs.Count > 250)
                    {
                        DnsQueryLogs.RemoveAt(DnsQueryLogs.Count - 1);
                    }
                });
                break;
            }

            case NetworkEventKind.ArpSecurityAlert when evt.SecurityAlert is not null:
            {
                ArpSecurityAlert alert = evt.SecurityAlert;
                DispatchToUiOrInline(() =>
                {
                    SecurityAlerts.Insert(0, alert);
                    IsArpAlertActive = true;
                    ArpGuardStatusText = $"ARP ALERT: {alert.OffendingMac}";
                    StatusMessage = alert.Description;
                    _timelineStore.RecordArpSpoofAlert(alert);
                    AppMonitor.RefreshAlertsOnly();

                    if (_profileStore.Preferences.NotifyOnArpAlert)
                    {
                        _trayService.ShowNotification("ARP Spoof Attack Detected!", alert.Description, isWarning: true);
                    }
                });
                break;
            }
        }
    }

    private void OnDeviceRuleModified(DeviceViewModel vm)
    {
        _profileStore.UpdateFromDevice(vm.Model);
        RefreshDeviceSummaryCounts();
        if (vm.CanThrottle)
        {
            if (vm.IsSpoofed)
            {
                _engine.SpoofDevice(vm.Model);
                StatusMessage = $"{vm.DisplayName} is now MONITORED (active ARP redirection).";
            }
            else
            {
                _engine.UnspoofDevice(vm.Model, burstCount: 5);
                StatusMessage = $"{vm.DisplayName} is now BYPASSED (direct to gateway).";
            }
        }
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        SampleTelemetryTick();
    }

    internal void SampleTelemetryTick(DateTime? utcNow = null)
    {
        double totalRxBps = 0;
        double totalTxBps = 0;
        long tickRxDeltaBytes = 0;
        long tickTxDeltaBytes = 0;
        long sessionTotal = 0;
        long totalHttpTls = 0;
        long totalTcp = 0;
        long totalUdp = 0;
        long totalDnsQ = 0;

        int throttled = 0;
        int blocked = 0;

        foreach (DeviceViewModel vm in Devices)
        {
            NetworkDevice dev = vm.Model;
            (long rxDelta, long txDelta) = dev.ConsumeRateDeltas();
            (long rxHistDelta, long txHistDelta) = dev.ConsumeHistoryDeltas();

            tickRxDeltaBytes += rxDelta;
            tickTxDeltaBytes += txDelta;

            if (rxHistDelta > 0 || txHistDelta > 0)
            {
                _historyStore.RecordUsageSample(
                    dev.Mac,
                    dev.DisplayName,
                    dev.Ip.ToString(),
                    dev.Vendor,
                    rxHistDelta,
                    txHistDelta,
                    rxDelta,
                    txDelta);
            }

            (long todayBytes, long weekBytes, long monthBytes) = _historyStore.GetDeviceQuickTotals(dev.Mac);
            vm.SampleTick(rxDelta, txDelta, todayBytes, weekBytes, monthBytes);

            totalRxBps += vm.CurrentDownloadBps;
            totalTxBps += vm.CurrentUploadBps;
            sessionTotal += dev.TotalSessionBytes;

            totalHttpTls += dev.HttpTlsBytes;
            totalTcp += dev.TcpBytes;
            totalUdp += dev.UdpBytes + dev.DnsBytes;
            totalDnsQ += dev.DnsQueryCount;

            if (vm.IsBlocked || dev.IsQuotaExceeded) blocked++;
            else if (vm.DownloadCapKBps > 0 || vm.UploadCapKBps > 0) throttled++;

            if (dev.IsGateway && dev.PingRttMs >= 0)
            {
                GatewayLatencyText = $"{dev.PingRttMs} ms";
            }
        }

        if (totalRxBps > _peakDownloadBps)
        {
            _peakDownloadBps = totalRxBps;
        }

        TotalDownloadText = BandwidthFormatter.FormatRate(totalRxBps, _useBitsPerSecond);
        TotalUploadText = BandwidthFormatter.FormatRate(totalTxBps, _useBitsPerSecond);
        PeakDownloadText = BandwidthFormatter.FormatRate(_peakDownloadBps, _useBitsPerSecond);
        SessionTotalText = BandwidthFormatter.FormatBytes(sessionTotal);

        OnlineDevicesCount = Devices.Count;
        ThrottledDevicesCount = throttled;
        BlockedDevicesCount = blocked;

        Array.Copy(_netDownloadBuffer, 1, _netDownloadBuffer, 0, MainChartPoints - 1);
        _netDownloadBuffer[MainChartPoints - 1] = totalRxBps;
        var downSnap = new double[MainChartPoints];
        Array.Copy(_netDownloadBuffer, downSnap, MainChartPoints);
        NetworkDownloadSeries = downSnap;

        Array.Copy(_netUploadBuffer, 1, _netUploadBuffer, 0, MainChartPoints - 1);
        _netUploadBuffer[MainChartPoints - 1] = totalTxBps;
        var upSnap = new double[MainChartPoints];
        Array.Copy(_netUploadBuffer, upSnap, MainChartPoints);
        NetworkUploadSeries = upSnap;

        long safeSession = Math.Max(1L, sessionTotal);
        ProtoHttpTlsSummary = $"{BandwidthFormatter.FormatBytes(totalHttpTls)} ({(totalHttpTls * 100.0 / safeSession):F0}%)";
        ProtoTcpSummary = $"{BandwidthFormatter.FormatBytes(totalTcp)} ({(totalTcp * 100.0 / safeSession):F0}%)";
        ProtoUdpSummary = $"{BandwidthFormatter.FormatBytes(totalUdp)} ({(totalUdp * 100.0 / safeSession):F0}%)";
        ProtoDnsSummary = $"{totalDnsQ} DNS queries";

        // Update System Tray tooltip & menu header
        string modeLabel = _isRedirecting ? "Active" : "Monitoring";
        string trayTip = $"FishyNet ({modeLabel}) — ↓ {TotalDownloadText} | ↑ {TotalUploadText} ({OnlineDevicesCount} hosts)";
        string menuHeader = $"FishyNet — ↓ {TotalDownloadText}  ↑ {TotalUploadText}";
        _trayService.UpdateLiveStatus(trayTip, menuHeader, _isRedirecting);

        // Smart Schedules Autonomous Evaluation & Auto-Gaming latency guard
        double gatewayPingMs = 0;
        NetworkDevice? gw = Devices.FirstOrDefault(d => d.IsGateway)?.Model;
        if (gw != null && gw.PingRttMs >= 0)
        {
            gatewayPingMs = gw.PingRttMs;
        }

        SmartSchedules.Evaluate(gatewayPingMs, _registry.AllDevices, DateTime.Now);

        foreach (DeviceViewModel vm in Devices)
        {
            vm.RefreshExternalCaps();
        }

        UpdateTotalSpeedTickerText();

        // Sample App & Website Monitor + Time-Machine rings every tick
        DateTime sampleUtc = utcNow ?? DateTime.UtcNow;
        AppTrafficSnapshot appSnapshot = _appTracker.SampleTick(sampleUtc);
        WebsiteTrafficSnapshot webSnapshot = _websiteTracker.GetSnapshot(localOnlyFilter: null, timestampUtc: sampleUtc);
        _appHistoryStore.RecordSample(sampleUtc, appSnapshot, webSnapshot);
        long timelineRxBytes = Math.Max(tickRxDeltaBytes, appSnapshot.TotalDownloadBps);
        long timelineTxBytes = Math.Max(tickTxDeltaBytes, appSnapshot.TotalUploadBps);
        _timelineStore.RecordFromSnapshots(timelineRxBytes, timelineTxBytes, appSnapshot, sampleUtc);

        if (IsAppMonitorSection)
        {
            _ticksSinceAppMonitorRefresh = 0;
            AppMonitor.RefreshFromTrackers(sampleUtc);
        }
        else
        {
            AppMonitor.RefreshAlertsOnly();
            _ticksSinceAppMonitorRefresh++;
            if (_ticksSinceAppMonitorRefresh >= 5)
            {
                _ticksSinceAppMonitorRefresh = 0;
                AppMonitor.RefreshFromTrackers(sampleUtc);
            }
        }

        _ticksSinceHistoryFlush++;
        if (_ticksSinceHistoryFlush >= 20)
        {
            _ticksSinceHistoryFlush = 0;
            _historyStore.Save();
            _appHistoryStore.Flush();
            if (_activeWorkspaceTab == 1)
            {
                RefreshHistoryReport();
            }
        }
    }

    private bool IsDomainGloballyOrDeviceSinkholed(Ipv4Address deviceIp, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        NetworkDevice? dev = _registry.TryGetByIp(deviceIp);
        if (dev is not null && dev.IsDomainBlocked(domain))
        {
            return true;
        }

        return IsDomainGloballyBlocked(domain);
    }

    private bool IsDomainGloballyBlocked(string domain)
    {
        List<string> globalBlocked = _profileStore.Preferences.GlobalBlockedDomains;
        for (int i = 0; i < globalBlocked.Count; i++)
        {
            string rule = globalBlocked[i].Trim().TrimStart('*').TrimStart('.');
            if (string.IsNullOrEmpty(rule))
            {
                continue;
            }

            if (domain.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                (domain.EndsWith(rule, StringComparison.OrdinalIgnoreCase) &&
                 domain.Length > rule.Length &&
                 domain[^(rule.Length + 1)] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private bool? LookupLiveDomainSinkholeState(string sourceHostIp, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        if (IsDomainGloballyBlocked(domain))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sourceHostIp) &&
            Ipv4Address.TryParse(sourceHostIp, out Ipv4Address ip) &&
            _registry.TryGetByIp(ip) is { } dev)
        {
            return dev.IsDomainBlocked(domain);
        }

        return null;
    }

    private (bool Success, bool IsNowSinkholed, string Message) HandleWebsiteSinkholeToggle(WebsiteTrafficRowViewModel row)
    {
        string domain = row.Domain.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
        {
            return (false, row.IsSinkholed, "Cannot toggle DNS sinkhole: domain is empty.");
        }

        if (row.IsLocalHost)
        {
            return (false, false, $"DNS sinkhole applies to monitored LAN devices only ({domain}); use App Firewall to block local applications.");
        }

        DeviceViewModel? targetVm = FindLanDeviceByIp(row.SourceHostIp);
        if (targetVm is not null)
        {
            bool currentlyBlocked = targetVm.Model.IsDomainBlocked(domain);
            if (currentlyBlocked)
            {
                targetVm.RemoveBlockedDomain(domain);
                return (true, false, $"Removed DNS sinkhole for '{domain}' on {targetVm.DisplayName}.");
            }

            targetVm.AddBlockedDomain(domain);
            return (true, true, $"Added DNS sinkhole for '{domain}' on {targetVm.DisplayName}.");
        }

        var targets = new List<DeviceViewModel>();
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].CanThrottle && Devices[i].IsSpoofed)
            {
                targets.Add(Devices[i]);
            }
        }

        if (targets.Count == 0)
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                if (Devices[i].CanThrottle)
                {
                    targets.Add(Devices[i]);
                }
            }
        }

        if (targets.Count == 0)
        {
            return (false, false, $"No monitored LAN devices available to sinkhole '{domain}'.");
        }

        bool allBlocked = true;
        for (int i = 0; i < targets.Count; i++)
        {
            if (!targets[i].Model.IsDomainBlocked(domain))
            {
                allBlocked = false;
                break;
            }
        }

        bool targetState = !allBlocked;
        var updatedModels = new List<NetworkDevice>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            DeviceViewModel vm = targets[i];
            if (targetState)
            {
                if (!vm.BlockedDomains.Contains(domain))
                {
                    vm.BlockedDomains.Add(domain);
                }
                vm.Model.AddBlockedDomain(domain);
            }
            else
            {
                vm.BlockedDomains.Remove(domain);
                vm.Model.RemoveBlockedDomain(domain);
            }
            updatedModels.Add(vm.Model);
        }

        _profileStore.UpdateBatch(updatedModels);
        return (true, targetState, targetState
            ? $"Added DNS sinkhole for '{domain}' across {targets.Count} LAN devices."
            : $"Removed DNS sinkhole for '{domain}' across {targets.Count} LAN devices.");
    }

    private DeviceViewModel? FindLanDeviceByIp(string ipText)
    {
        if (string.IsNullOrWhiteSpace(ipText))
        {
            return null;
        }

        for (int i = 0; i < Devices.Count; i++)
        {
            DeviceViewModel vm = Devices[i];
            if (vm.CanThrottle && string.Equals(vm.IpAddressText, ipText, StringComparison.OrdinalIgnoreCase))
            {
                return vm;
            }
        }

        return null;
    }

    public void ToggleGamingBoost()
    {
        if (IsGamingBoostActive)
        {
            foreach (DeviceViewModel vm in Devices)
            {
                if (vm.CanThrottle && _gamingBoostSavedCaps.TryGetValue(vm.Model.Mac, out var saved))
                {
                    vm.DownloadCapKBps = saved.DownCap;
                    vm.UploadCapKBps = saved.UpCap;
                    vm.IsSpoofed = saved.WasSpoofed;
                }
            }
            _gamingBoostSavedCaps.Clear();
            IsGamingBoostActive = false;
            StatusMessage = "Gaming Boost deactivated. Restored original LAN device caps.";
        }
        else
        {
            _gamingBoostSavedCaps.Clear();
            int count = 0;
            foreach (DeviceViewModel vm in Devices)
            {
                if (vm.CanThrottle)
                {
                    _gamingBoostSavedCaps[vm.Model.Mac] = (vm.DownloadCapKBps, vm.UploadCapKBps, vm.IsSpoofed);
                    vm.DownloadCapKBps = 64;
                    vm.UploadCapKBps = 32;
                    vm.IsSpoofed = true;
                    count++;
                }
            }
            if (!IsRedirecting && SelectedAdapter is not null)
            {
                ToggleRedirection();
            }
            IsGamingBoostActive = true;
            StatusMessage = $"⚡ Gaming Boost ACTIVE: Throttled {count} other devices to 64 KB/s to preserve gaming latency.";
        }
    }

    private async Task RunPeriodicLatencySweepAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                await NetworkToolsService.SweepDeviceLatenciesAsync(_registry.AllDevices, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient ping errors
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkEnvironmentChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkEnvironmentChanged;

        lock (_adapterRefreshLock)
        {
            _adapterRefreshCts?.Cancel();
            _adapterRefreshCts?.Dispose();
            _adapterRefreshCts = null;
        }

        _telemetryTimer.Stop();
        _backgroundCts.Cancel();

        Devices.CollectionChanged -= OnDevicesCollectionChanged;
        for (int i = 0; i < _subscribedDevices.Count; i++)
        {
            _subscribedDevices[i].BatchSelectionChanged -= OnDeviceBatchSelectionChanged;
        }
        _subscribedDevices.Clear();

        // Flush any remaining historical usage deltas before exit
        foreach (NetworkDevice dev in _registry.AllDevices)
        {
            (long rxHistDelta, long txHistDelta) = dev.ConsumeHistoryDeltas();
            if (rxHistDelta > 0 || txHistDelta > 0)
            {
                _historyStore.RecordUsageSample(dev.Mac, dev.DisplayName, dev.Ip.ToString(), dev.Vendor, rxHistDelta, txHistDelta);
            }
        }

        _historyStore.Save();
        _appHistoryStore.Flush();
        _profileStore.Save();
        _engine.UnregisterTelemetryObserver(_appTracker);
        _engine.UnregisterTelemetryObserver(_websiteTracker);
        _engine.Dispose();
        _trayService.Dispose();
    }
}
