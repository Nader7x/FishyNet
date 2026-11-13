using System.Collections.Concurrent;
using FishyNet.Core;
using FishyNet.Core.Packets;

namespace FishyNet.Features.AppMonitor;

/// <summary>
/// Tracks contacted websites and domains across "This PC" and monitored LAN devices by combining
/// TLS ClientHello SNI, plaintext HTTP Host headers, and DNS A/AAAA reverse IP->Domain mappings.
/// </summary>
public sealed class WebsiteTrafficTracker : IPacketTelemetryObserver
{
    private const int MaxDnsCacheEntries = 8192;
    private const int MaxFlowEntries = 8192;
    private const int MaxWebsiteEntries = 2048;

    private readonly record struct IpAddressKey(bool IsIpv6, Ipv4Address Ipv4, ulong Ipv6High, ulong Ipv6Low)
    {
        public static IpAddressKey FromIpv4(Ipv4Address ip) => new(false, ip, 0UL, 0UL);

        public static IpAddressKey FromDnsMapping(in DnsIpMapping mapping) => mapping.IsIpv6
            ? new(true, Ipv4Address.Zero, mapping.Ipv6High, mapping.Ipv6Low)
            : new(false, mapping.Ipv4, 0UL, 0UL);
    }

    private readonly record struct DeviceIpKey(Ipv4Address DeviceIp, IpAddressKey RemoteIp);
    private readonly record struct FlowKey(
        Ipv4Address DeviceIp,
        byte TransportProtocol,
        Ipv4Address LocalIp,
        ushort LocalPort,
        Ipv4Address RemoteIp,
        ushort RemotePort);

    private readonly record struct WebsiteKey(string Domain, Ipv4Address SourceHostIp, string ProtocolPort);

    private sealed class DnsReverseRecord
    {
        public string Domain { get; init; } = string.Empty;
        public string FormattedIp { get; init; } = string.Empty;
        public long ExpiresUtcTicks { get; set; }
    }

    private sealed class WebsiteAccumulator
    {
        public required string EntryKey { get; init; }
        public required string Domain { get; init; }
        public required string ProtocolPort { get; init; }
        public required Ipv4Address SourceHostIp { get; init; }
        public required bool IsLocalHost { get; init; }

        public string ResolvedIp { get; set; } = string.Empty;
        public string SourceHost { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public long DownloadBytes;
        public long UploadBytes;
        public long HitCount;
        public long LastSeenUtcTicks;
        public volatile bool IsSinkholed;
    }

    private readonly DeviceRegistry? _deviceRegistry;
    private readonly AppTrafficTracker? _appTracker;
    private readonly Func<Ipv4Address, string, bool>? _sinkholeCheck;

    private readonly ConcurrentDictionary<DeviceIpKey, DnsReverseRecord> _deviceDnsCache = new();
    private readonly ConcurrentDictionary<IpAddressKey, DnsReverseRecord> _globalDnsCache = new();
    private readonly ConcurrentDictionary<FlowKey, (string Domain, long LastSeenTicks)> _flowDomainMap = new();
    private readonly ConcurrentDictionary<WebsiteKey, WebsiteAccumulator> _websites = new();

    public WebsiteTrafficTracker(
        DeviceRegistry? deviceRegistry = null,
        AppTrafficTracker? appTracker = null,
        Func<Ipv4Address, string, bool>? sinkholeCheck = null)
    {
        _deviceRegistry = deviceRegistry;
        _appTracker = appTracker;
        _sinkholeCheck = sinkholeCheck;
    }

