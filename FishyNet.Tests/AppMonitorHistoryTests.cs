using System.IO;
using FishyNet.Features.AppMonitor;
using Xunit;

namespace FishyNet.Tests;

public sealed class AppMonitorHistoryTests : IDisposable
{
    private readonly string _tempFile;

    public AppMonitorHistoryTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"fishynet_app_monitor_history_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void RecordSample_ComputesMonotonicDeltas_WithoutDoubleCounting_AndHandlesTrackerRestart()
    {
        var store = new AppMonitorHistoryStore(_tempFile);
        var day1 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var targetDate = DateOnly.FromDateTime(day1);

        // Tick 1: initial session totals (1000 down, 250 up)
        store.RecordSample(
            day1,
            CreateAppSnapshot(day1, ("chrome.exe", "Google Chrome", 1000, 250)),
            CreateWebsiteSnapshot(day1, ("github.com|192.168.1.10|443 · TLS", "github.com", true, 500, 100, 3)));

        // Tick 2: identical snapshot (simulates 1s tick with no new traffic) -> must not double-count
        store.RecordSample(
            day1.AddSeconds(1),
            CreateAppSnapshot(day1.AddSeconds(1), ("chrome.exe", "Google Chrome", 1000, 250)),
            CreateWebsiteSnapshot(day1.AddSeconds(1), ("github.com|192.168.1.10|443 · TLS", "github.com", true, 500, 100, 3)));

        // Tick 3: session totals increase to (1400 down, 350 up) -> delta (+400, +100)
        store.RecordSample(
            day1.AddSeconds(2),
            CreateAppSnapshot(day1.AddSeconds(2), ("chrome.exe", "Google Chrome", 1400, 350)),
            CreateWebsiteSnapshot(day1.AddSeconds(2), ("github.com|192.168.1.10|443 · TLS", "github.com", true, 800, 150, 5)));

        // Tick 4: tracker/app restarts, session totals reset to (200 down, 50 up) -> delta (+200, +50)
        store.RecordSample(
            day1.AddSeconds(3),
            CreateAppSnapshot(day1.AddSeconds(3), ("chrome.exe", "Google Chrome", 200, 50)),
            CreateWebsiteSnapshot(day1.AddSeconds(3), ("github.com|192.168.1.10|443 · TLS", "github.com", true, 120, 30, 1)));

        IReadOnlyList<AppTrafficEntry> apps = store.QueryApplications(targetDate, targetDate);
        AppTrafficEntry chrome = Assert.Single(apps);
        Assert.Equal(1600, chrome.TotalDownloadBytes);
        Assert.Equal(400, chrome.TotalUploadBytes);

        IReadOnlyList<WebsiteTrafficEntry> sites = store.QueryWebsites(targetDate, targetDate);
        WebsiteTrafficEntry github = Assert.Single(sites);
        Assert.Equal(920, github.DownloadBytes);
        Assert.Equal(180, github.UploadBytes);
        Assert.Equal(6, github.HitCount);
    }

