using FishyNet.Core;
using FishyNet.Core.Packets;
using FishyNet.Features.SmartSchedules;
using Xunit;

namespace FishyNet.Tests;

public sealed class SmartScheduleEngineTests
{
    [Fact]
    public void AutoGamingLatencyGuard_TriggersWhenPingExceedsThreshold_AndRestoresWhenRecovered()
    {
        var engine = new SmartScheduleEngine();
        engine.PopulateDefaultRules();

        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("00:11:22:33:44:01", out MacAddress gwMac);
        var gateway = new NetworkDevice(gwIp, gwMac, isGateway: true);

        Ipv4Address.TryParse("192.168.1.50", out Ipv4Address tvIp);
        MacAddress.TryParse("00:11:22:33:44:50", out MacAddress tvMac);
        var tvDevice = new NetworkDevice(tvIp, tvMac)
        {
            DeviceCategory = "Media/Console",
            DownloadCapKBps = 5000,
            UploadCapKBps = 2000
        };

        Ipv4Address.TryParse("192.168.1.60", out Ipv4Address pcIp);
        MacAddress.TryParse("00:11:22:33:44:60", out MacAddress pcMac);
        var pcDevice = new NetworkDevice(pcIp, pcMac, isLocalHost: true)
        {
            DeviceCategory = "Workstation",
            DownloadCapKBps = 0,
            UploadCapKBps = 0
        };

        List<NetworkDevice> devices = [gateway, tvDevice, pcDevice];
        DateTime now = new(2026, 11, 10, 14, 0, 0);

        // 1. Initial evaluation with normal ping (25 ms) -> Rule should stay in Standby
        engine.Evaluate(currentGatewayPingMs: 25.0, devices, now);

        SmartScheduleRule pingRule = Assert.Single(engine.Rules, r => r.RuleType == SmartRuleType.PingLatencyTrigger);
        Assert.False(pingRule.IsCurrentlyTriggered);
        Assert.Equal(5000, tvDevice.DownloadCapKBps);
        Assert.Equal(2000, tvDevice.UploadCapKBps);

        // 2. High ping spike (85 ms >= 60 ms threshold) -> Rule triggers and auto-throttles TV
        engine.Evaluate(currentGatewayPingMs: 85.0, devices, now);

        Assert.True(pingRule.IsCurrentlyTriggered);
        Assert.Contains("ACTIVE", pingRule.StatusDescription);
        Assert.Equal(100, tvDevice.DownloadCapKBps); // Throttled to 100 KB/s
        Assert.Equal(100, tvDevice.UploadCapKBps);
        Assert.Equal(0, pcDevice.DownloadCapKBps);   // LocalHost PC is unaffected!

        // 3. Ping recovers back to 18 ms -> Rule ceases and gracefully restores TV's original caps
        engine.Evaluate(currentGatewayPingMs: 18.0, devices, now);

        Assert.False(pingRule.IsCurrentlyTriggered);
        Assert.Equal(5000, tvDevice.DownloadCapKBps); // Restored to 5000 KB/s
        Assert.Equal(2000, tvDevice.UploadCapKBps);   // Restored to 2000 KB/s
    }

    [Fact]
    public void TimeOfDaySchedule_EnforcesCapInsideWindow_AndRestoresOutsideWindow()
    {
        var engine = new SmartScheduleEngine();
        engine.Rules.Clear();

        var nightRule = new SmartScheduleRule(
            name: "Nighttime Cap",
            description: "Cap mobile from 8PM to 11PM",
            ruleType: SmartRuleType.TimeOfDayWindow,
            isEnabled: true,
            targetCategory: "Mobile",
            throttleCapKBps: 250,
            startTime: new TimeSpan(20, 0, 0),
            endTime: new TimeSpan(23, 0, 0));
        engine.Rules.Add(nightRule);

        Ipv4Address.TryParse("192.168.1.105", out Ipv4Address phoneIp);
        MacAddress.TryParse("AA:BB:CC:11:22:33", out MacAddress phoneMac);
        var phone = new NetworkDevice(phoneIp, phoneMac)
        {
            DeviceCategory = "Mobile",
            DownloadCapKBps = 0,
            UploadCapKBps = 0
        };

        List<NetworkDevice> devices = [phone];

        // 1. At 7:30 PM (before window) -> Standby
        DateTime beforeWindow = new(2026, 11, 10, 19, 30, 0);
        engine.Evaluate(0, devices, beforeWindow);
        Assert.False(nightRule.IsCurrentlyTriggered);
        Assert.Equal(0, phone.DownloadCapKBps);

        // 2. At 8:15 PM (inside window) -> ACTIVE, throttles to 250 KB/s
        DateTime insideWindow = new(2026, 11, 10, 20, 15, 0);
        engine.Evaluate(0, devices, insideWindow);
        Assert.True(nightRule.IsCurrentlyTriggered);
        Assert.Equal(250, phone.DownloadCapKBps);

        // 3. At 11:30 PM (after window) -> Standby, restores to 0 KB/s
        DateTime afterWindow = new(2026, 11, 10, 23, 30, 0);
        engine.Evaluate(0, devices, afterWindow);
        Assert.False(nightRule.IsCurrentlyTriggered);
        Assert.Equal(0, phone.DownloadCapKBps);
    }

