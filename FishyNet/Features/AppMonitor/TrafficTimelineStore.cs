using FishyNet.Core;
using FishyNet.Core.Formatting;

namespace FishyNet.Features.AppMonitor;

/// <summary>
/// Thread-safe, ring-buffered Time-Machine store maintaining rolling buckets for
/// Live (1s), 5m (5s), 1h (1m), 24h (15m), and 7D (2h) windows, top-N stacked slices,
/// rolling 3x traffic spike detection, and correlated security/access alert markers.
/// </summary>
public sealed class TrafficTimelineStore
{
    private const int MaxStoredAlerts = 500;

    private sealed class BucketSlot
    {
        public DateTime StartUtc;
        public long DownloadBytes;
        public long UploadBytes;
        public readonly Dictionary<string, (long DownloadBytes, long UploadBytes)> Slices =
            new(8, StringComparer.OrdinalIgnoreCase);

        public long TotalBytes => DownloadBytes + UploadBytes;

        public void Reset(DateTime startUtc)
        {
            StartUtc = startUtc;
            DownloadBytes = 0;
            UploadBytes = 0;
            Slices.Clear();
        }

        public void Add(long rxBytes, long txBytes, IReadOnlyList<TimelineSeriesSlice>? slices)
        {
            if (rxBytes > 0) DownloadBytes += rxBytes;
            if (txBytes > 0) UploadBytes += txBytes;

            if (slices is null || slices.Count == 0)
            {
                return;
            }

            for (int i = 0; i < slices.Count; i++)
            {
                TimelineSeriesSlice s = slices[i];
                if (string.IsNullOrWhiteSpace(s.Label) || (s.DownloadBytes <= 0 && s.UploadBytes <= 0))
                {
                    continue;
                }

                if (Slices.TryGetValue(s.Label, out var existing))
                {
                    Slices[s.Label] = (existing.DownloadBytes + s.DownloadBytes, existing.UploadBytes + s.UploadBytes);
                }
                else
                {
                    Slices[s.Label] = (s.DownloadBytes, s.UploadBytes);
                }
            }
        }
    }

    private sealed class WindowRing
    {
        public readonly TimeMachineWindow Window;
        public readonly TimeSpan BucketDuration;
        public readonly long BucketTicks;
        public readonly int Capacity;
        public readonly BucketSlot[] Slots;

        // Index of newest slot and count of populated slots (<= Capacity)
        public int HeadIndex;
        public int Count;
        public DateTime LastBucketStartUtc;

        public WindowRing(TimeMachineWindow window, TimeSpan bucketDuration, int capacity)
        {
            Window = window;
            BucketDuration = bucketDuration;
            BucketTicks = bucketDuration.Ticks;
            Capacity = capacity;
            Slots = new BucketSlot[capacity];
            for (int i = 0; i < capacity; i++)
            {
                Slots[i] = new BucketSlot();
            }
        }

        public BucketSlot AddSample(
            DateTime utcTimestamp,
            long rxBytes,
            long txBytes,
            IReadOnlyList<TimelineSeriesSlice>? slices)
        {
            long bucketTicks = (utcTimestamp.Ticks / BucketTicks) * BucketTicks;
            var bucketStart = new DateTime(bucketTicks, DateTimeKind.Utc);

            if (Count == 0)
            {
                HeadIndex = 0;
                Count = 1;
                LastBucketStartUtc = bucketStart;
                Slots[0].Reset(bucketStart);
                Slots[0].Add(rxBytes, txBytes, slices);
                return Slots[0];
            }

            if (bucketStart > LastBucketStartUtc)
            {
                long stepCount = (bucketStart.Ticks - LastBucketStartUtc.Ticks) / BucketTicks;
                if (stepCount >= Capacity)
                {
                    // Time jumped past full ring capacity: reset ring anchored at current bucket
                    HeadIndex = 0;
                    Count = 1;
                    LastBucketStartUtc = bucketStart;
                    Slots[0].Reset(bucketStart);
                    Slots[0].Add(rxBytes, txBytes, slices);
                    return Slots[0];
                }

                for (long s = 1; s <= stepCount; s++)
                {
                    HeadIndex = (HeadIndex + 1) % Capacity;
                    if (Count < Capacity) Count++;
                    var intermediateStart = new DateTime(LastBucketStartUtc.Ticks + (s * BucketTicks), DateTimeKind.Utc);
                    Slots[HeadIndex].Reset(intermediateStart);
                }

                LastBucketStartUtc = bucketStart;
                Slots[HeadIndex].Add(rxBytes, txBytes, slices);
                return Slots[HeadIndex];
            }

            long stepsBack = (LastBucketStartUtc.Ticks - bucketStart.Ticks) / BucketTicks;
            if (stepsBack >= 0 && stepsBack < Count)
            {
                int targetIndex = (HeadIndex - (int)stepsBack + Capacity) % Capacity;
                Slots[targetIndex].Add(rxBytes, txBytes, slices);
                return Slots[targetIndex];
            }

            // Older than window retention; ignore
            return Slots[HeadIndex];
        }