    [Fact]
    public void FlushAndReload_PersistsAcrossStoreInstances_AndQueriesDateRangesCorrectly()
    {
        var store = new AppMonitorHistoryStore(_tempFile);

        var nov08 = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var nov10 = new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
        var nov12 = new DateTime(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc);

        // Day 1 (Nov 08): Chrome 100/20, Discord 50/10
        store.RecordSample(
            nov08,
            CreateAppSnapshot(nov08,
                ("chrome.exe", "Google Chrome", 100, 20),
                ("discord.exe", "Discord", 50, 10)),
            CreateWebsiteSnapshot(nov08,
                ("example.com|192.168.1.10|443 · TLS", "example.com", true, 300, 40, 2)));

        // Day 2 (Nov 10): Chrome delta +200/+30 (session 300/50), Steam new 400/80
        store.RecordSample(
            nov10,
            CreateAppSnapshot(nov10,
                ("chrome.exe", "Google Chrome", 300, 50),
                ("steam.exe", "Steam", 400, 80)),
            CreateWebsiteSnapshot(nov10,
                ("example.com|192.168.1.10|443 · TLS", "example.com", true, 500, 90, 4),
                ("lan-tv.local|192.168.1.55|443 · TLS", "lan-tv.local", false, 1000, 100, 5)));

        // Day 3 (Nov 12): Chrome delta +500/+100 (session 800/150)
        store.RecordSample(
            nov12,
            CreateAppSnapshot(nov12,
                ("chrome.exe", "Google Chrome", 800, 150)),
            null);

        store.Flush();

        // Reload in a fresh store instance
        var reloaded = new AppMonitorHistoryStore(_tempFile);

        (DateOnly minDate, DateOnly maxDate) = reloaded.GetAvailableDateBounds();
        Assert.Equal(new DateOnly(2026, 11, 8), minDate);
        Assert.Equal(new DateOnly(2026, 11, 12), maxDate);

        // Query [Nov 09, Nov 11] -> should include ONLY Nov 10 (excludes Nov 08 and Nov 12)
        IReadOnlyList<AppTrafficEntry> midRangeApps = reloaded.QueryApplications(
            new DateOnly(2026, 11, 9),
            new DateOnly(2026, 11, 11));

        Assert.Equal(2, midRangeApps.Count);
        Assert.Equal("steam.exe", midRangeApps[0].AppKey);
        Assert.Equal(400, midRangeApps[0].TotalDownloadBytes);
        Assert.Equal("chrome.exe", midRangeApps[1].AppKey);
        Assert.Equal(200, midRangeApps[1].TotalDownloadBytes);
        Assert.Equal(30, midRangeApps[1].TotalUploadBytes);

        // Query [Nov 08, Nov 12] -> aggregates all 3 days for Chrome
        IReadOnlyList<AppTrafficEntry> fullRangeApps = reloaded.QueryApplications(
            new DateOnly(2026, 11, 8),
            new DateOnly(2026, 11, 12));

        Assert.Equal(3, fullRangeApps.Count);
        AppTrafficEntry chromeFull = fullRangeApps.Single(a => a.AppKey == "chrome.exe");
        Assert.Equal(800, chromeFull.TotalDownloadBytes);
        Assert.Equal(150, chromeFull.TotalUploadBytes);
    }

    [Fact]
    public void QueryWebsites_AppliesLocalOnlyFilter()
    {
        var store = new AppMonitorHistoryStore(_tempFile);
        var day = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var date = DateOnly.FromDateTime(day);

        store.RecordSample(
            day,
            null,
            CreateWebsiteSnapshot(day,
                ("docs.microsoft.com|192.168.1.10|443 · TLS", "docs.microsoft.com", true, 600, 120, 3),
                ("netflix.com|192.168.1.50|443 · TLS", "netflix.com", false, 5000, 200, 10)));

        IReadOnlyList<WebsiteTrafficEntry> all = store.QueryWebsites(date, date, localOnlyFilter: null);
        Assert.Equal(2, all.Count);

        IReadOnlyList<WebsiteTrafficEntry> localOnly = store.QueryWebsites(date, date, localOnlyFilter: true);
        WebsiteTrafficEntry localSite = Assert.Single(localOnly);
        Assert.Equal("docs.microsoft.com", localSite.Domain);

        IReadOnlyList<WebsiteTrafficEntry> lanOnly = store.QueryWebsites(date, date, localOnlyFilter: false);
        WebsiteTrafficEntry lanSite = Assert.Single(lanOnly);
        Assert.Equal("netflix.com", lanSite.Domain);
    }

    [Fact]
    public void Clear_RemovesAllPersistedData_AndResetsDateBounds()
    {
        var store = new AppMonitorHistoryStore(_tempFile);
        var day = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var date = DateOnly.FromDateTime(day);

        store.RecordSample(
            day,
            CreateAppSnapshot(day, ("chrome.exe", "Google Chrome", 1000, 200)),
            CreateWebsiteSnapshot(day, ("github.com|192.168.1.10|443 · TLS", "github.com", true, 500, 100, 2)));
        store.Flush();

        store.Clear();

        Assert.Empty(store.QueryApplications(date, date));
        Assert.Empty(store.QueryWebsites(date, date));
        Assert.Equal((DateOnly.MinValue, DateOnly.MinValue), store.GetAvailableDateBounds());

        var reloaded = new AppMonitorHistoryStore(_tempFile);
        Assert.Empty(reloaded.QueryApplications(date, date));
        Assert.Empty(reloaded.QueryWebsites(date, date));
        Assert.Equal((DateOnly.MinValue, DateOnly.MinValue), reloaded.GetAvailableDateBounds());
    }

