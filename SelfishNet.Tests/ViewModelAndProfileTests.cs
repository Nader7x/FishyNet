using System.IO;
using SelfishNetv3.Core;
using SelfishNetv3.Core.Native;
using SelfishNetv3.Core.Packets;
using SelfishNetv3.Features;
using SelfishNetv3.ViewModels;
using Xunit;

namespace SelfishNet.Tests;

public sealed class ViewModelAndProfileTests : IDisposable
{
    private readonly string _tempProfileFile;

    public ViewModelAndProfileTests()
    {
        _tempProfileFile = Path.Combine(Path.GetTempPath(), $"selfishnet_profile_test_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void DeviceProfileStore_PersistsNicknamesCapsQuotasAndBlockedDomains()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        Ipv4Address.TryParse("192.168.1.77", out Ipv4Address ip);
        MacAddress.TryParse("28:CF:E9:11:22:33", out MacAddress mac);

        var dev = new NetworkDevice(ip, mac)
        {
            CustomNickname = "iPhone 16 Pro",
            DeviceCategory = "Mobile",
            DownloadCapKBps = 350,
            UploadCapKBps = 100,
            IsBlocked = false,
            IsSpoofed = true,
            DailyQuotaBytes = 500L * 1024 * 1024
        };
        dev.AddBlockedDomain("tiktok.com");

        store.UpdateFromDevice(dev);

        var reloadedStore = new DeviceProfileStore(_tempProfileFile);
        var freshDev = new NetworkDevice(ip, mac);
        reloadedStore.ApplySavedProfileToDevice(freshDev);

        Assert.Equal("iPhone 16 Pro", freshDev.CustomNickname);
        Assert.Equal("Mobile", freshDev.DeviceCategory);
        Assert.Equal(350, freshDev.DownloadCapKBps);
        Assert.Equal(100, freshDev.UploadCapKBps);
        Assert.Equal(500L * 1024 * 1024, freshDev.DailyQuotaBytes);
        Assert.True(freshDev.IsDomainBlocked("tiktok.com"));
        Assert.True(freshDev.IsDomainBlocked("api.tiktok.com"));
        Assert.False(freshDev.IsDomainBlocked("github.com"));
    }

    [Fact]
    public void DeviceViewModel_UpdatesFriendlyThroughputAndQuotaState()
    {
        Ipv4Address.TryParse("192.168.1.88", out Ipv4Address ip);
        MacAddress.TryParse("00:1B:21:44:55:66", out MacAddress mac);
        var model = new NetworkDevice(ip, mac)
        {
            DownloadCapKBps = 1024, // 1 MB/s cap
            DailyQuotaBytes = 100L * 1024 * 1024 // 100 MB daily quota
        };

        var vm = new DeviceViewModel(model, useBitsPerSecond: false);
        vm.SampleTick(
            rawRxBps: 512 * 1024,
            rawTxBps: 128 * 1024,
            todayTotalBytes: 50L * 1024 * 1024,
            weekTotalBytes: 350L * 1024 * 1024,
            monthTotalBytes: 2L * 1024 * 1024 * 1024);

        Assert.Contains("KB/s", vm.DownloadSpeedText);
        Assert.Equal("50.00 MB", vm.TodayUsageText);
        Assert.Equal("350.0 MB", vm.WeekUsageText);
        Assert.Equal("2.00 GB", vm.MonthUsageText);
        Assert.False(model.IsQuotaExceeded);

        // Toggle to ISP Bits/sec (Mbps)
        vm.SetUnitMode(useBitsPerSecond: true);
        Assert.Contains("Mbps", vm.DownloadSpeedText);

        // Exceed daily quota (120 MB > 100 MB)
        vm.SampleTick(0, 0, todayTotalBytes: 120L * 1024 * 1024, weekTotalBytes: 420L * 1024 * 1024, monthTotalBytes: 2L * 1024 * 1024 * 1024);
        Assert.True(model.IsQuotaExceeded);
        Assert.Equal("QUOTA HIT", vm.StatusBadgeText);
    }

    [Fact]
    public void DeviceViewModel_SwitchesBetweenBypassedAndMonitoredStates()
    {
        Ipv4Address.TryParse("192.168.1.95", out Ipv4Address ip);
        MacAddress.TryParse("AC:BC:32:77:88:99", out MacAddress mac);
        var model = new NetworkDevice(ip, mac);
        var vm = new DeviceViewModel(model, useBitsPerSecond: false);

        // Initially MONITORED
        Assert.True(vm.IsSpoofed);
        Assert.Equal("MONITORED", vm.StatusBadgeText);

        // Switch to BYPASSED via BypassDeviceCommand
        Assert.True(vm.BypassDeviceCommand.CanExecute(null));
        vm.BypassDeviceCommand.Execute(null);
        Assert.False(vm.IsSpoofed);
        Assert.False(model.IsSpoofed);
        Assert.Equal("BYPASSED", vm.StatusBadgeText);

        // Switch back from BYPASSED to MONITORED via MonitorDeviceCommand
        Assert.True(vm.MonitorDeviceCommand.CanExecute(null));
        vm.MonitorDeviceCommand.Execute(null);
        Assert.True(vm.IsSpoofed);
        Assert.True(model.IsSpoofed);
        Assert.Equal("MONITORED", vm.StatusBadgeText);

        // Toggle via clickable badge command (MONITORED -> BYPASSED -> MONITORED)
        vm.ToggleMonitoredCommand.Execute(null);
        Assert.Equal("BYPASSED", vm.StatusBadgeText);
        vm.ToggleMonitoredCommand.Execute(null);
        Assert.Equal("MONITORED", vm.StatusBadgeText);

        // Setting a bandwidth cap on a BYPASSED device automatically switches it back to MONITORED/THROTTLED
        vm.BypassDeviceCommand.Execute(null);
        Assert.Equal("BYPASSED", vm.StatusBadgeText);
        vm.DownloadCapKBps = 250;
        Assert.True(vm.IsSpoofed);
        Assert.Equal("THROTTLED", vm.StatusBadgeText);
    }

    [Fact]
    public void WpcapNative_LoadsInstalledDriverVersion()
    {
        bool available = WpcapNative.IsDriverAvailable(out string versionInfo);
        Assert.False(string.IsNullOrWhiteSpace(versionInfo));
        if (available)
        {
            Assert.Contains("cap", versionInfo, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_tempProfileFile))
        {
            File.Delete(_tempProfileFile);
        }
    }
}
