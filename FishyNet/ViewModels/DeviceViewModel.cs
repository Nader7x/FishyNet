using System.Collections.ObjectModel;
using System.Windows;
using FishyNet.Core;
using FishyNet.Core.Formatting;

namespace FishyNet.ViewModels;

/// <summary>
/// Reactive MVVM presentation model for a single discovered LAN device.
/// Provides friendly auto-scaled throughput numbers, visual bar ratios, cap saturation percentages,
/// per-row sparkline history, and historical usage summaries.
/// </summary>
public sealed class DeviceViewModel : ObservableObject
{
    private const int SparklineLength = 30;
    private readonly double[] _downloadHistory = new double[SparklineLength];

    private double _smoothedDownloadBps;
    private double _smoothedUploadBps;
    private bool _useBitsPerSecond;

    private string _displayName = string.Empty;
    private string _customNickname = string.Empty;
    private string _vendor = string.Empty;
    private string _category = string.Empty;
    private string _hostname = string.Empty;
    private string _ipText = string.Empty;

    private string _downloadSpeedText = "0 B/s";
    private string _uploadSpeedText = "0 B/s";
    private double _downloadBarRatio;
    private double _uploadBarRatio;
    private string _downloadCapStatusText = "Unlimited";
    private string _uploadCapStatusText = "Unlimited";

    private string _sessionDownloadText = "0 B";
    private string _sessionUploadText = "0 B";
    private string _sessionTotalText = "0 B";
    private string _todayUsageText = "0 B";
    private string _weekUsageText = "0 B";
    private string _monthUsageText = "0 B";
    private string _protocolBreakdownText = "No traffic yet";
    private string _pingLatencyText = "—";
    private string _statusBadgeText = "ONLINE";
    private string _statusColorHex = "#10B981";

    private int _downloadCapKBps;
    private int _uploadCapKBps;
    private bool _isBlocked;
    private bool _isSpoofed;
    private long _dailyQuotaMB;

    private IReadOnlyList<double> _sparklineSnapshot = Array.Empty<double>();

    public event Action<DeviceViewModel>? RuleModified;

    public NetworkDevice Model { get; }

    public ObservableCollection<string> BlockedDomains { get; } = [];

    public RelayCommand ToggleMonitoredCommand { get; }
    public RelayCommand MonitorDeviceCommand { get; }
    public RelayCommand BypassDeviceCommand { get; }
    public RelayCommand ClearCapsCommand { get; }
    public RelayCommand CopyIpCommand { get; }
    public RelayCommand CopyMacCommand { get; }

