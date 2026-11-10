using System.Net;
using FishyNet.Core;

namespace FishyNet.Features;

/// <summary>
/// Resolves LAN hostnames asynchronously using modern Dns.GetHostEntryAsync with bounded concurrency
/// to replace the legacy obsolete Dns.BeginResolve callback.
/// </summary>
public sealed class HostResolverService
{
    private readonly DeviceRegistry _registry;
    private readonly SemaphoreSlim _concurrencyGate = new(8, 8);

    public HostResolverService(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public async Task ResolveDeviceHostnameAsync(NetworkDevice device, CancellationToken ct = default)
    {
        if (device.Ip.IsZero || !string.IsNullOrWhiteSpace(device.Hostname))
        {
            return;
        }

        if (device.IsLocalHost)
        {
            device.Hostname = Dns.GetHostName();
            _registry.NotifyDeviceMetadataUpdated(device);
            return;
        }

        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

            IPHostEntry entry = await Dns.GetHostEntryAsync(device.Ip.ToIPAddress().ToString(), timeoutCts.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(entry.HostName) &&
                !string.Equals(entry.HostName, device.Ip.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                device.Hostname = entry.HostName;
                _registry.NotifyDeviceMetadataUpdated(device);
            }
        }
        catch
        {
            // Host does not have PTR record or timed out; keep vendor/IP fallback
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }
}
