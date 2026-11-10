using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;

namespace SelfishNetv3.Core.Packets;

public enum ProtocolKind : byte
{
    Other = 0,
    Arp = 1,
    Icmp = 2,
    Tcp = 3,
    Udp = 4,
    Dns = 5,
    HttpTls = 6
}

/// <summary>
/// Zero-allocation 32-bit IPv4 address stored in host byte order (big-endian numeric comparison friendly).
/// e.g. 192.168.1.1 -> 0xC0A80101.
/// </summary>
public readonly record struct Ipv4Address(uint HostOrderValue) : IComparable<Ipv4Address>
{
    public static readonly Ipv4Address Zero = new(0);
    public static readonly Ipv4Address Broadcast = new(0xFFFFFFFFu);

    public byte B0 => (byte)(HostOrderValue >> 24);
    public byte B1 => (byte)(HostOrderValue >> 16);
    public byte B2 => (byte)(HostOrderValue >> 8);
    public byte B3 => (byte)HostOrderValue;

    public bool IsZero => HostOrderValue == 0;

    public static Ipv4Address FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return Zero;
        return new Ipv4Address(BinaryPrimitives.ReadUInt32BigEndian(bytes));
    }

    public static Ipv4Address FromIPAddress(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (address.TryWriteBytes(bytes, out int written) && written == 4)
        {
            return FromBytes(bytes);
        }
        return Zero;
    }

    public IPAddress ToIPAddress()
    {
        Span<byte> bytes = stackalloc byte[4];
        WriteTo(bytes);
        return new IPAddress(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, HostOrderValue);
    }

    public static bool TryParse(ReadOnlySpan<char> text, out Ipv4Address result)
    {
        result = Zero;
        text = text.Trim();
        if (text.IsEmpty) return false;

        uint value = 0;
        int partCount = 0;
        int currentPart = 0;
        bool hasDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= '0' and <= '9')
            {
                currentPart = (currentPart * 10) + (c - '0');
                if (currentPart > 255) return false;
                hasDigit = true;
            }
            else if (c == '.')
            {
                if (!hasDigit || partCount >= 3) return false;
                value = (value << 8) | (uint)currentPart;
                currentPart = 0;
                hasDigit = false;
                partCount++;
            }
            else
            {
                return false;
            }
        }

        if (!hasDigit || partCount != 3) return false;
        value = (value << 8) | (uint)currentPart;
        result = new Ipv4Address(value);
        return true;
    }

    public static IEnumerable<Ipv4Address> EnumerateSubnetHosts(Ipv4Address localIp, Ipv4Address subnetMask, int maxHosts = 1024)
    {
        if (localIp.IsZero || subnetMask.IsZero) yield break;

        uint mask = subnetMask.HostOrderValue;
        uint network = localIp.HostOrderValue & mask;
        uint broadcast = network | ~mask;

        if (broadcast <= network + 1) yield break;

        uint firstHost = network + 1;
        uint lastHost = broadcast - 1;
        uint count = lastHost - firstHost + 1;
        if (count > (uint)maxHosts)
        {
            // Cap scan window around localIp for large /16 or /8 networks
            uint halfWindow = (uint)maxHosts / 2;
            uint start = localIp.HostOrderValue > firstHost + halfWindow
                ? localIp.HostOrderValue - halfWindow
                : firstHost;
            uint end = Math.Min(lastHost, start + (uint)maxHosts - 1);
            firstHost = start;
            lastHost = end;
        }

        for (uint current = firstHost; current <= lastHost; current++)
        {
            yield return new Ipv4Address(current);
        }
    }

    public int CompareTo(Ipv4Address other) => HostOrderValue.CompareTo(other.HostOrderValue);

    public override string ToString() => $"{B0}.{B1}.{B2}.{B3}";
}