    public void OnDnsMappingsObserved(in PacketFlowObservation flow, ReadOnlySpan<DnsIpMapping> mappings)
    {
        if (mappings.IsEmpty)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        long nowTicks = nowUtc.Ticks;

        for (int i = 0; i < mappings.Length; i++)
        {
            ref readonly DnsIpMapping mapping = ref mappings[i];
            if (string.IsNullOrWhiteSpace(mapping.Domain))
            {
                continue;
            }

            string domain = mapping.Domain;
            string ipText = mapping.FormatAddress();
            IpAddressKey ipKey = IpAddressKey.FromDnsMapping(in mapping);

            long ttlSeconds = mapping.TtlSeconds == 0
                ? 1800L
                : Math.Clamp((long)mapping.TtlSeconds, 60L, 86400L);
            long expiresTicks = nowTicks + TimeSpan.FromSeconds(ttlSeconds).Ticks;

            var record = new DnsReverseRecord
            {
                Domain = domain,
                FormattedIp = ipText,
                ExpiresUtcTicks = expiresTicks
            };

            if (_deviceDnsCache.Count >= MaxDnsCacheEntries) _deviceDnsCache.Clear();
            if (_globalDnsCache.Count >= MaxDnsCacheEntries) _globalDnsCache.Clear();

            _deviceDnsCache[new DeviceIpKey(flow.DeviceIp, ipKey)] = record;
            _globalDnsCache[ipKey] = record;

            // Record initial DNS resolution visibility only if a higher-layer TLS/QUIC/HTTP entry
            // for this (domain, sourceHostIp) doesn't already exist.
            if (!HasHigherLayerEntry(domain, flow.DeviceIp))
            {
                string dnsPortProto = flow.RemotePort == 5353 ? "5353 · DNS" : "53 · DNS";
                WebsiteAccumulator acc = GetOrCreateWebsite(domain, in flow, dnsPortProto, ipText, nowTicks);
                acc.ResolvedIp = ipText;
                Interlocked.Increment(ref acc.HitCount);
                Interlocked.Exchange(ref acc.LastSeenUtcTicks, nowTicks);

                if (i == 0 && flow.ByteCount > 0)
                {
                    // DNS response packet is inbound to the querying host
                    Interlocked.Add(ref acc.DownloadBytes, flow.ByteCount);
                }
            }
        }
    }

    public void OnTlsSniObserved(in PacketFlowObservation flow, string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        long nowTicks = nowUtc.Ticks;
        string domain = serverName.Trim().ToLowerInvariant();
        string resolvedIp = flow.RemoteIp.IsZero ? string.Empty : flow.RemoteIp.ToString();

        CacheFlowDomain(in flow, domain, resolvedIp, nowTicks);

        string protocolPort = $"{(flow.RemotePort == 0 ? (ushort)443 : flow.RemotePort)} · TLS";
        PromoteDnsPlaceholderIfExists(domain, flow.DeviceIp, out long carriedHits, out string? carriedIp);

        WebsiteAccumulator acc = GetOrCreateWebsite(
            domain,
            in flow,
            protocolPort,
            string.IsNullOrEmpty(resolvedIp) ? carriedIp ?? string.Empty : resolvedIp,
            nowTicks);

        Interlocked.Add(ref acc.HitCount, 1L + carriedHits);
        Interlocked.Exchange(ref acc.LastSeenUtcTicks, nowTicks);
        EnrichHostAndApp(acc, in flow, domain);
    }

    public void OnHttpHostObserved(in PacketFlowObservation flow, string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        long nowTicks = nowUtc.Ticks;
        string domain = host.Trim().ToLowerInvariant();
        string resolvedIp = flow.RemoteIp.IsZero ? string.Empty : flow.RemoteIp.ToString();

        CacheFlowDomain(in flow, domain, resolvedIp, nowTicks);

        string protocolPort = $"{(flow.RemotePort == 0 ? (ushort)80 : flow.RemotePort)} · HTTP";
        PromoteDnsPlaceholderIfExists(domain, flow.DeviceIp, out long carriedHits, out string? carriedIp);

        WebsiteAccumulator acc = GetOrCreateWebsite(
            domain,
            in flow,
            protocolPort,
            string.IsNullOrEmpty(resolvedIp) ? carriedIp ?? string.Empty : resolvedIp,
            nowTicks);

        Interlocked.Add(ref acc.HitCount, 1L + carriedHits);
        Interlocked.Exchange(ref acc.LastSeenUtcTicks, nowTicks);
        EnrichHostAndApp(acc, in flow, domain);
    }

