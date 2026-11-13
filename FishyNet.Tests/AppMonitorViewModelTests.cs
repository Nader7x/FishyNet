using System.Buffers.Binary;
using System.IO;
using System.Net.NetworkInformation;
using FishyNet.Controls;
using FishyNet.Core;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;
using FishyNet.Features;
using FishyNet.Features.AppMonitor;
using FishyNet.ViewModels;
using Xunit;

namespace FishyNet.Tests;

public sealed class AppMonitorViewModelTests : IDisposable
{
    private readonly string _tempProfileFile;

    public AppMonitorViewModelTests()
    {
        _tempProfileFile = Path.Combine(Path.GetTempPath(), $"fishynet_appmon_test_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void TabSwitching_AndSourceFilter_UpdateBooleansAndWebsites()
    {
        var registry = new DeviceRegistry();
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        Ipv4Address.TryParse("192.168.1.50", out Ipv4Address tvIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address githubIp);
        Ipv4Address.TryParse("93.184.216.34", out Ipv4Address exampleIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress gwMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:50", out MacAddress tvMac);

        registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);
        NetworkDevice tvDev = registry.RegisterOrUpdateDevice(tvIp, tvMac);
        tvDev.Hostname = "Bedroom-TV";

        var appTracker = new AppTrafficTracker(new FakeSocketTableProvider([]), new FakeProcessMetadataResolver([]));
        var websiteTracker = new WebsiteTrafficTracker(registry, appTracker);
        var timeline = new TrafficTimelineStore();

        var localFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, githubIp, 50001, 443, 2048, true, true, localIp, localMac);
        websiteTracker.OnTlsSniObserved(in localFlow, "github.com");
        websiteTracker.OnPacketFlow(in localFlow);

        var lanFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, tvIp, exampleIp, 41002, 80, 4096, true, false, tvIp, tvMac);
        websiteTracker.OnHttpHostObserved(in lanFlow, "example.com");
        websiteTracker.OnPacketFlow(in lanFlow);

        var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline);
        vm.RefreshFromTrackers();

        // Tab switching
        Assert.Equal(AppMonitorTab.Apps, vm.SelectedTab);
        Assert.True(vm.IsAppsTab);
        Assert.False(vm.IsWebsitesTab);

        vm.SelectTabCommand.Execute("Websites");
        Assert.Equal(AppMonitorTab.Websites, vm.SelectedTab);
        Assert.True(vm.IsWebsitesTab);
        Assert.False(vm.IsAppsTab);

        vm.SelectTabCommand.Execute("TrafficTypes");
        Assert.True(vm.IsTrafficTypesTab);

        vm.SelectTabCommand.Execute(AppMonitorTab.Alerts);
        Assert.True(vm.IsAlertsTab);

        // Source filtering
        Assert.Equal(TrafficSourceFilter.AllLanDevices, vm.SourceFilter);
        Assert.True(vm.IsAllLanSource);
        Assert.False(vm.IsThisPcSource);
        Assert.Equal(2, vm.Websites.Count);
        Assert.Contains(vm.Websites, w => w.Domain == "github.com" && w.IsLocalHost);
        Assert.Contains(vm.Websites, w => w.Domain == "example.com" && !w.IsLocalHost);

        vm.SetSourceFilterCommand.Execute("ThisPcOnly");
        Assert.Equal(TrafficSourceFilter.ThisPcOnly, vm.SourceFilter);
        Assert.False(vm.IsAllLanSource);
        Assert.True(vm.IsThisPcSource);
        WebsiteTrafficRowViewModel onlyLocal = Assert.Single(vm.Websites);
        Assert.Equal("github.com", onlyLocal.Domain);

