using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
using FishyNet.Features.SmartSchedules;

namespace FishyNet.ViewModels;

public enum DeviceViewMode
{
    Table,
    Bento,
    Topology
}

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

    private readonly DispatcherTimer _telemetryTimer;
    private readonly CancellationTokenSource _backgroundCts = new();
    private int _ticksSinceHistoryFlush;
    private double _peakDownloadBps;
    private bool _disposed;

    private NetworkAdapterInfo? _selectedAdapter;
    private DeviceViewModel? _selectedDevice;
    private string _searchQuery = string.Empty;
    private string _statusFilter = "All";
    private int _activeWorkspaceTab;

    private bool _isRedirecting;
    private bool _isScanning;
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
    private bool _isInspectorOpen;
    private bool _isGamingBoostActive;
    private bool _isMiniHudActive;
    private string _totalSpeedTickerText = "▼ 0 B/s   ▲ 0 B/s   ● Ping —";
    private readonly Dictionary<MacAddress, (int DownCap, int UpCap, bool WasSpoofed)> _gamingBoostSavedCaps = new();

    public event Action? RequestShowWindow;
    public event Action? RequestExplicitExit;
    public event Action<bool>? RequestMiniHudVisibilityChanged;
    public event Action? RequestMinimize;
    public event Action? RequestMaximizeRestore;
    public event Action? RequestClose;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ICollectionView FilteredDevices { get; }

    public IReadOnlyList<BandwidthPresetProfile> Presets => DeviceProfileStore.BuiltInPresets;
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
    public RelayCommand ToggleInspectorCommand { get; }
    public RelayCommand CloseInspectorCommand { get; }
    public RelayCommand DeselectDeviceCommand { get; }
    public RelayCommand ToggleSmartRuleCommand { get; }
    public RelayCommand ResetSmartRulesCommand { get; }
    public RelayCommand MinimizeWindowCommand { get; }
    public RelayCommand MaximizeWindowCommand { get; }
    public RelayCommand CloseWindowCommand { get; }

    public MainViewModel()
    {
        _registry = new DeviceRegistry();
        _engine = new CaptureEngine(_registry);
        _profileStore = new DeviceProfileStore();
        _historyStore = new UsageHistoryStore();
        _hostResolver = new HostResolverService(_registry);
        _trayService = new TrayIconService();

        _useBitsPerSecond = _profileStore.Preferences.UseBitsPerSecond;
        _closeToTray = _profileStore.Preferences.CloseToTray;

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
            }
        }
    }

    public bool IsTableView => CurrentViewMode == DeviceViewMode.Table;
    public bool IsBentoView => CurrentViewMode == DeviceViewMode.Bento;
    public bool IsTopologyView => CurrentViewMode == DeviceViewMode.Topology;

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
            if (SetProperty(ref _selectedAdapter, value) && value is not null)
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
            DriverStatusText = version;
        }
        else
        {
            DriverStatusText = "Npcap / WinPcap Not Detected";
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

        IReadOnlyList<NetworkAdapterInfo> foundAdapters = CaptureEngine.DiscoverSuitableAdapters();
        foreach (NetworkAdapterInfo nic in foundAdapters)
        {
            Adapters.Add(nic);
        }

        if (Adapters.Count > 0)
        {
            string? prefId = _profileStore.Preferences.PreferredAdapterId;
            NetworkAdapterInfo chosen = Adapters.FirstOrDefault(a => a.Id == prefId) ?? Adapters[0];
            SelectedAdapter = chosen;
        }
        else
        {
            StatusMessage = "No active IPv4 network adapter with a default gateway was found.";
        }

        RefreshHistoryReport();
        _telemetryTimer.Start();
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
        int count = 0;
        foreach (DeviceViewModel vm in Devices)
        {
            if (vm.CanThrottle)
            {
                vm.DownloadCapKBps = preset.DownloadCapKBps;
                vm.UploadCapKBps = preset.UploadCapKBps;
                vm.IsBlocked = preset.BlockAll;
                vm.IsSpoofed = true;
                count++;
            }
        }

        StatusMessage = $"Applied '{preset.Name}' profile to {count} LAN devices.";
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
        foreach (DeviceViewModel vm in Devices)
        {
            if (vm.CanThrottle)
            {
                vm.DownloadCapKBps = 0;
                vm.UploadCapKBps = 0;
                vm.IsBlocked = false;
                vm.IsSpoofed = false;
            }
        }

        _engine.CompleteUnspoof();
        StatusMessage = "Bypassed all LAN hosts, restored ARP tables, and cleared active caps.";
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

    private async Task ProcessRegistryEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (NetworkEvent evt in _registry.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (evt.Kind)
                {
                    case NetworkEventKind.DeviceDiscovered when evt.Device is not null:
                    {
                        NetworkDevice dev = evt.Device;
                        _profileStore.ApplySavedProfileToDevice(dev);
                        _ = _hostResolver.ResolveDeviceHostnameAsync(dev, ct);

                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            if (Devices.Any(d => d.Model.Mac == dev.Mac)) return;

                            var vm = new DeviceViewModel(dev, _useBitsPerSecond);
                            vm.RuleModified += OnDeviceRuleModified;
                            Devices.Add(vm);
                            SelectedDevice ??= vm;
                            OnlineDevicesCount = Devices.Count;

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
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            DeviceViewModel? vm = Devices.FirstOrDefault(d => d.Model.Mac == dev.Mac);
                            vm?.SyncMetadataFromModel();
                        });
                        break;
                    }

                    case NetworkEventKind.DnsQueryObserved when evt.DnsRecord is not null:
                    {
                        DnsQueryRecord dns = evt.DnsRecord;
                        Application.Current?.Dispatcher.BeginInvoke(() =>
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
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            SecurityAlerts.Insert(0, alert);
                            IsArpAlertActive = true;
                            ArpGuardStatusText = $"ARP ALERT: {alert.OffendingMac}";
                            StatusMessage = alert.Description;

                            if (_profileStore.Preferences.NotifyOnArpAlert)
                            {
                                _trayService.ShowNotification("ARP Spoof Attack Detected!", alert.Description, isWarning: true);
                            }
                        });
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void OnDeviceRuleModified(DeviceViewModel vm)
    {
        _profileStore.UpdateFromDevice(vm.Model);
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
        double totalRxBps = 0;
        double totalTxBps = 0;
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

        _ticksSinceHistoryFlush++;
        if (_ticksSinceHistoryFlush >= 20)
        {
            _ticksSinceHistoryFlush = 0;
            _historyStore.Save();
            if (_activeWorkspaceTab == 1)
            {
                RefreshHistoryReport();
            }
        }
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

        _telemetryTimer.Stop();
        _backgroundCts.Cancel();

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
        _profileStore.Save();
        _engine.Dispose();
        _trayService.Dispose();
    }
}