        public IEnumerable<BucketSlot> EnumerateOldestToNewest()
        {
            if (Count == 0) yield break;

            int oldest = (HeadIndex - Count + 1 + Capacity) % Capacity;
            for (int i = 0; i < Count; i++)
            {
                yield return Slots[(oldest + i) % Capacity];
            }
        }
    }

    private readonly Lock _sync = new();
    private readonly WindowRing _liveRing;
    private readonly Dictionary<TimeMachineWindow, WindowRing> _rings;
    private readonly LinkedList<MonitorAlertRecord> _alerts = new();

    private readonly double _spikeMultiplier;
    private readonly long _minSpikeThresholdBps;
    private readonly int _minBaselineSamples;
    private readonly TimeSpan _spikeCooldown;
    private DateTime _lastSpikeAlertUtc = DateTime.MinValue;

    public event Action<MonitorAlertRecord>? AlertRaised;

    public TrafficTimelineStore(
        double spikeMultiplier = 3.0,
        long minSpikeThresholdBps = 128 * 1024,
        int minBaselineSamples = 8,
        TimeSpan? spikeCooldown = null)
    {
        _spikeMultiplier = Math.Max(1.5, spikeMultiplier);
        _minSpikeThresholdBps = Math.Max(1, minSpikeThresholdBps);
        _minBaselineSamples = Math.Max(3, minBaselineSamples);
        _spikeCooldown = spikeCooldown ?? TimeSpan.FromSeconds(15);

        _liveRing = new WindowRing(TimeMachineWindow.Live, TimeSpan.FromSeconds(1), 120);
        _rings = new Dictionary<TimeMachineWindow, WindowRing>
        {
            [TimeMachineWindow.Live] = _liveRing,
            [TimeMachineWindow.FiveMinutes] = new WindowRing(TimeMachineWindow.FiveMinutes, TimeSpan.FromSeconds(5), 60),
            [TimeMachineWindow.OneHour] = new WindowRing(TimeMachineWindow.OneHour, TimeSpan.FromMinutes(1), 60),
            [TimeMachineWindow.TwentyFourHours] = new WindowRing(TimeMachineWindow.TwentyFourHours, TimeSpan.FromMinutes(15), 96),
            [TimeMachineWindow.SevenDays] = new WindowRing(TimeMachineWindow.SevenDays, TimeSpan.FromHours(2), 84)
        };
    }

    public void RecordSample(
        DateTime timestampUtc,
        long rxBytes,
        long txBytes,
        IReadOnlyList<TimelineSeriesSlice>? slices = null)
    {
        DateTime utc = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : timestampUtc.ToUniversalTime();

        rxBytes = Math.Max(0, rxBytes);
        txBytes = Math.Max(0, txBytes);

        MonitorAlertRecord? spikeAlert = null;

        lock (_sync)
        {
            BucketSlot currentLive = _liveRing.AddSample(utc, rxBytes, txBytes, slices);
            foreach (KeyValuePair<TimeMachineWindow, WindowRing> kv in _rings)
            {
                if (kv.Key != TimeMachineWindow.Live)
                {
                    kv.Value.AddSample(utc, rxBytes, txBytes, slices);
                }
            }

            spikeAlert = TryDetectSpikeLocked(utc, currentLive);
        }

        if (spikeAlert is not null)
        {
            AlertRaised?.Invoke(spikeAlert);
        }
    }