        vm.SetSourceFilterCommand.Execute(TrafficSourceFilter.AllLanDevices);
        Assert.Equal(2, vm.Websites.Count);
    }

    [Fact]
    public void SearchQuery_FiltersAppsAndWebsitesCleanly()
    {
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address githubIp);
        Ipv4Address.TryParse("162.159.130.234", out Ipv4Address discordIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);

        var sockets = new FakeSocketTableProvider(
        [
            new SocketConnectionEntry(6, false, localIp, 0, 0, 51001, githubIp, 0, 0, 443, 1001, 5),
            new SocketConnectionEntry(6, false, localIp, 0, 0, 51002, discordIp, 0, 0, 443, 1002, 5)
        ]);
        var metadata = new FakeProcessMetadataResolver(new Dictionary<int, ProcessMetadata>
        {
            [1001] = new ProcessMetadata(1001, "chrome.exe", "Google Chrome", @"C:\Apps\Chrome\chrome.exe", "Google LLC", null),
            [1002] = new ProcessMetadata(1002, "discord.exe", "Discord", @"C:\Apps\Discord\discord.exe", "Discord Inc.", null)
        });

        var timeline = new TrafficTimelineStore();
        var appTracker = new AppTrafficTracker(sockets, metadata, timelineStore: timeline);
        var websiteTracker = new WebsiteTrafficTracker(appTracker: appTracker);

        var chromeFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, githubIp, 51001, 443, 12_000, false, true, localIp, localMac);
        appTracker.OnTlsSniObserved(in chromeFlow, "api.github.com");
        appTracker.OnPacketFlow(in chromeFlow);
        websiteTracker.OnTlsSniObserved(in chromeFlow, "api.github.com");
        websiteTracker.OnPacketFlow(in chromeFlow);

        var discordFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, discordIp, 51002, 443, 4_000, false, true, localIp, localMac);
        appTracker.OnTlsSniObserved(in discordFlow, "gateway.discord.gg");
        appTracker.OnPacketFlow(in discordFlow);
        websiteTracker.OnTlsSniObserved(in discordFlow, "gateway.discord.gg");
        websiteTracker.OnPacketFlow(in discordFlow);

        appTracker.SampleTick();

        var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline);
        vm.RefreshFromTrackers();

        Assert.Equal(2, vm.Applications.Count);
        Assert.Equal(2, vm.Websites.Count);

        vm.SearchQuery = "discord";
        AppTrafficRowViewModel filteredApp = Assert.Single(vm.Applications);
        Assert.Equal("discord.exe", filteredApp.AppName);
        WebsiteTrafficRowViewModel filteredWeb = Assert.Single(vm.Websites);
        Assert.Equal("gateway.discord.gg", filteredWeb.Domain);

        vm.SearchQuery = "Google LLC";
        Assert.Equal("chrome.exe", Assert.Single(vm.Applications).AppName);
        Assert.Empty(vm.Websites);

        vm.SearchQuery = string.Empty;
        Assert.Equal(2, vm.Applications.Count);
        Assert.Equal(2, vm.Websites.Count);
    }

    [Fact]
    public void TimeMachineWindow_MapsTimelineBucketsSeriesAlertsAndInspectSummary()
    {
        var timeline = new TrafficTimelineStore(
            spikeMultiplier: 3.0,
            minSpikeThresholdBps: 100 * 1024,
            minBaselineSamples: 4,
            spikeCooldown: TimeSpan.FromSeconds(10));
        var appTracker = new AppTrafficTracker(new FakeSocketTableProvider([]), new FakeProcessMetadataResolver([]), timelineStore: timeline);
        var websiteTracker = new WebsiteTrafficTracker();

        DateTime t0 = new(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        for (int s = 0; s < 5; s++)
        {
            timeline.RecordSample(
                t0.AddSeconds(s),
                rxBytes: 20_000,
                txBytes: 5_000,
                slices:
                [
                    new TimelineSeriesSlice("chrome.exe", 15_000, 3_000),
                    new TimelineSeriesSlice("steam.exe", 5_000, 2_000)
                ]);
        }

        // Spike on second 5
        timeline.RecordSample(
            t0.AddSeconds(5),
            rxBytes: 300_000,
            txBytes: 20_000,
            slices:
            [
                new TimelineSeriesSlice("steam.exe", 280_000, 15_000),
                new TimelineSeriesSlice("chrome.exe", 20_000, 5_000)
            ]);

        var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline);
        vm.RefreshFromTrackers(t0.AddSeconds(5));

        Assert.Equal(TimeMachineWindow.Live, vm.SelectedTimeWindow);
        Assert.True(vm.IsLiveWindow);
        Assert.Equal(6, vm.TimelineBuckets.Count);
        Assert.Equal(2, vm.TimelineSeries.Count);
        Assert.Contains(vm.TimelineSeries, s => s.Name == "steam.exe");
        Assert.Contains(vm.TimelineSeries, s => s.Name == "chrome.exe");

        TimelineAlertMarker marker = Assert.Single(vm.TimelineAlerts);
        Assert.Equal(5, marker.BucketIndex);
        Assert.Equal(TimelineAlertSeverity.Warning, marker.Severity);
        Assert.Contains("Spike", marker.Title, StringComparison.OrdinalIgnoreCase);

        Assert.NotEqual("0 B/s", vm.PeakTrafficText);
        Assert.NotEqual("0 B", vm.WindowTotalRxText);
        Assert.NotEqual("0 B", vm.WindowTotalTxText);

        // Inspect bucket 5
        vm.InspectedIndex = 5;
        vm.IsInspectLocked = true;
        Assert.Contains("[LOCKED]", vm.InspectedSummary);
        Assert.Contains("steam.exe", vm.InspectedSummary);
        Assert.Contains("Alert:", vm.InspectedSummary);

        // Unlock inspect
        vm.UnlockInspectCommand.Execute(null);
        Assert.False(vm.IsInspectLocked);
        Assert.Equal(-1, vm.InspectedIndex);
        Assert.Equal(-1, vm.HoveredIndex);

        // Switch Time-Machine windows
        vm.SelectTimeWindowCommand.Execute("5m");
        Assert.True(vm.IsFiveMinutesWindow);
        Assert.Equal(2, vm.TimelineBuckets.Count);

        vm.SelectTimeWindowCommand.Execute("OneHour");
        Assert.True(vm.IsOneHourWindow);
        Assert.Single(vm.TimelineBuckets);

        vm.SelectTimeWindowCommand.Execute("24h");
        Assert.True(vm.IsTwentyFourHoursWindow);

        vm.SelectTimeWindowCommand.Execute("7d");
        Assert.True(vm.IsSevenDaysWindow);
    }

    [Fact]
    public async Task FirewallToggle_AndTrafficTypesBreakdown_WorkAndReportErrorsSafely()
    {
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address githubIp);
        Ipv4Address.TryParse("142.250.185.78", out Ipv4Address googleIp);
        Ipv4Address.TryParse("93.184.216.34", out Ipv4Address httpIp);
        Ipv4Address.TryParse("8.8.8.8", out Ipv4Address dnsIp);
        Ipv4Address.TryParse("1.2.3.4", out Ipv4Address rawIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);

        var sockets = new FakeSocketTableProvider(
        [
            new SocketConnectionEntry(6, false, localIp, 0, 0, 52001, githubIp, 0, 0, 443, 1001, 5),
            new SocketConnectionEntry(17, false, localIp, 0, 0, 52002, googleIp, 0, 0, 443, 1002, 0),
            new SocketConnectionEntry(6, false, localIp, 0, 0, 52005, rawIp, 0, 0, 9000, 1002, 5)
        ]);
        var metadata = new FakeProcessMetadataResolver(new Dictionary<int, ProcessMetadata>
        {
            [1001] = new ProcessMetadata(1001, "chrome.exe", "Google Chrome", @"C:\Apps\Chrome\chrome.exe", "Google LLC", null),
            [1002] = new ProcessMetadata(1002, "updater.exe", "App Updater", @"C:\Apps\Updater\updater.exe", "Acme Corp", null)
        });

        var firewall = new MockFirewallController();
        var timeline = new TrafficTimelineStore();
        var appTracker = new AppTrafficTracker(sockets, metadata, firewall, timeline);
        var websiteTracker = new WebsiteTrafficTracker(appTracker: appTracker);

        var tlsFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, githubIp, 52001, 443, 60_000, false, true, localIp, localMac);
        appTracker.OnTlsSniObserved(in tlsFlow, "github.com");
        appTracker.OnPacketFlow(in tlsFlow);
        websiteTracker.OnTlsSniObserved(in tlsFlow, "github.com");
        websiteTracker.OnPacketFlow(in tlsFlow);

        var quicFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 17, localIp, googleIp, 52002, 443, 20_000, false, true, localIp, localMac);
        appTracker.OnPacketFlow(in quicFlow);
        websiteTracker.OnTlsSniObserved(in quicFlow, "google.com");
        // Keep QUIC entry in websiteTracker by adding UDP 443 flow after DNS mapping:
        var dnsMap = new DnsIpMapping("quic.google.com", 1, 300, googleIp, 0, 0);
        var dnsFlow = new PacketFlowObservation(ProtocolKind.Dns, 17, localIp, dnsIp, 52003, 53, 500, false, true, localIp, localMac);
        websiteTracker.OnDnsMappingsObserved(in dnsFlow, [dnsMap]);
        websiteTracker.OnPacketFlow(in quicFlow);

        var httpFlow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, httpIp, 52004, 80, 10_000, false, true, localIp, localMac);
        websiteTracker.OnHttpHostObserved(in httpFlow, "example.com");
        websiteTracker.OnPacketFlow(in httpFlow);

        // Raw non-domain flow attributes remainder into Other
        var rawFlow = new PacketFlowObservation(ProtocolKind.Tcp, 6, localIp, rawIp, 52005, 9000, 10_000, true, true, localIp, localMac);
        appTracker.OnPacketFlow(in rawFlow);

        appTracker.SampleTick();

        var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline, firewall);
        vm.RefreshFromTrackers();

        // BandwidthShareRatio check
        Assert.Equal(2, vm.Applications.Count);
        double ratioSum = vm.Applications.Sum(a => a.BandwidthShareRatio);
        Assert.InRange(ratioSum, 0.99, 1.01);
        Assert.All(vm.Applications, a => Assert.InRange(a.BandwidthShareRatio, 0.0, 1.0));

        // TrafficTypes breakdown check
        Assert.Equal(7, vm.TrafficTypes.Count);
        TrafficTypeBreakdownRow tlsRow = Assert.Single(vm.TrafficTypes, r => r.TypeName == "TLS / HTTPS");
        Assert.Equal(60_000, tlsRow.TotalBytes);
        Assert.True(tlsRow.ShareRatio > 0.5);
        Assert.Contains("KB", tlsRow.TotalVolumeText);

        TrafficTypeBreakdownRow httpRow = Assert.Single(vm.TrafficTypes, r => r.TypeName == "HTTP");
        Assert.Equal(10_000, httpRow.TotalBytes);

        TrafficTypeBreakdownRow otherRow = Assert.Single(vm.TrafficTypes, r => r.TypeName == "Other");
        Assert.Equal(10_000, otherRow.TotalBytes);

        // Firewall Block / Unblock toggle
        AppTrafficRowViewModel chromeRow = Assert.Single(vm.Applications, a => a.AppName == "chrome.exe");
        Assert.False(chromeRow.IsFirewallBlocked);
        Assert.Equal("Block", chromeRow.BlockButtonText);

        await vm.ToggleBlockAppAsync(chromeRow);
        Assert.True(chromeRow.IsFirewallBlocked);
        Assert.Equal("Unblock", chromeRow.BlockButtonText);
        Assert.True(firewall.IsAppBlocked(chromeRow.ExePath));

        await vm.ToggleBlockAppAsync(chromeRow);
        Assert.False(chromeRow.IsFirewallBlocked);
        Assert.Equal("Block", chromeRow.BlockButtonText);
        Assert.False(firewall.IsAppBlocked(chromeRow.ExePath));

        // Safe failure handling (result.Success == false and thrown exception)
        firewall.NextFailureMessage = "Access denied: administrator privileges required.";
        await vm.ToggleBlockAppAsync(chromeRow);
        Assert.False(chromeRow.IsFirewallBlocked);
        Assert.Contains("Access denied", vm.StatusMessage);

        firewall.ThrowOnNextCall = true;
        await vm.ToggleBlockAppAsync(chromeRow);
        Assert.False(chromeRow.IsFirewallBlocked);
        Assert.Contains("Firewall update failed", vm.StatusMessage);
    }

    [Fact]
    public void Alerts_AcknowledgeBannerAndClearHistory_WorkEndToEnd()
    {
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address githubIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);

        var sockets = new FakeSocketTableProvider(
        [
            new SocketConnectionEntry(6, false, localIp, 0, 0, 53001, githubIp, 0, 0, 443, 2001, 5)
        ]);
        var metadata = new FakeProcessMetadataResolver(new Dictionary<int, ProcessMetadata>
        {
            [2001] = new ProcessMetadata(2001, "curl.exe", "cURL CLI", @"C:\Tools\curl.exe", "cURL", null)
        });

        var timeline = new TrafficTimelineStore();
        var appTracker = new AppTrafficTracker(sockets, metadata, timelineStore: timeline);
        var websiteTracker = new WebsiteTrafficTracker(appTracker: appTracker);

        var flow = new PacketFlowObservation(ProtocolKind.HttpTls, 6, localIp, githubIp, 53001, 443, 1024, true, true, localIp, localMac);
        appTracker.OnPacketFlow(in flow);
        websiteTracker.OnTlsSniObserved(in flow, "curl.se");
        websiteTracker.OnPacketFlow(in flow);
        appTracker.SampleTick();

        timeline.RecordAlert(MonitorAlertKind.DnsAnomaly, MonitorAlertSeverity.Warning, "DNS Anomaly", "Suspicious lookup", "bad.example", "192.168.1.50");

        var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline);
        vm.RefreshFromTrackers();

        Assert.Equal(2, vm.Alerts.Count);
        Assert.Equal(2, vm.UnacknowledgedAlertCount);
        Assert.True(vm.HasUnacknowledgedAlerts);
        Assert.True(vm.HasBannerAlert);
        Assert.NotNull(vm.LatestBannerAlert);

        // Acknowledge app clears NEW flag + corresponding FirstNetworkAccess alert
        AppTrafficRowViewModel curlRow = Assert.Single(vm.Applications);
        Assert.True(curlRow.IsNewFirstAccess);
        vm.AcknowledgeAppCommand.Execute(curlRow);
        Assert.False(curlRow.IsNewFirstAccess);
        Assert.Equal(1, vm.UnacknowledgedAlertCount);

        // Acknowledge remaining banner alert
        vm.AcknowledgeAlertCommand.Execute(null);
        Assert.Equal(0, vm.UnacknowledgedAlertCount);
        Assert.False(vm.HasUnacknowledgedAlerts);
        Assert.False(vm.HasBannerAlert);
        Assert.Null(vm.LatestBannerAlert);

        // Raise another alert then test AcknowledgeAllAlertsCommand
        timeline.RecordAlert(MonitorAlertKind.ArpSpoof, MonitorAlertSeverity.Critical, "ARP Spoof", "Conflict", "00:11:22:33:44:99", "192.168.1.1");
        vm.RefreshAlertsOnly();
        Assert.Equal(1, vm.UnacknowledgedAlertCount);
        vm.AcknowledgeAllAlertsCommand.Execute(null);
        Assert.Equal(0, vm.UnacknowledgedAlertCount);

        // Clear history resets collections
        vm.ClearMonitorHistoryCommand.Execute(null);
        Assert.Empty(vm.Applications);
        Assert.Empty(vm.Websites);
        Assert.Empty(vm.Alerts);
        Assert.Empty(vm.TimelineBuckets);
    }

    [Fact]
    public void MainViewModel_AppMonitorIntegration_BridgesTelemetryAlertsSinkholeAndDisposesCleanly()
    {
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        Ipv4Address.TryParse("192.168.1.50", out Ipv4Address phoneIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address remoteIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress gwMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:50", out MacAddress phoneMac);
        MacAddress.TryParse("DE:AD:BE:EF:00:01", out MacAddress rogueMac);

        var sockets = new FakeSocketTableProvider(
        [
            new SocketConnectionEntry(6, false, localIp, 0, 0, 54123, remoteIp, 0, 0, 443, 3001, 5)
        ]);
        var metadata = new FakeProcessMetadataResolver(new Dictionary<int, ProcessMetadata>
        {
            [3001] = new ProcessMetadata(3001, "msedge.exe", "Microsoft Edge", @"C:\Apps\Edge\msedge.exe", "Microsoft Corp", null)
        });
        var firewall = new MockFirewallController();
        var store = new DeviceProfileStore(_tempProfileFile);

        using var vm = new MainViewModel(store, firewall, sockets, metadata);
        Assert.NotNull(vm.AppMonitor);

        // Configure network, discover LAN device, and trigger ARP spoof alert
        vm.Registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        vm.Registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);
        NetworkDevice phoneDev = vm.Registry.RegisterOrUpdateDevice(phoneIp, phoneMac);
        phoneDev.Hostname = "Pixel-8";
        vm.Registry.RegisterOrUpdateDevice(gwIp, rogueMac); // Triggers ArpSecurityAlert

        int processed = vm.ProcessPendingRegistryEvents();
        Assert.True(processed >= 4);

        Assert.Contains(vm.AppMonitor.Alerts, a => a.Kind == MonitorAlertKind.NewLanDevice && a.SubjectName == "Pixel-8");
        Assert.Contains(vm.AppMonitor.Alerts, a => a.Kind == MonitorAlertKind.ArpSpoof);

        // Feed raw Ethernet + IPv4 + TCP + TLS ClientHello frames via CaptureEngine
        var adapter = new NetworkAdapterInfo(
            "test-nic", "Ethernet", "Test Adapter", NetworkInterfaceType.Ethernet,
            localIp, new Ipv4Address(0xFFFFFF00u), gwIp, localMac, false);

        byte[] localFrame = BuildEthernetTlsFrame(localMac, gwMac, localIp, remoteIp, 54123, 443, "docs.github.com");
        vm.Engine.ProcessObservedIpv4Frame(localFrame, localFrame.Length, adapter, activeHandle: nint.Zero, allowForwarding: false);

        byte[] lanFrame = BuildEthernetTlsFrame(phoneMac, localMac, phoneIp, remoteIp, 45123, 443, "ads.example.net");
        vm.Engine.ProcessObservedIpv4Frame(lanFrame, lanFrame.Length, adapter, activeHandle: nint.Zero, allowForwarding: true);

        // Switch to AppMonitor section and run 1s tick
        vm.NavigateSectionCommand.Execute(AppSection.AppMonitor);
        Assert.True(vm.IsAppMonitorSection);

        DateTime tickUtc = DateTime.UtcNow;
        vm.SampleTelemetryTick(tickUtc);

        Assert.Contains(vm.AppMonitor.Applications, a => a.AppName == "msedge.exe");
        WebsiteTrafficRowViewModel lanWeb = Assert.Single(vm.AppMonitor.Websites, w => w.Domain == "ads.example.net");
        WebsiteTrafficRowViewModel localWeb = Assert.Single(vm.AppMonitor.Websites, w => w.Domain == "docs.github.com");

        // Toggle DNS sinkhole ON & OFF for LAN device
        Assert.False(lanWeb.IsSinkholed);
        vm.AppMonitor.ToggleSinkholeDomainCommand.Execute(lanWeb);
        Assert.True(lanWeb.IsSinkholed);
        Assert.Equal("Unsinkhole", lanWeb.SinkholeButtonText);
        Assert.True(phoneDev.IsDomainBlocked("ads.example.net"));

        vm.AppMonitor.ToggleSinkholeDomainCommand.Execute(lanWeb);
        Assert.False(lanWeb.IsSinkholed);
        Assert.Equal("Sinkhole", lanWeb.SinkholeButtonText);
        Assert.False(phoneDev.IsDomainBlocked("ads.example.net"));

        // Verify unsinkholed state remains false after refresh
        vm.AppMonitor.RefreshFromTrackers(tickUtc.AddSeconds(1));
        Assert.False(Assert.Single(vm.AppMonitor.Websites, w => w.Domain == "ads.example.net").IsSinkholed);

        // Local-only website sinkhole reports informative status message without mutating device
        vm.AppMonitor.ToggleSinkholeDomainCommand.Execute(localWeb);
        Assert.False(localWeb.IsSinkholed);
        Assert.Contains("LAN devices only", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Toggle Mbps unit mode syncs into AppMonitor
        vm.ToggleUnitModeCommand.Execute(null);
        Assert.True(vm.AppMonitor.UseBitsPerSecond);
        Assert.Contains("bps", vm.AppMonitor.PeakTrafficText, StringComparison.OrdinalIgnoreCase);

        // Dispose unregisters telemetry observers cleanly
        vm.Dispose();
        byte[] postDisposeFrame = BuildEthernetTlsFrame(localMac, gwMac, localIp, remoteIp, 54124, 443, "after-dispose.test");
        vm.Engine.ProcessObservedIpv4Frame(postDisposeFrame, postDisposeFrame.Length, adapter, activeHandle: nint.Zero, allowForwarding: false);
        Assert.DoesNotContain(vm.WebsiteTracker.GetSnapshot().Websites, w => w.Domain == "after-dispose.test");
    }

    private static byte[] BuildEthernetTlsFrame(
        MacAddress srcMac,
        MacAddress dstMac,
        Ipv4Address srcIp,
        Ipv4Address dstIp,
        ushort srcPort,
        ushort dstPort,
        string serverName)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(serverName);
        int sniExtPayloadLen = 2 + 1 + 2 + nameBytes.Length;
        int extensionsLen = 4 + sniExtPayloadLen;
        int helloBodyLen = 2 + 32 + 1 + 2 + 2 + 1 + 1 + 2 + extensionsLen;
        int handshakeLen = 4 + helloBodyLen;
        byte[] tlsPayload = new byte[5 + handshakeLen];

        tlsPayload[0] = 0x16;
        tlsPayload[1] = 0x03;
        tlsPayload[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(3, 2), (ushort)handshakeLen);
        tlsPayload[5] = 0x01;
        tlsPayload[6] = (byte)(helloBodyLen >> 16);
        tlsPayload[7] = (byte)(helloBodyLen >> 8);
        tlsPayload[8] = (byte)helloBodyLen;

        int pos = 9;
        tlsPayload[pos++] = 0x03;
        tlsPayload[pos++] = 0x03;
        pos += 32;
        tlsPayload[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), 2);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), 0x1301);
        pos += 2;
        tlsPayload[pos++] = 1;
        tlsPayload[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), (ushort)extensionsLen);
        pos += 2;

        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos + 2, 2), (ushort)sniExtPayloadLen);
        pos += 4;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), (ushort)(1 + 2 + nameBytes.Length));
        pos += 2;
        tlsPayload[pos++] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(tlsPayload.AsSpan(pos, 2), (ushort)nameBytes.Length);
        pos += 2;
        nameBytes.CopyTo(tlsPayload.AsSpan(pos));

        byte[] frame = new byte[14 + 20 + 20 + tlsPayload.Length];
        dstMac.WriteTo(frame.AsSpan(0, 6));
        srcMac.WriteTo(frame.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), PacketPrimitives.EtherTypeIpv4);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)(20 + 20 + tlsPayload.Length));
        frame[23] = 6;
        srcIp.WriteTo(frame.AsSpan(26, 4));
        dstIp.WriteTo(frame.AsSpan(30, 4));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), dstPort);
        frame[34 + 12] = 0x50;
        tlsPayload.CopyTo(frame.AsSpan(54));
        return frame;
    }

    [Fact]
    public void UsagePeriodDropdown_SwitchesBetweenLiveAndHistoricalPresets_AndQueriesStore()
    {
        string historyPath = Path.Combine(Path.GetTempPath(), $"fishynet_vm_hist_{Guid.NewGuid():N}.json");
        try
        {
            var historyStore = new AppMonitorHistoryStore(historyPath);
            DateTime todayUtc = DateTime.UtcNow.Date.AddHours(10);
            DateTime yesterdayUtc = todayUtc.AddDays(-1);
            DateTime threeDaysAgoUtc = todayUtc.AddDays(-3);

            var chromeOld = new AppTrafficEntry(
                "chrome.exe", 101, "chrome.exe", "Google Chrome", @"C:\chrome.exe", "Google LLC", null,
                0, 0, 1_000, 200, ["TLS"], ["github.com"], 1, false, false, threeDaysAgoUtc, threeDaysAgoUtc);
            var steamYesterday = new AppTrafficEntry(
                "steam.exe", 102, "steam.exe", "Steam", @"C:\steam.exe", "Valve", null,
                0, 0, 5_000, 500, ["QUIC"], ["steampowered.com"], 1, false, false, yesterdayUtc, yesterdayUtc);
            var discordToday = new AppTrafficEntry(
                "discord.exe", 103, "discord.exe", "Discord", @"C:\discord.exe", "Discord Inc.", null,
                0, 0, 2_000, 400, ["TLS"], ["discord.gg"], 1, false, false, todayUtc, todayUtc);

            var webOld = new WebsiteTrafficEntry("w1", "github.com", "140.82.121.6", "TLS/443", "This PC", "192.168.1.10", true, "chrome.exe", 1_000, 200, 1, threeDaysAgoUtc, false);
            var webYesterday = new WebsiteTrafficEntry("w2", "steampowered.com", "23.1.2.3", "QUIC/443", "This PC", "192.168.1.10", true, "steam.exe", 5_000, 500, 2, yesterdayUtc, false);
            var webToday = new WebsiteTrafficEntry("w3", "discord.gg", "162.159.130.234", "TLS/443", "This PC", "192.168.1.10", true, "discord.exe", 2_000, 400, 3, todayUtc, false);

            historyStore.RecordSample(
                threeDaysAgoUtc,
                new AppTrafficSnapshot(threeDaysAgoUtc, [chromeOld], 0, 0, 1_000, 200, 0),
                new WebsiteTrafficSnapshot(threeDaysAgoUtc, [webOld], 1_000, 200, 1));
            historyStore.RecordSample(
                yesterdayUtc,
                new AppTrafficSnapshot(yesterdayUtc, [steamYesterday], 0, 0, 5_000, 500, 0),
                new WebsiteTrafficSnapshot(yesterdayUtc, [webYesterday], 5_000, 500, 1));
            historyStore.RecordSample(
                todayUtc,
                new AppTrafficSnapshot(todayUtc, [discordToday], 0, 0, 2_000, 400, 0),
                new WebsiteTrafficSnapshot(todayUtc, [webToday], 2_000, 400, 1));

            var appTracker = new AppTrafficTracker(new FakeSocketTableProvider([]), new FakeProcessMetadataResolver([]));
            var websiteTracker = new WebsiteTrafficTracker();
            var timeline = new TrafficTimelineStore();

            var vm = new AppMonitorViewModel(appTracker, websiteTracker, timeline, historyStore: historyStore);

            // Default: Live Session
            Assert.Equal(UsagePeriodPreset.LiveSession, vm.SelectedUsagePeriod.Preset);
            Assert.False(vm.IsHistoricalMode);
            Assert.False(vm.IsCustomDateRange);
            Assert.Equal("Live Session", vm.ActiveDateRangeSummary);
            Assert.Equal(7, vm.AvailableUsagePeriods.Count);

            // Switch to Today
            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.Today);
            Assert.True(vm.IsHistoricalMode);
            Assert.False(vm.IsCustomDateRange);
            Assert.Equal(DateOnly.FromDateTime(todayUtc).ToString("yyyy-MM-dd"), vm.ActiveDateRangeSummary);
            Assert.Equal("discord.exe", Assert.Single(vm.Applications).AppName);
            Assert.Equal("discord.gg", Assert.Single(vm.Websites).Domain);
            Assert.Equal(1, vm.ActiveAppCount);
            Assert.Equal(1, vm.UniqueDomainCount);
            Assert.NotEqual("0 B", vm.HistoricalTotalRxText);
            Assert.NotEqual("0 B", vm.HistoricalTotalTxText);

            // Switch to Yesterday
            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.Yesterday);
            Assert.Equal("steam.exe", Assert.Single(vm.Applications).AppName);
            Assert.Equal("steampowered.com", Assert.Single(vm.Websites).Domain);

            // Switch to Last 7 Days
            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.Last7Days);
            Assert.Equal(3, vm.Applications.Count);
            Assert.Equal(3, vm.Websites.Count);
            Assert.Contains("→", vm.ActiveDateRangeSummary);

            // Switch back to Live Session
            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.LiveSession);
            Assert.False(vm.IsHistoricalMode);
            Assert.Equal("Live Session", vm.ActiveDateRangeSummary);
            Assert.Empty(vm.Applications);
            Assert.Empty(vm.Websites);
        }
        finally
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
        }
    }

    [Fact]
    public void CustomDateRange_FiltersMultiDayHistory_ClampsDates_AndSupportsSearchAndSourceFilter()
    {
        string historyPath = Path.Combine(Path.GetTempPath(), $"fishynet_vm_custom_{Guid.NewGuid():N}.json");
        try
        {
            var historyStore = new AppMonitorHistoryStore(historyPath);
            DateTime day1 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            DateTime day2 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            DateTime day3 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

            var chromeDay1 = new AppTrafficEntry("chrome.exe", 1, "chrome.exe", "Google Chrome", @"C:\chrome.exe", "Google LLC", null, 0, 0, 1_000, 100, ["TLS"], ["github.com"], 1, false, false, day1, day1);
            var chromeDay2 = new AppTrafficEntry("chrome.exe", 1, "chrome.exe", "Google Chrome", @"C:\chrome.exe", "Google LLC", null, 0, 0, 3_000, 300, ["TLS"], ["github.com"], 1, false, false, day2, day2);
            var edgeDay3 = new AppTrafficEntry("msedge.exe", 2, "msedge.exe", "Microsoft Edge", @"C:\msedge.exe", "Microsoft", null, 0, 0, 9_000, 900, ["TLS"], ["bing.com"], 1, false, false, day3, day3);

            var localWebDay1 = new WebsiteTrafficEntry("w1", "github.com", "140.82.121.6", "TLS/443", "This PC", "192.168.1.10", true, "chrome.exe", 1_000, 100, 1, day1, false);
            var lanWebDay2 = new WebsiteTrafficEntry("w2", "netflix.com", "52.1.2.3", "TLS/443", "Bedroom-TV", "192.168.1.50", false, "", 4_000, 300, 2, day2, false);
            var localWebDay3 = new WebsiteTrafficEntry("w3", "bing.com", "204.79.197.200", "TLS/443", "This PC", "192.168.1.10", true, "msedge.exe", 9_000, 900, 1, day3, false);

            historyStore.RecordSample(day1, new AppTrafficSnapshot(day1, [chromeDay1], 0, 0, 1_000, 100, 0), new WebsiteTrafficSnapshot(day1, [localWebDay1], 1_000, 100, 1));
            historyStore.RecordSample(day2, new AppTrafficSnapshot(day2, [chromeDay2], 0, 0, 2_000, 200, 0), new WebsiteTrafficSnapshot(day2, [lanWebDay2], 4_000, 300, 1));
            historyStore.RecordSample(day3, new AppTrafficSnapshot(day3, [edgeDay3], 0, 0, 9_000, 900, 0), new WebsiteTrafficSnapshot(day3, [localWebDay3], 9_000, 900, 1));

            var vm = new AppMonitorViewModel(
                new AppTrafficTracker(new FakeSocketTableProvider([]), new FakeProcessMetadataResolver([])),
                new WebsiteTrafficTracker(),
                new TrafficTimelineStore(),
                historyStore: historyStore);

            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.CustomRange);
            Assert.True(vm.IsCustomDateRange);
            Assert.True(vm.IsHistoricalMode);

            vm.CustomFromDate = new DateTime(2026, 11, 10);
            vm.CustomToDate = new DateTime(2026, 11, 11);
            Assert.Equal("2026-11-10 → 2026-11-11", vm.ActiveDateRangeSummary);

            // Aggregated chrome across day1 + day2 = 3000 RX, 300 TX (msedge on day3 excluded)
            AppTrafficRowViewModel chromeRow = Assert.Single(vm.Applications);
            Assert.Equal("chrome.exe", chromeRow.AppName);
            Assert.Equal(3_000, chromeRow.TotalDownloadBytes);
            Assert.Equal(300, chromeRow.TotalUploadBytes);
            Assert.Equal(2, vm.Websites.Count);

            // SourceFilter: ThisPcOnly excludes Bedroom-TV netflix.com
            vm.SetSourceFilterCommand.Execute("ThisPcOnly");
            Assert.Equal("github.com", Assert.Single(vm.Websites).Domain);

            vm.SetSourceFilterCommand.Execute("AllLanDevices");
            Assert.Equal(2, vm.Websites.Count);

            // SearchQuery in historical mode
            vm.SearchQuery = "netflix";
            Assert.Empty(vm.Applications);
            Assert.Equal("netflix.com", Assert.Single(vm.Websites).Domain);

            vm.SearchQuery = string.Empty;
            Assert.Single(vm.Applications);
            Assert.Equal(2, vm.Websites.Count);

            // Date clamping: CustomFromDate > CustomToDate clamps CustomToDate up
            vm.CustomFromDate = new DateTime(2026, 11, 12);
            Assert.Equal(new DateTime(2026, 11, 12), vm.CustomToDate);
            Assert.Equal("2026-11-12", vm.ActiveDateRangeSummary);
            Assert.Equal("msedge.exe", Assert.Single(vm.Applications).AppName);

            // Date clamping: CustomToDate < CustomFromDate clamps CustomFromDate down
            vm.CustomToDate = new DateTime(2026, 11, 10);
            Assert.Equal(new DateTime(2026, 11, 10), vm.CustomFromDate);
            vm.ApplyCustomDateRangeCommand.Execute(null);
            Assert.Equal("chrome.exe", Assert.Single(vm.Applications).AppName);
            Assert.Equal(1_000, vm.Applications[0].TotalDownloadBytes);
        }
        finally
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
        }
    }

    [Fact]
    public void ClearMonitorHistoryCommand_ClearsHistoricalStoreAndResetsHistoricalView()
    {
        string historyPath = Path.Combine(Path.GetTempPath(), $"fishynet_vm_clear_{Guid.NewGuid():N}.json");
        try
        {
            var historyStore = new AppMonitorHistoryStore(historyPath);
            DateTime todayUtc = DateTime.UtcNow.Date.AddHours(10);
            var app = new AppTrafficEntry("chrome.exe", 1, "chrome.exe", "Google Chrome", @"C:\chrome.exe", "Google LLC", null, 0, 0, 2_000, 500, ["TLS"], ["github.com"], 1, false, false, todayUtc, todayUtc);
            var web = new WebsiteTrafficEntry("w1", "github.com", "140.82.121.6", "TLS/443", "This PC", "192.168.1.10", true, "chrome.exe", 2_000, 500, 1, todayUtc, false);

            historyStore.RecordSample(
                todayUtc,
                new AppTrafficSnapshot(todayUtc, [app], 0, 0, 2_000, 500, 0),
                new WebsiteTrafficSnapshot(todayUtc, [web], 2_000, 500, 1));

            var vm = new AppMonitorViewModel(
                new AppTrafficTracker(new FakeSocketTableProvider([]), new FakeProcessMetadataResolver([])),
                new WebsiteTrafficTracker(),
                new TrafficTimelineStore(),
                historyStore: historyStore);

            vm.SelectedUsagePeriod = vm.AvailableUsagePeriods.First(p => p.Preset == UsagePeriodPreset.Today);
            Assert.Single(vm.Applications);
            Assert.Single(vm.Websites);

            vm.ClearMonitorHistoryCommand.Execute(null);

            Assert.Empty(vm.Applications);
            Assert.Empty(vm.Websites);
            Assert.Empty(historyStore.QueryApplications(DateOnly.MinValue, DateOnly.MaxValue));
            Assert.Empty(historyStore.QueryWebsites(DateOnly.MinValue, DateOnly.MaxValue));
        }
        finally
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_tempProfileFile))
        {
            File.Delete(_tempProfileFile);
        }
    }

    private sealed class FakeSocketTableProvider(IReadOnlyList<SocketConnectionEntry> entries) : ISocketTableProvider
    {
        public bool TryReadSockets(List<SocketConnectionEntry> destination)
        {
            destination.Clear();
            destination.AddRange(entries);
            return true;
        }
    }

    private sealed class FakeProcessMetadataResolver(Dictionary<int, ProcessMetadata> map) : IProcessMetadataResolver
    {
        public ProcessMetadata Resolve(int processId)
        {
            return map.TryGetValue(processId, out ProcessMetadata? meta)
                ? meta
                : new ProcessMetadata(processId, "unknown.exe", "Unknown", string.Empty, string.Empty, null);
        }
    }

    private sealed class MockFirewallController : IAppFirewallController
    {
        private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

        public string? NextFailureMessage { get; set; }
        public bool ThrowOnNextCall { get; set; }

        public IReadOnlyCollection<string> BlockedExePaths => _blocked;

        public bool IsAppBlocked(string exePath) => !string.IsNullOrWhiteSpace(exePath) && _blocked.Contains(exePath);

        public Task<AppFirewallResult> BlockAppAsync(string exePath, string? appDisplayName = null, CancellationToken ct = default)
        {
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("Simulated netsh crash");
            }

            if (!string.IsNullOrEmpty(NextFailureMessage))
            {
                string msg = NextFailureMessage;
                NextFailureMessage = null;
                return Task.FromResult(new AppFirewallResult(false, string.Empty, msg));
            }

            _blocked.Add(exePath);
            return Task.FromResult(new AppFirewallResult(true, "FishyNet_Block_Test", $"Blocked {appDisplayName ?? exePath}"));
        }

        public Task<AppFirewallResult> UnblockAppAsync(string exePath, CancellationToken ct = default)
        {
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("Simulated netsh crash");
            }

            _blocked.Remove(exePath);
            return Task.FromResult(new AppFirewallResult(true, "FishyNet_Block_Test", $"Unblocked {exePath}"));
        }

        public Task<int> ClearAllFishyNetRulesAsync(CancellationToken ct = default)
        {
            int count = _blocked.Count;
            _blocked.Clear();
            return Task.FromResult(count);
        }
    }
}