    [Fact]
    public void Retention_CapsBucketsAt365Days()
    {
        var store = new AppMonitorHistoryStore(_tempFile);
        var startDay = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        long runningRx = 0;
        for (int i = 0; i < 370; i++)
        {
            runningRx += 100;
            DateTime ts = startDay.AddDays(i);
            store.RecordSample(
                ts,
                CreateAppSnapshot(ts, ("chrome.exe", "Google Chrome", runningRx, 0)),
                null);
        }

        (DateOnly minDate, DateOnly maxDate) = store.GetAvailableDateBounds();
        Assert.Equal(DateOnly.FromDateTime(startDay.AddDays(5)), minDate);
        Assert.Equal(DateOnly.FromDateTime(startDay.AddDays(369)), maxDate);

        IReadOnlyList<AppTrafficEntry> retained = store.QueryApplications(DateOnly.MinValue, DateOnly.MaxValue);
        AppTrafficEntry chrome = Assert.Single(retained);
        Assert.Equal(365L * 100L, chrome.TotalDownloadBytes);
    }

    private static AppTrafficSnapshot CreateAppSnapshot(
        DateTime utcNow,
        params (string AppKey, string DisplayName, long DownloadBytes, long UploadBytes)[] apps)
    {
        var entries = apps.Select(a => new AppTrafficEntry(
            AppKey: a.AppKey,
            ProcessId: 1234,
            ProcessName: a.AppKey,
            DisplayName: a.DisplayName,
            ExePath: $@"C:\Apps\{a.AppKey}",
            Publisher: "Test Publisher",
            Icon: null,
            DownloadBps: 1024,
            UploadBps: 512,
            TotalDownloadBytes: a.DownloadBytes,
            TotalUploadBytes: a.UploadBytes,
            ProtocolTags: ["TLS", "TCP"],
            ContactedHosts: ["github.com"],
            ActiveConnections: 2,
            IsNewFirstAccess: false,
            IsFirewallBlocked: false,
            FirstSeenUtc: utcNow,
            LastSeenUtc: utcNow)).ToList();

        return new AppTrafficSnapshot(
            utcNow,
            entries,
            entries.Sum(e => e.DownloadBps),
            entries.Sum(e => e.UploadBps),
            entries.Sum(e => e.TotalDownloadBytes),
            entries.Sum(e => e.TotalUploadBytes),
            0);
    }

    private static WebsiteTrafficSnapshot CreateWebsiteSnapshot(
        DateTime utcNow,
        params (string EntryKey, string Domain, bool IsLocal, long DownloadBytes, long UploadBytes, long HitCount)[] sites)
    {
        var entries = sites.Select(s => new WebsiteTrafficEntry(
            EntryKey: s.EntryKey,
            Domain: s.Domain,
            ResolvedIp: "140.82.121.4",
            ProtocolPort: "443 · TLS",
            SourceHost: s.IsLocal ? "This PC" : "LAN Device",
            SourceHostIp: s.IsLocal ? "192.168.1.10" : "192.168.1.50",
            IsLocalHost: s.IsLocal,
            AppName: "chrome.exe",
            DownloadBytes: s.DownloadBytes,
            UploadBytes: s.UploadBytes,
            HitCount: s.HitCount,
            LastSeenUtc: utcNow,
            IsSinkholed: false)).ToList();

        return new WebsiteTrafficSnapshot(
            utcNow,
            entries,
            entries.Sum(e => e.DownloadBytes),
            entries.Sum(e => e.UploadBytes),
            entries.Select(e => e.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }

        string tmp = _tempFile + ".tmp";
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }
    }
}
