using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FishyNet.Core.Packets;
using FishyNet.Features;

namespace FishyNet.Core;

public enum NetworkEventKind
{
    DeviceDiscovered,
    DeviceUpdated,
    DeviceRemoved,
    DnsQueryObserved,
    ArpSecurityAlert
}

public sealed record DnsQueryRecord(
    DateTime TimestampUtc,
    Ipv4Address DeviceIp,
    MacAddress DeviceMac,
    string DeviceName,
    string Domain,
    bool WasBlocked);

public sealed record ArpSecurityAlert(
    DateTime TimestampUtc,
    Ipv4Address TargetIp,
    MacAddress ExpectedMac,
    MacAddress OffendingMac,
    string Description);

public sealed record NetworkEvent(
    NetworkEventKind Kind,
    NetworkDevice? Device = null,
    DnsQueryRecord? DnsRecord = null,
    ArpSecurityAlert? SecurityAlert = null);

/// <summary>
/// Thread-safe, lock-free O(1) registry of active LAN devices keyed by both IPv4 and MAC address.
/// Emits asynchronous events to a Channel so packet worker threads never block on UI thread locks.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<Ipv4Address, NetworkDevice> _byIp = new();
    private readonly ConcurrentDictionary<MacAddress, NetworkDevice> _byMac = new();
    private readonly Channel<NetworkEvent> _eventChannel = Channel.CreateUnbounded<NetworkEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private MacAddress _trustedGatewayMac = MacAddress.Zero;
    private Ipv4Address _gatewayIp = Ipv4Address.Zero;
    private MacAddress _localMac = MacAddress.Zero;
    private long _lastArpAlertTicks;

    public ChannelReader<NetworkEvent> Events => _eventChannel.Reader;

    public IReadOnlyCollection<NetworkDevice> AllDevices => (IReadOnlyCollection<NetworkDevice>)_byMac.Values;

    public int Count => _byMac.Count;

    public MacAddress GatewayMac => _trustedGatewayMac;
    public Ipv4Address GatewayIp => _gatewayIp;

    public void ConfigureLocalNetwork(Ipv4Address localIp, MacAddress localMac, Ipv4Address gatewayIp)
    {
        _localMac = localMac;
        _gatewayIp = gatewayIp;

        if (!localIp.IsZero && !localMac.IsZero)
        {
            RegisterOrUpdateDevice(localIp, localMac, isGateway: false, isLocalHost: true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NetworkDevice? TryGetByIp(Ipv4Address ip)
    {
        return _byIp.TryGetValue(ip, out NetworkDevice? device) ? device : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NetworkDevice? TryGetByMac(MacAddress mac)
    {
        return _byMac.TryGetValue(mac, out NetworkDevice? device) ? device : null;
    }

    public NetworkDevice? GetGatewayDevice()
    {
        if (!_gatewayIp.IsZero && _byIp.TryGetValue(_gatewayIp, out NetworkDevice? gw))
        {
            return gw;
        }
        return _byMac.Values.FirstOrDefault(d => d.IsGateway);
    }

    public NetworkDevice? GetLocalDevice()
    {
        if (!_localMac.IsZero && _byMac.TryGetValue(_localMac, out NetworkDevice? local))
        {
            return local;
        }
        return _byMac.Values.FirstOrDefault(d => d.IsLocalHost);
    }

    public NetworkDevice RegisterOrUpdateDevice(
        Ipv4Address ip,
        MacAddress mac,
        bool isGateway = false,
        bool isLocalHost = false)
    {
        if (ip == _gatewayIp && !_gatewayIp.IsZero)
        {
            isGateway = true;
            // Passive ARP Spoof / MITM Guard check
            if (!_trustedGatewayMac.IsZero &&
                mac != _trustedGatewayMac &&
                mac != _localMac &&
                !mac.IsBroadcast &&
                !mac.IsZero)
            {
                RaiseArpAlertIfNotThrottled(ip, _trustedGatewayMac, mac,
                    $"ARP Poisoning detected: MAC {mac} is claiming Gateway IP {ip} (expected {_trustedGatewayMac}).");
                // Do not overwrite our trusted gateway MAC with a foreign attacker's MAC
                if (_byMac.TryGetValue(_trustedGatewayMac, out NetworkDevice? existingGw))
                {
                    return existingGw;
                }
            }
            else if (_trustedGatewayMac.IsZero && mac != _localMac && !mac.IsZero && !mac.IsBroadcast)
            {
                _trustedGatewayMac = mac;
            }
        }

        if (_byMac.TryGetValue(mac, out NetworkDevice? existingByMac))
        {
            existingByMac.TouchLastSeen();
            if (existingByMac.Ip != ip && !ip.IsZero)
            {
                _byIp.TryRemove(existingByMac.Ip, out _);
                existingByMac.Ip = ip;
                _byIp[ip] = existingByMac;
                _eventChannel.Writer.TryWrite(new NetworkEvent(NetworkEventKind.DeviceUpdated, existingByMac));
            }
            return existingByMac;
        }

        VendorInfo vendorInfo = OuiVendorLookup.Resolve(mac);
        var newDevice = new NetworkDevice(ip, mac, isGateway, isLocalHost)
        {
            Vendor = vendorInfo.VendorName,
            DeviceCategory = isGateway ? "Router" : (isLocalHost ? "Workstation" : vendorInfo.DefaultCategory)
        };

        if (_byMac.TryAdd(mac, newDevice))
        {
            if (!ip.IsZero)
            {
                _byIp[ip] = newDevice;
            }
            _eventChannel.Writer.TryWrite(new NetworkEvent(NetworkEventKind.DeviceDiscovered, newDevice));
            return newDevice;
        }

        return _byMac[mac];
    }

    public void NotifyDeviceMetadataUpdated(NetworkDevice device)
    {
        _eventChannel.Writer.TryWrite(new NetworkEvent(NetworkEventKind.DeviceUpdated, device));
    }

    public void PublishDnsQuery(NetworkDevice device, string domain, bool wasBlocked)
    {
        var record = new DnsQueryRecord(
            DateTime.UtcNow,
            device.Ip,
            device.Mac,
            device.DisplayName,
            domain,
            wasBlocked);
        _eventChannel.Writer.TryWrite(new NetworkEvent(NetworkEventKind.DnsQueryObserved, device, DnsRecord: record));
    }

    private void RaiseArpAlertIfNotThrottled(Ipv4Address targetIp, MacAddress expectedMac, MacAddress offendingMac, string description)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long prevTicks = Interlocked.Read(ref _lastArpAlertTicks);
        // Throttle duplicate alerts to at most once every 5 seconds
        if (nowTicks - prevTicks < TimeSpan.FromSeconds(5).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastArpAlertTicks, nowTicks);
        var alert = new ArpSecurityAlert(DateTime.UtcNow, targetIp, expectedMac, offendingMac, description);
        _eventChannel.Writer.TryWrite(new NetworkEvent(NetworkEventKind.ArpSecurityAlert, SecurityAlert: alert));
    }

    public void Clear()
    {
        _byIp.Clear();
        _byMac.Clear();
        _trustedGatewayMac = MacAddress.Zero;
    }
}
