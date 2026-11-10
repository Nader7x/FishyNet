using System.Buffers.Binary;
using FishyNet.Core;
using FishyNet.Core.Packets;
using FishyNet.Core.Throttling;
using FishyNet.Features;
using Xunit;

namespace FishyNet.Tests;

public sealed class PacketAndEngineTests
{
    [Fact]
    public void Ipv4AndMacPrimitives_ParseFormatAndEnumerateCorrectly()
    {
        Assert.True(Ipv4Address.TryParse("192.168.1.42", out Ipv4Address ip));
        Assert.Equal("192.168.1.42", ip.ToString());

        Assert.True(Ipv4Address.TryParse("255.255.255.0", out Ipv4Address mask));
        List<Ipv4Address> hosts = [.. Ipv4Address.EnumerateSubnetHosts(ip, mask)];
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.254", hosts[^1].ToString());

        Assert.True(MacAddress.TryParse("50:C7:BF:12:34:56", out MacAddress mac));
        Assert.Equal("50:C7:BF:12:34:56", mac.ToString());
        Assert.Equal(0x50C7BFu, mac.OuiPrefix);
        Assert.Equal("TP-Link", OuiVendorLookup.Resolve(mac).VendorName);
    }

    [Fact]
    public void ArpPacket_RoundTripsZeroAllocation()
    {
        Span<byte> frame = stackalloc byte[PacketPrimitives.ArpFrameLength];
        MacAddress.TryParse("AA:BB:CC:DD:EE:01", out MacAddress srcMac);
        MacAddress.TryParse("11:22:33:44:55:66", out MacAddress dstMac);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address srcIp);
        Ipv4Address.TryParse("192.168.1.50", out Ipv4Address dstIp);

        PacketPrimitives.WriteArpPacket(
            frame,
            dstMac,
            srcMac,
            PacketPrimitives.ArpOpReply,
            srcMac,
            srcIp,
            dstMac,
            dstIp);

        Assert.True(PacketPrimitives.TryParseArp(
            frame,
            out MacAddress parsedEthSrc,
            out ushort op,
            out MacAddress senderMac,
            out Ipv4Address senderIp,
            out MacAddress targetMac,
            out Ipv4Address targetIp));

        Assert.Equal(srcMac, parsedEthSrc);
        Assert.Equal(PacketPrimitives.ArpOpReply, op);
        Assert.Equal(srcMac, senderMac);
        Assert.Equal(srcIp, senderIp);
        Assert.Equal(dstMac, targetMac);
        Assert.Equal(dstIp, targetIp);
    }

    [Fact]
    public void DnsExtractor_DecodesUdpPort53QueryDomain()
    {
        // Build Ethernet (14) + IPv4 (20) + UDP (8) + DNS Header (12) + QNAME "\x07youtube\x03com\x00" (13) + QTYPE/QCLASS (4)
        Span<byte> frame = stackalloc byte[71];
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12, 2), PacketPrimitives.EtherTypeIpv4);
        frame[14] = 0x45; // IPv4, IHL=5 (20 bytes)
        frame[23] = 17;   // UDP
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(34, 2), 54321); // Src Port
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(36, 2), 53);    // Dst Port 53 (DNS)

        int dnsOffset = 42;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(dnsOffset + 4, 2), 1); // QDCOUNT = 1

        ReadOnlySpan<byte> qname = [7, (byte)'y', (byte)'o', (byte)'u', (byte)'t', (byte)'u', (byte)'b', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0];
        qname.CopyTo(frame.Slice(dnsOffset + 12));

        Assert.True(PacketPrimitives.TryInspectIpv4Packet(
            frame,
            out _,
            out _,
            out _,
            out _,
            out ProtocolKind proto,
            out _,
            out ushort dstPort,
            out int l4PayloadOffset));

        Assert.Equal(ProtocolKind.Dns, proto);
        Assert.Equal(53, dstPort);
        Assert.True(PacketPrimitives.TryExtractDnsQueryDomain(frame, l4PayloadOffset, out string domain));
        Assert.Equal("youtube.com", domain);
    }

    [Fact]
    public void TokenBucketLimiter_EnforcesCapAndAllowsBurst()
    {
        var limiter = new TokenBucketLimiter();
        long t0 = 1_000_000;

        // 20 KB/s cap -> initial burst is max(16384, 5120) = 16384 bytes
        Assert.True(limiter.TryConsume(10000, 20 * 1024, t0));
        Assert.True(limiter.TryConsume(6000, 20 * 1024, t0));
        // Next 1500-byte packet at same timestamp exceeds burst -> throttled!
        Assert.False(limiter.TryConsume(1500, 20 * 1024, t0));

        // Advance time by 1 full second (Stopwatch.Frequency ticks) -> replenishes 16384 bytes
        long t1 = t0 + System.Diagnostics.Stopwatch.Frequency;
        Assert.True(limiter.TryConsume(1500, 20 * 1024, t1));
    }

    [Fact]
    public async Task DeviceRegistry_DetectsGatewayArpSpoofingAttack()
    {
        var registry = new DeviceRegistry();
        Ipv4Address.TryParse("192.168.1.10", out Ipv4Address localIp);
        MacAddress.TryParse("00:1B:21:AA:BB:CC", out MacAddress localMac);
        Ipv4Address.TryParse("192.168.1.1", out Ipv4Address gwIp);
        MacAddress.TryParse("50:C7:BF:00:11:22", out MacAddress realGwMac);
        MacAddress.TryParse("66:77:88:99:AA:BB", out MacAddress attackerMac);

        registry.ConfigureLocalNetwork(localIp, localMac, gwIp);
        registry.RegisterOrUpdateDevice(gwIp, realGwMac);
        Assert.Equal(realGwMac, registry.GatewayMac);

        // Simulate attacker claiming gateway IP
        registry.RegisterOrUpdateDevice(gwIp, attackerMac);

        // Trusted gateway MAC must remain untouched
        Assert.Equal(realGwMac, registry.GatewayMac);

        // Verify security alert event was emitted
        bool foundAlert = false;
        while (registry.Events.TryRead(out NetworkEvent? evt))
        {
            if (evt.Kind == NetworkEventKind.ArpSecurityAlert && evt.SecurityAlert is not null)
            {
                foundAlert = true;
                Assert.Equal(attackerMac, evt.SecurityAlert.OffendingMac);
                Assert.Equal(realGwMac, evt.SecurityAlert.ExpectedMac);
            }
        }
        Assert.True(foundAlert);
        await Task.CompletedTask;
    }
}