    public void OnPacketFlow(in PacketFlowObservation flow)
    {
        if (flow.ByteCount <= 0 || flow.RemoteIp.IsZero)
        {
            return;
        }

        // DNS response packets are accounted in OnDnsMappingsObserved; skip double-counting
        if (flow.Protocol == ProtocolKind.Dns && !flow.IsOutbound && flow.RemotePort is 53 or 5353)
        {
            return;
        }

        long nowTicks = DateTime.UtcNow.Ticks;
        var flowKey = new FlowKey(flow.DeviceIp, flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);

        bool isNewFlowHit = false;
        string? domain = null;

        if (_flowDomainMap.TryGetValue(flowKey, out var cachedFlow))
        {
            domain = cachedFlow.Domain;
            if ((nowTicks - cachedFlow.LastSeenTicks) > TimeSpan.FromSeconds(30).Ticks)
            {
                isNewFlowHit = true;
            }
            _flowDomainMap[flowKey] = (domain, nowTicks);
        }
        else if (TryLookupReverseDns(flow.DeviceIp, flow.RemoteIp, nowTicks, out string? dnsDomain) &&
                 !string.IsNullOrEmpty(dnsDomain))
        {
            domain = dnsDomain;
            isNewFlowHit = true;
            if (_flowDomainMap.Count >= MaxFlowEntries) _flowDomainMap.Clear();
            _flowDomainMap[flowKey] = (dnsDomain, nowTicks);
        }

        if (string.IsNullOrEmpty(domain))
        {
            return;
        }

        string protocolPort = FormatProtocolPort(in flow);
        PromoteDnsPlaceholderIfExists(domain, flow.DeviceIp, out long carriedHits, out _);

        WebsiteAccumulator acc = GetOrCreateWebsite(domain, in flow, protocolPort, flow.RemoteIp.ToString(), nowTicks);
        if (flow.IsOutbound)
        {
            Interlocked.Add(ref acc.UploadBytes, flow.ByteCount);
        }
        else
        {
            Interlocked.Add(ref acc.DownloadBytes, flow.ByteCount);
        }

        if (isNewFlowHit || carriedHits > 0)
        {
            Interlocked.Add(ref acc.HitCount, (isNewFlowHit ? 1L : 0L) + carriedHits);
        }

        Interlocked.Exchange(ref acc.LastSeenUtcTicks, nowTicks);
        EnrichHostAndApp(acc, in flow, domain);
    }

    public bool TryResolveDomainForIp(Ipv4Address remoteIp, out string domain, Ipv4Address? deviceIp = null)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        if (deviceIp.HasValue && TryLookupReverseDns(deviceIp.Value, remoteIp, nowTicks, out string? perDevice))
        {
            domain = perDevice!;
            return true;
        }

        var ipKey = IpAddressKey.FromIpv4(remoteIp);
        if (_globalDnsCache.TryGetValue(ipKey, out DnsReverseRecord? rec) && rec.ExpiresUtcTicks >= nowTicks)
        {
            domain = rec.Domain;
            return true;
        }

        domain = string.Empty;
        return false;
    }

