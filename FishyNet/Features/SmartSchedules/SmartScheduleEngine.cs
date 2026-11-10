using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using FishyNet.Core;
using FishyNet.Core.Packets;

namespace FishyNet.Features.SmartSchedules;

/// <summary>
/// Autonomous schedule and latency trigger engine.
/// Evaluates rules such as time-of-day caps and high-ping Auto-Gaming throttling,
/// automatically throttling matching devices and gracefully restoring original caps when conditions clear.
/// </summary>
public sealed class SmartScheduleEngine
{
    private readonly struct SavedCap(int downloadCap, int uploadCap)
    {
        public readonly int DownloadCap = downloadCap;
        public readonly int UploadCap = uploadCap;
    }

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<MacAddress, SavedCap>> _activeRuleSavedCaps = new();

    public ObservableCollection<SmartScheduleRule> Rules { get; } = [];

    public SmartScheduleEngine()
    {
        PopulateDefaultRules();
    }

    public void PopulateDefaultRules()
    {
        Rules.Clear();

        // 1. Auto-Gaming Latency Guard (> 60 ms ping)
        Rules.Add(new SmartScheduleRule(
            name: "Auto-Gaming Latency Guard",
            description: "Auto-throttle TV & Mobile devices when Gateway Ping exceeds 60 ms to eliminate lag spikes",
            ruleType: SmartRuleType.PingLatencyTrigger,
            isEnabled: true,
            latencyThresholdMs: 60.0,
            targetCategory: "Media/Console",
            throttleCapKBps: 100));

        // 2. Nighttime Bandwidth Saver (8:00 PM - 12:00 AM)
        Rules.Add(new SmartScheduleRule(
            name: "Nighttime Bandwidth Saver",
            description: "Enforce 500 KB/s cap on heavy streaming devices between 8:00 PM and 12:00 AM",
            ruleType: SmartRuleType.TimeOfDayWindow,
            isEnabled: false,
            targetCategory: "Mobile",
            throttleCapKBps: 500,
            startTime: new TimeSpan(20, 0, 0),
            endTime: new TimeSpan(23, 59, 59)));

        // 3. Workday Priority Shield (9:00 AM - 5:00 PM)
        Rules.Add(new SmartScheduleRule(
            name: "Workday Priority Shield",
            description: "Enforce 200 KB/s cap on consoles & entertainment devices during working hours (9 AM - 5 PM)",
            ruleType: SmartRuleType.TimeOfDayWindow,
            isEnabled: false,
            targetCategory: "Media/Console",
            throttleCapKBps: 200,
            startTime: new TimeSpan(9, 0, 0),
            endTime: new TimeSpan(17, 0, 0)));
    }

    /// <summary>
    /// Evaluates all rules against current telemetry and device list.
    /// Safely applies caps and restores original caps upon rule cessation.
    /// </summary>
    public void Evaluate(double currentGatewayPingMs, IReadOnlyList<NetworkDevice> devices, DateTime now)
    {
        TimeSpan currentTime = now.TimeOfDay;

        foreach (SmartScheduleRule rule in Rules)
        {
            if (!rule.IsEnabled)
            {
                if (rule.IsCurrentlyTriggered)
                {
                    RevertRule(rule, devices);
                    rule.IsCurrentlyTriggered = false;
                    rule.StatusDescription = "Disabled";
                }
                continue;
            }

            bool shouldTrigger = false;
            string status;

            switch (rule.RuleType)
            {
                case SmartRuleType.PingLatencyTrigger:
                    if (currentGatewayPingMs > 0 && currentGatewayPingMs >= rule.LatencyThresholdMs)
                    {
                        shouldTrigger = true;
                        status = $"ACTIVE: Ping {currentGatewayPingMs:F0} ms >= {rule.LatencyThresholdMs:F0} ms threshold";
                    }
                    else
                    {
                        status = currentGatewayPingMs > 0
                            ? $"Standby (Ping {currentGatewayPingMs:F0} ms < {rule.LatencyThresholdMs:F0} ms)"
                            : "Standby (Awaiting Ping)";
                    }
                    break;

                case SmartRuleType.TimeOfDayWindow:
                    bool inWindow;
                    if (rule.StartTime <= rule.EndTime)
                    {
                        inWindow = currentTime >= rule.StartTime && currentTime <= rule.EndTime;
                    }
                    else
                    {
                        // Overnight window (e.g. 22:00 to 04:00)
                        inWindow = currentTime >= rule.StartTime || currentTime <= rule.EndTime;
                    }

                    if (inWindow)
                    {
                        shouldTrigger = true;
                        status = $"ACTIVE: Inside schedule window ({rule.StartTime:hh\\:mm} - {rule.EndTime:hh\\:mm})";
                    }
                    else
                    {
                        status = $"Standby (Window {rule.StartTime:hh\\:mm} - {rule.EndTime:hh\\:mm})";
                    }
                    break;

                default:
                    status = "Standby";
                    break;
            }

            if (shouldTrigger)
            {
                if (!rule.IsCurrentlyTriggered)
                {
                    ApplyRule(rule, devices);
                    rule.IsCurrentlyTriggered = true;
                }
                rule.StatusDescription = status;
            }
            else
            {
                if (rule.IsCurrentlyTriggered)
                {
                    RevertRule(rule, devices);
                    rule.IsCurrentlyTriggered = false;
                }
                rule.StatusDescription = status;
            }
        }
    }

    private void ApplyRule(SmartScheduleRule rule, IReadOnlyList<NetworkDevice> devices)
    {
        ConcurrentDictionary<MacAddress, SavedCap> savedCaps = _activeRuleSavedCaps.GetOrAdd(rule.Id, _ => new ConcurrentDictionary<MacAddress, SavedCap>());

        foreach (NetworkDevice device in devices)
        {
            if (device.IsGateway || device.IsLocalHost) continue;
            if (!MatchesTargetCategory(device, rule.TargetCategory)) continue;

            // Save original caps if not already saved
            savedCaps.TryAdd(device.Mac, new SavedCap(device.DownloadCapKBps, device.UploadCapKBps));

            // Enforce cap
            device.DownloadCapKBps = rule.ThrottleCapKBps;
            if (device.UploadCapKBps > rule.ThrottleCapKBps || device.UploadCapKBps == 0)
            {
                device.UploadCapKBps = rule.ThrottleCapKBps;
            }
        }
    }

    private void RevertRule(SmartScheduleRule rule, IReadOnlyList<NetworkDevice> devices)
    {
        if (_activeRuleSavedCaps.TryRemove(rule.Id, out ConcurrentDictionary<MacAddress, SavedCap>? savedCaps))
        {
            foreach (NetworkDevice device in devices)
            {
                if (savedCaps.TryGetValue(device.Mac, out SavedCap saved))
                {
                    device.DownloadCapKBps = saved.DownloadCap;
                    device.UploadCapKBps = saved.UploadCap;
                }
            }
        }
    }

    private static bool MatchesTargetCategory(NetworkDevice device, string targetCategory)
    {
        if (string.Equals(targetCategory, "AllOther", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(device.DeviceCategory, targetCategory, StringComparison.OrdinalIgnoreCase)) return true;

        if (string.Equals(targetCategory, "Media/Console", StringComparison.OrdinalIgnoreCase) &&
            (device.DeviceCategory.Contains("Media", StringComparison.OrdinalIgnoreCase) ||
             device.DeviceCategory.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
             device.DeviceCategory.Contains("TV", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
