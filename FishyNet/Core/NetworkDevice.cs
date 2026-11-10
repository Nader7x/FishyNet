using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FishyNet.Core.Packets;
using FishyNet.Core.Throttling;

namespace FishyNet.Core;

public sealed class NetworkDevice
{
    private long _deltaReceivedBytes;
    private long _deltaSentBytes;
    private long _unflushedHistoryReceivedBytes;
    private long _unflushedHistorySentBytes;
    private long _totalReceivedBytes;
    private long _totalSentBytes;

    private long _httpTlsBytes;
    private long _tcpBytes;
    private long _udpBytes;
    private long _dnsBytes;
    private long _icmpBytes;
    private long _otherBytes;
    private long _dnsQueryCount;
    private long _droppedPacketsCount;
    private long _lastSeenUtcTicks;
    private int _pingRttMs = -1;

    private TokenBucketLimiter _downloadBucket;
    private TokenBucketLimiter _uploadBucket;
    private readonly ConcurrentDictionary<string, byte> _blockedDomains = new(StringComparer.OrdinalIgnoreCase);

    public Ipv4Address Ip { get; set; }
    public MacAddress Mac { get; init; }
    public bool IsGateway { get; set; }
    public bool IsLocalHost { get; set; }

    public string Hostname { get; set; } = string.Empty;
    public string Vendor { get; set; } = "Unknown Vendor";
    public string CustomNickname { get; set; } = string.Empty;
    public string DeviceCategory { get; set; } = "Device";

    /// <summary>
    /// Download rate limit in KB/s (0 = Unlimited).
    /// </summary>
    public volatile int DownloadCapKBps;

    /// <summary>
    /// Upload rate limit in KB/s (0 = Unlimited).
    /// </summary>
    public volatile int UploadCapKBps;

    /// <summary>
    /// When true, all forwarded packets (and DNS queries) for this device are dropped.
    /// </summary>
    public volatile bool IsBlocked;

    /// <summary>
    /// When true, active ARP spoofing redirects this device's traffic through our host.
    /// </summary>
    public volatile bool IsSpoofed = true;

    /// <summary>
    /// Optional daily data quota in bytes (0 = Unlimited).
    /// </summary>
    public long DailyQuotaBytes { get; set; }

    /// <summary>
    /// Set to true when daily usage exceeds DailyQuotaBytes.
    /// </summary>
    public volatile bool IsQuotaExceeded;

