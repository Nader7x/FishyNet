using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FishyNet.Core.Throttling;

/// <summary>
/// High-precision, zero-allocation Token Bucket bandwidth shaper.
/// Replenishes byte tokens continuously based on Stopwatch high-resolution timestamps
/// to avoid the legacy 1-second hard cutoff stall.
/// </summary>
public struct TokenBucketLimiter
{
    private static readonly double TickFrequency = Stopwatch.Frequency;

    private long _lastTimestamp;
    private double _availableTokens;

    /// <summary>
    /// Attempts to consume <paramref name="packetBytes"/> from the bucket given a rate cap in bytes/sec.
    /// Returns true if the packet is allowed to pass; false if it should be dropped/throttled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryConsume(int packetBytes, int rateCapBytesPerSec, long currentTimestamp)
    {
        if (rateCapBytesPerSec <= 0)
        {
            return true; // 0 means unrestricted
        }

        // Allow at least 16 KB (approx. 10x standard Ethernet MTU) or 250ms of burst
        double maxBurstBytes = Math.Max(16384.0, rateCapBytesPerSec * 0.25);

        if (_lastTimestamp == 0)
        {
            _lastTimestamp = currentTimestamp;
            _availableTokens = maxBurstBytes;
        }
        else
        {
            long elapsedTicks = currentTimestamp - _lastTimestamp;
            if (elapsedTicks > 0)
            {
                _lastTimestamp = currentTimestamp;
                double elapsedSeconds = elapsedTicks / TickFrequency;
                _availableTokens = Math.Min(maxBurstBytes, _availableTokens + (elapsedSeconds * rateCapBytesPerSec));
            }
        }

        if (_availableTokens >= packetBytes)
        {
            _availableTokens -= packetBytes;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _lastTimestamp = 0;
        _availableTokens = 0;
    }
}