    public WebsiteTrafficSnapshot GetSnapshot(bool? localOnlyFilter = null, DateTime? timestampUtc = null)
    {
        DateTime nowUtc = timestampUtc ?? DateTime.UtcNow;
        var list = new List<WebsiteTrafficEntry>(_websites.Count);
        var uniqueDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalRx = 0;
        long totalTx = 0;

        foreach (WebsiteAccumulator acc in _websites.Values)
        {
            if (localOnlyFilter.HasValue && acc.IsLocalHost != localOnlyFilter.Value)
            {
                continue;
            }

            // Refresh live SourceHost name & sinkhole status cleanly on snapshot build
            string sourceHost = acc.IsLocalHost
                ? "This PC"
                : (_deviceRegistry?.TryGetByIp(acc.SourceHostIp)?.DisplayName ?? acc.SourceHost);

            bool sinkholed = acc.IsSinkholed || IsDomainSinkholed(acc.SourceHostIp, acc.Domain);
            acc.IsSinkholed = sinkholed;

            long rx = Interlocked.Read(ref acc.DownloadBytes);
            long tx = Interlocked.Read(ref acc.UploadBytes);
            long hits = Interlocked.Read(ref acc.HitCount);
            long lastTicks = Interlocked.Read(ref acc.LastSeenUtcTicks);

            totalRx += rx;
            totalTx += tx;
            uniqueDomains.Add(acc.Domain);

            list.Add(new WebsiteTrafficEntry(
                acc.EntryKey,
                acc.Domain,
                acc.ResolvedIp,
                acc.ProtocolPort,
                sourceHost,
                acc.SourceHostIp.ToString(),
                acc.IsLocalHost,
                acc.AppName,
                rx,
                tx,
                hits,
                new DateTime(lastTicks, DateTimeKind.Utc),
                sinkholed));
        }

        list.Sort(static (a, b) =>
        {
            int bytesCmp = b.TotalBytes.CompareTo(a.TotalBytes);
            return bytesCmp != 0 ? bytesCmp : b.LastSeenUtc.CompareTo(a.LastSeenUtc);
        });

        return new WebsiteTrafficSnapshot(
            nowUtc,
            list,
            totalRx,
            totalTx,
            uniqueDomains.Count);
    }

    public void Clear()
    {
        _websites.Clear();
        _flowDomainMap.Clear();
        _deviceDnsCache.Clear();
        _globalDnsCache.Clear();
    }

    private void CacheFlowDomain(in PacketFlowObservation flow, string domain, string resolvedIp, long nowTicks)
    {
        var flowKey = new FlowKey(flow.DeviceIp, flow.TransportProtocol, flow.LocalIp, flow.LocalPort, flow.RemoteIp, flow.RemotePort);
        if (_flowDomainMap.Count >= MaxFlowEntries) _flowDomainMap.Clear();
        _flowDomainMap[flowKey] = (domain, nowTicks);

        if (!flow.RemoteIp.IsZero)
        {
            var ipKey = IpAddressKey.FromIpv4(flow.RemoteIp);
            var dnsRec = new DnsReverseRecord
            {
                Domain = domain,
                FormattedIp = resolvedIp,
                ExpiresUtcTicks = nowTicks + TimeSpan.FromMinutes(30).Ticks
            };
            if (_deviceDnsCache.Count >= MaxDnsCacheEntries) _deviceDnsCache.Clear();
            if (_globalDnsCache.Count >= MaxDnsCacheEntries) _globalDnsCache.Clear();
            _deviceDnsCache[new DeviceIpKey(flow.DeviceIp, ipKey)] = dnsRec;
            _globalDnsCache[ipKey] = dnsRec;
        }
    }

    private bool TryLookupReverseDns(Ipv4Address deviceIp, Ipv4Address remoteIp, long nowTicks, out string? domain)
    {
        var ipKey = IpAddressKey.FromIpv4(remoteIp);
        if (_deviceDnsCache.TryGetValue(new DeviceIpKey(deviceIp, ipKey), out DnsReverseRecord? devRec) &&
            devRec.ExpiresUtcTicks >= nowTicks)
        {
            domain = devRec.Domain;
            return true;
        }

        if (_globalDnsCache.TryGetValue(ipKey, out DnsReverseRecord? globRec) &&
            globRec.ExpiresUtcTicks >= nowTicks)
        {
            domain = globRec.Domain;
            return true;
        }

        domain = null;
        return false;
    }

