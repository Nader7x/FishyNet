using FishyNet.Core;
using FishyNet.Core.Packets;
using FishyNet.ViewModels;
using Xunit;

namespace FishyNet.Tests;

public sealed class UiRedesignViewModelTests
{
    [Fact]
    public void GamingBoost_ThrottlesAllOtherDevices_AndRestoresOnToggle()
    {
        var vm = new MainViewModel();

        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("00:11:22:33:44:01", out MacAddress gwMac);
        var gwDev = new NetworkDevice(gwIp, gwMac, isGateway: true);
        var gwVm = new DeviceViewModel(gwDev, useBitsPerSecond: false);

        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address hostIp);
        MacAddress.TryParse("00:11:22:33:44:10", out MacAddress hostMac);
        var hostDev = new NetworkDevice(hostIp, hostMac, isLocalHost: true);
        var hostVm = new DeviceViewModel(hostDev, useBitsPerSecond: false);

        Ipv4Address.TryParse("192.168.1.25", out Ipv4Address tvIp);
        MacAddress.TryParse("00:11:22:33:44:25", out MacAddress tvMac);
        var tvDev = new NetworkDevice(tvIp, tvMac)
        {
            DeviceCategory = "Media/Console",
            DownloadCapKBps = 5000,
            UploadCapKBps = 2000
        };
        var tvVm = new DeviceViewModel(tvDev, useBitsPerSecond: false);

        Ipv4Address.TryParse("192.168.1.35", out Ipv4Address phoneIp);
        MacAddress.TryParse("00:11:22:33:44:35", out MacAddress phoneMac);
        var phoneDev = new NetworkDevice(phoneIp, phoneMac)
        {
            DeviceCategory = "Mobile",
            DownloadCapKBps = 0,
            UploadCapKBps = 0
        };
        var phoneVm = new DeviceViewModel(phoneDev, useBitsPerSecond: false);

        vm.Devices.Add(gwVm);
        vm.Devices.Add(hostVm);
        vm.Devices.Add(tvVm);
        vm.Devices.Add(phoneVm);

        // 1. Initial State
        Assert.False(vm.IsGamingBoostActive);
        Assert.Equal("BOOST MY PC", vm.GamingBoostButtonText);

        // 2. Toggle Gaming Boost ON
        vm.ToggleGamingBoostCommand.Execute(null);

        Assert.True(vm.IsGamingBoostActive);
        Assert.Equal("BOOST ACTIVE", vm.GamingBoostButtonText);
        Assert.Contains("BOOST ACTIVE", vm.TotalSpeedTickerText);

        // Gateway and Host should NOT be throttled
        Assert.Equal(0, gwVm.DownloadCapKBps);
        Assert.Equal(0, hostVm.DownloadCapKBps);

        // TV and Phone should be capped to 64 KB/s down and 32 KB/s up
        Assert.Equal(64, tvVm.DownloadCapKBps);
        Assert.Equal(32, tvVm.UploadCapKBps);
        Assert.Equal(64, phoneVm.DownloadCapKBps);
        Assert.Equal(32, phoneVm.UploadCapKBps);

        // 3. Toggle Gaming Boost OFF -> Original caps restored!
        vm.ToggleGamingBoostCommand.Execute(null);

