using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FishyNet.Core;
using FishyNet.Core.Packets;

namespace FishyNet.Features;

public sealed class SavedDeviceProfile
{
    public string MacAddress { get; set; } = string.Empty;
    public string CustomNickname { get; set; } = string.Empty;
    public string Category { get; set; } = "Device";
    public int DownloadCapKBps { get; set; }
    public int UploadCapKBps { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsSpoofed { get; set; } = true;
    public long DailyQuotaMB { get; set; }
    public List<string> BlockedDomains { get; set; } = [];
}

public sealed record BandwidthPresetProfile(
    string Id,
    string Name,
    string Description,
    int DownloadCapKBps,
    int UploadCapKBps,
    bool BlockAll);

public sealed class AppPreferences
{
    public bool UseBitsPerSecond { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnNewDevice { get; set; } = true;
    public bool NotifyOnArpAlert { get; set; } = true;
    public bool NotifyOnQuotaExceeded { get; set; } = true;
    public string? PreferredAdapterId { get; set; }
    public List<SavedDeviceProfile> Devices { get; set; } = [];
    public List<string> GlobalBlockedDomains { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppPreferences))]
internal partial class ProfileStoreJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Manages persistent device customizations (nicknames, categories, caps, quotas, domain rules)
/// and user preferences using AOT-safe System.Text.Json source generation.
/// </summary>
public sealed class DeviceProfileStore
{
    private readonly string _filePath;
    private readonly object _sync = new();
    private AppPreferences _preferences = new();

    public static IReadOnlyList<BandwidthPresetProfile> BuiltInPresets { get; } =
    [
        new("unrestricted", "Unrestricted", "Full LAN speed (0 KB/s cap)", 0, 0, false),
        new("gaming", "Gaming Priority", "Cap background hosts to 180 KB/s Down / 60 KB/s Up for low latency", 180, 60, false),
        new("streaming_sd", "SD Streaming", "Cap to 400 KB/s Down / 100 KB/s Up (~3.2 Mbps)", 400, 100, false),
        new("strict", "Strict Throttle", "Light browsing only: 64 KB/s Down / 32 KB/s Up", 64, 32, false),
        new("quarantine", "Quarantine (Block)", "Drop all forwarded packets & DNS queries", 0, 0, true)
    ];

    public AppPreferences Preferences => _preferences;

    public DeviceProfileStore(string? customFilePath = null)
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
            _filePath = Path.Combine(dir, "profiles.json");

            if (!File.Exists(_filePath))
            {
                string legacyPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SelfishNetV3", "profiles.json");
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
                    AppPreferences? loaded = JsonSerializer.Deserialize(json, ProfileStoreJsonContext.Default.AppPreferences);
                    if (loaded is not null)
                    {
                        _preferences = loaded;
                    }
                }
            }
            catch
            {
                _preferences = new AppPreferences();
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
                string json = JsonSerializer.Serialize(_preferences, ProfileStoreJsonContext.Default.AppPreferences);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            catch
            {
                // Ignore disk write errors on read-only paths
            }
        }
    }

    public void ApplySavedProfileToDevice(NetworkDevice device)
    {
        if (device.IsLocalHost || device.IsGateway) return;

        string macKey = device.Mac.ToString();
        lock (_sync)
        {
            SavedDeviceProfile? saved = _preferences.Devices.FirstOrDefault(
                d => string.Equals(d.MacAddress, macKey, StringComparison.OrdinalIgnoreCase));

            if (saved is not null)
            {
                if (!string.IsNullOrWhiteSpace(saved.CustomNickname))
                {
                    device.CustomNickname = saved.CustomNickname;
                }
                if (!string.IsNullOrWhiteSpace(saved.Category))
                {
                    device.DeviceCategory = saved.Category;
                }
                device.DownloadCapKBps = saved.DownloadCapKBps;
                device.UploadCapKBps = saved.UploadCapKBps;
                device.IsBlocked = saved.IsBlocked;
                device.IsSpoofed = saved.IsSpoofed;
                device.DailyQuotaBytes = saved.DailyQuotaMB * 1024L * 1024L;
                device.SetBlockedDomains(saved.BlockedDomains.Concat(_preferences.GlobalBlockedDomains));
            }
            else if (_preferences.GlobalBlockedDomains.Count > 0)
            {
                device.SetBlockedDomains(_preferences.GlobalBlockedDomains);
            }
        }
    }

    public void UpdateFromDevice(NetworkDevice device)
    {
        if (device.IsLocalHost || device.IsGateway) return;

        string macKey = device.Mac.ToString();
        lock (_sync)
        {
            SavedDeviceProfile? existing = _preferences.Devices.FirstOrDefault(
                d => string.Equals(d.MacAddress, macKey, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new SavedDeviceProfile { MacAddress = macKey };
                _preferences.Devices.Add(existing);
            }

            existing.CustomNickname = device.CustomNickname;
            existing.Category = device.DeviceCategory;
            existing.DownloadCapKBps = device.DownloadCapKBps;
            existing.UploadCapKBps = device.UploadCapKBps;
            existing.IsBlocked = device.IsBlocked;
            existing.IsSpoofed = device.IsSpoofed;
            existing.DailyQuotaMB = device.DailyQuotaBytes / (1024L * 1024L);
            existing.BlockedDomains = [.. device.BlockedDomains];
        }

        Save();
    }
}
