using System.IO;
using FishyNet.Core;
using FishyNet.Core.Packets;
using FishyNet.Features;
using FishyNet.ViewModels;
using Xunit;

namespace FishyNet.Tests;

public sealed class ThrottleAllAndBatchTests : IDisposable
{
    private readonly string _tempProfileFile;

    public ThrottleAllAndBatchTests()
    {
        _tempProfileFile = Path.Combine(Path.GetTempPath(), $"fishynet_throttle_batch_{Guid.NewGuid():N}.json");
    }

    private static DeviceViewModel CreateDeviceVm(
        string ipText,
        string macText,
        bool isGateway = false,
        bool isLocalHost = false,
        string category = "Device",
        string hostname = "")
    {
        Ipv4Address.TryParse(ipText, out Ipv4Address ip);
        MacAddress.TryParse(macText, out MacAddress mac);
        var dev = new NetworkDevice(ip, mac, isGateway, isLocalHost)
        {
            DeviceCategory = category,
            Hostname = hostname
        };
        return new DeviceViewModel(dev, useBitsPerSecond: false);
    }

    [Fact]
    public void DeviceProfileStore_UpdateBatch_PersistsMultipleDevicesInSingleSave()
    {
        var store = new DeviceProfileStore(_tempProfileFile);

        DeviceViewModel gw = CreateDeviceVm("192.168.1.1", "00:11:22:33:44:01", isGateway: true);
        DeviceViewModel host = CreateDeviceVm("192.168.1.2", "00:11:22:33:44:02", isLocalHost: true);
        DeviceViewModel tv = CreateDeviceVm("192.168.1.20", "00:11:22:33:44:20", category: "Media/Console");
        DeviceViewModel phone = CreateDeviceVm("192.168.1.21", "00:11:22:33:44:21", category: "Mobile");

        gw.Model.DownloadCapKBps = 999; // Should be ignored by UpdateBatch
        host.Model.DownloadCapKBps = 888; // Should be ignored by UpdateBatch

        tv.ApplyBatchLimits(400, 100, isBlocked: false, isSpoofed: true);
        tv.Model.CustomNickname = "Living Room TV";
        phone.ApplyBatchLimits(150, 50, isBlocked: true, isSpoofed: true);

        store.Preferences.ThemePaletteId = "graphite_mint";
        store.Preferences.ThrottleAllEnabled = true;
        store.Preferences.ThrottleAllDownloadCapKBps = 250;
        store.Preferences.ThrottleAllUploadCapKBps = 64;

        store.UpdateBatch([gw.Model, host.Model, tv.Model, phone.Model]);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(2, store.Preferences.Devices.Count);

        var reloaded = new DeviceProfileStore(_tempProfileFile);
        Assert.Equal("graphite_mint", reloaded.Preferences.ThemePaletteId);
        Assert.True(reloaded.Preferences.ThrottleAllEnabled);
        Assert.Equal(250, reloaded.Preferences.ThrottleAllDownloadCapKBps);
        Assert.Equal(64, reloaded.Preferences.ThrottleAllUploadCapKBps);

        var freshTv = new NetworkDevice(tv.Model.Ip, tv.Model.Mac);
        reloaded.ApplySavedProfileToDevice(freshTv);
        Assert.Equal("Living Room TV", freshTv.CustomNickname);
        Assert.Equal(400, freshTv.DownloadCapKBps);
        Assert.Equal(100, freshTv.UploadCapKBps);
        Assert.False(freshTv.IsBlocked);
        Assert.True(freshTv.IsSpoofed);

        var freshPhone = new NetworkDevice(phone.Model.Ip, phone.Model.Mac);
        reloaded.ApplySavedProfileToDevice(freshPhone);
        Assert.Equal(150, freshPhone.DownloadCapKBps);
        Assert.Equal(50, freshPhone.UploadCapKBps);
        Assert.True(freshPhone.IsBlocked);
        Assert.True(freshPhone.IsSpoofed);
    }

