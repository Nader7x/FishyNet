using System.IO;
using SelfishNetv3.Core.Packets;
using SelfishNetv3.Features;
using Xunit;

namespace SelfishNet.Tests;

public sealed class UsageHistoryStoreTests : IDisposable
{
    private readonly string _tempFile;

    public UsageHistoryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"selfishnet_history_test_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void RecordAndQueryUsage_SupportsTodayWeekMonthAndAllTimeRollups()
    {
        var store = new UsageHistoryStore(_tempFile);
        var today = new DateOnly(2026, 11, 9);

        Assert.True(MacAddress.TryParse("AA:BB:CC:11:22:33", out MacAddress mac1));
        Assert.True(MacAddress.TryParse("DE:AD:BE:EF:00:01", out MacAddress mac2));

        // Today sample for Device 1 (100 MB Down, 20 MB Up)
        store.RecordUsageSample(mac1, "Nader Desktop", "192.168.1.10", "Intel",
            100L * 1024 * 1024, 20L * 1024 * 1024, 5_000_000, 1_000_000, today);

        // 4 days ago sample for Device 1 with older DHCP IP (200 MB Down, 50 MB Up)
        store.RecordUsageSample(mac1, "Nader Desktop", "192.168.1.99", "Intel",
            200L * 1024 * 1024, 50L * 1024 * 1024, 8_000_000, 2_000_000, today.AddDays(-4));

        // 15 days ago sample for Device 1 (300 MB Down, 100 MB Up)
        store.RecordUsageSample(mac1, "Nader Desktop", "192.168.1.10", "Intel",
            300L * 1024 * 1024, 100L * 1024 * 1024, 6_000_000, 1_000_000, today.AddDays(-15));

        // 45 days ago sample for Device 2 (500 MB Down, 50 MB Up)
        store.RecordUsageSample(mac2, "Living Room TV", "192.168.1.55", "Samsung",
            500L * 1024 * 1024, 50L * 1024 * 1024, 4_000_000, 500_000, today.AddDays(-45));

        store.Save();

        // Reload from disk to verify persistence round-trip
        var reloaded = new UsageHistoryStore(_tempFile);

        UsageHistoryReport todayReport = reloaded.GetUsageReport(UsageTimeRange.Today, today);
        Assert.Single(todayReport.DeviceSummaries);
        Assert.Equal(120L * 1024 * 1024, todayReport.GrandTotalBytes);

        UsageHistoryReport weekReport = reloaded.GetUsageReport(UsageTimeRange.Past7Days, today);
        Assert.Single(weekReport.DeviceSummaries);
        Assert.Equal(370L * 1024 * 1024, weekReport.GrandTotalBytes);
        Assert.Equal(7, weekReport.NetworkDailySeries.Count);

        UsageHistoryReport monthReport = reloaded.GetUsageReport(UsageTimeRange.Past30Days, today);
        Assert.Single(monthReport.DeviceSummaries);
        Assert.Equal(770L * 1024 * 1024, monthReport.GrandTotalBytes);
        Assert.Equal(30, monthReport.NetworkDailySeries.Count);
        Assert.Equal(3, monthReport.DeviceSummaries[0].ActiveDays);

        UsageHistoryReport allTimeReport = reloaded.GetUsageReport(UsageTimeRange.AllTime, today);
        Assert.Equal(2, allTimeReport.DeviceSummaries.Count);
        Assert.Equal((770L + 550L) * 1024 * 1024, allTimeReport.GrandTotalBytes);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
