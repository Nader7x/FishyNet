using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishyNet.Features.AppMonitor;

public sealed class AppDailyBucketRecord
{
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd"
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public long PeakDownloadBps { get; set; }
    public long PeakUploadBps { get; set; }
    public List<string> ProtocolTags { get; set; } = [];
    public List<string> ContactedHosts { get; set; } = [];
    public string FirstSeenUtc { get; set; } = string.Empty;
    public string LastSeenUtc { get; set; } = string.Empty;
}

public sealed class AppHistoryLedgerEntry
{
    public string AppKey { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public List<AppDailyBucketRecord> DailyBuckets { get; set; } = [];
}

public sealed class WebsiteDailyBucketRecord
{
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd"
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public long HitCount { get; set; }
    public string LastSeenUtc { get; set; } = string.Empty;
}

public sealed class WebsiteHistoryLedgerEntry
{
    public string EntryKey { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ResolvedIp { get; set; } = string.Empty;
    public string ProtocolPort { get; set; } = string.Empty;
    public string SourceHost { get; set; } = string.Empty;
    public string SourceHostIp { get; set; } = string.Empty;
    public bool IsLocalHost { get; set; }
    public string AppName { get; set; } = string.Empty;
    public bool IsSinkholed { get; set; }
    public List<WebsiteDailyBucketRecord> DailyBuckets { get; set; } = [];
}

public sealed class AppMonitorHistoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<AppHistoryLedgerEntry> Applications { get; set; } = [];
    public List<WebsiteHistoryLedgerEntry> Websites { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppMonitorHistoryDocument))]