    [Fact]
    public void DeviceViewModel_IsBatchSelectedAndApplyBatchLimits_WorkWithoutRuleModifiedWrites()
    {
        DeviceViewModel phone = CreateDeviceVm("192.168.1.30", "00:11:22:33:44:30");
        DeviceViewModel gw = CreateDeviceVm("192.168.1.1", "00:11:22:33:44:01", isGateway: true);

        int ruleModifiedCount = 0;
        int batchChangedCount = 0;
        var changedProps = new List<string?>();

        phone.RuleModified += _ => ruleModifiedCount++;
        phone.BatchSelectionChanged += _ => batchChangedCount++;
        phone.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        phone.IsBatchSelected = true;
        Assert.True(phone.IsBatchSelected);
        Assert.Equal(1, batchChangedCount);
        Assert.Equal(0, ruleModifiedCount);
        Assert.Contains(nameof(DeviceViewModel.IsBatchSelected), changedProps);

        changedProps.Clear();
        phone.ApplyBatchLimits(320, 85, isBlocked: false, isSpoofed: true, suppressRuleModified: true);

        Assert.Equal(0, ruleModifiedCount);
        Assert.Equal(320, phone.DownloadCapKBps);
        Assert.Equal(85, phone.UploadCapKBps);
        Assert.True(phone.IsSpoofed);
        Assert.True(phone.IsThrottled);
        Assert.Equal("THROTTLED", phone.StatusBadgeText);
        Assert.Contains(nameof(DeviceViewModel.DownloadCapKBps), changedProps);
        Assert.Contains(nameof(DeviceViewModel.UploadCapKBps), changedProps);
        Assert.Contains(nameof(DeviceViewModel.IsThrottled), changedProps);

        // Gateway guard on ApplyBatchLimits
        gw.ApplyBatchLimits(100, 50, isBlocked: true, isSpoofed: true, suppressRuleModified: false);
        Assert.Equal(0, gw.DownloadCapKBps);
        Assert.Equal(0, gw.UploadCapKBps);
        Assert.False(gw.IsBlocked);
        Assert.False(gw.IsSpoofed);
    }

    [Fact]
    public void ApplyThrottleAllCommand_SetsCustomCapsGuardsSystemHostsAndSavesOnce()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        DeviceViewModel gw = CreateDeviceVm("192.168.1.1", "00:11:22:33:44:01", isGateway: true);
        DeviceViewModel host = CreateDeviceVm("192.168.1.10", "00:11:22:33:44:10", isLocalHost: true);
        DeviceViewModel bypassed = CreateDeviceVm("192.168.1.25", "00:11:22:33:44:25");
        bypassed.ApplyBatchLimits(null, null, null, isSpoofed: false);
        DeviceViewModel monitored = CreateDeviceVm("192.168.1.26", "00:11:22:33:44:26");
        DeviceViewModel blocked = CreateDeviceVm("192.168.1.27", "00:11:22:33:44:27");
        blocked.ApplyBatchLimits(null, null, isBlocked: true, isSpoofed: true);

        vm.Devices.Add(gw);
        vm.Devices.Add(host);
        vm.Devices.Add(bypassed);
        vm.Devices.Add(monitored);
        vm.Devices.Add(blocked);

        Assert.Equal(250, vm.ThrottleAllDownloadCapKBps);
        Assert.Equal(64, vm.ThrottleAllUploadCapKBps);
        Assert.False(vm.IsThrottleAllActive);
        Assert.True(vm.ThrottleAllKeepAppliedToNewDevices);

        vm.ThrottleAllDownloadCapKBps = 350;
        vm.ThrottleAllUploadCapKBps = 90;
        vm.ApplyThrottleAllCommand.Execute(null);

        Assert.True(vm.IsThrottleAllActive);
        Assert.Equal(1, store.SaveCount);

        // Gateway and LocalHost must never be throttled or spoofed
        Assert.Equal(0, gw.DownloadCapKBps);
        Assert.Equal(0, gw.UploadCapKBps);
        Assert.False(gw.IsSpoofed);
        Assert.Equal(0, host.DownloadCapKBps);
        Assert.Equal(0, host.UploadCapKBps);
        Assert.False(host.IsSpoofed);

