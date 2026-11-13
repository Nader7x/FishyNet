using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using FishyNet.Core;
using FishyNet.Core.Native;
using FishyNet.Core.Packets;
using FishyNet.Features.AppMonitor;
using Xunit;

namespace FishyNet.Tests;

public sealed class AppAndWebsiteMonitorTests
{
    [Fact]
    public void TryInspectIpv4Packet_RespectsTcpOptionsDataOffsetGreaterThan5()
    {
        // Ethernet (14) + IPv4 (20) + TCP with DataOffset=8 (32 bytes = 20 + 12 bytes options) + 4 bytes payload
        Span<byte> frame = stackalloc byte[14 + 20 + 32 + 4];
        frame.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12, 2), PacketPrimitives.EtherTypeIpv4);
        frame[14] = 0x45; // IPv4, IHL=5
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(16, 2), 20 + 32 + 4);
        frame[23] = 6; // TCP

        int l4Offset = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(l4Offset, 2), 51234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(l4Offset + 2, 2), 443);
        frame[l4Offset + 12] = 0x80; // DataOffset = 8 (32 bytes)

        // Marker payload at offset 66 (14 + 20 + 32)
        frame[66] = 0xAA;
        frame[67] = 0xBB;
        frame[68] = 0xCC;
        frame[69] = 0xDD;

        Assert.True(PacketPrimitives.TryInspectIpv4Packet(
            frame,
            out _,
            out _,
            out _,
            out _,
            out ProtocolKind proto,
            out byte ipProto,
            out ushort srcPort,
            out ushort dstPort,
            out int l4PayloadOffset,
            out int ipEndOffset));

        Assert.Equal(ProtocolKind.HttpTls, proto);
        Assert.Equal((byte)6, ipProto);
        Assert.Equal((ushort)51234, srcPort);
        Assert.Equal((ushort)443, dstPort);
        Assert.Equal(66, l4PayloadOffset);
        Assert.Equal(70, ipEndOffset);
        Assert.Equal(0xAA, frame[l4PayloadOffset]);
    }

    [Fact]
    public void TryInspectIpv4Packet_RejectsInvalidOrTruncatedTcpDataOffset()
    {
        Span<byte> invalidOffsetFrame = stackalloc byte[64];
        invalidOffsetFrame.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(invalidOffsetFrame.Slice(12, 2), PacketPrimitives.EtherTypeIpv4);
        invalidOffsetFrame[14] = 0x45;
        invalidOffsetFrame[23] = 6;
        invalidOffsetFrame[34 + 12] = 0x40; // DataOffset = 4 (16 bytes < 20 minimum)

        Assert.False(PacketPrimitives.TryInspectIpv4Packet(
            invalidOffsetFrame, out _, out _, out _, out _, out _, out _, out _, out _));

        // DataOffset = 15 (60 bytes) exceeds remaining frame length (64 - 34 = 30)
        invalidOffsetFrame[34 + 12] = 0xF0;
        Assert.False(PacketPrimitives.TryInspectIpv4Packet(
            invalidOffsetFrame, out _, out _, out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void TryExtractTlsClientHelloSni_ParsesStandardAndSegmentedClientHello()
    {
        byte[] clientHello = BuildTlsClientHelloPayload("Api.GitHub.Com");

        Assert.True(PacketPrimitives.TryExtractTlsClientHelloSni(clientHello, out string sni));
        Assert.Equal("api.github.com", sni);

        // Simulate multi-segment TLS 1.3 ClientHello where recordLen is larger than captured first TCP segment,
        // while SNI extension still sits inside the captured prefix.
        byte[] inflatedRecord = (byte[])clientHello.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(inflatedRecord.AsSpan(3, 2), 1600);
        Assert.True(PacketPrimitives.TryExtractTlsClientHelloSni(inflatedRecord, out string segmentedSni));
        Assert.Equal("api.github.com", segmentedSni);

        // Non-handshake TLS record (Application Data 0x17) must return false
        byte[] appData = (byte[])clientHello.Clone();
        appData[0] = 0x17;
        Assert.False(PacketPrimitives.TryExtractTlsClientHelloSni(appData, out _));
    }

    [Fact]
    public void TryExtractHttpHost_ParsesHostHeaderAndStripsPort()
    {
        byte[] httpGet = "GET /v1/stream HTTP/1.1\r\nUser-Agent: FishyTest\r\nHoSt: Media.Example.COM:8080\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();
        Assert.True(PacketPrimitives.TryExtractHttpHost(httpGet, out string host));
        Assert.Equal("media.example.com", host);

        byte[] httpIpv6 = "POST /upload HTTP/1.1\r\nHost: [2001:DB8::1]:8080\r\n\r\n"u8.ToArray();
        Assert.True(PacketPrimitives.TryExtractHttpHost(httpIpv6, out string ipv6Host));
        Assert.Equal("[2001:db8::1]", ipv6Host);

        byte[] nonHttp = [0x16, 0x03, 0x01, 0x00, 0x20, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        Assert.False(PacketPrimitives.TryExtractHttpHost(nonHttp, out _));
    }

    [Fact]
    public void TryExtractDnsResponseMappings_ParsesCompressedPointersForAAndAaaa()
    {
        byte[] dnsResp = BuildDnsResponseWithCompressedPointers(
            "YouTube.com",
            IPAddress.Parse("142.250.185.78"),
            IPAddress.Parse("2607:f8b0:4005:80b::200e"));

        var mappings = new DnsIpMapping[8];
        Assert.True(PacketPrimitives.TryExtractDnsResponseMappings(dnsResp, mappings, out int count));
        Assert.Equal(2, count);

        Assert.Equal("youtube.com", mappings[0].Domain);
        Assert.True(mappings[0].IsIpv4);
        Assert.Equal("142.250.185.78", mappings[0].FormatAddress());

        Assert.Equal("youtube.com", mappings[1].Domain);
        Assert.True(mappings[1].IsIpv6);
        Assert.Equal("2607:f8b0:4005:80b::200e", mappings[1].FormatAddress());
    }

    [Fact]
    public void TryExtractDnsResponseMappings_SafelyRejectsCyclicCompressionPointers()
    {
        // Build DNS response with QR=1, QDCOUNT=0, ANCOUNT=1, and Answer NAME = 0xC0 0x0C (self-loop at offset 12)
        byte[] malicious = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(2, 2), 0x8180); // QR=1, NOERROR
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(4, 2), 0);      // QDCOUNT=0
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(6, 2), 1);      // ANCOUNT=1

        malicious[12] = 0xC0;
        malicious[13] = 0x0C; // Self-loop pointer -> offset 12!
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(14, 2), 1);   // TYPE A
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(16, 2), 1);   // CLASS IN
        BinaryPrimitives.WriteUInt32BigEndian(malicious.AsSpan(18, 4), 60);  // TTL
        BinaryPrimitives.WriteUInt16BigEndian(malicious.AsSpan(22, 2), 4);   // RDLEN
        malicious[24] = 1; malicious[25] = 2; malicious[26] = 3; malicious[27] = 4;

        var mappings = new DnsIpMapping[4];
        Assert.False(PacketPrimitives.TryExtractDnsResponseMappings(malicious, mappings, out int count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void IpHelperNative_TableParsersDecodePortsAndDualStackMappings()
    {
        // Construct an in-memory MIB_TCPTABLE_OWNER_PID with 1 row
        int headerOffset = (int)Marshal.OffsetOf<IpHelperNative.MibTcpTableOwnerPid>(nameof(IpHelperNative.MibTcpTableOwnerPid.table));
        int rowSize = Marshal.SizeOf<IpHelperNative.MibTcpRowOwnerPid>();
        byte[] tcp4Buf = new byte[headerOffset + rowSize];

        BinaryPrimitives.WriteUInt32LittleEndian(tcp4Buf.AsSpan(0, 4), 1); // dwNumEntries = 1
        Span<byte> rowSpan = tcp4Buf.AsSpan(headerOffset, rowSize);
        BinaryPrimitives.WriteUInt32LittleEndian(rowSpan.Slice(0, 4), 5); // MIB_TCP_STATE_ESTAB
        rowSpan[4] = 192; rowSpan[5] = 168; rowSpan[6] = 1; rowSpan[7] = 10; // local IP
        rowSpan[8] = 0xC8; rowSpan[9] = 0x01; // big-endian local port 51201
        rowSpan[12] = 140; rowSpan[13] = 82; rowSpan[14] = 121; rowSpan[15] = 6; // remote IP
        rowSpan[16] = 0x01; rowSpan[17] = 0xBB; // big-endian remote port 443
        BinaryPrimitives.WriteUInt32LittleEndian(rowSpan.Slice(20, 4), 4242); // PID = 4242

        var parsed = new List<SocketConnectionEntry>();
        IpHelperNative.ParseTcp4Table(tcp4Buf, parsed);

        Assert.Single(parsed);
        Assert.Equal((byte)6, parsed[0].IpProtocol);
        Assert.Equal("192.168.1.10", parsed[0].EffectiveLocalIpv4.ToString());
        Assert.Equal((ushort)51201, parsed[0].LocalPort);
        Assert.Equal("140.82.121.6", parsed[0].EffectiveRemoteIpv4.ToString());
        Assert.Equal((ushort)443, parsed[0].RemotePort);
        Assert.Equal(4242, parsed[0].OwningPid);

        // Also verify real OS call succeeds safely on Windows without crashing
        if (OperatingSystem.IsWindows())
        {
            var liveSockets = new List<SocketConnectionEntry>(64);
            Assert.True(IpHelperNative.TrySnapshotSockets(liveSockets));
        }
    }

    [Fact]
    public void ProcessMetadataResolver_HeadlessResolutionIsSafeAndThreadSafe()
    {
        // In xUnit, Application.Current is null — Icon must safely be null without throwing
        var resolver = new ProcessMetadataResolver();
        ProcessMetadata current = resolver.Resolve(Environment.ProcessId);

        Assert.Equal(Environment.ProcessId, current.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(current.ProcessName));
        Assert.False(string.IsNullOrWhiteSpace(current.ExePath));
        Assert.Null(current.Icon);

        // Special PIDs 0 and 4
        Assert.Equal("System", resolver.Resolve(4).ProcessName);
        Assert.Equal("System Idle", resolver.Resolve(0).ProcessName);
    }

    [Fact]
    public void AppTrafficTracker_AttributesFlowsDetectsProtocolsAndRaisesFirstAccessAlert()
    {
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address githubIp);
        Ipv4Address.TryParse("142.250.185.78", out Ipv4Address googleIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);

        var fakeSockets = new FakeSocketTableProvider(
        [
            new SocketConnectionEntry(6, false, localIp, 0, 0, 55001, githubIp, 0, 0, 443, 1001, 5),
            new SocketConnectionEntry(17, true, Ipv4Address.Zero, 0, 0x0000FFFF_C0A8010AUL, 55002, Ipv4Address.Zero, 0, 0, 0, 1001, 0)
        ]);

        var fakeMetadata = new FakeProcessMetadataResolver(new Dictionary<int, ProcessMetadata>
        {
            [1001] = new ProcessMetadata(1001, "chrome.exe", "Google Chrome", @"C:\Apps\Chrome\chrome.exe", "Google LLC", null)
        });

        var timeline = new TrafficTimelineStore();
        var tracker = new AppTrafficTracker(fakeSockets, fakeMetadata, timelineStore: timeline);

        var alerts = new List<MonitorAlertRecord>();
        tracker.AlertRaised += alerts.Add;

        var tlsFlow = new PacketFlowObservation(
            ProtocolKind.HttpTls, 6, localIp, githubIp, 55001, 443, 1200, IsOutbound: true, IsLocalHost: true, localIp, localMac);
        tracker.OnTlsSniObserved(in tlsFlow, "api.github.com");
        tracker.OnPacketFlow(in tlsFlow);

        var tlsInbound = tlsFlow with { IsOutbound = false, ByteCount = 4800 };
        tracker.OnPacketFlow(in tlsInbound);

        // QUIC UDP 443 over dual-stack IPv4-mapped IPv6 socket
        var quicFlow = new PacketFlowObservation(
            ProtocolKind.HttpTls, 17, localIp, googleIp, 55002, 443, 2400, IsOutbound: false, IsLocalHost: true, localIp, localMac);
        tracker.OnPacketFlow(in quicFlow);

        AppTrafficSnapshot snap = tracker.SampleTick();

        Assert.Single(alerts);
        Assert.Equal(MonitorAlertKind.FirstNetworkAccess, alerts[0].Kind);
        Assert.Equal("chrome.exe", alerts[0].SubjectName);

        Assert.Single(snap.Applications);
        AppTrafficEntry app = snap.Applications[0];
        Assert.Equal("chrome.exe", app.ProcessName);
        Assert.Equal("Google LLC", app.Publisher);
        Assert.Equal(7200, app.TotalDownloadBytes);
        Assert.Equal(1200, app.TotalUploadBytes);
        Assert.Contains("TLS", app.ProtocolTags);
        Assert.Contains("QUIC", app.ProtocolTags);
        Assert.Contains("api.github.com", app.ContactedHosts);
        Assert.True(app.IsNewFirstAccess);
        Assert.Equal(2, app.ActiveConnections);

        // Subsequent packets for same exe must NOT raise duplicate First Network Access alert
        tracker.OnPacketFlow(in tlsInbound);
        Assert.Single(alerts);

        Assert.True(tracker.AcknowledgeFirstAccess(app.AppKey));
        Assert.False(tracker.GetSnapshot().Applications[0].IsNewFirstAccess);
    }

    [Fact]
    public async Task AppFirewallService_ValidatesPathsAndUsesSafeRulePrefix()
    {
        var invocations = new List<IReadOnlyList<string>>();
        var svc = new AppFirewallService((args, _) =>
        {
            invocations.Add(args);
            return Task.FromResult((0, "Ok."));
        }, requireExistingFile: false);

        // Reject relative paths, quote injection, non-exe, and protected Windows system binaries
        AppFirewallResult relRes = await svc.BlockAppAsync("chrome.exe");
        Assert.False(relRes.Success);

        AppFirewallResult injRes = await svc.BlockAppAsync(@"C:\Apps\bad""app.exe");
        Assert.False(injRes.Success);

        AppFirewallResult sysRes = await svc.BlockAppAsync(@"C:\Windows\System32\svchost.exe");
        Assert.False(sysRes.Success);
        Assert.Empty(invocations);

        string targetExe = @"C:\Program Files\Demo\demo_app.exe";
        AppFirewallResult blockRes = await svc.BlockAppAsync(targetExe, "Demo App");
        Assert.True(blockRes.Success);
        Assert.StartsWith("FishyNet_Block_", blockRes.RulePrefix);
        Assert.True(svc.IsAppBlocked(targetExe));
        Assert.Contains(invocations, args => args.Contains("dir=out") && args.Contains($"program={targetExe}"));
        Assert.Contains(invocations, args => args.Contains("dir=in") && args.Contains($"program={targetExe}"));

        AppFirewallResult unblockRes = await svc.UnblockAppAsync(targetExe);
        Assert.True(unblockRes.Success);
        Assert.False(svc.IsAppBlocked(targetExe));
    }

    [Fact]
    public void WebsiteTrafficTracker_CorrelatesDnsReverseCacheTlsSniAndLanDevices()
    {
        var registry = new DeviceRegistry();
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress gwMac);
        Ipv4Address.TryParse("192.168.1.50", out Ipv4Address tvIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:50", out MacAddress tvMac);
        Ipv4Address.TryParse("8.8.8.8", out Ipv4Address dnsServer);
        Ipv4Address.TryParse("142.250.185.78", out Ipv4Address ytIp);
        Ipv4Address.TryParse("93.184.216.34", out Ipv4Address exampleIp);

        registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);
        NetworkDevice tvDev = registry.RegisterOrUpdateDevice(tvIp, tvMac);
        tvDev.Hostname = "LivingRoom-TV";
        tvDev.AddBlockedDomain("youtube.com");

        var tracker = new WebsiteTrafficTracker(registry);

        // 1. Local PC receives DNS response mapping youtube.com -> 142.250.185.78
        byte[] dnsPayload = BuildDnsResponseWithCompressedPointers(
            "youtube.com",
            IPAddress.Parse("142.250.185.78"),
            IPAddress.Parse("2607:f8b0:4005:80b::200e"));
        Span<DnsIpMapping> mappings = new DnsIpMapping[4];
        Assert.True(PacketPrimitives.TryExtractDnsResponseMappings(dnsPayload, mappings, out int mapCount));

        var dnsFlow = new PacketFlowObservation(
            ProtocolKind.Dns, 17, localIp, dnsServer, 55100, 53, 180, IsOutbound: false, IsLocalHost: true, localIp, localMac);
        tracker.OnDnsMappingsObserved(in dnsFlow, mappings.Slice(0, mapCount));

        // Subsequent QUIC (UDP 443) traffic on Local PC resolves via DNS reverse cache and promotes "53 · DNS" -> "443 · QUIC"
        var quicFlow = new PacketFlowObservation(
            ProtocolKind.HttpTls, 17, localIp, ytIp, 55101, 443, 50_000, IsOutbound: false, IsLocalHost: true, localIp, localMac);
        tracker.OnPacketFlow(in quicFlow);

        // 2. Spoofed LAN device (LivingRoom-TV) opens TLS ClientHello SNI to youtube.com (sinkholed!) and HTTP to example.com
        var lanTlsFlow = new PacketFlowObservation(
            ProtocolKind.HttpTls, 6, tvIp, ytIp, 41000, 443, 512, IsOutbound: true, IsLocalHost: false, tvIp, tvMac);
        tracker.OnTlsSniObserved(in lanTlsFlow, "youtube.com");
        tracker.OnPacketFlow(in lanTlsFlow);

        var lanHttpFlow = new PacketFlowObservation(
            ProtocolKind.HttpTls, 6, tvIp, exampleIp, 41001, 80, 1200, IsOutbound: true, IsLocalHost: false, tvIp, tvMac);
        tracker.OnHttpHostObserved(in lanHttpFlow, "example.com");
        tracker.OnPacketFlow(in lanHttpFlow);

        WebsiteTrafficSnapshot all = tracker.GetSnapshot();
        Assert.Equal(3, all.Websites.Count);

        WebsiteTrafficEntry localYt = Assert.Single(all.Websites, w => w.IsLocalHost && w.Domain == "youtube.com");
        Assert.Equal("443 · QUIC", localYt.ProtocolPort);
        Assert.Equal("142.250.185.78", localYt.ResolvedIp);
        Assert.Equal("This PC", localYt.SourceHost);
        Assert.Equal(50_000, localYt.DownloadBytes);
        Assert.False(localYt.IsSinkholed);

        WebsiteTrafficEntry tvYt = Assert.Single(all.Websites, w => !w.IsLocalHost && w.Domain == "youtube.com");
        Assert.Equal("443 · TLS", tvYt.ProtocolPort);
        Assert.Equal("LivingRoom-TV", tvYt.SourceHost);
        Assert.True(tvYt.IsSinkholed);

        WebsiteTrafficEntry tvExample = Assert.Single(all.Websites, w => !w.IsLocalHost && w.Domain == "example.com");
        Assert.Equal("80 · HTTP", tvExample.ProtocolPort);
        Assert.Equal(1200, tvExample.UploadBytes);
    }

    [Fact]
    public void CaptureEngine_PassiveDispatchWithTcpOptionsRoutesToObservers()
    {
        var registry = new DeviceRegistry();
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        Ipv4Address.TryParse("140.82.121.6", out Ipv4Address remoteIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress gwMac);

        var adapter = new NetworkAdapterInfo(
            "test-nic", "Ethernet", "Test NIC", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            localIp, new Ipv4Address(0xFFFFFF00u), gwIp, localMac, false);

        registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);

        using var engine = new CaptureEngine(registry);
        var websiteTracker = new WebsiteTrafficTracker(registry);
        engine.RegisterTelemetryObserver(websiteTracker);

        // Build Ethernet + IPv4 + TCP (DataOffset = 8 -> 32 bytes with 12-byte options) + TLS ClientHello ("docs.microsoft.com")
        byte[] tlsPayload = BuildTlsClientHelloPayload("docs.microsoft.com");
        byte[] frame = new byte[14 + 20 + 32 + tlsPayload.Length];

        gwMac.WriteTo(frame.AsSpan(0, 6));
        localMac.WriteTo(frame.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), PacketPrimitives.EtherTypeIpv4);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)(20 + 32 + tlsPayload.Length));
        frame[23] = 6; // TCP
        localIp.WriteTo(frame.AsSpan(26, 4));
        remoteIp.WriteTo(frame.AsSpan(30, 4));

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 54123);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 443);
        frame[34 + 12] = 0x80; // DataOffset = 8
        tlsPayload.CopyTo(frame.AsSpan(14 + 20 + 32));

        engine.ProcessObservedIpv4Frame(frame, frame.Length, adapter, activeHandle: nint.Zero, allowForwarding: false);

        WebsiteTrafficSnapshot snap = websiteTracker.GetSnapshot();
        WebsiteTrafficEntry entry = Assert.Single(snap.Websites);
        Assert.Equal("docs.microsoft.com", entry.Domain);
        Assert.Equal("443 · TLS", entry.ProtocolPort);
        Assert.Equal("This PC", entry.SourceHost);
        Assert.Equal(frame.Length, entry.UploadBytes);

        // StartRedirection without bound adapter must return clean error (compatibility check)
        Assert.False(engine.StartRedirection(out string? err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TrafficTimelineStore_SupportsTimeMachineWindowsTopSlicesAndSpikeDetection()
    {
        var store = new TrafficTimelineStore(
            spikeMultiplier: 3.0,
            minSpikeThresholdBps: 100 * 1024,
            minBaselineSamples: 6,
            spikeCooldown: TimeSpan.FromSeconds(10));

        var alerts = new List<MonitorAlertRecord>();
        store.AlertRaised += alerts.Add;

        DateTime t0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        // 8 seconds steady baseline @ 60 KB/s total
        for (int s = 0; s < 8; s++)
        {
            store.RecordSample(
                t0.AddSeconds(s),
                rxBytes: 50_000,
                txBytes: 10_000,
                slices:
                [
                    new TimelineSeriesSlice("chrome.exe", 35_000, 6_000),
                    new TimelineSeriesSlice("steam.exe", 10_000, 2_000),
                    new TimelineSeriesSlice("discord.exe", 5_000, 2_000)
                ]);
        }

        Assert.Empty(alerts);

        // Second 8: massive 6x spike (360 KB/s) led by steam.exe -> triggers TrafficSpike alert!
        store.RecordSample(
            t0.AddSeconds(8),
            rxBytes: 340_000,
            txBytes: 20_000,
            slices:
            [
                new TimelineSeriesSlice("steam.exe", 300_000, 15_000),
                new TimelineSeriesSlice("chrome.exe", 40_000, 5_000)
            ]);

        // Second 9: still high within 10s cooldown -> must NOT emit duplicate spike alert
        store.RecordSample(t0.AddSeconds(9), rxBytes: 350_000, txBytes: 20_000);

        Assert.Single(alerts);
        Assert.Equal(MonitorAlertKind.TrafficSpike, alerts[0].Kind);
        Assert.Equal("steam.exe", alerts[0].SubjectName);

        // External alert marker (ARP Spoof)
        store.RecordArpSpoofAlert(new ArpSecurityAlert(
            t0.AddSeconds(4),
            new Ipv4Address(0xC0A80101u),
            MacAddress.Broadcast,
            MacAddress.Zero,
            "Gateway ARP conflict"));

        TrafficTimelineSnapshot live = store.GetSnapshot(TimeMachineWindow.Live, topSliceCount: 2);
        Assert.Equal(10, live.Buckets.Count);
        Assert.Contains("steam.exe", live.SeriesLabels);
        Assert.Contains("chrome.exe", live.SeriesLabels);
        Assert.Contains("Other", live.SeriesLabels);
        Assert.Equal(2, live.AlertsInWindow.Count);
        Assert.Contains(live.Buckets[4].Alerts, a => a.Kind == MonitorAlertKind.ArpSpoof);
        Assert.Contains(live.Buckets[8].Alerts, a => a.Kind == MonitorAlertKind.TrafficSpike);

        // 5m (5s buckets) & 1h (1m buckets) windows aggregate cleanly
        TrafficTimelineSnapshot fiveMin = store.GetSnapshot(TimeMachineWindow.FiveMinutes);
        Assert.Equal(2, fiveMin.Buckets.Count);
        TrafficTimelineSnapshot oneHour = store.GetSnapshot(TimeMachineWindow.OneHour);
        Assert.Single(oneHour.Buckets);
        Assert.Equal(live.TotalDownloadBytes, oneHour.TotalDownloadBytes);
    }

    [Fact]
    public void PacketPrimitives_ZeroHeapAllocationsWhenNoDomainExtracted()
    {
        byte[] encryptedTcpFrame = new byte[120];
        BinaryPrimitives.WriteUInt16BigEndian(encryptedTcpFrame.AsSpan(12, 2), PacketPrimitives.EtherTypeIpv4);
        encryptedTcpFrame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(encryptedTcpFrame.AsSpan(16, 2), (ushort)(120 - 14));
        encryptedTcpFrame[23] = 6; // TCP
        BinaryPrimitives.WriteUInt16BigEndian(encryptedTcpFrame.AsSpan(34, 2), 51000);
        BinaryPrimitives.WriteUInt16BigEndian(encryptedTcpFrame.AsSpan(36, 2), 443);
        encryptedTcpFrame[34 + 12] = 0x50;
        // TLS ApplicationData (0x17) -> not ClientHello, not HTTP
        encryptedTcpFrame[54] = 0x17;
        encryptedTcpFrame[55] = 0x03;
        encryptedTcpFrame[56] = 0x03;

        var dnsScratch = new DnsIpMapping[4];

        // Warmup JIT
        PacketPrimitives.TryInspectIpv4Packet(encryptedTcpFrame, out _, out _, out _, out _, out _, out _, out _, out int offset);
        PacketPrimitives.TryExtractTlsClientHelloSni(encryptedTcpFrame.AsSpan(offset), out _);
        PacketPrimitives.TryExtractHttpHost(encryptedTcpFrame.AsSpan(offset), out _);
        PacketPrimitives.TryExtractDnsResponseMappings(encryptedTcpFrame.AsSpan(offset), dnsScratch, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            PacketPrimitives.TryInspectIpv4Packet(encryptedTcpFrame, out _, out _, out _, out _, out _, out _, out _, out int l4Off);
            ReadOnlySpan<byte> payload = encryptedTcpFrame.AsSpan(l4Off);
            PacketPrimitives.TryExtractTlsClientHelloSni(payload, out _);
            PacketPrimitives.TryExtractHttpHost(payload, out _);
            PacketPrimitives.TryExtractDnsResponseMappings(payload, dnsScratch, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void CaptureEngine_ForwardingModeRoutesLanDeviceDnsAndTlsWithoutPollutingAppTracker()
    {
        var registry = new DeviceRegistry();
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        Ipv4Address.TryParse("192.168.1.77", out Ipv4Address phoneIp);
        Ipv4Address.TryParse("142.250.185.78", out Ipv4Address remoteIp);
        MacAddress.TryParse("AA:BB:CC:DD:EE:10", out MacAddress localMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress gwMac);
        MacAddress.TryParse("AA:BB:CC:DD:EE:77", out MacAddress phoneMac);

        var adapter = new NetworkAdapterInfo(
            "test-nic", "Ethernet", "Test NIC", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            localIp, new Ipv4Address(0xFFFFFF00u), gwIp, localMac, false);

        registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        registry.RegisterOrUpdateDevice(gwIp, gwMac, isGateway: true);
        NetworkDevice phoneDev = registry.RegisterOrUpdateDevice(phoneIp, phoneMac);
        phoneDev.Hostname = "Pixel-Phone";

        using var engine = new CaptureEngine(registry);
        var appTracker = new AppTrafficTracker(new FakeSocketTableProvider([]), new ProcessMetadataResolver());
        var websiteTracker = new WebsiteTrafficTracker(registry, appTracker);

        engine.RegisterTelemetryObserver(appTracker);
        engine.RegisterTelemetryObserver(websiteTracker);

        // Outbound TLS ClientHello from spoofed LAN device Pixel-Phone -> 142.250.185.78:443
        byte[] tlsPayload = BuildTlsClientHelloPayload("mail.google.com");
        byte[] frame = new byte[14 + 20 + 20 + tlsPayload.Length];
        localMac.WriteTo(frame.AsSpan(0, 6));
        phoneMac.WriteTo(frame.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), PacketPrimitives.EtherTypeIpv4);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)(20 + 20 + tlsPayload.Length));
        frame[23] = 6; // TCP
        phoneIp.WriteTo(frame.AsSpan(26, 4));
        remoteIp.WriteTo(frame.AsSpan(30, 4));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 49152);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 443);
        frame[34 + 12] = 0x50;
        tlsPayload.CopyTo(frame.AsSpan(54));

        engine.ProcessObservedIpv4Frame(frame, frame.Length, adapter, activeHandle: nint.Zero, allowForwarding: true);

        // AppTrafficTracker only attributes Local PC flows
        Assert.Empty(appTracker.GetSnapshot().Applications);

        // WebsiteTrafficTracker captures LAN device domain via TLS SNI
        WebsiteTrafficSnapshot lanOnly = websiteTracker.GetSnapshot(localOnlyFilter: false);
        WebsiteTrafficEntry entry = Assert.Single(lanOnly.Websites);
        Assert.Equal("mail.google.com", entry.Domain);
        Assert.Equal("Pixel-Phone", entry.SourceHost);
        Assert.Equal("443 · TLS", entry.ProtocolPort);
        Assert.Empty(websiteTracker.GetSnapshot(localOnlyFilter: true).Websites);
    }

    [Fact]
    public void IpHelperNative_ParsesTcp6Udp4AndUdp6TablesCorrectly()
    {
        // UDP4
        int udp4Off = (int)Marshal.OffsetOf<IpHelperNative.MibUdpTableOwnerPid>(nameof(IpHelperNative.MibUdpTableOwnerPid.table));
        int udp4Row = Marshal.SizeOf<IpHelperNative.MibUdpRowOwnerPid>();
        byte[] udp4Buf = new byte[udp4Off + udp4Row];
        BinaryPrimitives.WriteUInt32LittleEndian(udp4Buf.AsSpan(0, 4), 1);
        udp4Buf[udp4Off + 0] = 192; udp4Buf[udp4Off + 1] = 168; udp4Buf[udp4Off + 2] = 1; udp4Buf[udp4Off + 3] = 10;
        udp4Buf[udp4Off + 4] = 0x14; udp4Buf[udp4Off + 5] = 0xE9; // port 5353
        BinaryPrimitives.WriteUInt32LittleEndian(udp4Buf.AsSpan(udp4Off + 8, 4), 777);

        var list = new List<SocketConnectionEntry>();
        IpHelperNative.ParseUdp4Table(udp4Buf, list);
        Assert.Single(list);
        Assert.Equal((byte)17, list[0].IpProtocol);
        Assert.Equal((ushort)5353, list[0].LocalPort);
        Assert.Equal(777, list[0].OwningPid);

        // TCP6 with IPv4-mapped ::ffff:192.168.1.10
        list.Clear();
        int tcp6Off = (int)Marshal.OffsetOf<IpHelperNative.MibTcp6TableOwnerPid>(nameof(IpHelperNative.MibTcp6TableOwnerPid.table));
        int tcp6Row = Marshal.SizeOf<IpHelperNative.MibTcp6RowOwnerPid>();
        byte[] tcp6Buf = new byte[tcp6Off + tcp6Row];
        BinaryPrimitives.WriteUInt32LittleEndian(tcp6Buf.AsSpan(0, 4), 1);
        tcp6Buf[tcp6Off + 10] = 0xFF; tcp6Buf[tcp6Off + 11] = 0xFF;
        tcp6Buf[tcp6Off + 12] = 192; tcp6Buf[tcp6Off + 13] = 168; tcp6Buf[tcp6Off + 14] = 1; tcp6Buf[tcp6Off + 15] = 10;
        tcp6Buf[tcp6Off + 20] = 0xD0; tcp6Buf[tcp6Off + 21] = 0x02; // port 53250
        tcp6Buf[tcp6Off + 34] = 0xFF; tcp6Buf[tcp6Off + 35] = 0xFF;
        tcp6Buf[tcp6Off + 36] = 8; tcp6Buf[tcp6Off + 37] = 8; tcp6Buf[tcp6Off + 38] = 8; tcp6Buf[tcp6Off + 39] = 8;
        tcp6Buf[tcp6Off + 44] = 0x01; tcp6Buf[tcp6Off + 45] = 0xBB; // port 443
        BinaryPrimitives.WriteUInt32LittleEndian(tcp6Buf.AsSpan(tcp6Off + 52, 4), 888);

        IpHelperNative.ParseTcp6Table(tcp6Buf, list);
        Assert.Single(list);
        Assert.True(list[0].IsIpv4MappedIpv6);
        Assert.Equal("192.168.1.10", list[0].EffectiveLocalIpv4.ToString());
        Assert.Equal("8.8.8.8", list[0].EffectiveRemoteIpv4.ToString());
        Assert.Equal((ushort)53250, list[0].LocalPort);
        Assert.Equal((ushort)443, list[0].RemotePort);
        Assert.Equal(888, list[0].OwningPid);
    }

    private static byte[] BuildTlsClientHelloPayload(string serverName)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(serverName);

        // SNI extension payload: listLen (2) + nameType(1=0x00) + nameLen(2) + nameBytes
        int sniExtPayloadLen = 2 + 1 + 2 + nameBytes.Length;
        int extensionsLen = 4 + sniExtPayloadLen;

        // ClientHello body: legacyVer(2) + random(32) + sessionIdLen(1) + cipherSuitesLen(2) + cipher(2)
        //                   + compLen(1) + comp(1) + extensionsLen(2) + extensions
        int helloBodyLen = 2 + 32 + 1 + 2 + 2 + 1 + 1 + 2 + extensionsLen;
        int handshakeLen = 4 + helloBodyLen;
        byte[] packet = new byte[5 + handshakeLen];

        packet[0] = 0x16; // Handshake
        packet[1] = 0x03;
        packet[2] = 0x01; // TLS 1.0 legacy record
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3, 2), (ushort)handshakeLen);

        packet[5] = 0x01; // ClientHello
        packet[6] = (byte)(helloBodyLen >> 16);
        packet[7] = (byte)(helloBodyLen >> 8);
        packet[8] = (byte)helloBodyLen;

        int pos = 9;
        packet[pos++] = 0x03; packet[pos++] = 0x03; // TLS 1.2
        pos += 32; // Random zeros
        packet[pos++] = 0; // SessionID len = 0
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), 2); // CipherSuites len = 2
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), 0x1301); // TLS_AES_128_GCM_SHA256
        pos += 2;
        packet[pos++] = 1; // Comp methods len
        packet[pos++] = 0; // Null compression
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), (ushort)extensionsLen);
        pos += 2;

        // Extension 0x0000: server_name
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos + 2, 2), (ushort)sniExtPayloadLen);
        pos += 4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), (ushort)(1 + 2 + nameBytes.Length));
        pos += 2;
        packet[pos++] = 0x00; // host_name
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), (ushort)nameBytes.Length);
        pos += 2;
        nameBytes.CopyTo(packet.AsSpan(pos));

        return packet;
    }

    private static byte[] BuildDnsResponseWithCompressedPointers(string domain, IPAddress ipv4, IPAddress ipv6)
    {
        var buf = new List<byte>(128);
        // Header (12 bytes)
        buf.AddRange([0xAB, 0xCD, 0x81, 0x80, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00]);

        // Question at offset 12: QNAME + QTYPE(1) + QCLASS(1)
        foreach (string label in domain.Split('.'))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0x00);
        buf.AddRange([0x00, 0x01, 0x00, 0x01]);

        // Answer 1: compressed pointer 0xC0 0x0C -> offset 12, TYPE A (1), CLASS IN (1), TTL 300, RDLEN 4
        buf.AddRange([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x04]);
        buf.AddRange(ipv4.GetAddressBytes());

        // Answer 2: compressed pointer 0xC0 0x0C -> offset 12, TYPE AAAA (28), CLASS IN (1), TTL 300, RDLEN 16
        buf.AddRange([0xC0, 0x0C, 0x00, 0x1C, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x10]);
        buf.AddRange(ipv6.GetAddressBytes());

        return buf.ToArray();
    }

    private sealed class FakeSocketTableProvider(IReadOnlyList<SocketConnectionEntry> entries) : ISocketTableProvider
    {
        public bool TryReadSockets(List<SocketConnectionEntry> destination)
        {
            destination.Clear();
            destination.AddRange(entries);
            return true;
        }
    }

    private sealed class FakeProcessMetadataResolver(Dictionary<int, ProcessMetadata> map) : IProcessMetadataResolver
    {
        public ProcessMetadata Resolve(int processId)
        {
            return map.TryGetValue(processId, out ProcessMetadata? meta)
                ? meta
                : new ProcessMetadata(processId, "unknown.exe", "Unknown", string.Empty, string.Empty, null);
        }
    }
}
