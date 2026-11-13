using System.Collections.Concurrent;
using System.Diagnostics;
using FishyNet.Core;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;

namespace FishyNet.Features.AppMonitor;

public interface ISocketTableProvider
{
    bool TryReadSockets(List<SocketConnectionEntry> destination);
}

public sealed class IpHelperSocketTableProvider : ISocketTableProvider
{
    public static IpHelperSocketTableProvider Instance { get; } = new();

    public bool TryReadSockets(List<SocketConnectionEntry> destination)
    {
        return IpHelperNative.TrySnapshotSockets(destination, includeIpv6: true);
    }
}

/// <summary>
/// Correlates Local PC 5-tuple packet flows against Windows IP Helper TCP/UDP PID tables,
/// resolves process metadata, computes per-app live RX/TX Bps, protocol tags, contacted hosts,
/// and raises First Network Access alerts for newly observed executables.
/// </summary>
public sealed class AppTrafficTracker : IPacketTelemetryObserver
{
    private const int MaxFlowCacheEntries = 4096;
    private const int MaxTrackedApps = 512;
    private const int MaxContactedPerApp = 24;

    [Flags]
    private enum ProtocolTagFlags
    {
        None = 0,
        Tls = 1 << 0,
        Quic = 1 << 1,
        Http = 1 << 2,
        Dns = 1 << 3,
        Tcp = 1 << 4,
        Udp = 1 << 5,
        Icmp = 1 << 6
    }

    private readonly record struct TcpExactKey(Ipv4Address LocalIp, ushort LocalPort, Ipv4Address RemoteIp, ushort RemotePort);
    private readonly record struct TcpWildcardKey(ushort LocalPort, Ipv4Address RemoteIp, ushort RemotePort);
    private readonly record struct UdpExactKey(Ipv4Address LocalIp, ushort LocalPort);
    private readonly record struct Flow5TupleKey(byte IpProto, Ipv4Address LocalIp, ushort LocalPort, Ipv4Address RemoteIp, ushort RemotePort);

    private sealed class SocketLookupTable
    {
        public readonly Dictionary<TcpExactKey, int> TcpExact = new(256);
        public readonly Dictionary<TcpWildcardKey, int> TcpWildcard = new(256);
        public readonly Dictionary<UdpExactKey, int> UdpExact = new(128);
        public readonly Dictionary<ushort, int> UdpWildcard = new(128);
        public readonly Dictionary<int, int> ConnectionsPerPid = new(64);
    }

    private sealed class AppAccumulator
    {
        public required string AppKey { get; init; }
        public ProcessMetadata Metadata { get; set; }
        public DateTime FirstSeenUtc { get; init; }
        public long LastSeenUtcTicks;

        public long DeltaDownloadBytes;
        public long DeltaUploadBytes;
        public long TotalDownloadBytes;
        public long TotalUploadBytes;
        public long CurrentDownloadBps;
        public long CurrentUploadBps;

        public int ProtocolMask;
        public volatile bool IsNewFirstAccess;

        public readonly ConcurrentDictionary<int, byte> Pids = new();
        public readonly ConcurrentDictionary<string, long> Domains = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<Ipv4Address, long> RemoteIps = new();

        public AppAccumulator(ProcessMetadata metadata, DateTime nowUtc)
        {
            Metadata = metadata;
            FirstSeenUtc = nowUtc;
            LastSeenUtcTicks = nowUtc.Ticks;
        }
    }

    private readonly ISocketTableProvider _socketProvider;
    private readonly IProcessMetadataResolver _metadataResolver;
    private readonly IAppFirewallController? _firewallController;
    private readonly TrafficTimelineStore? _timelineStore;

    private readonly Lock _refreshLock = new();
    private readonly List<SocketConnectionEntry> _scratchSockets = new(512);
    private SocketLookupTable _lookupTable = new();
    private long _lastSocketRefreshTicks;
    private readonly long _minOnDemandRefreshTicks;

    private readonly ConcurrentDictionary<Flow5TupleKey, (int Pid, long SeenTicks)> _recentFlows = new();
    private readonly ConcurrentDictionary<Flow5TupleKey, string> _flowDomains = new();
    private readonly ConcurrentDictionary<string, AppAccumulator> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _knownAppKeys = new(StringComparer.OrdinalIgnoreCase);

    private long _lastSampleTimestampTicks;

