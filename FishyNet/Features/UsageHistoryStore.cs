using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FishyNet.Core;
using FishyNet.Core.Formatting;
using FishyNet.Core.Packets;

namespace FishyNet.Features;

public enum UsageTimeRange
{
    Today = 0,
    Past7Days = 1,
    Past30Days = 2,
    AllTime = 3
}

public sealed class DailyBucketRecord
{
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd"
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public long PeakDownloadBps { get; set; }
    public long PeakUploadBps { get; set; }
}

public sealed class DeviceHistoryLedgerEntry
{
    public string MacAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LastKnownIp { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string LastSeenUtc { get; set; } = string.Empty;
    public List<DailyBucketRecord> DailyBuckets { get; set; } = [];
}

public sealed class UsageHistoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<DeviceHistoryLedgerEntry> Devices { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UsageHistoryDocument))]
internal partial class UsageHistoryJsonContext : JsonSerializerContext
{
}

public sealed record DailyUsagePoint(
    DateOnly Date,
    string Label,
    long DownloadBytes,
    long UploadBytes)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
    public string DownloadFormatted => BandwidthFormatter.FormatBytes(DownloadBytes);
    public string UploadFormatted => BandwidthFormatter.FormatBytes(UploadBytes);
    public string TotalFormatted => BandwidthFormatter.FormatBytes(TotalBytes);
}

public sealed record DeviceUsageSummary(
    string MacAddress,
    string DisplayName,
    string LastKnownIp,
    string Vendor,
    long DownloadBytes,
    long UploadBytes,
    long PeakDownloadBps,
    long PeakUploadBps,
    int ActiveDays,
    double SharePercentage,
    IReadOnlyList<DailyUsagePoint> DailySeries)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
    public string DownloadFormatted => BandwidthFormatter.FormatBytes(DownloadBytes);
    public string UploadFormatted => BandwidthFormatter.FormatBytes(UploadBytes);
    public string TotalFormatted => BandwidthFormatter.FormatBytes(TotalBytes);
    public string PeakDownloadFormatted => BandwidthFormatter.FormatRate(PeakDownloadBps);
    public string ShareFormatted => $"{SharePercentage:F1}%";
}

public sealed record UsageHistoryReport(
    UsageTimeRange Range,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    IReadOnlyList<DailyUsagePoint> NetworkDailySeries,
    IReadOnlyList<DeviceUsageSummary> DeviceSummaries)
{
    public long GrandTotalBytes => TotalDownloadBytes + TotalUploadBytes;
    public string TotalDownloadFormatted => BandwidthFormatter.FormatBytes(TotalDownloadBytes);
    public string TotalUploadFormatted => BandwidthFormatter.FormatBytes(TotalUploadBytes);
    public string GrandTotalFormatted => BandwidthFormatter.FormatBytes(GrandTotalBytes);
}

