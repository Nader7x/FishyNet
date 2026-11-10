using System.Runtime.CompilerServices;

namespace SelfishNetv3.Core.Formatting;

/// <summary>
/// Provides human-friendly, adaptive representations for network speeds (B/s, KB/s, MB/s, GB/s or bps/Kbps/Mbps/Gbps),
/// cumulative byte volumes (KB, MB, GB, TB), logarithmic visual bar ratios, and cap utilization labels.
/// </summary>
public static class BandwidthFormatter
{
    private static readonly string[] ByteRateUnits = ["B/s", "KB/s", "MB/s", "GB/s"];
    private static readonly string[] BitRateUnits = ["bps", "Kbps", "Mbps", "Gbps"];
    private static readonly string[] VolumeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a throughput value in bytes/sec into a friendly auto-scaled string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatRate(double bytesPerSecond, bool useBitsPerSecond = false)
    {
        if (bytesPerSecond < 1.0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return useBitsPerSecond ? "0 bps" : "0 B/s";
        }

        double value = useBitsPerSecond ? bytesPerSecond * 8.0 : bytesPerSecond;
        string[] units = useBitsPerSecond ? BitRateUnits : ByteRateUnits;
        double divisor = useBitsPerSecond ? 1000.0 : 1024.0;

        int unitIndex = 0;
        while (value >= divisor && unitIndex < units.Length - 1)
        {
            value /= divisor;
            unitIndex++;
        }

        return unitIndex switch
        {
            0 => $"{value:F0} {units[0]}",
            1 => value >= 100.0 ? $"{value:F0} {units[1]}" : $"{value:F1} {units[1]}",
            _ => value >= 100.0 ? $"{value:F1} {units[unitIndex]}" : $"{value:F2} {units[unitIndex]}"
        };
    }

    /// <summary>
    /// Formats a cumulative byte count into a friendly volume string (e.g. "420 KB", "84.5 MB", "12.40 GB").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatBytes(long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return "0 B";
        }

        double value = totalBytes;
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < VolumeUnits.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex switch
        {
            0 => $"{totalBytes} B",
            1 => value >= 100.0 ? $"{value:F0} KB" : $"{value:F1} KB",
            2 => value >= 100.0 ? $"{value:F1} MB" : $"{value:F2} MB",
            _ => $"{value:F2} {VolumeUnits[unitIndex]}"
        };
    }

    /// <summary>
    /// Computes a smooth visual bar fill ratio in [0.0, 1.0].
    /// If a bandwidth cap is set on the device, returns exact saturation against the cap.
    /// Otherwise uses a logarithmic scale anchored up to 50 MB/s so both light (50 KB/s) and heavy (25 MB/s) flows are clear.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeUtilizationRatio(double bytesPerSecond, int capKilobytesPerSecond = 0)
    {
        if (bytesPerSecond <= 1.0)
        {
            return 0.0;
        }

        if (capKilobytesPerSecond > 0)
        {
            double capBytesPerSec = capKilobytesPerSecond * 1024.0;
            return Math.Clamp(bytesPerSecond / capBytesPerSec, 0.0, 1.0);
        }

        // Log10 scale from 1 KB/s (10^3) to 100 MB/s (10^8)
        double logVal = Math.Log10(Math.Max(1024.0, bytesPerSecond));
        double normalized = (logVal - 3.0) / 5.0;
        return Math.Clamp(normalized, 0.04, 1.0);
    }

    /// <summary>
    /// Formats a friendly cap status badge (e.g., "Unlimited" or "78% of 500 KB/s").
    /// </summary>
    public static string FormatCapStatus(double currentBytesPerSec, int capKilobytesPerSecond, bool useBitsPerSecond = false)
    {
        if (capKilobytesPerSecond <= 0)
        {
            return "Unlimited";
        }

        double capBytesPerSec = capKilobytesPerSecond * 1024.0;
        int pct = (int)Math.Clamp(Math.Round((currentBytesPerSec / capBytesPerSec) * 100.0), 0, 100);
        string formattedCap = FormatRate(capBytesPerSec, useBitsPerSecond);
        return $"{pct}% of {formattedCap}";
    }
}