    public event Action<MonitorAlertRecord>? AlertRaised;

    public AppTrafficTracker(
        ISocketTableProvider? socketTableProvider = null,
        IProcessMetadataResolver? metadataResolver = null,
        IAppFirewallController? firewallController = null,
        TrafficTimelineStore? timelineStore = null,
        TimeSpan? minOnDemandRefreshInterval = null)
    {
        _socketProvider = socketTableProvider ?? IpHelperSocketTableProvider.Instance;
        _metadataResolver = metadataResolver ?? new ProcessMetadataResolver();
        _firewallController = firewallController;
        _timelineStore = timelineStore;

        double minRefreshSec = (minOnDemandRefreshInterval ?? TimeSpan.FromMilliseconds(50)).TotalSeconds;
        _minOnDemandRefreshTicks = (long)(minRefreshSec * Stopwatch.Frequency);
        _lastSampleTimestampTicks = Stopwatch.GetTimestamp();
    }

    public void SeedKnownApplications(IEnumerable<string> appKeys)
    {
        foreach (string key in appKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _knownAppKeys[key.Trim()] = 1;
            }
        }
    }

    public bool AcknowledgeFirstAccess(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey)) return false;
        if (_apps.TryGetValue(appKey, out AppAccumulator? acc))
        {
            acc.IsNewFirstAccess = false;
            return true;
        }
        return false;
    }

    public void RefreshSocketTable()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_refreshLock)
        {
            if (!_socketProvider.TryReadSockets(_scratchSockets))
            {
                _lastSocketRefreshTicks = now;
                return;
            }

            var next = new SocketLookupTable();
            for (int i = 0; i < _scratchSockets.Count; i++)
            {
                SocketConnectionEntry entry = _scratchSockets[i];
                if (entry.OwningPid < 0) continue;

                next.ConnectionsPerPid.TryGetValue(entry.OwningPid, out int existingCount);
                next.ConnectionsPerPid[entry.OwningPid] = existingCount + 1;

                if (entry.IpProtocol == 6) // TCP
                {
                    Ipv4Address local4 = entry.EffectiveLocalIpv4;
                    Ipv4Address remote4 = entry.EffectiveRemoteIpv4;

                    if (entry.LocalPort != 0 && entry.RemotePort != 0 && !remote4.IsZero)
                    {
                        if (!local4.IsZero)
                        {
                            next.TcpExact.TryAdd(new TcpExactKey(local4, entry.LocalPort, remote4, entry.RemotePort), entry.OwningPid);
                        }
                        next.TcpWildcard.TryAdd(new TcpWildcardKey(entry.LocalPort, remote4, entry.RemotePort), entry.OwningPid);
                    }
                }
                else if (entry.IpProtocol == 17) // UDP
                {
                    if (entry.LocalPort != 0)
                    {
                        Ipv4Address local4 = entry.EffectiveLocalIpv4;
                        if (!local4.IsZero)
                        {
                            next.UdpExact.TryAdd(new UdpExactKey(local4, entry.LocalPort), entry.OwningPid);
                        }
                        if (entry.IsWildcardLocal || !entry.IsIpv6 || entry.IsIpv4MappedIpv6)
                        {
                            next.UdpWildcard.TryAdd(entry.LocalPort, entry.OwningPid);
                        }
                    }
                }
            }

            Volatile.Write(ref _lookupTable, next);
            _lastSocketRefreshTicks = now;
        }
    }

    public void OnPacketFlow(in PacketFlowObservation flow)
    {
        if (!flow.IsLocalHost || flow.ByteCount <= 0)
        {
            return;
        }

        int pid = ResolveFlowPid(in flow);
        if (pid < 0)
        {
            if (flow.TransportProtocol == 1)
            {
                pid = 4; // ICMP belongs to Windows kernel/system
            }
            else
            {
                pid = -1; // Unattributed short-lived socket
            }
        }

        ProcessMetadata meta = _metadataResolver.Resolve(pid);
        string appKey = BuildAppKey(meta);
        DateTime nowUtc = DateTime.UtcNow;

        AppAccumulator acc = GetOrCreateAccumulator(appKey, meta, nowUtc);
        acc.Pids.TryAdd(pid, 1);
        Interlocked.Exchange(ref acc.LastSeenUtcTicks, nowUtc.Ticks);

        if (flow.IsOutbound)
        {
            Interlocked.Add(ref acc.DeltaUploadBytes, flow.ByteCount);
            Interlocked.Add(ref acc.TotalUploadBytes, flow.ByteCount);
        }
        else
        {
            Interlocked.Add(ref acc.DeltaDownloadBytes, flow.ByteCount);
            Interlocked.Add(ref acc.TotalDownloadBytes, flow.ByteCount);
        }

        var flowKey = new Flow5TupleKey(flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        bool hasTlsSni = false;
        bool hasHttpHost = false;

        if (_flowDomains.TryGetValue(flowKey, out string? domain))
        {
            RecordContactedDomain(acc, domain, flow.ByteCount);
            if (flow.RemotePort is 443 or 8443) hasTlsSni = true;
            else if (flow.RemotePort is 80 or 8080) hasHttpHost = true;
        }
        else if (!flow.RemoteIp.IsZero)
        {
            RecordContactedIp(acc, flow.RemoteIp, flow.ByteCount);
        }

        int tagBits = (int)ClassifyProtocolFlags(in flow, hasTlsSni, hasHttpHost);
        if (tagBits != 0)
        {
            Interlocked.Or(ref acc.ProtocolMask, tagBits);
        }
    }

    public void OnTlsSniObserved(in PacketFlowObservation flow, string serverName)
    {
        if (!flow.IsLocalHost || string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        var key = new Flow5TupleKey(flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        if (_flowDomains.Count >= MaxFlowCacheEntries)
        {
            _flowDomains.Clear();
        }
        _flowDomains[key] = serverName;
    }

    public void OnHttpHostObserved(in PacketFlowObservation flow, string host)
    {
        if (!flow.IsLocalHost || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var key = new Flow5TupleKey(flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        if (_flowDomains.Count >= MaxFlowCacheEntries)
        {
            _flowDomains.Clear();
        }
        _flowDomains[key] = host;
    }

    public void OnDnsMappingsObserved(in PacketFlowObservation flow, ReadOnlySpan<DnsIpMapping> mappings)
    {
        // DNS domain tracking is handled primarily by WebsiteTrafficTracker;
        // AppTrafficTracker tags DNS protocol via OnPacketFlow.
    }

    public bool TryResolveAppName(in PacketFlowObservation flow, out string appName)
    {
        appName = string.Empty;
        if (!flow.IsLocalHost) return false;

        int pid = ResolveFlowPid(in flow, allowRefresh: false);
        if (pid < 0) return false;

        ProcessMetadata meta = _metadataResolver.Resolve(pid);
        appName = meta.ProcessName;
        return !string.IsNullOrEmpty(appName);
    }

    /// <summary>
    /// Samples live RX/TX Bps rates from atomic per-interval deltas, refreshes OS socket table,
    /// and returns a sorted snapshot for UI consumption and TimelineStore ingestion.
    /// </summary>
    public AppTrafficSnapshot SampleTick(DateTime? timestampUtc = null)
    {
        RefreshSocketTable();

        long nowTicks = Stopwatch.GetTimestamp();
        long prevTicks = Interlocked.Exchange(ref _lastSampleTimestampTicks, nowTicks);
        double elapsedSeconds = Math.Max(0.25, (double)(nowTicks - prevTicks) / Stopwatch.Frequency);

        DateTime utcNow = timestampUtc ?? DateTime.UtcNow;
        SocketLookupTable table = Volatile.Read(ref _lookupTable);

        var entries = new List<AppTrafficEntry>(_apps.Count);
        long totalRxBps = 0;
        long totalTxBps = 0;
        long totalRxBytes = 0;
        long totalTxBytes = 0;
        int unseenNewCount = 0;

        foreach (AppAccumulator acc in _apps.Values)
        {
            long deltaRx = Interlocked.Exchange(ref acc.DeltaDownloadBytes, 0);
            long deltaTx = Interlocked.Exchange(ref acc.DeltaUploadBytes, 0);
            long rxBps = (long)Math.Round(deltaRx / elapsedSeconds);
            long txBps = (long)Math.Round(deltaTx / elapsedSeconds);

            Volatile.Write(ref acc.CurrentDownloadBps, rxBps);
            Volatile.Write(ref acc.CurrentUploadBps, txBps);

            long totRx = Interlocked.Read(ref acc.TotalDownloadBytes);
            long totTx = Interlocked.Read(ref acc.TotalUploadBytes);

            totalRxBps += rxBps;
            totalTxBps += txBps;
            totalRxBytes += totRx;
            totalTxBytes += totTx;

            if (acc.IsNewFirstAccess) unseenNewCount++;

            entries.Add(BuildEntry(acc, table, rxBps, txBps, totRx, totTx));
        }

        entries.Sort(static (a, b) =>
        {
            int bpsCmp = b.TotalBps.CompareTo(a.TotalBps);
            return bpsCmp != 0 ? bpsCmp : b.TotalBytes.CompareTo(a.TotalBytes);
        });

        return new AppTrafficSnapshot(
            utcNow,
            entries,
            totalRxBps,
            totalTxBps,
            totalRxBytes,
            totalTxBytes,
            unseenNewCount);
    }

    /// <summary>
    /// Reads current snapshot without resetting live rate deltas.
    /// </summary>
    public AppTrafficSnapshot GetSnapshot(DateTime? timestampUtc = null)
    {
        DateTime utcNow = timestampUtc ?? DateTime.UtcNow;
        SocketLookupTable table = Volatile.Read(ref _lookupTable);

        var entries = new List<AppTrafficEntry>(_apps.Count);
        long totalRxBps = 0;
        long totalTxBps = 0;
        long totalRxBytes = 0;
        long totalTxBytes = 0;
        int unseenNewCount = 0;

        foreach (AppAccumulator acc in _apps.Values)
        {
            long rxBps = Volatile.Read(ref acc.CurrentDownloadBps);
            long txBps = Volatile.Read(ref acc.CurrentUploadBps);
            long totRx = Interlocked.Read(ref acc.TotalDownloadBytes);
            long totTx = Interlocked.Read(ref acc.TotalUploadBytes);

            totalRxBps += rxBps;
            totalTxBps += txBps;
            totalRxBytes += totRx;
            totalTxBytes += totTx;
            if (acc.IsNewFirstAccess) unseenNewCount++;

            entries.Add(BuildEntry(acc, table, rxBps, txBps, totRx, totTx));
        }

        entries.Sort(static (a, b) =>
        {
            int bpsCmp = b.TotalBps.CompareTo(a.TotalBps);
            return bpsCmp != 0 ? bpsCmp : b.TotalBytes.CompareTo(a.TotalBytes);
        });

        return new AppTrafficSnapshot(
            utcNow,
            entries,
            totalRxBps,
            totalTxBps,
            totalRxBytes,
            totalTxBytes,
            unseenNewCount);
    }

    public void Clear()
    {
        _apps.Clear();
        _recentFlows.Clear();
        _flowDomains.Clear();
    }

    private int ResolveFlowPid(in PacketFlowObservation flow, bool allowRefresh = true)
    {
        if (flow.TransportProtocol is not (6 or 17))
        {
            return -1;
        }

        SocketLookupTable table = Volatile.Read(ref _lookupTable);
        if (TryLookupInTable(table, in flow, out int pid))
        {
            CacheRecentFlowPid(in flow, pid);
            return pid;
        }

        var flowKey = new Flow5TupleKey(flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        long now = Stopwatch.GetTimestamp();

        if (_recentFlows.TryGetValue(flowKey, out var cached) &&
            (now - cached.SeenTicks) < Stopwatch.Frequency * 5L)
        {
            return cached.Pid;
        }

        if (allowRefresh && (now - Interlocked.Read(ref _lastSocketRefreshTicks)) >= _minOnDemandRefreshTicks)
        {
            RefreshSocketTable();
            table = Volatile.Read(ref _lookupTable);
            if (TryLookupInTable(table, in flow, out pid))
            {
                CacheRecentFlowPid(in flow, pid);
                return pid;
            }
        }

        return -1;
    }

    private static bool TryLookupInTable(SocketLookupTable table, in PacketFlowObservation flow, out int pid)
    {
        if (flow.TransportProtocol == 6)
        {
            if (table.TcpExact.TryGetValue(new TcpExactKey(flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort), out pid) && pid > 0)
            {
                return true;
            }

            if (table.TcpWildcard.TryGetValue(new TcpWildcardKey(flow.LocalPort, flow.RemoteIp, flow.RemotePort), out pid) && pid > 0)
            {
                return true;
            }

            // Fallback when TCP socket just entered TIME_WAIT (PID 0) if no better PID exists
            if (table.TcpWildcard.TryGetValue(new TcpWildcardKey(flow.LocalPort, flow.RemoteIp, flow.RemotePort), out pid))
            {
                return true;
            }

            pid = -1;
            return false;
        }

        if (flow.TransportProtocol == 17)
        {
            if (table.UdpExact.TryGetValue(new UdpExactKey(flow.LocalIp, flow.LocalPort), out pid))
            {
                return true;
            }

            if (table.UdpWildcard.TryGetValue(flow.LocalPort, out pid))
            {
                return true;
            }
        }

        pid = -1;
        return false;
    }

    private void CacheRecentFlowPid(in PacketFlowObservation flow, int pid)
    {
        if (pid <= 0) return;

        if (_recentFlows.Count >= MaxFlowCacheEntries)
        {
            _recentFlows.Clear();
        }

        var key = new Flow5TupleKey(flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        _recentFlows[key] = (pid, Stopwatch.GetTimestamp());
    }

    private AppAccumulator GetOrCreateAccumulator(string appKey, ProcessMetadata meta, DateTime nowUtc)
    {
        if (_apps.TryGetValue(appKey, out AppAccumulator? existing))
        {
            if (string.IsNullOrEmpty(existing.Metadata.ExePath) && !string.IsNullOrEmpty(meta.ExePath))
            {
                existing.Metadata = meta;
            }
            return existing;
        }

        if (_apps.Count >= MaxTrackedApps)
        {
            // Evict oldest inactive entry if full
            string? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<string, AppAccumulator> kv in _apps)
            {
                long t = Interlocked.Read(ref kv.Value.LastSeenUtcTicks);
                if (t < oldestTicks)
                {
                    oldestTicks = t;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey is not null)
            {
                _apps.TryRemove(oldestKey, out _);
            }
        }

        var created = new AppAccumulator(meta, nowUtc) { AppKey = appKey };
        bool isRealApp = meta.ProcessId > 4 && !string.IsNullOrWhiteSpace(meta.ProcessName);
        bool isFirstSeen = isRealApp && _knownAppKeys.TryAdd(appKey, 1);

        if (_apps.TryAdd(appKey, created))
        {
            if (isFirstSeen)
            {
                created.IsNewFirstAccess = true;
                long alertId = MonitorAlertIdGenerator.Next();
                string target = !string.IsNullOrEmpty(meta.ExePath) ? meta.ExePath : meta.ProcessName;
                var alert = new MonitorAlertRecord(
                    alertId,
                    nowUtc,
                    MonitorAlertKind.FirstNetworkAccess,
                    MonitorAlertSeverity.Info,
                    $"First Network Access: {meta.ProcessName}",
                    $"{meta.DisplayName} ({meta.ProcessName}) made its first observed network connection.",
                    meta.ProcessName,
                    target);

                AlertRaised?.Invoke(alert);
                _timelineStore?.RecordAlert(alert);
            }
            return created;
        }

        return _apps[appKey];
    }

    private static string BuildAppKey(ProcessMetadata meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.ExePath))
        {
            return meta.ExePath;
        }
        return $"proc:{meta.ProcessName}:{meta.ProcessId}";
    }

    private static ProtocolTagFlags ClassifyProtocolFlags(in PacketFlowObservation flow, bool hasTlsSni, bool hasHttpHost)
    {
        ProtocolTagFlags flags = ProtocolTagFlags.None;

        if (flow.TransportProtocol == 6)
        {
            flags |= ProtocolTagFlags.Tcp;
            if (hasTlsSni || flow.RemotePort is 443 or 8443 || flow.LocalPort is 443 or 8443)
            {
                flags |= ProtocolTagFlags.Tls;
            }
            else if (hasHttpHost || flow.RemotePort is 80 or 8080 || flow.LocalPort is 80 or 8080)
            {
                flags |= ProtocolTagFlags.Http;
            }
        }
        else if (flow.TransportProtocol == 17)
        {
            flags |= ProtocolTagFlags.Udp;
            if (flow.Protocol == ProtocolKind.Dns || flow.RemotePort is 53 or 5353 || flow.LocalPort is 53 or 5353)
            {
                flags |= ProtocolTagFlags.Dns;
            }
            else if (flow.RemotePort == 443 || flow.LocalPort == 443)
            {
                flags |= ProtocolTagFlags.Quic;
            }
        }
        else if (flow.TransportProtocol == 1 || flow.Protocol == ProtocolKind.Icmp)
        {
            flags |= ProtocolTagFlags.Icmp;
        }

        return flags;
    }

    private static void RecordContactedDomain(AppAccumulator acc, string domain, int bytes)
    {
        if (acc.Domains.TryGetValue(domain, out _))
        {
            acc.Domains.AddOrUpdate(domain, bytes, (_, old) => old + bytes);
            return;
        }

        if (acc.Domains.Count < MaxContactedPerApp)
        {
            acc.Domains.AddOrUpdate(domain, bytes, (_, old) => old + bytes);
        }
    }

    private static void RecordContactedIp(AppAccumulator acc, Ipv4Address remoteIp, int bytes)
    {
        if (acc.RemoteIps.TryGetValue(remoteIp, out _))
        {
            acc.RemoteIps.AddOrUpdate(remoteIp, bytes, (_, old) => old + bytes);
            return;
        }

        if (acc.RemoteIps.Count < MaxContactedPerApp)
        {
            acc.RemoteIps.AddOrUpdate(remoteIp, bytes, (_, old) => old + bytes);
        }
    }

    private AppTrafficEntry BuildEntry(
        AppAccumulator acc,
        SocketLookupTable table,
        long rxBps,
        long txBps,
        long totalRx,
        long totalTx)
    {
        ProcessMetadata meta = acc.Metadata;
        int activeConnections = 0;
        foreach (int pid in acc.Pids.Keys)
        {
            if (table.ConnectionsPerPid.TryGetValue(pid, out int count))
            {
                activeConnections += count;
            }
        }

        var tags = FormatProtocolTags((ProtocolTagFlags)Volatile.Read(ref acc.ProtocolMask));
        var hosts = BuildTopHostsList(acc);
        bool isBlocked = _firewallController is not null &&
                         !string.IsNullOrEmpty(meta.ExePath) &&
                         _firewallController.IsAppBlocked(meta.ExePath);

        return new AppTrafficEntry(
            acc.AppKey,
            meta.ProcessId,
            meta.ProcessName,
            meta.DisplayName,
            meta.ExePath,
            meta.Publisher,
            meta.Icon,
            rxBps,
            txBps,
            totalRx,
            totalTx,
            tags,
            hosts,
            activeConnections,
            acc.IsNewFirstAccess,
            isBlocked,
            acc.FirstSeenUtc,
            new DateTime(Interlocked.Read(ref acc.LastSeenUtcTicks), DateTimeKind.Utc));
    }

    private static IReadOnlyList<string> FormatProtocolTags(ProtocolTagFlags flags)
    {
        if (flags == ProtocolTagFlags.None)
        {
            return [];
        }

        var list = new List<string>(5);
        if ((flags & ProtocolTagFlags.Tls) != 0) list.Add("TLS");
        if ((flags & ProtocolTagFlags.Quic) != 0) list.Add("QUIC");
        if ((flags & ProtocolTagFlags.Http) != 0) list.Add("HTTP");
        if ((flags & ProtocolTagFlags.Dns) != 0) list.Add("DNS");
        if ((flags & ProtocolTagFlags.Icmp) != 0) list.Add("ICMP");
        if ((flags & ProtocolTagFlags.Tcp) != 0 && (flags & (ProtocolTagFlags.Tls | ProtocolTagFlags.Http)) == 0) list.Add("TCP");
        if ((flags & ProtocolTagFlags.Udp) != 0 && (flags & (ProtocolTagFlags.Quic | ProtocolTagFlags.Dns)) == 0) list.Add("UDP");
        return list;
    }

    private static IReadOnlyList<string> BuildTopHostsList(AppAccumulator acc)
    {
        if (acc.Domains.IsEmpty && acc.RemoteIps.IsEmpty)
        {
            return [];
        }

        var combined = new List<(string Host, long Bytes)>(8);
        foreach (KeyValuePair<string, long> kv in acc.Domains)
        {
            combined.Add((kv.Key, kv.Value));
        }

        if (combined.Count < 6)
        {
            foreach (KeyValuePair<Ipv4Address, long> kv in acc.RemoteIps)
            {
                combined.Add((kv.Key.ToString(), kv.Value));
            }
        }

        combined.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        int take = Math.Min(5, combined.Count);
        var result = new string[take];
        for (int i = 0; i < take; i++)
        {
            result[i] = combined[i].Host;
        }
        return result;
    }
}