        // Bypassed device automatically becomes spoofed/throttled
        Assert.Equal(350, bypassed.DownloadCapKBps);
        Assert.Equal(90, bypassed.UploadCapKBps);
        Assert.True(bypassed.IsSpoofed);
        Assert.Equal("THROTTLED", bypassed.StatusBadgeText);

        Assert.Equal(350, monitored.DownloadCapKBps);
        Assert.Equal(90, monitored.UploadCapKBps);
        Assert.True(monitored.IsSpoofed);

        // Blocked device keeps blocked state while getting caps updated
        Assert.Equal(350, blocked.DownloadCapKBps);
        Assert.Equal(90, blocked.UploadCapKBps);
        Assert.True(blocked.IsBlocked);
        Assert.Equal("BLOCKED", blocked.StatusBadgeText);

        // Reload preferences verify persistence
        using var reloadedVm = new MainViewModel(new DeviceProfileStore(_tempProfileFile));
        Assert.True(reloadedVm.IsThrottleAllActive);
        Assert.Equal(350, reloadedVm.ThrottleAllDownloadCapKBps);
        Assert.Equal(90, reloadedVm.ThrottleAllUploadCapKBps);
    }

    [Fact]
    public void ApplyThrottleAllPresetAndClearAllThrottles_HandlePresetsAndClearing()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        DeviceViewModel tv = CreateDeviceVm("192.168.1.40", "00:11:22:33:44:40");
        DeviceViewModel phone = CreateDeviceVm("192.168.1.41", "00:11:22:33:44:41");
        vm.Devices.Add(tv);
        vm.Devices.Add(phone);

        vm.ApplyThrottleAllPresetCommand.Execute("256:64");
        Assert.True(vm.IsThrottleAllActive);
        Assert.Equal(256, vm.ThrottleAllDownloadCapKBps);
        Assert.Equal(64, vm.ThrottleAllUploadCapKBps);
        Assert.Equal(256, tv.DownloadCapKBps);
        Assert.Equal(64, tv.UploadCapKBps);

        vm.ApplyThrottleAllPresetCommand.Execute("1024");
        Assert.True(vm.IsThrottleAllActive);
        Assert.Equal(1024, vm.ThrottleAllDownloadCapKBps);
        Assert.Equal(1024, phone.DownloadCapKBps);
        Assert.Equal(64, phone.UploadCapKBps);

        vm.ApplyThrottleAllPresetCommand.Execute("5120");
        Assert.Equal(5120, tv.DownloadCapKBps);

        vm.ApplyThrottleAllPresetCommand.Execute("0");
        Assert.False(vm.IsThrottleAllActive);
        Assert.Equal(0, tv.DownloadCapKBps);
        Assert.Equal(0, tv.UploadCapKBps);
        Assert.Equal(0, phone.DownloadCapKBps);
        Assert.Equal(0, phone.UploadCapKBps);

        vm.ApplyThrottleAllPresetCommand.Execute("256");
        Assert.True(vm.IsThrottleAllActive);
        Assert.Equal(256, tv.DownloadCapKBps);

        vm.ClearAllThrottlesCommand.Execute(null);
        Assert.False(vm.IsThrottleAllActive);
        Assert.Equal(0, tv.DownloadCapKBps);
        Assert.Equal(0, tv.UploadCapKBps);
        Assert.False(store.Preferences.ThrottleAllEnabled);
    }

    [Fact]
    public void ProcessRegistryEvents_AutoThrottlesNewlyDiscoveredDevices_WhenThrottleAllActive()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        vm.ThrottleAllDownloadCapKBps = 300;
        vm.ThrottleAllUploadCapKBps = 80;
        vm.ApplyThrottleAllCommand.Execute(null);
        Assert.True(vm.IsThrottleAllActive);
        Assert.True(vm.ThrottleAllKeepAppliedToNewDevices);

        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("00:11:22:33:44:01", out MacAddress gwMac);
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        MacAddress.TryParse("00:11:22:33:44:10", out MacAddress localMac);
        vm.Registry.ConfigureLocalNetwork(localIp, localMac, gwIp);

        NetworkDevice gwDev = vm.Registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);
        gwDev.Hostname = "router.local";

        Ipv4Address.TryParse("192.168.1.77", out Ipv4Address phoneIp);
        MacAddress.TryParse("00:11:22:33:44:77", out MacAddress phoneMac);
        NetworkDevice phoneDev = vm.Registry.RegisterOrUpdateDevice(phoneIp, phoneMac);
        phoneDev.Hostname = "guest-phone";

        int processed = vm.ProcessPendingRegistryEvents();
        Assert.True(processed >= 3);

        DeviceViewModel phoneVm = Assert.Single(vm.Devices, d => d.Model.Mac == phoneMac);
        Assert.Equal(300, phoneVm.DownloadCapKBps);
        Assert.Equal(80, phoneVm.UploadCapKBps);
        Assert.True(phoneVm.IsSpoofed);

        DeviceViewModel gwVm = Assert.Single(vm.Devices, d => d.Model.Mac == gwMac);
        Assert.Equal(0, gwVm.DownloadCapKBps);
        Assert.Equal(0, gwVm.UploadCapKBps);
        Assert.False(gwVm.IsSpoofed);

        DeviceViewModel localVm = Assert.Single(vm.Devices, d => d.Model.Mac == localMac);
        Assert.Equal(0, localVm.DownloadCapKBps);
        Assert.False(localVm.IsSpoofed);

        // When ThrottleAllKeepAppliedToNewDevices is false, subsequent devices are not auto-throttled
        vm.ThrottleAllKeepAppliedToNewDevices = false;
        Ipv4Address.TryParse("192.168.1.88", out Ipv4Address tabletIp);
        MacAddress.TryParse("00:11:22:33:44:88", out MacAddress tabletMac);
        NetworkDevice tabletDev = vm.Registry.RegisterOrUpdateDevice(tabletIp, tabletMac);
        tabletDev.Hostname = "guest-tablet";

        vm.ProcessPendingRegistryEvents();

        DeviceViewModel tabletVm = Assert.Single(vm.Devices, d => d.Model.Mac == tabletMac);
        Assert.Equal(0, tabletVm.DownloadCapKBps);
        Assert.Equal(0, tabletVm.UploadCapKBps);
    }

    [Fact]
    public void BatchSelection_TracksCountsToggleSelectAllFilteredAndClearWithoutSaving()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        DeviceViewModel gw = CreateDeviceVm("192.168.1.1", "00:11:22:33:44:01", isGateway: true, hostname: "gateway");
        DeviceViewModel host = CreateDeviceVm("192.168.1.10", "00:11:22:33:44:10", isLocalHost: true, hostname: "host-pc");
        DeviceViewModel tv = CreateDeviceVm("192.168.1.20", "00:11:22:33:44:20", category: "Media/Console", hostname: "livingroom-tv");
        DeviceViewModel phone = CreateDeviceVm("192.168.1.21", "00:11:22:33:44:21", category: "Mobile", hostname: "iphone-16");
        DeviceViewModel laptop = CreateDeviceVm("192.168.1.22", "00:11:22:33:44:22", category: "Workstation", hostname: "work-laptop");

        vm.Devices.Add(gw);
        vm.Devices.Add(host);
        vm.Devices.Add(tv);
        vm.Devices.Add(phone);
        vm.Devices.Add(laptop);

        Assert.Equal(0, vm.SelectedBatchCount);
        Assert.False(vm.HasBatchSelection);
        Assert.False(vm.IsAllFilteredSelected);
        Assert.False(vm.ApplyBatchThrottleCommand.CanExecute(null));

        phone.IsBatchSelected = true;
        Assert.Equal(1, vm.SelectedBatchCount);
        Assert.True(vm.HasBatchSelection);
        Assert.False(vm.IsAllFilteredSelected);
        Assert.True(vm.ApplyBatchThrottleCommand.CanExecute(null));

        // Even if Gateway is marked IsBatchSelected, it is excluded from actionable selection count
        gw.IsBatchSelected = true;
        Assert.Equal(1, vm.SelectedBatchCount);

        // Filtered toggle affects only matching filtered rows
        vm.SearchQuery = "iphone";
        Assert.True(vm.IsAllFilteredSelected);
        vm.ToggleSelectAllFilteredCommand.Execute(null); // Unselects filtered iphone
        Assert.False(phone.IsBatchSelected);
        Assert.False(vm.IsAllFilteredSelected);

        vm.SearchQuery = string.Empty;
        vm.ToggleSelectAllFilteredCommand.Execute(null); // Selects all CanThrottle devices
        Assert.Equal(3, vm.SelectedBatchCount);
        Assert.True(vm.IsAllFilteredSelected);
        Assert.True(tv.IsBatchSelected);
        Assert.True(phone.IsBatchSelected);
        Assert.True(laptop.IsBatchSelected);
        Assert.False(host.IsBatchSelected);

        vm.ToggleSelectAllFilteredCommand.Execute(null); // Unselects all filtered
        Assert.Equal(0, vm.SelectedBatchCount);
        Assert.False(vm.HasBatchSelection);
        Assert.False(vm.IsAllFilteredSelected);

        tv.IsBatchSelected = true;
        laptop.IsBatchSelected = true;
        Assert.Equal(2, vm.SelectedBatchCount);

        vm.ClearBatchSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedBatchCount);
        Assert.False(vm.HasBatchSelection);
        Assert.False(tv.IsBatchSelected);
        Assert.False(laptop.IsBatchSelected);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void BatchThrottleCommands_MutateOnlyCheckedThrottleableDevicesInSingleSaveEach()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        DeviceViewModel gw = CreateDeviceVm("192.168.1.1", "00:11:22:33:44:01", isGateway: true);
        DeviceViewModel tv = CreateDeviceVm("192.168.1.20", "00:11:22:33:44:20");
        DeviceViewModel phone = CreateDeviceVm("192.168.1.21", "00:11:22:33:44:21");
        DeviceViewModel untouched = CreateDeviceVm("192.168.1.22", "00:11:22:33:44:22");

        vm.Devices.Add(gw);
        vm.Devices.Add(tv);
        vm.Devices.Add(phone);
        vm.Devices.Add(untouched);

        Assert.Equal(500, vm.BatchDownloadCapKBps);
        Assert.Equal(120, vm.BatchUploadCapKBps);

        gw.IsBatchSelected = true;
        tv.IsBatchSelected = true;
        phone.IsBatchSelected = true;

        // 1. ApplyBatchThrottle
        vm.BatchDownloadCapKBps = 450;
        vm.BatchUploadCapKBps = 110;
        vm.ApplyBatchThrottleCommand.Execute(null);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0, gw.DownloadCapKBps);
        Assert.Equal(450, tv.DownloadCapKBps);
        Assert.Equal(110, tv.UploadCapKBps);
        Assert.Equal(450, phone.DownloadCapKBps);
        Assert.Equal(110, phone.UploadCapKBps);
        Assert.Equal(0, untouched.DownloadCapKBps);

        // 2. BatchBypass & BatchMonitor
        vm.BatchBypassCommand.Execute(null);
        Assert.Equal(2, store.SaveCount);
        Assert.False(tv.IsSpoofed);
        Assert.False(phone.IsSpoofed);
        Assert.True(untouched.IsSpoofed);

        vm.BatchMonitorCommand.Execute(null);
        Assert.Equal(3, store.SaveCount);
        Assert.True(tv.IsSpoofed);
        Assert.True(phone.IsSpoofed);

        // 3. BatchBlock & BatchUnblock
        vm.BatchBlockCommand.Execute(null);
        Assert.Equal(4, store.SaveCount);
        Assert.True(tv.IsBlocked);
        Assert.True(phone.IsBlocked);
        Assert.False(untouched.IsBlocked);
        Assert.False(gw.IsBlocked);

        vm.BatchUnblockCommand.Execute(null);
        Assert.Equal(5, store.SaveCount);
        Assert.False(tv.IsBlocked);
        Assert.False(phone.IsBlocked);

        // 4. BatchClearCaps
        vm.BatchClearCapsCommand.Execute(null);
        Assert.Equal(6, store.SaveCount);
        Assert.Equal(0, tv.DownloadCapKBps);
        Assert.Equal(0, tv.UploadCapKBps);
        Assert.Equal(0, phone.DownloadCapKBps);
        Assert.Equal(0, phone.UploadCapKBps);
    }

    [Fact]
    public void ThemePaletteAndActiveSection_PersistAndSyncCorrectly()
    {
        var store = new DeviceProfileStore(_tempProfileFile);
        using var vm = new MainViewModel(store);

        Assert.Equal("obsidian_cyan", vm.ThemePaletteId);
        Assert.Equal(4, vm.AvailableThemePalettes.Count);
        Assert.Contains(vm.AvailableThemePalettes, p => p.Id == "obsidian_cyan");
        Assert.Contains(vm.AvailableThemePalettes, p => p.Id == "graphite_mint");
        Assert.Contains(vm.AvailableThemePalettes, p => p.Id == "carbon_cobalt");
        Assert.Contains(vm.AvailableThemePalettes, p => p.Id == "smoked_onyx");

        string? requestedPalette = null;
        vm.ThemePaletteRequested += id => requestedPalette = id;

        vm.SelectThemePaletteCommand.Execute("carbon_cobalt");
        Assert.Equal("carbon_cobalt", vm.ThemePaletteId);
        Assert.Equal("carbon_cobalt", requestedPalette);

        // Invalid palette ID is ignored
        requestedPalette = null;
        vm.SelectThemePaletteCommand.Execute("invalid_palette");
        Assert.Equal("carbon_cobalt", vm.ThemePaletteId);
        Assert.Null(requestedPalette);

        // Selecting via ThemePaletteOption record
        ThemePaletteOption mint = Assert.Single(vm.AvailableThemePalettes, p => p.Id == "graphite_mint");
        vm.SelectThemePaletteCommand.Execute(mint);
        Assert.Equal("graphite_mint", vm.ThemePaletteId);
        Assert.Equal("graphite_mint", requestedPalette);

        // Reloading restores persisted palette
        using var reloaded = new MainViewModel(new DeviceProfileStore(_tempProfileFile));
        Assert.Equal("graphite_mint", reloaded.ThemePaletteId);

        // Section navigation & DeviceViewMode sync
        Assert.Equal(AppSection.LanControl, vm.ActiveSection);
        Assert.Equal(DeviceViewMode.Table, vm.CurrentViewMode);

        vm.NavigateSectionCommand.Execute("AppMonitor");
        Assert.Equal(AppSection.AppMonitor, vm.ActiveSection);

        vm.NavigateSectionCommand.Execute("HistoryPolicies");
        Assert.Equal(AppSection.HistoryPolicies, vm.ActiveSection);

        vm.SetViewModeCommand.Execute("Bento");
        Assert.Equal(DeviceViewMode.Bento, vm.CurrentViewMode);
        Assert.Equal(AppSection.LanControl, vm.ActiveSection);

        vm.NavigateSectionCommand.Execute(AppSection.TopologySecurity);
        Assert.Equal(AppSection.TopologySecurity, vm.ActiveSection);
        Assert.Equal(DeviceViewMode.Topology, vm.CurrentViewMode);
        Assert.True(vm.IsTopologyView);

        vm.NavigateSectionCommand.Execute("LanControl");
        Assert.Equal(AppSection.LanControl, vm.ActiveSection);
        Assert.Equal(DeviceViewMode.Bento, vm.CurrentViewMode);

        vm.SetViewModeCommand.Execute("Topology");
        Assert.Equal(AppSection.TopologySecurity, vm.ActiveSection);
    }

    public void Dispose()
    {
        if (File.Exists(_tempProfileFile))
        {
            File.Delete(_tempProfileFile);
        }
    }
}
