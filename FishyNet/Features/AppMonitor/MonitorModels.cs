using System.Windows.Media.Imaging;
using FishyNet.Core.Formatting;

namespace FishyNet.Features.AppMonitor;

/// <summary>
/// Rolling Time-Machine window granularity for stacked traffic timeline and alerts.
/// </summary>
public enum TimeMachineWindow
{
    Live = 0,
    FiveMinutes = 1,
    OneHour = 2,
    TwentyFourHours = 3,
    SevenDays = 4
}

public enum MonitorAlertKind
{
    FirstNetworkAccess = 0,
    TrafficSpike = 1,
    ArpSpoof = 2,
    NewLanDevice = 3,
    DnsAnomaly = 4,
    FirewallAction = 5
}

public enum MonitorAlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public sealed record MonitorAlertRecord(
    long Id,
    DateTime TimestampUtc,
    MonitorAlertKind Kind,
    MonitorAlertSeverity Severity,
    string Title,
    string Description,
    string SubjectName,
    string TargetDetail,
    bool IsAcknowledged = false)
{
    public string TimestampLocalText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

    public string KindLabel => Kind switch
    {
        MonitorAlertKind.FirstNetworkAccess => "FIRST ACCESS",
        MonitorAlertKind.TrafficSpike => "SPIKE 3x",
        MonitorAlertKind.ArpSpoof => "ARP SPOOF",
        MonitorAlertKind.NewLanDevice => "NEW DEVICE",
        MonitorAlertKind.DnsAnomaly => "DNS ANOMALY",
        MonitorAlertKind.FirewallAction => "FIREWALL",
        _ => "ALERT"
    };
}

public sealed record ProcessMetadata(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string ExePath,
    string Publisher,
    BitmapSource? Icon);

public sealed record AppTrafficEntry(
    string AppKey,
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string ExePath,
    string Publisher,
    BitmapSource? Icon,
    long DownloadBps,
    long UploadBps,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    IReadOnlyList<string> ProtocolTags,
    IReadOnlyList<string> ContactedHosts,
    int ActiveConnections,
    bool IsNewFirstAccess,
    bool IsFirewallBlocked,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc)
{
    public long TotalBytes => TotalDownloadBytes + TotalUploadBytes;
    public long TotalBps => DownloadBps + UploadBps;
    public string DownloadRateText => BandwidthFormatter.FormatRate(DownloadBps);
    public string UploadRateText => BandwidthFormatter.FormatRate(UploadBps);
    public string TotalDownloadText => BandwidthFormatter.FormatBytes(TotalDownloadBytes);
    public string TotalUploadText => BandwidthFormatter.FormatBytes(TotalUploadBytes);
    public string TotalBytesText => BandwidthFormatter.FormatBytes(TotalBytes);
    public string ProtocolsSummary => ProtocolTags.Count > 0 ? string.Join(" · ", ProtocolTags) : "TCP/UDP";
    public string TopHostsSummary => ContactedHosts.Count > 0 ? string.Join(", ", ContactedHosts) : "—";
    public string LastSeenLocalText => LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
}

public sealed record AppTrafficSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<AppTrafficEntry> Applications,
    long TotalDownloadBps,
    long TotalUploadBps,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    int UnseenNewAppsCount);

public sealed record WebsiteTrafficEntry(
    string EntryKey,
    string Domain,
    string ResolvedIp,
    string ProtocolPort,
    string SourceHost,
    string SourceHostIp,
    bool IsLocalHost,
    string AppName,
    long DownloadBytes,
    long UploadBytes,
    long HitCount,
    DateTime LastSeenUtc,
    bool IsSinkholed)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
    public string DownloadBytesText => BandwidthFormatter.FormatBytes(DownloadBytes);
    public string UploadBytesText => BandwidthFormatter.FormatBytes(UploadBytes);
    public string TotalBytesText => BandwidthFormatter.FormatBytes(TotalBytes);
    public string LastSeenLocalText => LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
}

public sealed record WebsiteTrafficSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<WebsiteTrafficEntry> Websites,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    int UniqueDomainCount);

public sealed record TimelineSeriesSlice(
    string Label,
    long DownloadBytes,
    long UploadBytes,
    bool IsOther = false)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
}

public sealed record TimelineBucketSnapshot(
    DateTime BucketStartUtc,
    DateTime BucketEndUtc,
    string Label,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    double DownloadBps,
    double UploadBps,
    IReadOnlyList<TimelineSeriesSlice> Slices,
    IReadOnlyList<MonitorAlertRecord> Alerts)
{
    public long TotalBytes => TotalDownloadBytes + TotalUploadBytes;
    public double TotalBps => DownloadBps + UploadBps;
    public bool HasAlerts => Alerts.Count > 0;
}

public sealed record TrafficTimelineSnapshot(
    TimeMachineWindow Window,
    DateTime GeneratedUtc,
    IReadOnlyList<TimelineBucketSnapshot> Buckets,
    IReadOnlyList<string> SeriesLabels,
    IReadOnlyList<MonitorAlertRecord> AlertsInWindow,
    double PeakTotalBps,
    long TotalDownloadBytes,
    long TotalUploadBytes);

internal static class MonitorAlertIdGenerator
{
    private static long _sequence;

    public static long Next() => Interlocked.Increment(ref _sequence);
}