internal partial class AppMonitorHistoryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Thread-safe, Native AOT-safe persistent daily history store for per-application and
/// per-website/domain traffic telemetry, supporting monotonic delta tracking and calendar date-range queries.
/// </summary>
public sealed class AppMonitorHistoryStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private const int MaxRetentionDays = 365;
    private const int MaxContactedHostsPerBucket = 24;

    private readonly string _filePath;
    private readonly Lock _sync = new();
    private AppMonitorHistoryDocument _doc = new();

    private readonly Dictionary<string, (long DownloadBytes, long UploadBytes)> _lastAppSessionTotals =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (long DownloadBytes, long UploadBytes, long HitCount)> _lastWebsiteSessionTotals =
        new(StringComparer.OrdinalIgnoreCase);

    public AppMonitorHistoryStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _filePath = customFilePath;
        }
        else
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FishyNet");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "app-monitor-history.json");
        }

        Load();
    }

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    AppMonitorHistoryDocument? loaded = JsonSerializer.Deserialize(
                        json, AppMonitorHistoryJsonContext.Default.AppMonitorHistoryDocument);
                    if (loaded is not null)
                    {
                        _doc = loaded;
                    }
                }
            }
            catch
            {
                _doc = new AppMonitorHistoryDocument();
            }
        }
    }

    /// <summary>
    /// Atomically persists the current history document to disk.
    /// </summary>
    public void Flush()
    {
        lock (_sync)
        {
            SaveUnlocked();
        }
    }

    /// <summary>
    /// Clears all persisted application and website history and resets session delta watermarks.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _doc.Applications.Clear();
            _doc.Websites.Clear();
            _lastAppSessionTotals.Clear();
            _lastWebsiteSessionTotals.Clear();
            SaveUnlocked();
        }
    }

    /// <summary>
    /// Records a point-in-time snapshot of application and website traffic.
    /// Computes positive deltas from previously seen session totals so repeated ticks never double-count,
    /// and resets cleanly if a tracker or application restarts with lower cumulative totals.
    /// </summary>
    public void RecordSample(DateTime utcNow, AppTrafficSnapshot? apps, WebsiteTrafficSnapshot? websites)
    {
        DateTime utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        DateOnly date = DateOnly.FromDateTime(utc);
        string dateKey = date.ToString(DateFormat, CultureInfo.InvariantCulture);

        lock (_sync)
        {
            if (apps is not null)
            {
                foreach (AppTrafficEntry entry in apps.Applications)
                {
                    RecordAppEntryUnlocked(entry, dateKey, utc);
                }
            }

            if (websites is not null)
            {
                foreach (WebsiteTrafficEntry entry in websites.Websites)
                {
                    RecordWebsiteEntryUnlocked(entry, dateKey, utc);
                }
            }
        }
    }

    /// <summary>
    /// Queries aggregated per-application traffic summed across all days in [fromDate, toDate] (inclusive).
    /// </summary>
    public IReadOnlyList<AppTrafficEntry> QueryApplications(DateOnly fromDate, DateOnly toDate)
    {
        if (toDate < fromDate)
        {
            return [];
        }

        var results = new List<AppTrafficEntry>();

        lock (_sync)
        {
            foreach (AppHistoryLedgerEntry ledger in _doc.Applications)
            {
                long totalRx = 0;
                long totalTx = 0;
                long peakRxBps = 0;
                long peakTxBps = 0;
                DateTime firstSeen = DateTime.MaxValue;
                DateTime lastSeen = DateTime.MinValue;
                HashSet<string>? tags = null;
                HashSet<string>? hosts = null;
                bool matchedAnyBucket = false;

                foreach (AppDailyBucketRecord bucket in ledger.DailyBuckets)
                {
                    if (!TryParseBucketDate(bucket.Date, out DateOnly d) || d < fromDate || d > toDate)
                    {
                        continue;
                    }

                    matchedAnyBucket = true;
                    totalRx += bucket.DownloadBytes;
                    totalTx += bucket.UploadBytes;
                    if (bucket.PeakDownloadBps > peakRxBps) peakRxBps = bucket.PeakDownloadBps;
                    if (bucket.PeakUploadBps > peakTxBps) peakTxBps = bucket.PeakUploadBps;

                    if (TryParseUtc(bucket.FirstSeenUtc, out DateTime bFirst) && bFirst < firstSeen)
                    {
                        firstSeen = bFirst;
                    }

                    if (TryParseUtc(bucket.LastSeenUtc, out DateTime bLast) && bLast > lastSeen)
                    {
                        lastSeen = bLast;
                    }

                    if (bucket.ProtocolTags.Count > 0)
                    {
                        tags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string tag in bucket.ProtocolTags)
                        {
                            if (!string.IsNullOrWhiteSpace(tag)) tags.Add(tag);
                        }
                    }

                    if (bucket.ContactedHosts.Count > 0)
                    {
                        hosts ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string host in bucket.ContactedHosts)
                        {
                            if (!string.IsNullOrWhiteSpace(host) && hosts.Count < MaxContactedHostsPerBucket)
                            {
                                hosts.Add(host);
                            }
                        }
                    }
                }

                if (!matchedAnyBucket)
                {
                    continue;
                }

                if (firstSeen == DateTime.MaxValue) firstSeen = DateTime.UtcNow;
                if (lastSeen == DateTime.MinValue) lastSeen = firstSeen;

                results.Add(new AppTrafficEntry(
                    AppKey: ledger.AppKey,
                    ProcessId: 0,
                    ProcessName: ledger.ProcessName,
                    DisplayName: ledger.DisplayName,
                    ExePath: ledger.ExePath,
                    Publisher: ledger.Publisher,
                    Icon: null,
                    DownloadBps: 0,
                    UploadBps: 0,
                    TotalDownloadBytes: totalRx,
                    TotalUploadBytes: totalTx,
                    ProtocolTags: tags is not null ? tags.ToList() : [],
                    ContactedHosts: hosts is not null ? hosts.ToList() : [],
                    ActiveConnections: 0,
                    IsNewFirstAccess: false,
                    IsFirewallBlocked: false,
                    FirstSeenUtc: firstSeen,
                    LastSeenUtc: lastSeen));
            }
        }

        results.Sort(static (a, b) =>
        {
            int bytesCmp = b.TotalBytes.CompareTo(a.TotalBytes);
            return bytesCmp != 0 ? bytesCmp : b.LastSeenUtc.CompareTo(a.LastSeenUtc);
        });

        return results;
    }

    /// <summary>
    /// Queries aggregated per-website/domain traffic summed across all days in [fromDate, toDate] (inclusive).
    /// </summary>
    public IReadOnlyList<WebsiteTrafficEntry> QueryWebsites(
        DateOnly fromDate,
        DateOnly toDate,
        bool? localOnlyFilter = null)
    {
        if (toDate < fromDate)
        {
            return [];
        }

        var results = new List<WebsiteTrafficEntry>();

        lock (_sync)
        {
            foreach (WebsiteHistoryLedgerEntry ledger in _doc.Websites)
            {
                if (localOnlyFilter.HasValue && ledger.IsLocalHost != localOnlyFilter.Value)
                {
                    continue;
                }

                long totalRx = 0;
                long totalTx = 0;
                long totalHits = 0;
                DateTime lastSeen = DateTime.MinValue;
                bool matchedAnyBucket = false;

                foreach (WebsiteDailyBucketRecord bucket in ledger.DailyBuckets)
                {
                    if (!TryParseBucketDate(bucket.Date, out DateOnly d) || d < fromDate || d > toDate)
                    {
                        continue;
                    }

                    matchedAnyBucket = true;
                    totalRx += bucket.DownloadBytes;
                    totalTx += bucket.UploadBytes;
                    totalHits += bucket.HitCount;

                    if (TryParseUtc(bucket.LastSeenUtc, out DateTime bLast) && bLast > lastSeen)
                    {
                        lastSeen = bLast;
                    }
                }

                if (!matchedAnyBucket)
                {
                    continue;
                }

                if (lastSeen == DateTime.MinValue) lastSeen = DateTime.UtcNow;

                results.Add(new WebsiteTrafficEntry(
                    EntryKey: ledger.EntryKey,
                    Domain: ledger.Domain,
                    ResolvedIp: ledger.ResolvedIp,
                    ProtocolPort: ledger.ProtocolPort,
                    SourceHost: ledger.SourceHost,
                    SourceHostIp: ledger.SourceHostIp,
                    IsLocalHost: ledger.IsLocalHost,
                    AppName: ledger.AppName,
                    DownloadBytes: totalRx,
                    UploadBytes: totalTx,
                    HitCount: totalHits,
                    LastSeenUtc: lastSeen,
                    IsSinkholed: ledger.IsSinkholed));
            }
        }

        results.Sort(static (a, b) =>
        {
            int bytesCmp = b.TotalBytes.CompareTo(a.TotalBytes);
            return bytesCmp != 0 ? bytesCmp : b.LastSeenUtc.CompareTo(a.LastSeenUtc);
        });

        return results;
    }

    /// <summary>
    /// Returns the inclusive (MinDate, MaxDate) bounds of all persisted daily buckets,
    /// or (DateOnly.MinValue, DateOnly.MinValue) when the store has no records.
    /// </summary>
    public (DateOnly MinDate, DateOnly MaxDate) GetAvailableDateBounds()
    {
        DateOnly minDate = DateOnly.MaxValue;
        DateOnly maxDate = DateOnly.MinValue;
        bool hasAny = false;

        lock (_sync)
        {
            foreach (AppHistoryLedgerEntry app in _doc.Applications)
            {
                foreach (AppDailyBucketRecord bucket in app.DailyBuckets)
                {
                    if (TryParseBucketDate(bucket.Date, out DateOnly d))
                    {
                        hasAny = true;
                        if (d < minDate) minDate = d;
                        if (d > maxDate) maxDate = d;
                    }
                }
            }

            foreach (WebsiteHistoryLedgerEntry site in _doc.Websites)
            {
                foreach (WebsiteDailyBucketRecord bucket in site.DailyBuckets)
                {
                    if (TryParseBucketDate(bucket.Date, out DateOnly d))
                    {
                        hasAny = true;
                        if (d < minDate) minDate = d;
                        if (d > maxDate) maxDate = d;
                    }
                }
            }
        }

        return hasAny ? (minDate, maxDate) : (DateOnly.MinValue, DateOnly.MinValue);
    }

    private void RecordAppEntryUnlocked(AppTrafficEntry entry, string dateKey, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(entry.AppKey))
        {
            return;
        }

        long currentRx = Math.Max(0L, entry.TotalDownloadBytes);
        long currentTx = Math.Max(0L, entry.TotalUploadBytes);

        long deltaRx;
        long deltaTx;
        if (_lastAppSessionTotals.TryGetValue(entry.AppKey, out var prev))
        {
            deltaRx = currentRx >= prev.DownloadBytes ? currentRx - prev.DownloadBytes : currentRx;
            deltaTx = currentTx >= prev.UploadBytes ? currentTx - prev.UploadBytes : currentTx;
        }
        else
        {
            deltaRx = currentRx;
            deltaTx = currentTx;
        }

        _lastAppSessionTotals[entry.AppKey] = (currentRx, currentTx);

        AppHistoryLedgerEntry? ledger = _doc.Applications.FirstOrDefault(
            a => string.Equals(a.AppKey, entry.AppKey, StringComparison.OrdinalIgnoreCase));

        if (ledger is null)
        {
            if (deltaRx == 0 && deltaTx == 0 && entry.DownloadBps == 0 && entry.UploadBps == 0)
            {
                return;
            }

            ledger = new AppHistoryLedgerEntry
            {
                AppKey = entry.AppKey,
                ProcessName = entry.ProcessName,
                DisplayName = entry.DisplayName,
                ExePath = entry.ExePath,
                Publisher = entry.Publisher
            };
            _doc.Applications.Add(ledger);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(entry.ProcessName)) ledger.ProcessName = entry.ProcessName;
            if (!string.IsNullOrWhiteSpace(entry.DisplayName)) ledger.DisplayName = entry.DisplayName;
            if (!string.IsNullOrWhiteSpace(entry.ExePath)) ledger.ExePath = entry.ExePath;
            if (!string.IsNullOrWhiteSpace(entry.Publisher)) ledger.Publisher = entry.Publisher;
        }

        AppDailyBucketRecord? bucket = ledger.DailyBuckets.FirstOrDefault(b => b.Date == dateKey);
        if (bucket is null)
        {
            if (deltaRx == 0 && deltaTx == 0 && entry.DownloadBps == 0 && entry.UploadBps == 0)
            {
                return;
            }

            bucket = new AppDailyBucketRecord
            {
                Date = dateKey,
                FirstSeenUtc = (entry.FirstSeenUtc != default ? entry.FirstSeenUtc : utcNow).ToString("O", CultureInfo.InvariantCulture),
                LastSeenUtc = (entry.LastSeenUtc != default ? entry.LastSeenUtc : utcNow).ToString("O", CultureInfo.InvariantCulture)
            };
            ledger.DailyBuckets.Add(bucket);

            if (ledger.DailyBuckets.Count > MaxRetentionDays)
            {
                ledger.DailyBuckets.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
                ledger.DailyBuckets.RemoveRange(0, ledger.DailyBuckets.Count - MaxRetentionDays);
            }
        }

        if (deltaRx > 0) bucket.DownloadBytes += deltaRx;
        if (deltaTx > 0) bucket.UploadBytes += deltaTx;
        if (entry.DownloadBps > bucket.PeakDownloadBps) bucket.PeakDownloadBps = entry.DownloadBps;
        if (entry.UploadBps > bucket.PeakUploadBps) bucket.PeakUploadBps = entry.UploadBps;

        DateTime effectiveLast = entry.LastSeenUtc != default ? entry.LastSeenUtc : utcNow;
        if (!TryParseUtc(bucket.LastSeenUtc, out DateTime existingLast) || effectiveLast > existingLast)
        {
            bucket.LastSeenUtc = effectiveLast.ToString("O", CultureInfo.InvariantCulture);
        }

        MergeUniqueStrings(bucket.ProtocolTags, entry.ProtocolTags, int.MaxValue);
        MergeUniqueStrings(bucket.ContactedHosts, entry.ContactedHosts, MaxContactedHostsPerBucket);
    }

    private void RecordWebsiteEntryUnlocked(WebsiteTrafficEntry entry, string dateKey, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(entry.EntryKey))
        {
            return;
        }

        long currentRx = Math.Max(0L, entry.DownloadBytes);
        long currentTx = Math.Max(0L, entry.UploadBytes);
        long currentHits = Math.Max(0L, entry.HitCount);

        long deltaRx;
        long deltaTx;
        long deltaHits;
        if (_lastWebsiteSessionTotals.TryGetValue(entry.EntryKey, out var prev))
        {
            deltaRx = currentRx >= prev.DownloadBytes ? currentRx - prev.DownloadBytes : currentRx;
            deltaTx = currentTx >= prev.UploadBytes ? currentTx - prev.UploadBytes : currentTx;
            deltaHits = currentHits >= prev.HitCount ? currentHits - prev.HitCount : currentHits;
        }
        else
        {
            deltaRx = currentRx;
            deltaTx = currentTx;
            deltaHits = currentHits;
        }

        _lastWebsiteSessionTotals[entry.EntryKey] = (currentRx, currentTx, currentHits);

        WebsiteHistoryLedgerEntry? ledger = _doc.Websites.FirstOrDefault(
            w => string.Equals(w.EntryKey, entry.EntryKey, StringComparison.OrdinalIgnoreCase));

        if (ledger is null)
        {
            if (deltaRx == 0 && deltaTx == 0 && deltaHits == 0)
            {
                return;
            }

            ledger = new WebsiteHistoryLedgerEntry
            {
                EntryKey = entry.EntryKey,
                Domain = entry.Domain,
                ResolvedIp = entry.ResolvedIp,
                ProtocolPort = entry.ProtocolPort,
                SourceHost = entry.SourceHost,
                SourceHostIp = entry.SourceHostIp,
                IsLocalHost = entry.IsLocalHost,
                AppName = entry.AppName,
                IsSinkholed = entry.IsSinkholed
            };
            _doc.Websites.Add(ledger);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(entry.ResolvedIp)) ledger.ResolvedIp = entry.ResolvedIp;
            if (!string.IsNullOrWhiteSpace(entry.SourceHost)) ledger.SourceHost = entry.SourceHost;
            if (!string.IsNullOrWhiteSpace(entry.AppName)) ledger.AppName = entry.AppName;
            ledger.IsSinkholed = entry.IsSinkholed;
        }

        WebsiteDailyBucketRecord? bucket = ledger.DailyBuckets.FirstOrDefault(b => b.Date == dateKey);
        if (bucket is null)
        {
            if (deltaRx == 0 && deltaTx == 0 && deltaHits == 0)
            {
                return;
            }

            bucket = new WebsiteDailyBucketRecord
            {
                Date = dateKey,
                LastSeenUtc = (entry.LastSeenUtc != default ? entry.LastSeenUtc : utcNow).ToString("O", CultureInfo.InvariantCulture)
            };
            ledger.DailyBuckets.Add(bucket);

            if (ledger.DailyBuckets.Count > MaxRetentionDays)
            {
                ledger.DailyBuckets.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
                ledger.DailyBuckets.RemoveRange(0, ledger.DailyBuckets.Count - MaxRetentionDays);
            }
        }

        if (deltaRx > 0) bucket.DownloadBytes += deltaRx;
        if (deltaTx > 0) bucket.UploadBytes += deltaTx;
        if (deltaHits > 0) bucket.HitCount += deltaHits;

        DateTime effectiveLast = entry.LastSeenUtc != default ? entry.LastSeenUtc : utcNow;
        if (!TryParseUtc(bucket.LastSeenUtc, out DateTime existingLast) || effectiveLast > existingLast)
        {
            bucket.LastSeenUtc = effectiveLast.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(_doc, AppMonitorHistoryJsonContext.Default.AppMonitorHistoryDocument);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch
        {
            // Ignore IO errors on read-only filesystems
        }
    }

    private static void MergeUniqueStrings(List<string> target, IReadOnlyList<string> incoming, int maxCount)
    {
        if (incoming.Count == 0 || target.Count >= maxCount)
        {
            return;
        }

        foreach (string item in incoming)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(item);
                if (target.Count >= maxCount) break;
            }
        }
    }

    private static bool TryParseBucketDate(string dateStr, out DateOnly date) =>
        DateOnly.TryParseExact(dateStr, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryParseUtc(string isoUtc, out DateTime utc) =>
        DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc);
}