/// <summary>
/// Persistent MAC-indexed usage history store recording daily download/upload byte totals per device
/// and computing rollups for Today, Past 7 Days (Week), Past 30 Days (Month), and All-Time.
/// </summary>
public sealed class UsageHistoryStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _filePath;
    private readonly object _sync = new();
    private UsageHistoryDocument _doc = new();

    public UsageHistoryStore(string? customFilePath = null)
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
            _filePath = Path.Combine(dir, "usage_history.json");

            if (!File.Exists(_filePath))
            {
                string legacyPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SelfishNetV3", "usage_history.json");
                if (File.Exists(legacyPath))
                {
                    try { File.Copy(legacyPath, _filePath, overwrite: false); } catch { }
                }
            }
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
                    UsageHistoryDocument? loaded = JsonSerializer.Deserialize(json, UsageHistoryJsonContext.Default.UsageHistoryDocument);
                    if (loaded is not null)
                    {
                        _doc = loaded;
                    }
                }
            }
            catch
            {
                _doc = new UsageHistoryDocument();
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpPath = _filePath + ".tmp";
                string json = JsonSerializer.Serialize(_doc, UsageHistoryJsonContext.Default.UsageHistoryDocument);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            catch
            {
                // Ignore IO error on read-only filesystems
            }
        }
    }

    public void RecordUsageSample(
        MacAddress mac,
        string displayName,
        string ipAddress,
        string vendor,
        long receivedDeltaBytes,
        long sentDeltaBytes,
        long currentDownloadRateBps = 0,
        long currentUploadRateBps = 0,
        DateOnly? dateOverride = null)
    {
        if (mac.IsZero || mac.IsBroadcast) return;
        if (receivedDeltaBytes <= 0 && sentDeltaBytes <= 0 && currentDownloadRateBps <= 0 && currentUploadRateBps <= 0)
        {
            return;
        }

        DateOnly date = dateOverride ?? DateOnly.FromDateTime(DateTime.Now);
        string dateKey = date.ToString(DateFormat, CultureInfo.InvariantCulture);
        string macKey = mac.ToString();

        lock (_sync)
        {
            DeviceHistoryLedgerEntry? entry = _doc.Devices.FirstOrDefault(
                d => string.Equals(d.MacAddress, macKey, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                entry = new DeviceHistoryLedgerEntry
                {
                    MacAddress = macKey,
                    DisplayName = displayName,
                    LastKnownIp = ipAddress,
                    Vendor = vendor
                };
                _doc.Devices.Add(entry);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(displayName)) entry.DisplayName = displayName;
                if (!string.IsNullOrWhiteSpace(ipAddress)) entry.LastKnownIp = ipAddress;
                if (!string.IsNullOrWhiteSpace(vendor) && vendor != "Unknown Vendor") entry.Vendor = vendor;
            }

            entry.LastSeenUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            DailyBucketRecord? bucket = entry.DailyBuckets.FirstOrDefault(b => b.Date == dateKey);
            if (bucket is null)
            {
                bucket = new DailyBucketRecord { Date = dateKey };
                entry.DailyBuckets.Add(bucket);

                // Keep up to 365 daily buckets per device
                if (entry.DailyBuckets.Count > 365)
                {
                    entry.DailyBuckets.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
                    entry.DailyBuckets.RemoveRange(0, entry.DailyBuckets.Count - 365);
                }
            }

            if (receivedDeltaBytes > 0) bucket.DownloadBytes += receivedDeltaBytes;
            if (sentDeltaBytes > 0) bucket.UploadBytes += sentDeltaBytes;
            if (currentDownloadRateBps > bucket.PeakDownloadBps) bucket.PeakDownloadBps = currentDownloadRateBps;
            if (currentUploadRateBps > bucket.PeakUploadBps) bucket.PeakUploadBps = currentUploadRateBps;
        }
    }

    public long GetDeviceTodayTotalBytes(MacAddress mac, DateOnly? todayOverride = null)
    {
        DateOnly today = todayOverride ?? DateOnly.FromDateTime(DateTime.Now);
        string dateKey = today.ToString(DateFormat, CultureInfo.InvariantCulture);
        string macKey = mac.ToString();

        lock (_sync)
        {
            DeviceHistoryLedgerEntry? entry = _doc.Devices.FirstOrDefault(
                d => string.Equals(d.MacAddress, macKey, StringComparison.OrdinalIgnoreCase));
            DailyBucketRecord? bucket = entry?.DailyBuckets.FirstOrDefault(b => b.Date == dateKey);
            return bucket is not null ? bucket.DownloadBytes + bucket.UploadBytes : 0L;
        }
    }

    public (long TodayBytes, long WeekBytes, long MonthBytes) GetDeviceQuickTotals(MacAddress mac, DateOnly? referenceDate = null)
    {
        DateOnly today = referenceDate ?? DateOnly.FromDateTime(DateTime.Now);
        DateOnly weekStart = today.AddDays(-6);
        DateOnly monthStart = today.AddDays(-29);
        string macKey = mac.ToString();

        long todayTotal = 0;
        long weekTotal = 0;
        long monthTotal = 0;

        lock (_sync)
        {
            DeviceHistoryLedgerEntry? entry = _doc.Devices.FirstOrDefault(
                d => string.Equals(d.MacAddress, macKey, StringComparison.OrdinalIgnoreCase));

            if (entry is null) return (0, 0, 0);

            foreach (DailyBucketRecord bucket in entry.DailyBuckets)
            {
                if (!DateOnly.TryParseExact(bucket.Date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
                {
                    continue;
                }

                long sum = bucket.DownloadBytes + bucket.UploadBytes;
                if (d == today) todayTotal += sum;
                if (d >= weekStart && d <= today) weekTotal += sum;
                if (d >= monthStart && d <= today) monthTotal += sum;
            }
        }

        return (todayTotal, weekTotal, monthTotal);
    }

    public UsageHistoryReport GetUsageReport(UsageTimeRange range, DateOnly? referenceDate = null)
    {
        DateOnly today = referenceDate ?? DateOnly.FromDateTime(DateTime.Now);
        DateOnly startDate = range switch
        {
            UsageTimeRange.Today => today,
            UsageTimeRange.Past7Days => today.AddDays(-6),
            UsageTimeRange.Past30Days => today.AddDays(-29),
            _ => DateOnly.MinValue
        };

        int chartDays = range switch
        {
            UsageTimeRange.Today => 1,
            UsageTimeRange.Past7Days => 7,
            _ => 30
        };
        DateOnly chartStartDate = today.AddDays(-(chartDays - 1));

        var networkDailyTotals = new Dictionary<DateOnly, (long Rx, long Tx)>();
        for (int i = 0; i < chartDays; i++)
        {
            networkDailyTotals[chartStartDate.AddDays(i)] = (0L, 0L);
        }

        var rawDeviceRows = new List<(DeviceHistoryLedgerEntry Entry, long Rx, long Tx, long PeakRx, long PeakTx, int ActiveDays, List<DailyUsagePoint> Series)>();
        long grandRx = 0;
        long grandTx = 0;

        lock (_sync)
        {
            foreach (DeviceHistoryLedgerEntry entry in _doc.Devices)
            {
                long devRx = 0;
                long devTx = 0;
                long peakRx = 0;
                long peakTx = 0;
                int activeDays = 0;

                var deviceDayMap = new Dictionary<DateOnly, (long Rx, long Tx)>();
                for (int i = 0; i < chartDays; i++)
                {
                    deviceDayMap[chartStartDate.AddDays(i)] = (0L, 0L);
                }

                foreach (DailyBucketRecord b in entry.DailyBuckets)
                {
                    if (!DateOnly.TryParseExact(b.Date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
                    {
                        continue;
                    }

                    if (d >= startDate && d <= today)
                    {
                        devRx += b.DownloadBytes;
                        devTx += b.UploadBytes;
                        if (b.PeakDownloadBps > peakRx) peakRx = b.PeakDownloadBps;
                        if (b.PeakUploadBps > peakTx) peakTx = b.PeakUploadBps;
                        if (b.DownloadBytes + b.UploadBytes > 0) activeDays++;
                    }

                    if (d >= chartStartDate && d <= today)
                    {
                        deviceDayMap[d] = (b.DownloadBytes, b.UploadBytes);
                        var netPrev = networkDailyTotals[d];
                        networkDailyTotals[d] = (netPrev.Rx + b.DownloadBytes, netPrev.Tx + b.UploadBytes);
                    }
                }

                if (devRx > 0 || devTx > 0 || range == UsageTimeRange.AllTime)
                {
                    grandRx += devRx;
                    grandTx += devTx;

                    var devSeries = deviceDayMap
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new DailyUsagePoint(
                            kv.Key,
                            kv.Key.ToString("MMM dd", CultureInfo.InvariantCulture),
                            kv.Value.Rx,
                            kv.Value.Tx))
                        .ToList();

                    rawDeviceRows.Add((entry, devRx, devTx, peakRx, peakTx, activeDays, devSeries));
                }
            }
        }

        long grandTotal = Math.Max(1L, grandRx + grandTx);
        var summaries = rawDeviceRows
            .OrderByDescending(r => r.Rx + r.Tx)
            .Select(r => new DeviceUsageSummary(
                r.Entry.MacAddress,
                string.IsNullOrWhiteSpace(r.Entry.DisplayName) ? r.Entry.LastKnownIp : r.Entry.DisplayName,
                r.Entry.LastKnownIp,
                r.Entry.Vendor,
                r.Rx,
                r.Tx,
                r.PeakRx,
                r.PeakTx,
                r.ActiveDays,
                ((r.Rx + r.Tx) * 100.0) / grandTotal,
                r.Series))
            .ToList();

        var networkSeries = networkDailyTotals
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyUsagePoint(
                kv.Key,
                kv.Key.ToString("MMM dd", CultureInfo.InvariantCulture),
                kv.Value.Rx,
                kv.Value.Tx))
            .ToList();

        return new UsageHistoryReport(range, grandRx, grandTx, networkSeries, summaries);
    }

    public void ExportCsv(UsageTimeRange range, string destinationPath, DateOnly? referenceDate = null)
    {
        UsageHistoryReport report = GetUsageReport(range, referenceDate);
        var sb = new StringBuilder();
        sb.AppendLine("MAC Address,Device Name,Last IP,Vendor,Download Bytes,Download Friendly,Upload Bytes,Upload Friendly,Total Bytes,Total Friendly,Share %,Active Days");

        foreach (DeviceUsageSummary item in report.DeviceSummaries)
        {
            string safeName = item.DisplayName.Replace("\"", "\"\"");
            string safeVendor = item.Vendor.Replace("\"", "\"\"");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"\"{item.MacAddress}\",\"{safeName}\",\"{item.LastKnownIp}\",\"{safeVendor}\",{item.DownloadBytes},\"{item.DownloadFormatted}\",{item.UploadBytes},\"{item.UploadFormatted}\",{item.TotalBytes},\"{item.TotalFormatted}\",{item.SharePercentage:F2},{item.ActiveDays}");
        }

        File.WriteAllText(destinationPath, sb.ToString(), Encoding.UTF8);
    }

    public void ClearHistory()
    {
        lock (_sync)
        {
            _doc.Devices.Clear();
        }
        Save();
    }
}
