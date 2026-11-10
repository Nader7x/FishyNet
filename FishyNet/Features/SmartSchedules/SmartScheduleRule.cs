using FishyNet.ViewModels;

namespace FishyNet.Features.SmartSchedules;

public enum SmartRuleType
{
    PingLatencyTrigger,
    TimeOfDayWindow
}

public sealed class SmartScheduleRule : ObservableObject
{
    private bool _isEnabled;
    private string _name;
    private string _description;
    private SmartRuleType _ruleType;
    private double _latencyThresholdMs;
    private string _targetCategory;
    private int _throttleCapKBps;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private bool _isCurrentlyTriggered;
    private string _statusDescription = "Idle";

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public SmartRuleType RuleType
    {
        get => _ruleType;
        set => SetProperty(ref _ruleType, value);
    }

    public double LatencyThresholdMs
    {
        get => _latencyThresholdMs;
        set => SetProperty(ref _latencyThresholdMs, value);
    }

    public string TargetCategory
    {
        get => _targetCategory;
        set => SetProperty(ref _targetCategory, value);
    }

    public int ThrottleCapKBps
    {
        get => _throttleCapKBps;
        set => SetProperty(ref _throttleCapKBps, value);
    }

    public TimeSpan StartTime
    {
        get => _startTime;
        set => SetProperty(ref _startTime, value);
    }

    public TimeSpan EndTime
    {
        get => _endTime;
        set => SetProperty(ref _endTime, value);
    }

    public bool IsCurrentlyTriggered
    {
        get => _isCurrentlyTriggered;
        set => SetProperty(ref _isCurrentlyTriggered, value);
    }

    public string StatusDescription
    {
        get => _statusDescription;
        set => SetProperty(ref _statusDescription, value);
    }

    public SmartScheduleRule(
        string name,
        string description,
        SmartRuleType ruleType,
        bool isEnabled = true,
        double latencyThresholdMs = 60.0,
        string targetCategory = "AllOther",
        int throttleCapKBps = 100,
        TimeSpan? startTime = null,
        TimeSpan? endTime = null)
    {
        _name = name;
        _description = description;
        _ruleType = ruleType;
        _isEnabled = isEnabled;
        _latencyThresholdMs = latencyThresholdMs;
        _targetCategory = targetCategory;
        _throttleCapKBps = throttleCapKBps;
        _startTime = startTime ?? new TimeSpan(20, 0, 0); // 8:00 PM
        _endTime = endTime ?? new TimeSpan(23, 59, 59);   // 11:59 PM
    }
}