/// <summary>
/// Zero-allocation 48-bit MAC address stored in the lower 48 bits of a ulong (big-endian order).
/// </summary>
public readonly record struct MacAddress(ulong RawValue) : IComparable<MacAddress>
{
    public static readonly MacAddress Zero = new(0UL);
    public static readonly MacAddress Broadcast = new(0xFFFFFFFFFFFFUL);

    public bool IsZero => RawValue == 0UL;
    public bool IsBroadcast => (RawValue & 0xFFFFFFFFFFFFUL) == 0xFFFFFFFFFFFFUL;

    /// <summary>
    /// 24-bit IEEE OUI vendor prefix (e.g. 0x001A2B).
    /// </summary>
    public uint OuiPrefix => (uint)((RawValue >> 24) & 0xFFFFFFUL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6) return Zero;
        ulong high = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        ulong low = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2));
        return new MacAddress((high << 16) | low);
    }

    public static MacAddress FromPhysicalAddress(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length >= 6 ? FromBytes(bytes) : Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(RawValue >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)(RawValue & 0xFFFFUL));
    }

    public static bool TryParse(ReadOnlySpan<char> text, out MacAddress result)
    {
        result = Zero;
        text = text.Trim();
        if (text.Length < 12) return false;

        ulong acc = 0;
        int hexDigits = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ':' or '-' or '.') continue;

            int nibble = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (nibble < 0) return false;
            acc = (acc << 4) | (uint)nibble;
            hexDigits++;
        }

        if (hexDigits != 12) return false;
        result = new MacAddress(acc & 0xFFFFFFFFFFFFUL);
        return true;
    }

    public int CompareTo(MacAddress other) => RawValue.CompareTo(other.RawValue);

    public override string ToString()
    {
        return string.Create(17, RawValue, static (span, val) =>
        {
            ReadOnlySpan<char> hex = "0123456789ABCDEF";
            for (int i = 0; i < 6; i++)
            {
                byte b = (byte)((val >> ((5 - i) * 8)) & 0xFF);
                int offset = i * 3;
                span[offset] = hex[b >> 4];
                span[offset + 1] = hex[b & 0x0F];
                if (i < 5) span[offset + 2] = ':';
            }
        });
    }
}

public static class PacketPrimitives
{
    public const int ArpFrameLength = 42;
    public const int WakeOnLanPayloadLength = 102;
    public const ushort EtherTypeIpv4 = 0x0800;
    public const ushort EtherTypeArp = 0x0806;
    public const ushort ArpOpRequest = 1;
    public const ushort ArpOpReply = 2;