    private bool HasHigherLayerEntry(string domain, Ipv4Address sourceIp)
    {
        foreach (WebsiteKey key in _websites.Keys)
        {
            if (key.SourceHostIp == sourceIp &&
                string.Equals(key.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                !key.ProtocolPort.EndsWith("DNS", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void PromoteDnsPlaceholderIfExists(
        string domain,
        Ipv4Address sourceIp,
        out long carriedHits,
        out string? carriedIp)
    {
        carriedHits = 0;
        carriedIp = null;

        if (_websites.TryRemove(new WebsiteKey(domain, sourceIp, "53 · DNS"), out WebsiteAccumulator? dnsAcc) ||
            _websites.TryRemove(new WebsiteKey(domain, sourceIp, "5353 · DNS"), out dnsAcc))
        {
            carriedHits = Interlocked.Read(ref dnsAcc.HitCount);
            carriedIp = dnsAcc.ResolvedIp;
        }
    }

    private WebsiteAccumulator GetOrCreateWebsite(
        string domain,
        in PacketFlowObservation flow,
        string protocolPort,
        string resolvedIp,
        long nowTicks)
    {
        var key = new WebsiteKey(domain, flow.DeviceIp, protocolPort);
        if (_websites.TryGetValue(key, out WebsiteAccumulator? existing))
        {
            if (string.IsNullOrEmpty(existing.ResolvedIp) && !string.IsNullOrEmpty(resolvedIp))
            {
                existing.ResolvedIp = resolvedIp;
            }
            return existing;
        }

        if (_websites.Count >= MaxWebsiteEntries)
        {
            WebsiteKey? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<WebsiteKey, WebsiteAccumulator> kv in _websites)
            {
                long t = Interlocked.Read(ref kv.Value.LastSeenUtcTicks);
                if (t < oldestTicks)
                {
                    oldestTicks = t;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey.HasValue)
            {
                _websites.TryRemove(oldestKey.Value, out _);
            }
        }

        string sourceHost = flow.IsLocalHost
            ? "This PC"
            : (_deviceRegistry?.TryGetByIp(flow.DeviceIp)?.DisplayName ?? flow.DeviceIp.ToString());

        var created = new WebsiteAccumulator
        {
            EntryKey = $"{domain}|{flow.DeviceIp}|{protocolPort}",
            Domain = domain,
            ProtocolPort = protocolPort,
            SourceHostIp = flow.DeviceIp,
            IsLocalHost = flow.IsLocalHost,
            ResolvedIp = resolvedIp,
            SourceHost = sourceHost,
            AppName = flow.IsLocalHost ? string.Empty : "LAN Host",
            LastSeenUtcTicks = nowTicks,
            IsSinkholed = IsDomainSinkholed(flow.DeviceIp, domain)
        };

        return _websites.GetOrAdd(key, created);
    }

    private void EnrichHostAndApp(WebsiteAccumulator acc, in PacketFlowObservation flow, string domain)
    {
        if (flow.IsLocalHost && _appTracker is not null && string.IsNullOrEmpty(acc.AppName))
        {
            if (_appTracker.TryResolveAppName(in flow, out string appName))
            {
                acc.AppName = appName;
            }
        }

        acc.IsSinkholed = IsDomainSinkholed(flow.DeviceIp, domain);
    }

    private bool IsDomainSinkholed(Ipv4Address deviceIp, string domain)
    {
        if (_sinkholeCheck is not null && _sinkholeCheck(deviceIp, domain))
        {
            return true;
        }

        if (_deviceRegistry is not null)
        {
            NetworkDevice? device = _deviceRegistry.TryGetByIp(deviceIp);
            if (device is not null && device.IsDomainBlocked(domain))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatProtocolPort(in PacketFlowObservation flow)
    {
        ushort port = flow.RemotePort;
        if (flow.TransportProtocol == 17)
        {
            if (port == 443) return "443 · QUIC";
            if (port is 53 or 5353) return $"{port} · DNS";
            return port > 0 ? $"{port} · UDP" : "UDP";
        }

        if (flow.TransportProtocol == 6)
        {
            if (port is 443 or 8443) return $"{port} · TLS";
            if (port is 80 or 8080) return $"{port} · HTTP";
            return port > 0 ? $"{port} · TCP" : "TCP";
        }

        return flow.Protocol == ProtocolKind.Icmp ? "ICMP" : "IP";
    }
}