    public NetworkDevice(Ipv4Address ip, MacAddress mac, bool isGateway = false, bool isLocalHost = false)
    {
        Ip = ip;
        Mac = mac;
        IsGateway = isGateway;
        IsLocalHost = isLocalHost;
        IsSpoofed = !isGateway && !isLocalHost;
        _lastSeenUtcTicks = DateTime.UtcNow.Ticks;
    }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomNickname)) return CustomNickname;
            if (IsLocalHost) return "This PC (Local Host)";
            if (IsGateway) return !string.IsNullOrWhiteSpace(Hostname) ? $"Gateway ({Hostname})" : "Default Gateway / Router";
            if (!string.IsNullOrWhiteSpace(Hostname)) return Hostname;
            if (!string.IsNullOrWhiteSpace(Vendor) && Vendor != "Unknown Vendor") return $"{Vendor} ({Ip})";
            return $"Host {Ip}";
        }
    }

    public DateTime LastSeenUtc => new(Interlocked.Read(ref _lastSeenUtcTicks), DateTimeKind.Utc);
    public int PingRttMs
    {
        get => Volatile.Read(ref _pingRttMs);
        set => Volatile.Write(ref _pingRttMs, value);
    }

    public long TotalReceivedBytes => Interlocked.Read(ref _totalReceivedBytes);
    public long TotalSentBytes => Interlocked.Read(ref _totalSentBytes);
    public long TotalSessionBytes => TotalReceivedBytes + TotalSentBytes;

    public long HttpTlsBytes => Interlocked.Read(ref _httpTlsBytes);
    public long TcpBytes => Interlocked.Read(ref _tcpBytes);
    public long UdpBytes => Interlocked.Read(ref _udpBytes);
    public long DnsBytes => Interlocked.Read(ref _dnsBytes);
    public long IcmpBytes => Interlocked.Read(ref _icmpBytes);
    public long OtherBytes => Interlocked.Read(ref _otherBytes);
    public long DnsQueryCount => Interlocked.Read(ref _dnsQueryCount);
    public long DroppedPacketsCount => Interlocked.Read(ref _droppedPacketsCount);

    public IReadOnlyCollection<string> BlockedDomains => (IReadOnlyCollection<string>)_blockedDomains.Keys;

    public void SetBlockedDomains(IEnumerable<string> domains)
    {
        _blockedDomains.Clear();
        foreach (string d in domains)
        {
            string trimmed = d.Trim().TrimStart('*').TrimStart('.');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                _blockedDomains[trimmed] = 1;
            }
        }
    }

    public void AddBlockedDomain(string domain)
    {
        string trimmed = domain.Trim().TrimStart('*').TrimStart('.');
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _blockedDomains[trimmed] = 1;
        }
    }

    public void RemoveBlockedDomain(string domain)
    {
        string trimmed = domain.Trim().TrimStart('*').TrimStart('.');
        _blockedDomains.TryRemove(trimmed, out _);
    }

    public bool IsDomainBlocked(string queriedDomain)
    {
        if (_blockedDomains.IsEmpty || string.IsNullOrEmpty(queriedDomain))
        {
            return false;
        }

        if (_blockedDomains.ContainsKey(queriedDomain))
        {
            return true;
        }

        foreach (string rule in _blockedDomains.Keys)
        {
            if (queriedDomain.EndsWith(rule, StringComparison.OrdinalIgnoreCase) &&
                (queriedDomain.Length == rule.Length || queriedDomain[^(rule.Length + 1)] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TouchLastSeen()
    {
        Interlocked.Exchange(ref _lastSeenUtcTicks, DateTime.UtcNow.Ticks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAllowDownloadPacket(int packetBytes, long stopwatchTimestamp)
    {
        if (IsBlocked || IsQuotaExceeded)
        {
            Interlocked.Increment(ref _droppedPacketsCount);
            return false;
        }

        int capKBps = DownloadCapKBps;
        if (capKBps <= 0)
        {
            return true;
        }

        bool allowed = _downloadBucket.TryConsume(packetBytes, capKBps * 1024, stopwatchTimestamp);
        if (!allowed)
        {
            Interlocked.Increment(ref _droppedPacketsCount);
        }
        return allowed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAllowUploadPacket(int packetBytes, long stopwatchTimestamp)
    {
        if (IsBlocked || IsQuotaExceeded)
        {
            Interlocked.Increment(ref _droppedPacketsCount);
            return false;
        }

        int capKBps = UploadCapKBps;
        if (capKBps <= 0)
        {
            return true;
        }

        bool allowed = _uploadBucket.TryConsume(packetBytes, capKBps * 1024, stopwatchTimestamp);
        if (!allowed)
        {
            Interlocked.Increment(ref _droppedPacketsCount);
        }
        return allowed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordReceivedPacket(int packetBytes, ProtocolKind protocol)
    {
        Interlocked.Add(ref _deltaReceivedBytes, packetBytes);
        Interlocked.Add(ref _unflushedHistoryReceivedBytes, packetBytes);
        Interlocked.Add(ref _totalReceivedBytes, packetBytes);
        RecordProtocolBytes(packetBytes, protocol);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSentPacket(int packetBytes, ProtocolKind protocol)
    {
        Interlocked.Add(ref _deltaSentBytes, packetBytes);
        Interlocked.Add(ref _unflushedHistorySentBytes, packetBytes);
        Interlocked.Add(ref _totalSentBytes, packetBytes);
        RecordProtocolBytes(packetBytes, protocol);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordDnsQuery()
    {
        Interlocked.Increment(ref _dnsQueryCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordDroppedPacket()
    {
        Interlocked.Increment(ref _droppedPacketsCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordProtocolBytes(int packetBytes, ProtocolKind protocol)
    {
        switch (protocol)
        {
            case ProtocolKind.HttpTls:
                Interlocked.Add(ref _httpTlsBytes, packetBytes);
                break;
            case ProtocolKind.Tcp:
                Interlocked.Add(ref _tcpBytes, packetBytes);
                break;
            case ProtocolKind.Udp:
                Interlocked.Add(ref _udpBytes, packetBytes);
                break;
            case ProtocolKind.Dns:
                Interlocked.Add(ref _dnsBytes, packetBytes);
                break;
            case ProtocolKind.Icmp:
                Interlocked.Add(ref _icmpBytes, packetBytes);
                break;
            default:
                Interlocked.Add(ref _otherBytes, packetBytes);
                break;
        }
    }

    /// <summary>
    /// Atomically reads and resets the live 1-second byte delta counters.
    /// </summary>
    public (long ReceivedDelta, long SentDelta) ConsumeRateDeltas()
    {
        long rx = Interlocked.Exchange(ref _deltaReceivedBytes, 0);
        long tx = Interlocked.Exchange(ref _deltaSentBytes, 0);
        return (rx, tx);
    }

    /// <summary>
    /// Atomically reads and resets the unflushed historical byte delta counters for persistent storage.
    /// </summary>
    public (long ReceivedHistoryDelta, long SentHistoryDelta) ConsumeHistoryDeltas()
    {
        long rx = Interlocked.Exchange(ref _unflushedHistoryReceivedBytes, 0);
        long tx = Interlocked.Exchange(ref _unflushedHistorySentBytes, 0);
        return (rx, tx);
    }
}