    /// <summary>
    /// Writes a 42-byte Ethernet + IPv4 ARP frame directly into a preallocated Span with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArpPacket(
        Span<byte> buffer,
        MacAddress ethDestMac,
        MacAddress ethSrcMac,
        ushort arpOperation,
        MacAddress senderMac,
        Ipv4Address senderIp,
        MacAddress targetMac,
        Ipv4Address targetIp)
    {
        if (buffer.Length < ArpFrameLength)
        {
            throw new ArgumentException("ARP buffer must be at least 42 bytes.", nameof(buffer));
        }

        ethDestMac.WriteTo(buffer.Slice(0, 6));
        ethSrcMac.WriteTo(buffer.Slice(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(12, 2), EtherTypeArp);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(14, 2), 0x0001); // Hardware type: Ethernet (1)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(16, 2), EtherTypeIpv4); // Protocol type: IPv4 (0x0800)
        buffer[18] = 6; // Hardware size
        buffer[19] = 4; // Protocol size
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(20, 2), arpOperation);
        senderMac.WriteTo(buffer.Slice(22, 6));
        senderIp.WriteTo(buffer.Slice(28, 4));
        targetMac.WriteTo(buffer.Slice(32, 6));
        targetIp.WriteTo(buffer.Slice(38, 4));
    }

    /// <summary>
    /// Parses a 42-byte Ethernet ARP frame with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseArp(
        ReadOnlySpan<byte> frame,
        out MacAddress ethSrcMac,
        out ushort operation,
        out MacAddress senderMac,
        out Ipv4Address senderIp,
        out MacAddress targetMac,
        out Ipv4Address targetIp)
    {
        ethSrcMac = MacAddress.Zero;
        operation = 0;
        senderMac = MacAddress.Zero;
        senderIp = Ipv4Address.Zero;
        targetMac = MacAddress.Zero;
        targetIp = Ipv4Address.Zero;

        if (frame.Length < ArpFrameLength) return false;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        if (etherType != EtherTypeArp) return false;

        ethSrcMac = MacAddress.FromBytes(frame.Slice(6, 6));
        operation = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(20, 2));
        senderMac = MacAddress.FromBytes(frame.Slice(22, 6));
        senderIp = Ipv4Address.FromBytes(frame.Slice(28, 4));
        targetMac = MacAddress.FromBytes(frame.Slice(32, 6));
        targetIp = Ipv4Address.FromBytes(frame.Slice(38, 4));
        return true;
    }

    /// <summary>
    /// Rewrites the 12-byte Ethernet destination and source MAC addresses in-place inside the ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RewriteEthernetHeaderInPlace(Span<byte> frame, MacAddress newDestMac, MacAddress newSrcMac)
    {
        newDestMac.WriteTo(frame.Slice(0, 6));
        newSrcMac.WriteTo(frame.Slice(6, 6));
    }

    /// <summary>
    /// Inspects an Ethernet + IPv4 packet and extracts MACs, IPs, L4 ports, and L7 protocol classification without allocating.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryInspectIpv4Packet(
        ReadOnlySpan<byte> frame,
        out MacAddress ethDest,
        out MacAddress ethSrc,
        out Ipv4Address ipSrc,
        out Ipv4Address ipDest,
        out ProtocolKind protocol,
        out ushort srcPort,
        out ushort dstPort,
        out int l4PayloadOffset)
    {
        ethDest = MacAddress.Zero;
        ethSrc = MacAddress.Zero;
        ipSrc = Ipv4Address.Zero;
        ipDest = Ipv4Address.Zero;
        protocol = ProtocolKind.Other;
        srcPort = 0;
        dstPort = 0;
        l4PayloadOffset = 0;

        if (frame.Length < 34) return false;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        if (etherType != EtherTypeIpv4) return false;

        byte versionAndIhl = frame[14];
        if ((versionAndIhl >> 4) != 4) return false;
        int ihlBytes = (versionAndIhl & 0x0F) * 4;
        if (ihlBytes < 20 || frame.Length < 14 + ihlBytes) return false;

        ethDest = MacAddress.FromBytes(frame.Slice(0, 6));
        ethSrc = MacAddress.FromBytes(frame.Slice(6, 6));
        ipSrc = Ipv4Address.FromBytes(frame.Slice(26, 4));
        ipDest = Ipv4Address.FromBytes(frame.Slice(30, 4));

        byte ipProto = frame[23];
        int l4Offset = 14 + ihlBytes;

        switch (ipProto)
        {
            case 1: // ICMP
                protocol = ProtocolKind.Icmp;
                l4PayloadOffset = l4Offset;
                return true;

            case 6: // TCP
                protocol = ProtocolKind.Tcp;
                if (frame.Length >= l4Offset + 4)
                {
                    srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset, 2));
                    dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset + 2, 2));
                    if (srcPort is 80 or 443 or 8080 or 8443 || dstPort is 80 or 443 or 8080 or 8443)
                    {
                        protocol = ProtocolKind.HttpTls;
                    }
                }
                l4PayloadOffset = l4Offset + 20;
                return true;

            case 17: // UDP
                protocol = ProtocolKind.Udp;
                if (frame.Length >= l4Offset + 8)
                {
                    srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset, 2));
                    dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset + 2, 2));
                    if (srcPort is 53 or 5353 || dstPort is 53 or 5353)
                    {
                        protocol = ProtocolKind.Dns;
                    }
                    else if (srcPort == 443 || dstPort == 443)
                    {
                        protocol = ProtocolKind.HttpTls; // QUIC / HTTP3
                    }
                }
                l4PayloadOffset = l4Offset + 8;
                return true;

            default:
                protocol = ProtocolKind.Other;
                l4PayloadOffset = l4Offset;
                return true;
        }
    }

    /// <summary>
    /// Extracts the queried domain name from a UDP port 53 / 5353 DNS packet.
    /// Uses stackalloc char buffer during label decoding and allocates only the final string.
    /// </summary>
    public static bool TryExtractDnsQueryDomain(ReadOnlySpan<byte> frame, int udpPayloadOffset, out string domain)
    {
        domain = string.Empty;
        if (udpPayloadOffset <= 0 || frame.Length < udpPayloadOffset + 12) return false;

        ReadOnlySpan<byte> dns = frame.Slice(udpPayloadOffset);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(2, 2));
        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(4, 2));

        // Only inspect standard queries (QR bit == 0, opcode == 0) or mDNS with at least 1 question
        bool isResponse = (flags & 0x8000) != 0;
        if (isResponse || qdCount == 0 || dns.Length <= 13) return false;

        Span<char> charBuf = stackalloc char[255];
        int charLen = 0;
        int pos = 12;

        while (pos < dns.Length)
        {
            byte labelLen = dns[pos++];
            if (labelLen == 0)
            {
                break;
            }

            // Reject compressed pointers in initial question label or invalid lengths
            if ((labelLen & 0xC0) != 0 || labelLen > 63 || pos + labelLen > dns.Length)
            {
                return false;
            }

            if (charLen > 0)
            {
                if (charLen >= charBuf.Length) return false;
                charBuf[charLen++] = '.';
            }

            for (int i = 0; i < labelLen; i++)
            {
                byte b = dns[pos++];
                if (b is < 32 or > 126 || charLen >= charBuf.Length)
                {
                    return false;
                }
                charBuf[charLen++] = char.ToLowerInvariant((char)b);
            }
        }

        if (charLen < 3) return false;
        domain = new string(charBuf.Slice(0, charLen));
        return true;
    }

    /// <summary>
    /// Writes a 102-byte Wake-on-LAN (WoL) Magic Packet payload (6x 0xFF followed by 16x Target MAC).
    /// </summary>
    public static void WriteWakeOnLanMagicPacket(Span<byte> payload, MacAddress targetMac)
    {
        if (payload.Length < WakeOnLanPayloadLength)
        {
            throw new ArgumentException("Wake-on-LAN buffer must be at least 102 bytes.", nameof(payload));
        }

        payload.Slice(0, 6).Fill(0xFF);
        for (int i = 0; i < 16; i++)
        {
            targetMac.WriteTo(payload.Slice(6 + (i * 6), 6));
        }
    }
}