        Assert.False(vm.IsGamingBoostActive);
        Assert.Equal("BOOST MY PC", vm.GamingBoostButtonText);
        Assert.Equal(5000, tvVm.DownloadCapKBps);
        Assert.Equal(2000, tvVm.UploadCapKBps);
        Assert.Equal(0, phoneVm.DownloadCapKBps);
        Assert.Equal(0, phoneVm.UploadCapKBps);
    }

    [Fact]
    public void ViewModeSwitcher_SwitchesBetweenTableBentoAndTopology()
    {
        var vm = new MainViewModel();

        Assert.Equal(DeviceViewMode.Table, vm.CurrentViewMode);
        Assert.True(vm.IsTableView);
        Assert.False(vm.IsBentoView);
        Assert.False(vm.IsTopologyView);

        vm.SetViewModeCommand.Execute("Bento");
        Assert.Equal(DeviceViewMode.Bento, vm.CurrentViewMode);
        Assert.False(vm.IsTableView);
        Assert.True(vm.IsBentoView);
        Assert.False(vm.IsTopologyView);

        vm.SetViewModeCommand.Execute("Topology");
        Assert.Equal(DeviceViewMode.Topology, vm.CurrentViewMode);
        Assert.False(vm.IsTableView);
        Assert.False(vm.IsBentoView);
        Assert.True(vm.IsTopologyView);

        vm.SetViewModeCommand.Execute("Table");
        Assert.Equal(DeviceViewMode.Table, vm.CurrentViewMode);
        Assert.True(vm.IsTableView);
    }

    [Fact]
    public void CollapsibleInspectorDrawer_OpensOnSelect_AndClosesOnCommand()
    {
        var vm = new MainViewModel();

        Ipv4Address.TryParse("192.168.1.15", out Ipv4Address ip);
        MacAddress.TryParse("00:11:22:33:44:15", out MacAddress mac);
        var dev = new NetworkDevice(ip, mac);
        var devVm = new DeviceViewModel(dev, useBitsPerSecond: false);

        vm.IsInspectorOpen = false;
        Assert.False(vm.IsInspectorOpen);

        // Selecting a device auto-opens the drawer
        vm.SelectedDevice = devVm;
        Assert.True(vm.IsInspectorOpen);

        // Close command collapses the drawer
        vm.CloseInspectorCommand.Execute(null);
        Assert.False(vm.IsInspectorOpen);

        // Toggle command re-opens the drawer
        vm.ToggleInspectorCommand.Execute(null);
        Assert.True(vm.IsInspectorOpen);

        // Deselect command clears selected device
        vm.DeselectDeviceCommand.Execute(null);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public void MiniHudAndWindowChromeCommands_TriggerEvents()
    {
        var vm = new MainViewModel();

        bool miniHudTriggered = false;
        bool miniHudState = false;
        vm.RequestMiniHudVisibilityChanged += state =>
        {
            miniHudTriggered = true;
            miniHudState = state;
        };

        bool minTriggered = false;
        bool maxTriggered = false;
        bool closeTriggered = false;

        vm.RequestMinimize += () => minTriggered = true;
        vm.RequestMaximizeRestore += () => maxTriggered = true;
        vm.RequestClose += () => closeTriggered = true;

        vm.ToggleMiniHudCommand.Execute(null);
        Assert.True(miniHudTriggered);
        Assert.True(miniHudState);

        vm.MinimizeWindowCommand.Execute(null);
        Assert.True(minTriggered);

        vm.MaximizeWindowCommand.Execute(null);
        Assert.True(maxTriggered);

        vm.CloseWindowCommand.Execute(null);
        Assert.True(closeTriggered);
    }

    [Fact]
    public void DeviceViewModel_ExposesCategoryGlyph_AndSliderBinding()
    {
        Ipv4Address.TryParse("192.168.1.55", out Ipv4Address ip);
        MacAddress.TryParse("00:11:22:33:44:55", out MacAddress mac);
        var phone = new NetworkDevice(ip, mac)
        {
            DeviceCategory = "Mobile",
            DownloadCapKBps = 500
        };
        var vm = new DeviceViewModel(phone, useBitsPerSecond: false);

        Assert.Equal("📱", vm.CategoryIconGlyph);
        Assert.Equal(500.0, vm.DownloadCapSliderValue);
        Assert.True(vm.IsThrottled);

        vm.DownloadCapSliderValue = 1200.0;
        Assert.Equal(1200, vm.DownloadCapKBps);
        Assert.Equal(1200, phone.DownloadCapKBps);
    }

    [Fact]
    public void GamingBoost_EdgeCases_EmptyOrOnlyHostDevices()
    {
        var vm = new MainViewModel();

        // 1. Empty devices
        vm.ToggleGamingBoostCommand.Execute(null);
        Assert.True(vm.IsGamingBoostActive);
        vm.ToggleGamingBoostCommand.Execute(null);
        Assert.False(vm.IsGamingBoostActive);

        // 2. Only Gateway and Localhost
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("00:11:22:33:44:01", out MacAddress gwMac);
        var gwVm = new DeviceViewModel(new NetworkDevice(gwIp, gwMac, isGateway: true), false);

        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address hostIp);
        MacAddress.TryParse("00:11:22:33:44:10", out MacAddress hostMac);
        var hostVm = new DeviceViewModel(new NetworkDevice(hostIp, hostMac, isLocalHost: true), false);

        vm.Devices.Add(gwVm);
        vm.Devices.Add(hostVm);

        vm.ToggleGamingBoostCommand.Execute(null);
        Assert.True(vm.IsGamingBoostActive);
        Assert.Equal(0, gwVm.DownloadCapKBps);
        Assert.Equal(0, hostVm.DownloadCapKBps);

        vm.ToggleGamingBoostCommand.Execute(null);
        Assert.False(vm.IsGamingBoostActive);
    }

    [Fact]
    public void SetViewMode_InvalidString_LeavesCurrentModeUnchanged()
    {
        var vm = new MainViewModel();
        vm.SetViewModeCommand.Execute("NonExistentMode");
        Assert.Equal(DeviceViewMode.Table, vm.CurrentViewMode);

        vm.DeselectDeviceCommand.Execute(null);
        Assert.Null(vm.SelectedDevice);
    }
}