    public DeviceViewModel(NetworkDevice model, bool useBitsPerSecond)
    {
        Model = model;
        _useBitsPerSecond = useBitsPerSecond;
        _downloadCapKBps = model.DownloadCapKBps;
        _uploadCapKBps = model.UploadCapKBps;
        _isBlocked = model.IsBlocked;
        _isSpoofed = model.IsSpoofed;
        _dailyQuotaMB = model.DailyQuotaBytes / (1024L * 1024L);

        ToggleMonitoredCommand = new RelayCommand(
            () => IsSpoofed = !IsSpoofed,
            () => CanThrottle);
        MonitorDeviceCommand = new RelayCommand(
            () => IsSpoofed = true,
            () => CanThrottle && !IsSpoofed);
        BypassDeviceCommand = new RelayCommand(
            () => IsSpoofed = false,
            () => CanThrottle && IsSpoofed);
        ClearCapsCommand = new RelayCommand(
            () =>
            {
                DownloadCapKBps = 0;
                UploadCapKBps = 0;
                IsBlocked = false;
                IsSpoofed = true;
            },
            () => CanThrottle);
        CopyIpCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(IpAddressText); } catch { }
        });
        CopyMacCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(MacAddressText); } catch { }
        });

        foreach (string domain in model.BlockedDomains)
        {
            BlockedDomains.Add(domain);
        }

        SyncMetadataFromModel();
        UpdateFormattedStrings();
    }

    public string MacAddressText => Model.Mac.ToString();
    public bool IsGateway => Model.IsGateway;
    public bool IsLocalHost => Model.IsLocalHost;
    public bool CanThrottle => !Model.IsGateway && !Model.IsLocalHost;

    public string RoleBadgeText => Model.IsLocalHost
        ? "THIS PC"
        : (Model.IsGateway ? "GATEWAY" : Model.DeviceCategory.ToUpperInvariant());

    public string IpAddressText
    {
        get => _ipText;
        private set => SetProperty(ref _ipText, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string CustomNickname
    {
        get => _customNickname;
        set
        {
            if (SetProperty(ref _customNickname, value))
            {
                Model.CustomNickname = value.Trim();
                DisplayName = Model.DisplayName;
                RuleModified?.Invoke(this);
            }
        }
    }

    public string Vendor
    {
        get => _vendor;
        private set => SetProperty(ref _vendor, value);
    }

    public string Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value))
            {
                Model.DeviceCategory = value;
                OnPropertyChanged(nameof(RoleBadgeText));
                RuleModified?.Invoke(this);
            }
        }
    }

    public string Hostname
    {
        get => _hostname;
        private set => SetProperty(ref _hostname, value);
    }

    public double CurrentDownloadBps => _smoothedDownloadBps;
    public double CurrentUploadBps => _smoothedUploadBps;

    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set => SetProperty(ref _downloadSpeedText, value);
    }

    public string UploadSpeedText
    {
        get => _uploadSpeedText;
        private set => SetProperty(ref _uploadSpeedText, value);
    }

    public double DownloadBarRatio
    {
        get => _downloadBarRatio;
        private set => SetProperty(ref _downloadBarRatio, value);
    }

    public double UploadBarRatio
    {
        get => _uploadBarRatio;
        private set => SetProperty(ref _uploadBarRatio, value);
    }

    public string DownloadCapStatusText
    {
        get => _downloadCapStatusText;
        private set => SetProperty(ref _downloadCapStatusText, value);
    }

    public string UploadCapStatusText
    {
        get => _uploadCapStatusText;
        private set => SetProperty(ref _uploadCapStatusText, value);
    }

    public string SessionDownloadText
    {
        get => _sessionDownloadText;
        private set => SetProperty(ref _sessionDownloadText, value);
    }

    public string SessionUploadText
    {
        get => _sessionUploadText;
        private set => SetProperty(ref _sessionUploadText, value);
    }

    public string SessionTotalText
    {
        get => _sessionTotalText;
        private set => SetProperty(ref _sessionTotalText, value);
    }

    public string TodayUsageText
    {
        get => _todayUsageText;
        private set => SetProperty(ref _todayUsageText, value);
    }

    public string WeekUsageText
    {
        get => _weekUsageText;
        private set => SetProperty(ref _weekUsageText, value);
    }

    public string MonthUsageText
    {
        get => _monthUsageText;
        private set => SetProperty(ref _monthUsageText, value);
    }

    public string ProtocolBreakdownText
    {
        get => _protocolBreakdownText;
        private set => SetProperty(ref _protocolBreakdownText, value);
    }

    public string PingLatencyText
    {
        get => _pingLatencyText;
        private set => SetProperty(ref _pingLatencyText, value);
    }

    public string StatusBadgeText
    {
        get => _statusBadgeText;
        private set => SetProperty(ref _statusBadgeText, value);
    }

    public string StatusColorHex
    {
        get => _statusColorHex;
        private set => SetProperty(ref _statusColorHex, value);
    }

    public IReadOnlyList<double> SparklineSeries
    {
        get => _sparklineSnapshot;
        private set => SetProperty(ref _sparklineSnapshot, value);
    }

    public int DownloadCapKBps
    {
        get => _downloadCapKBps;
        set
        {
            int clamped = Math.Max(0, value);
            if (SetProperty(ref _downloadCapKBps, clamped))
            {
                Model.DownloadCapKBps = clamped;
                if (clamped > 0 && !_isSpoofed && CanThrottle)
                {
                    _isSpoofed = true;
                    Model.IsSpoofed = true;
                    NotifyMonitorStateChanged();
                }
                UpdateFormattedStrings();
                RuleModified?.Invoke(this);
            }
        }
    }

    public int UploadCapKBps
    {
        get => _uploadCapKBps;
        set
        {
            int clamped = Math.Max(0, value);
            if (SetProperty(ref _uploadCapKBps, clamped))
            {
                Model.UploadCapKBps = clamped;
                if (clamped > 0 && !_isSpoofed && CanThrottle)
                {
                    _isSpoofed = true;
                    Model.IsSpoofed = true;
                    NotifyMonitorStateChanged();
                }
                UpdateFormattedStrings();
                RuleModified?.Invoke(this);
            }
        }
    }

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (SetProperty(ref _isBlocked, value))
            {
                Model.IsBlocked = value;
                if (value && !_isSpoofed && CanThrottle)
                {
                    _isSpoofed = true;
                    Model.IsSpoofed = true;
                    NotifyMonitorStateChanged();
                }
                UpdateStatusBadge();
                RuleModified?.Invoke(this);
            }
        }
    }

    public bool IsSpoofed
    {
        get => _isSpoofed;
        set
        {
            if (!CanThrottle) return;
            if (SetProperty(ref _isSpoofed, value))
            {
                Model.IsSpoofed = value;
                NotifyMonitorStateChanged();
                UpdateStatusBadge();
                RuleModified?.Invoke(this);
            }
        }
    }

    public string MonitorToggleButtonText => !CanThrottle
        ? "System Host (Protected)"
        : (_isSpoofed ? "Switch to Bypassed (Direct ARP)" : "Switch to Monitored (Redirect ARP)");

    public string MonitorModeDescription => !CanThrottle
        ? "Gateway & Local Host are never ARP-redirected."
        : (_isSpoofed
            ? "Active: Traffic is routed through FishyNet for monitoring & shaping."
            : "Bypassed: Traffic flows directly to the router without redirection.");

    private void NotifyMonitorStateChanged()
    {
        OnPropertyChanged(nameof(IsSpoofed));
        OnPropertyChanged(nameof(MonitorToggleButtonText));
        OnPropertyChanged(nameof(MonitorModeDescription));
        ToggleMonitoredCommand.RaiseCanExecuteChanged();
        MonitorDeviceCommand.RaiseCanExecuteChanged();
        BypassDeviceCommand.RaiseCanExecuteChanged();
    }

    public long DailyQuotaMB
    {
        get => _dailyQuotaMB;
        set
        {
            long clamped = Math.Max(0L, value);
            if (SetProperty(ref _dailyQuotaMB, clamped))
            {
                Model.DailyQuotaBytes = clamped * 1024L * 1024L;
                if (clamped == 0)
                {
                    Model.IsQuotaExceeded = false;
                }
                UpdateStatusBadge();
                RuleModified?.Invoke(this);
            }
        }
    }

    public void AddBlockedDomain(string domain)
    {
        string cleaned = domain.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleaned) || BlockedDomains.Contains(cleaned)) return;

        BlockedDomains.Add(cleaned);
        Model.AddBlockedDomain(cleaned);
        RuleModified?.Invoke(this);
    }

    public void RemoveBlockedDomain(string domain)
    {
        if (BlockedDomains.Remove(domain))
        {
            Model.RemoveBlockedDomain(domain);
            RuleModified?.Invoke(this);
        }
    }

    public void SyncMetadataFromModel()
    {
        IpAddressText = Model.Ip.ToString();
        Vendor = Model.Vendor;
        Hostname = Model.Hostname;
        _customNickname = Model.CustomNickname;
        _category = Model.DeviceCategory;
        DisplayName = Model.DisplayName;
        OnPropertyChanged(nameof(CustomNickname));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(RoleBadgeText));
        UpdateStatusBadge();
    }

    public void SetUnitMode(bool useBitsPerSecond)
    {
        _useBitsPerSecond = useBitsPerSecond;
        UpdateFormattedStrings();
    }

    public void SampleTick(double rawRxBps, double rawTxBps, long todayTotalBytes, long weekTotalBytes, long monthTotalBytes)
    {
        // Fast response EMA (70% new sample, 30% previous) with snap-to-zero below 8 B/s
        _smoothedDownloadBps = rawRxBps < 8.0 ? 0.0 : (rawRxBps * 0.75) + (_smoothedDownloadBps * 0.25);
        _smoothedUploadBps = rawTxBps < 8.0 ? 0.0 : (rawTxBps * 0.75) + (_smoothedUploadBps * 0.25);

        Array.Copy(_downloadHistory, 1, _downloadHistory, 0, SparklineLength - 1);
        _downloadHistory[SparklineLength - 1] = _smoothedDownloadBps;

        var copy = new double[SparklineLength];
        Array.Copy(_downloadHistory, copy, SparklineLength);
        SparklineSeries = copy;

        TodayUsageText = BandwidthFormatter.FormatBytes(todayTotalBytes);
        WeekUsageText = BandwidthFormatter.FormatBytes(weekTotalBytes);
        MonthUsageText = BandwidthFormatter.FormatBytes(monthTotalBytes);

        // Enforce daily quota if set
        if (Model.DailyQuotaBytes > 0)
        {
            bool exceeded = todayTotalBytes >= Model.DailyQuotaBytes;
            if (Model.IsQuotaExceeded != exceeded)
            {
                Model.IsQuotaExceeded = exceeded;
            }
        }

        UpdateFormattedStrings();
    }

    private void UpdateFormattedStrings()
    {
        DownloadSpeedText = BandwidthFormatter.FormatRate(_smoothedDownloadBps, _useBitsPerSecond);
        UploadSpeedText = BandwidthFormatter.FormatRate(_smoothedUploadBps, _useBitsPerSecond);

        DownloadBarRatio = BandwidthFormatter.ComputeUtilizationRatio(_smoothedDownloadBps, _downloadCapKBps);
        UploadBarRatio = BandwidthFormatter.ComputeUtilizationRatio(_smoothedUploadBps, _uploadCapKBps);

        DownloadCapStatusText = BandwidthFormatter.FormatCapStatus(_smoothedDownloadBps, _downloadCapKBps, _useBitsPerSecond);
        UploadCapStatusText = BandwidthFormatter.FormatCapStatus(_smoothedUploadBps, _uploadCapKBps, _useBitsPerSecond);

        SessionDownloadText = BandwidthFormatter.FormatBytes(Model.TotalReceivedBytes);
        SessionUploadText = BandwidthFormatter.FormatBytes(Model.TotalSentBytes);
        SessionTotalText = BandwidthFormatter.FormatBytes(Model.TotalSessionBytes);

        int rtt = Model.PingRttMs;
        PingLatencyText = rtt < 0 ? "—" : (rtt == 0 ? "<1 ms" : $"{rtt} ms");

        long totalProto = Math.Max(1L, Model.TotalSessionBytes);
        if (Model.TotalSessionBytes > 0)
        {
            int webPct = (int)Math.Round((Model.HttpTlsBytes * 100.0) / totalProto);
            int tcpPct = (int)Math.Round((Model.TcpBytes * 100.0) / totalProto);
            int udpPct = (int)Math.Round(((Model.UdpBytes + Model.DnsBytes) * 100.0) / totalProto);
            ProtocolBreakdownText = $"Web/TLS {webPct}% · TCP {tcpPct}% · UDP {udpPct}% · DNS {Model.DnsQueryCount} q";
        }

        UpdateStatusBadge();
    }

    private void UpdateStatusBadge()
    {
        if (Model.IsLocalHost)
        {
            StatusBadgeText = "HOST PC";
            StatusColorHex = "#38BDF8";
        }
        else if (Model.IsGateway)
        {
            StatusBadgeText = "GATEWAY";
            StatusColorHex = "#10B981";
        }
        else if (!_isSpoofed)
        {
            StatusBadgeText = "BYPASSED";
            StatusColorHex = "#64748B";
        }
        else if (_isBlocked)
        {
            StatusBadgeText = "BLOCKED";
            StatusColorHex = "#EF4444";
        }
        else if (Model.IsQuotaExceeded)
        {
            StatusBadgeText = "QUOTA HIT";
            StatusColorHex = "#EF4444";
        }
        else if (_downloadCapKBps > 0 || _uploadCapKBps > 0)
        {
            StatusBadgeText = "THROTTLED";
            StatusColorHex = "#F59E0B";
        }
        else
        {
            StatusBadgeText = "MONITORED";
            StatusColorHex = "#10B981";
        }
    }
}
