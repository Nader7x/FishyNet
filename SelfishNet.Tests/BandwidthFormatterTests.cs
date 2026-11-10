using SelfishNetv3.Core.Formatting;
using Xunit;

namespace SelfishNet.Tests;

public sealed class BandwidthFormatterTests
{
    [Fact]
    public void FormatRate_BytesPerSecond_AutoScalesFriendlyUnits()
    {
        Assert.Equal("0 B/s", BandwidthFormatter.FormatRate(0, useBitsPerSecond: false));
        Assert.Equal("512 B/s", BandwidthFormatter.FormatRate(512, useBitsPerSecond: false));
        Assert.Equal("1.5 KB/s", BandwidthFormatter.FormatRate(1536, useBitsPerSecond: false));
        Assert.Equal("250 KB/s", BandwidthFormatter.FormatRate(250 * 1024, useBitsPerSecond: false));
        Assert.Equal("12.50 MB/s", BandwidthFormatter.FormatRate(12.5 * 1024 * 1024, useBitsPerSecond: false));
        Assert.Equal("1.25 GB/s", BandwidthFormatter.FormatRate(1.25 * 1024 * 1024 * 1024, useBitsPerSecond: false));
    }

    [Fact]
    public void FormatRate_BitsPerSecond_AutoScalesIspUnits()
    {
        Assert.Equal("0 bps", BandwidthFormatter.FormatRate(0, useBitsPerSecond: true));
        Assert.Equal("800 bps", BandwidthFormatter.FormatRate(100, useBitsPerSecond: true));
        Assert.Equal("500 Kbps", BandwidthFormatter.FormatRate(62500, useBitsPerSecond: true));
        Assert.Equal("100.0 Mbps", BandwidthFormatter.FormatRate(12_500_000, useBitsPerSecond: true));
    }

    [Fact]
    public void FormatBytes_AutoScalesDataVolumes()
    {
        Assert.Equal("0 B", BandwidthFormatter.FormatBytes(0));
        Assert.Equal("900 B", BandwidthFormatter.FormatBytes(900));
        Assert.Equal("64.0 KB", BandwidthFormatter.FormatBytes(64 * 1024));
        Assert.Equal("25.50 MB", BandwidthFormatter.FormatBytes((long)(25.5 * 1024 * 1024)));
        Assert.Equal("4.50 GB", BandwidthFormatter.FormatBytes((long)(4.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void ComputeUtilizationRatio_RespectsCapAndLogScale()
    {
        Assert.Equal(0.0, BandwidthFormatter.ComputeUtilizationRatio(0, capKilobytesPerSecond: 0));
        Assert.Equal(0.5, BandwidthFormatter.ComputeUtilizationRatio(250 * 1024, capKilobytesPerSecond: 500), 2);
        Assert.Equal(1.0, BandwidthFormatter.ComputeUtilizationRatio(800 * 1024, capKilobytesPerSecond: 500), 2);
        Assert.Equal("50% of 500 KB/s", BandwidthFormatter.FormatCapStatus(250 * 1024, 500, useBitsPerSecond: false));
        Assert.Equal("Unlimited", BandwidthFormatter.FormatCapStatus(250 * 1024, 0, useBitsPerSecond: false));
    }
}