    public void RecordFromSnapshots(
        long totalRxDeltaBytes,
        long totalTxDeltaBytes,
        AppTrafficSnapshot? appSnapshot,
        DateTime? timestampUtc = null)
    {
        DateTime utc = timestampUtc ?? DateTime.UtcNow;
        List<TimelineSeriesSlice>? slices = null;

        if (appSnapshot is not null && appSnapshot.Applications.Count > 0)
        {
            slices = new List<TimelineSeriesSlice>(Math.Min(8, appSnapshot.Applications.Count));
            long accountedRx = 0;
            long accountedTx = 0;

            for (int i = 0; i < appSnapshot.Applications.Count && i < 6; i++)
            {
                AppTrafficEntry app = appSnapshot.Applications[i];
                if (app.DownloadBps <= 0 && app.UploadBps <= 0) continue;

                slices.Add(new TimelineSeriesSlice(app.ProcessName, app.DownloadBps, app.UploadBps));
                accountedRx += app.DownloadBps;
                accountedTx += app.UploadBps;
            }

            long remRx = Math.Max(0, totalRxDeltaBytes - accountedRx);
            long remTx = Math.Max(0, totalTxDeltaBytes - accountedTx);
            if ((remRx > 0 || remTx > 0) && slices.Count > 0)
            {
                slices.Add(new TimelineSeriesSlice("Other", remRx, remTx, IsOther: true));
            }
        }

        RecordSample(utc, totalRxDeltaBytes, totalTxDeltaBytes, slices);
    }

    public MonitorAlertRecord RecordAlert(MonitorAlertRecord alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        MonitorAlertRecord normalized = alert.Id <= 0
            ? alert with { Id = MonitorAlertIdGenerator.Next() }
            : alert;

        lock (_sync)
        {
            AppendAlertLocked(normalized);
        }

        AlertRaised?.Invoke(normalized);
        return normalized;
    }

    public MonitorAlertRecord RecordAlert(
        MonitorAlertKind kind,
        MonitorAlertSeverity severity,
        string title,
        string description,
        string subjectName = "",
        string targetDetail = "",
        DateTime? timestampUtc = null)
    {
        var alert = new MonitorAlertRecord(
            MonitorAlertIdGenerator.Next(),
            timestampUtc ?? DateTime.UtcNow,
            kind,
            severity,
            title,
            description,
            subjectName,
            targetDetail);
        return RecordAlert(alert);
    }

    public MonitorAlertRecord RecordArpSpoofAlert(ArpSecurityAlert arpAlert)
    {
        return RecordAlert(
            MonitorAlertKind.ArpSpoof,
            MonitorAlertSeverity.Critical,
            $"ARP Spoof Alert ({arpAlert.TargetIp})",
            arpAlert.Description,
            arpAlert.OffendingMac.ToString(),
            arpAlert.TargetIp.ToString(),
            arpAlert.TimestampUtc);
    }

    public MonitorAlertRecord RecordNewLanDeviceAlert(NetworkDevice device, DateTime? timestampUtc = null)
    {
        return RecordAlert(
            MonitorAlertKind.NewLanDevice,
            MonitorAlertSeverity.Info,
            $"New LAN Device: {device.DisplayName}",
            $"Discovered {device.Vendor} ({device.Ip} / {device.Mac}) on the local network.",
            device.DisplayName,
            device.Ip.ToString(),
            timestampUtc);
    }

    public MonitorAlertRecord RecordDnsAnomalyAlert(
        string domain,
        string sourceHost,
        string reason,
        DateTime? timestampUtc = null)
    {
        return RecordAlert(
            MonitorAlertKind.DnsAnomaly,
            MonitorAlertSeverity.Warning,
            $"DNS Anomaly: {domain}",
            $"{sourceHost}: {reason}",
            domain,
            sourceHost,
            timestampUtc);
    }

    public IReadOnlyList<MonitorAlertRecord> GetAlerts(int maxCount = 100)
    {
        lock (_sync)
        {
            int take = Math.Min(maxCount, _alerts.Count);
            var list = new List<MonitorAlertRecord>(take);
            for (LinkedListNode<MonitorAlertRecord>? node = _alerts.Last; node is not null && list.Count < take; node = node.Previous)
            {
                list.Add(node.Value);
            }
            return list;
        }
    }