    [Fact]
    public void DisablingRule_RevertsAppliedCapsImmediately()
    {
        var engine = new SmartScheduleEngine();
        engine.Rules.Clear();

        var rule = new SmartScheduleRule(
            name: "Test Rule",
            description: "Test",
            ruleType: SmartRuleType.PingLatencyTrigger,
            isEnabled: true,
            latencyThresholdMs: 50.0,
            targetCategory: "AllOther",
            throttleCapKBps: 80);
        engine.Rules.Add(rule);

        Ipv4Address.TryParse("192.168.1.200", out Ipv4Address ip);
        MacAddress.TryParse("12:34:56:78:9A:BC", out MacAddress mac);
        var dev = new NetworkDevice(ip, mac)
        {
            DownloadCapKBps = 1500,
            UploadCapKBps = 500
        };

        List<NetworkDevice> devices = [dev];
        DateTime now = DateTime.UtcNow;

        // Trigger rule
        engine.Evaluate(75.0, devices, now);
        Assert.True(rule.IsCurrentlyTriggered);
        Assert.Equal(80, dev.DownloadCapKBps);

        // Disable rule
        rule.IsEnabled = false;
        engine.Evaluate(75.0, devices, now);

        Assert.False(rule.IsCurrentlyTriggered);
        Assert.Equal(1500, dev.DownloadCapKBps);
        Assert.Equal(500, dev.UploadCapKBps);
    }

    [Fact]
    public void OvernightTimeWindow_EvaluatesProperlyAcrossMidnight()
    {
        var engine = new SmartScheduleEngine();
        engine.Rules.Clear();

        var overnightRule = new SmartScheduleRule(
            name: "Overnight Window",
            description: "Test overnight window",
            ruleType: SmartRuleType.TimeOfDayWindow,
            isEnabled: true,
            targetCategory: "AllOther",
            throttleCapKBps: 50,
            startTime: new TimeSpan(22, 0, 0), // 10 PM
            endTime: new TimeSpan(4, 0, 0));   // 4 AM

        engine.Rules.Add(overnightRule);

        Ipv4Address.TryParse("192.168.1.111", out Ipv4Address ip);
        MacAddress.TryParse("AA:BB:CC:DD:EE:FF", out MacAddress mac);
        var dev = new NetworkDevice(ip, mac);
        List<NetworkDevice> devices = [dev];

        // 1. At 11:30 PM (before midnight) -> ACTIVE
        engine.Evaluate(0, devices, new DateTime(2026, 11, 10, 23, 30, 0));
        Assert.True(overnightRule.IsCurrentlyTriggered);
        Assert.Equal(50, dev.DownloadCapKBps);

        // 2. At 2:15 AM (after midnight) -> Still ACTIVE
        engine.Evaluate(0, devices, new DateTime(2026, 11, 11, 2, 15, 0));
        Assert.True(overnightRule.IsCurrentlyTriggered);
        Assert.Equal(50, dev.DownloadCapKBps);

        // 3. At 6:00 AM (outside window) -> Standby and restored
        engine.Evaluate(0, devices, new DateTime(2026, 11, 11, 6, 0, 0));
        Assert.False(overnightRule.IsCurrentlyTriggered);
        Assert.Equal(0, dev.DownloadCapKBps);
    }

    [Fact]
    public void EdgeCases_EmptyDevicesAndNegativePing_HandledGracefully()
    {
        var engine = new SmartScheduleEngine();
        engine.PopulateDefaultRules();

        // 1. Empty devices list
        engine.Evaluate(120.0, [], DateTime.Now);

        // 2. Negative ping
        engine.Evaluate(-1.0, [], DateTime.Now);

        // 3. Zero ping
        engine.Evaluate(0.0, [], DateTime.Now);
    }
}