    public bool AcknowledgeAlert(long alertId)
    {
        lock (_sync)
        {
            for (LinkedListNode<MonitorAlertRecord>? node = _alerts.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == alertId)
                {
                    node.Value = node.Value with { IsAcknowledged = true };
                    return true;
                }
            }
            return false;
        }
    }

    public void AcknowledgeAllAlerts()
    {
        lock (_sync)
        {
            for (LinkedListNode<MonitorAlertRecord>? node = _alerts.First; node is not null; node = node.Next)
            {
                if (!node.Value.IsAcknowledged)
                {
                    node.Value = node.Value with { IsAcknowledged = true };
                }
            }
        }
    }

    public TrafficTimelineSnapshot GetSnapshot(TimeMachineWindow window, int topSliceCount = 4)
    {
        topSliceCount = Math.Clamp(topSliceCount, 1, 8);

        lock (_sync)
        {
            WindowRing ring = _rings[window];
            double bucketSeconds = ring.BucketDuration.TotalSeconds;

            if (ring.Count == 0)
            {
                return new TrafficTimelineSnapshot(
                    window,
                    DateTime.UtcNow,
                    [],
                    [],
                    [],
                    0.0,
                    0L,
                    0L);
            }

            // 1. Collect raw slots and determine top-N series labels across the window
            var rawSlots = new List<BucketSlot>(ring.Count);
            var labelTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (BucketSlot slot in ring.EnumerateOldestToNewest())
            {
                rawSlots.Add(slot);
                foreach (KeyValuePair<string, (long DownloadBytes, long UploadBytes)> kv in slot.Slices)
                {
                    long sum = kv.Value.DownloadBytes + kv.Value.UploadBytes;
                    if (sum <= 0) continue;
                    labelTotals.TryGetValue(kv.Key, out long prev);
                    labelTotals[kv.Key] = prev + sum;
                }
            }

            var sortedLabels = new List<KeyValuePair<string, long>>(labelTotals);
            sortedLabels.Sort(static (a, b) => b.Value.CompareTo(a.Value));

            var topLabels = new List<string>(topSliceCount + 1);
            var topLabelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sortedLabels.Count && i < topSliceCount; i++)
            {
                if (!string.Equals(sortedLabels[i].Key, "Other", StringComparison.OrdinalIgnoreCase))
                {
                    topLabels.Add(sortedLabels[i].Key);
                    topLabelSet.Add(sortedLabels[i].Key);
                }
            }

            DateTime windowStartUtc = rawSlots[0].StartUtc;
            DateTime windowEndUtc = rawSlots[^1].StartUtc + ring.BucketDuration;

            var alertsInWindow = new List<MonitorAlertRecord>();
            foreach (MonitorAlertRecord alert in _alerts)
            {
                if (alert.TimestampUtc >= windowStartUtc && alert.TimestampUtc <= windowEndUtc)
                {
                    alertsInWindow.Add(alert);
                }
            }

            // 2. Build immutable TimelineBucketSnapshot list with consistent top-N slices + alert markers
            bool hasAnyOther = false;
            var buckets = new List<TimelineBucketSnapshot>(rawSlots.Count);
            double peakBps = 0.0;
            long grandRx = 0L;
            long grandTx = 0L;

            for (int i = 0; i < rawSlots.Count; i++)
            {
                BucketSlot slot = rawSlots[i];
                DateTime bStart = slot.StartUtc;
                DateTime bEnd = bStart + ring.BucketDuration;

                double rxBps = slot.DownloadBytes / bucketSeconds;
                double txBps = slot.UploadBytes / bucketSeconds;
                double totBps = rxBps + txBps;
                if (totBps > peakBps) peakBps = totBps;

                grandRx += slot.DownloadBytes;
                grandTx += slot.UploadBytes;

                var bucketSlices = new List<TimelineSeriesSlice>(topLabels.Count + 1);
                long slicedRx = 0;
                long slicedTx = 0;

                for (int t = 0; t < topLabels.Count; t++)
                {
                    string lbl = topLabels[t];
                    if (slot.Slices.TryGetValue(lbl, out var val))
                    {
                        bucketSlices.Add(new TimelineSeriesSlice(lbl, val.DownloadBytes, val.UploadBytes));
                        slicedRx += val.DownloadBytes;
                        slicedTx += val.UploadBytes;
                    }
                    else
                    {
                        bucketSlices.Add(new TimelineSeriesSlice(lbl, 0, 0));
                    }
                }

                long otherRx = Math.Max(0, slot.DownloadBytes - slicedRx);
                long otherTx = Math.Max(0, slot.UploadBytes - slicedTx);
                if (otherRx > 0 || otherTx > 0)
                {
                    hasAnyOther = true;
                    bucketSlices.Add(new TimelineSeriesSlice("Other", otherRx, otherTx, IsOther: true));
                }

                List<MonitorAlertRecord> bucketAlerts = [];
                for (int a = 0; a < alertsInWindow.Count; a++)
                {
                    MonitorAlertRecord al = alertsInWindow[a];
                    bool inBucket = al.TimestampUtc >= bStart &&
                                    (al.TimestampUtc < bEnd || (i == rawSlots.Count - 1 && al.TimestampUtc == bEnd));
                    if (inBucket)
                    {
                        bucketAlerts.Add(al);
                    }
                }

                buckets.Add(new TimelineBucketSnapshot(
                    bStart,
                    bEnd,
                    FormatBucketLabel(bStart, window),
                    slot.DownloadBytes,
                    slot.UploadBytes,
                    rxBps,
                    txBps,
                    bucketSlices,
                    bucketAlerts));
            }

            if (hasAnyOther && !topLabels.Contains("Other", StringComparer.OrdinalIgnoreCase))
            {
                topLabels.Add("Other");
            }

            return new TrafficTimelineSnapshot(
                window,
                DateTime.UtcNow,
                buckets,
                topLabels,
                alertsInWindow,
                peakBps,
                grandRx,
                grandTx);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (WindowRing ring in _rings.Values)
            {
                ring.Count = 0;
                ring.HeadIndex = 0;
                ring.LastBucketStartUtc = default;
                for (int i = 0; i < ring.Slots.Length; i++)
                {
                    ring.Slots[i].Reset(default);
                }
            }
            _alerts.Clear();
            _lastSpikeAlertUtc = DateTime.MinValue;
        }
    }

    private MonitorAlertRecord? TryDetectSpikeLocked(DateTime utcTimestamp, BucketSlot currentBucket)
    {
        if (_liveRing.Count <= _minBaselineSamples)
        {
            return null;
        }

        long currentTotalBps = currentBucket.TotalBytes; // 1s live bucket -> bytes == B/s
        if (currentTotalBps < _minSpikeThresholdBps)
        {
            return null;
        }

        if ((utcTimestamp - _lastSpikeAlertUtc) < _spikeCooldown)
        {
            return null;
        }

        // Compute rolling average over up to 30 prior 1s buckets (excluding current bucket)
        int availablePrior = Math.Min(30, _liveRing.Count - 1);
        long sumBaseline = 0;
        int counted = 0;

        for (int back = 1; back <= availablePrior; back++)
        {
            int idx = (_liveRing.HeadIndex - back + _liveRing.Capacity) % _liveRing.Capacity;
            BucketSlot prior = _liveRing.Slots[idx];
            sumBaseline += prior.TotalBytes;
            counted++;
        }

        if (counted < _minBaselineSamples)
        {
            return null;
        }

        double avgBaselineBps = (double)sumBaseline / counted;
        if (avgBaselineBps <= 0.0)
        {
            return null;
        }

        if (currentTotalBps < avgBaselineBps * _spikeMultiplier)
        {
            return null;
        }

        _lastSpikeAlertUtc = utcTimestamp;

        string topContributor = string.Empty;
        long topBytes = 0;
        foreach (KeyValuePair<string, (long DownloadBytes, long UploadBytes)> kv in currentBucket.Slices)
        {
            long tot = kv.Value.DownloadBytes + kv.Value.UploadBytes;
            if (tot > topBytes)
            {
                topBytes = tot;
                topContributor = kv.Key;
            }
        }

        double ratio = currentTotalBps / avgBaselineBps;
        string subject = string.IsNullOrEmpty(topContributor) ? "Network" : topContributor;
        string desc = string.IsNullOrEmpty(topContributor)
            ? $"Live throughput jumped {ratio:F1}x above rolling baseline ({BandwidthFormatter.FormatRate(currentTotalBps)} vs avg {BandwidthFormatter.FormatRate(avgBaselineBps)})."
            : $"Live throughput jumped {ratio:F1}x above rolling baseline ({BandwidthFormatter.FormatRate(currentTotalBps)} vs avg {BandwidthFormatter.FormatRate(avgBaselineBps)}), led by {topContributor}.";

        var alert = new MonitorAlertRecord(
            MonitorAlertIdGenerator.Next(),
            utcTimestamp,
            MonitorAlertKind.TrafficSpike,
            MonitorAlertSeverity.Warning,
            $"Traffic Spike ({ratio:F1}x)",
            desc,
            subject,
            BandwidthFormatter.FormatRate(currentTotalBps));

        AppendAlertLocked(alert);
        return alert;
    }

    private void AppendAlertLocked(MonitorAlertRecord alert)
    {
        if (_alerts.Count >= MaxStoredAlerts)
        {
            _alerts.RemoveFirst();
        }
        _alerts.AddLast(alert);
    }

    private static string FormatBucketLabel(DateTime utcTime, TimeMachineWindow window)
    {
        DateTime local = utcTime.ToLocalTime();
        return window switch
        {
            TimeMachineWindow.Live or TimeMachineWindow.FiveMinutes => local.ToString("HH:mm:ss"),
            TimeMachineWindow.OneHour or TimeMachineWindow.TwentyFourHours => local.ToString("HH:mm"),
            TimeMachineWindow.SevenDays => local.ToString("ddd HH:mm"),
            _ => local.ToString("HH:mm:ss")
        };
    }
}
