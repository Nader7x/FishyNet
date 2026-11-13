using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;

namespace FishyNet.Core.Packets;

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
/// Zero-allocation DNS answer mapping extracted from a DNS response (A or AAAA record).
/// </summary>
public readonly record struct DnsIpMapping(
    string Domain,
    ushort RecordType,
    uint TtlSeconds,
    Ipv4Address Ipv4,
    ulong Ipv6High,
    ulong Ipv6Low)
{
    public bool IsIpv4 => RecordType == 1;
    public bool IsIpv6 => RecordType == 28;

    public IPAddress ToIPAddress()
    {
        if (!IsIpv6)
        {
            return Ipv4.ToIPAddress();
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(0, 8), Ipv6High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), Ipv6Low);
        return new IPAddress(bytes);
    }

    public string FormatAddress()
    {
        if (!IsIpv6)
        {
            return Ipv4.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(0, 8), Ipv6High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), Ipv6Low);
        Span<char> chars = stackalloc char[46];
        if (new IPAddress(bytes).TryFormat(chars, out int written))
        {
            return new string(chars.Slice(0, written));
        }

        return string.Empty;
    }
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
        return TryInspectIpv4Packet(
            frame,
            out ethDest,
            out ethSrc,
            out ipSrc,
            out ipDest,
            out protocol,
            out _,
            out srcPort,
            out dstPort,
            out l4PayloadOffset,
            out _);
    }

    /// <summary>
    /// Inspects an Ethernet + IPv4 packet and extracts MACs, IPs, L4 protocol, ports, accurate L4 payload offset,
    /// and effective IP packet end offset without allocating.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryInspectIpv4Packet(
        ReadOnlySpan<byte> frame,
        out MacAddress ethDest,
        out MacAddress ethSrc,
        out Ipv4Address ipSrc,
        out Ipv4Address ipDest,
        out ProtocolKind protocol,
        out byte ipProto,
        out ushort srcPort,
        out ushort dstPort,
        out int l4PayloadOffset,
        out int ipPacketEndOffset)
    {
        ethDest = MacAddress.Zero;
        ethSrc = MacAddress.Zero;
        ipSrc = Ipv4Address.Zero;
        ipDest = Ipv4Address.Zero;
        protocol = ProtocolKind.Other;
        ipProto = 0;
        srcPort = 0;
        dstPort = 0;
        l4PayloadOffset = 0;
        ipPacketEndOffset = 0;

        if (frame.Length < 34) return false;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        if (etherType != EtherTypeIpv4) return false;

        byte versionAndIhl = frame[14];
        if ((versionAndIhl >> 4) != 4) return false;
        int ihlBytes = (versionAndIhl & 0x0F) * 4;
        if (ihlBytes < 20 || frame.Length < 14 + ihlBytes) return false;

        ushort totalLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2));
        ipPacketEndOffset = (totalLen >= ihlBytes && 14 + totalLen <= frame.Length)
            ? 14 + totalLen
            : frame.Length;

        ethDest = MacAddress.FromBytes(frame.Slice(0, 6));
        ethSrc = MacAddress.FromBytes(frame.Slice(6, 6));
        ipSrc = Ipv4Address.FromBytes(frame.Slice(26, 4));
        ipDest = Ipv4Address.FromBytes(frame.Slice(30, 4));

        ipProto = frame[23];
        int l4Offset = 14 + ihlBytes;

        switch (ipProto)
        {
            case 1: // ICMP
                protocol = ProtocolKind.Icmp;
                l4PayloadOffset = l4Offset;
                return true;

            case 6: // TCP
                if (frame.Length < l4Offset + 20) return false;
                int tcpHeaderLen = ((frame[l4Offset + 12] >> 4) & 0x0F) * 4;
                if (tcpHeaderLen < 20 || frame.Length < l4Offset + tcpHeaderLen) return false;

                protocol = ProtocolKind.Tcp;
                srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset, 2));
                dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4Offset + 2, 2));
                if (srcPort is 80 or 443 or 8080 or 8443 || dstPort is 80 or 443 or 8080 or 8443)
                {
                    protocol = ProtocolKind.HttpTls;
                }
                l4PayloadOffset = l4Offset + tcpHeaderLen;
                if (l4PayloadOffset > ipPacketEndOffset) ipPacketEndOffset = frame.Length;
                return true;

            case 17: // UDP
                if (frame.Length < l4Offset + 8) return false;
                protocol = ProtocolKind.Udp;
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
                l4PayloadOffset = l4Offset + 8;
                if (l4PayloadOffset > ipPacketEndOffset) ipPacketEndOffset = frame.Length;
                return true;

            default:
                protocol = ProtocolKind.Other;
                l4PayloadOffset = l4Offset;
                return true;
        }
    }

    /// <summary>
    /// Extracts the TLS ClientHello Server Name Indication (SNI) hostname from a TCP payload.
    /// Zero heap allocations when no valid SNI is present.
    /// </summary>
    public static bool TryExtractTlsClientHelloSni(ReadOnlySpan<byte> tcpPayload, out string sni)
    {
        sni = string.Empty;
        if (tcpPayload.Length < 43) return false;

        // TLS Record Header: ContentType=0x16 (Handshake), LegacyVersion=0x0300..0x0304
        if (tcpPayload[0] != 0x16 || tcpPayload[1] != 0x03 || tcpPayload[2] > 0x04)
        {
            return false;
        }

        ushort recordLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(3, 2));
        if (recordLen < 38) return false;

        // Clamp to available bytes so multi-segment TLS 1.3 ClientHellos (e.g. ML-KEM key shares)
        // still extract SNI when SNI resides in the first TCP segment.
        int availRecordEnd = Math.Min(tcpPayload.Length, 5 + recordLen);
        if (availRecordEnd < 43) return false;

        // Handshake Header: HandshakeType=0x01 (ClientHello)
        if (tcpPayload[5] != 0x01) return false;
        int handshakeLen = (tcpPayload[6] << 16) | (tcpPayload[7] << 8) | tcpPayload[8];
        if (handshakeLen < 34) return false;

        int handshakeEnd = Math.Min(availRecordEnd, 9 + handshakeLen);

        // Skip LegacyVersion (2) + Random (32)
        int pos = 9 + 34;
        if (pos >= handshakeEnd) return false;

        // Session ID
        byte sessionIdLen = tcpPayload[pos++];
        if (pos + sessionIdLen + 2 > handshakeEnd) return false;
        pos += sessionIdLen;

        // Cipher Suites
        ushort cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(pos, 2));
        pos += 2;
        if (cipherSuitesLen < 2 || (cipherSuitesLen & 1) != 0 || pos + cipherSuitesLen + 1 > handshakeEnd)
        {
            return false;
        }
        pos += cipherSuitesLen;

        // Compression Methods
        byte compMethodsLen = tcpPayload[pos++];
        if (compMethodsLen < 1 || pos + compMethodsLen + 2 > handshakeEnd) return false;
        pos += compMethodsLen;

        // Extensions Length
        ushort extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(pos, 2));
        pos += 2;
        int extensionsEnd = Math.Min(handshakeEnd, pos + extensionsLen);

        while (pos + 4 <= extensionsEnd)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(pos, 2));
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(pos + 2, 2));
            pos += 4;

            if (pos + extLen > extensionsEnd) break;

            if (extType == 0x0000) // server_name
            {
                if (extLen < 5) return false;
                ushort listLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(pos, 2));
                int listPos = pos + 2;
                int listEnd = Math.Min(pos + extLen, listPos + listLen);

                while (listPos + 3 <= listEnd)
                {
                    byte nameType = tcpPayload[listPos];
                    ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(tcpPayload.Slice(listPos + 1, 2));
                    listPos += 3;

                    if (listPos + nameLen > listEnd) break;

                    if (nameType == 0x00 && nameLen is >= 3 and <= 255)
                    {
                        return TryDecodeAsciiHost(tcpPayload.Slice(listPos, nameLen), out sni);
                    }

                    listPos += nameLen;
                }

                return false;
            }

            pos += extLen;
        }

        return false;
    }

    /// <summary>
    /// Extracts the HTTP Host header from a plaintext HTTP/1.x request TCP payload.
    /// Zero heap allocations when no valid Host header is found.
    /// </summary>
    public static bool TryExtractHttpHost(ReadOnlySpan<byte> tcpPayload, out string host)
    {
        host = string.Empty;
        if (tcpPayload.Length < 16 || !StartsWithHttpMethod(tcpPayload))
        {
            return false;
        }

        int scanLimit = Math.Min(tcpPayload.Length, 4096);
        ReadOnlySpan<byte> window = tcpPayload.Slice(0, scanLimit);

        int firstLf = window.IndexOf((byte)'\n');
        if (firstLf <= 0) return false;

        int pos = firstLf + 1;
        while (pos < window.Length)
        {
            int relLf = window.Slice(pos).IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = relLf >= 0
                ? window.Slice(pos, relLf)
                : window.Slice(pos);

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line.Slice(0, line.Length - 1);
            }

            if (line.IsEmpty)
            {
                break; // End of HTTP headers
            }

            if (line.Length > 5 && IsHostHeaderPrefix(line))
            {
                ReadOnlySpan<byte> val = TrimAsciiWhitespace(line.Slice(5));
                if (val.IsEmpty) return false;

                // Strip optional :port suffix (supporting bracketed IPv6 literals)
                if (val[0] == (byte)'[')
                {
                    int closeBracket = val.IndexOf((byte)']');
                    if (closeBracket > 1)
                    {
                        val = val.Slice(0, closeBracket + 1);
                    }
                }
                else
                {
                    int colon = val.IndexOf((byte)':');
                    if (colon > 0)
                    {
                        val = val.Slice(0, colon);
                    }
                }

                return TryDecodeAsciiHost(val, out host);
            }

            if (relLf < 0) break;
            pos += relLf + 1;
        }

        return false;
    }

    /// <summary>
    /// Extracts A (type 1) and AAAA (type 28) mappings from a DNS response (QR=1) UDP payload,
    /// supporting RFC 1035 0xC0 name compression pointers with bounds and cycle safety.
    /// </summary>
    public static bool TryExtractDnsResponseMappings(
        ReadOnlySpan<byte> udpPayload,
        Span<DnsIpMapping> mappings,
        out int count)
    {
        count = 0;
        if (udpPayload.Length < 12 || mappings.IsEmpty)
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(2, 2));
        bool isResponse = (flags & 0x8000) != 0;
        int rcode = flags & 0x000F;
        if (!isResponse || rcode != 0)
        {
            return false;
        }

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(6, 2));
        if (anCount == 0)
        {
            return false;
        }

        int offset = 12;

        // Skip question section without allocating
        for (int q = 0; q < qdCount; q++)
        {
            if (!TrySkipDnsName(udpPayload, ref offset) || offset + 4 > udpPayload.Length)
            {
                return false;
            }
            offset += 4;
        }

        int lastDecodedNameOffset = -1;
        string lastDecodedDomain = string.Empty;

        for (int a = 0; a < anCount && offset < udpPayload.Length; a++)
        {
            int rrNameOffset = offset;
            if (!TrySkipDnsName(udpPayload, ref offset) || offset + 10 > udpPayload.Length)
            {
                break;
            }

            ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(offset, 2));
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(offset + 2, 2));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(udpPayload.Slice(offset + 4, 4));
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(udpPayload.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > udpPayload.Length)
            {
                break;
            }

            // IN class = 1 (mask out mDNS cache-flush bit 0x8000)
            bool isInClass = (rrClass & 0x7FFF) == 1;
            bool isA = rrType == 1 && rdLength == 4;
            bool isAaaa = rrType == 28 && rdLength == 16;

            if (isInClass && (isA || isAaaa))
            {
                string domain;
                if (rrNameOffset == lastDecodedNameOffset && !string.IsNullOrEmpty(lastDecodedDomain))
                {
                    domain = lastDecodedDomain;
                }
                else if (TryReadDnsName(udpPayload, rrNameOffset, out domain))
                {
                    lastDecodedNameOffset = rrNameOffset;
                    lastDecodedDomain = domain;
                }
                else
                {
                    offset += rdLength;
                    continue;
                }

                if (isA)
                {
                    Ipv4Address ipv4 = Ipv4Address.FromBytes(udpPayload.Slice(offset, 4));
                    if (!ipv4.IsZero)
                    {
                        mappings[count++] = new DnsIpMapping(domain, 1, ttl, ipv4, 0UL, 0UL);
                    }
                }
                else
                {
                    ulong hi = BinaryPrimitives.ReadUInt64BigEndian(udpPayload.Slice(offset, 8));
                    ulong lo = BinaryPrimitives.ReadUInt64BigEndian(udpPayload.Slice(offset + 8, 8));
                    if (hi != 0UL || lo != 0UL)
                    {
                        mappings[count++] = new DnsIpMapping(domain, 28, ttl, Ipv4Address.Zero, hi, lo);
                    }
                }

                if (count >= mappings.Length)
                {
                    break;
                }
            }

            offset += rdLength;
        }

        return count > 0;
    }

    private static bool TrySkipDnsName(ReadOnlySpan<byte> dns, ref int offset)
    {
        int pos = offset;
        int labels = 0;

        while (pos < dns.Length && labels++ < 128)
        {
            byte len = dns[pos];
            if (len == 0)
            {
                offset = pos + 1;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 2 > dns.Length) return false;
                offset = pos + 2;
                return true;
            }

            if ((len & 0xC0) != 0 || len > 63 || pos + 1 + len > dns.Length)
            {
                return false;
            }

            pos += 1 + len;
        }

        return false;
    }

    private static bool TryReadDnsName(ReadOnlySpan<byte> dns, int startOffset, out string domain)
    {
        domain = string.Empty;
        if (startOffset < 0 || startOffset >= dns.Length) return false;

        Span<char> charBuf = stackalloc char[255];
        int charLen = 0;
        int pos = startOffset;
        int jumps = 0;
        int labelOps = 0;

        while (pos < dns.Length && labelOps++ < 128)
        {
            byte len = dns[pos];
            if (len == 0)
            {
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= dns.Length || ++jumps > 16)
                {
                    return false;
                }

                int target = ((len & 0x3F) << 8) | dns[pos + 1];
                if (target < 12 || target >= dns.Length || target == pos)
                {
                    return false;
                }

                pos = target;
                continue;
            }

            if ((len & 0xC0) != 0 || len > 63 || pos + 1 + len > dns.Length)
            {
                return false;
            }

            pos++;
            if (charLen > 0)
            {
                if (charLen >= charBuf.Length) return false;
                charBuf[charLen++] = '.';
            }

            for (int i = 0; i < len; i++)
            {
                byte b = dns[pos++];
                if (b is <= 32 or > 126 || charLen >= charBuf.Length)
                {
                    return false;
                }

                charBuf[charLen++] = b is >= (byte)'A' and <= (byte)'Z'
                    ? (char)(b + 32)
                    : (char)b;
            }
        }

        if (charLen < 3) return false;
        domain = new string(charBuf.Slice(0, charLen));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithHttpMethod(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return false;
        return payload.StartsWith("GET "u8)
            || payload.StartsWith("POST "u8)
            || payload.StartsWith("HEAD "u8)
            || payload.StartsWith("PUT "u8)
            || payload.StartsWith("DELETE "u8)
            || payload.StartsWith("OPTIONS "u8)
            || payload.StartsWith("PATCH "u8)
            || payload.StartsWith("CONNECT "u8)
            || payload.StartsWith("TRACE "u8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHostHeaderPrefix(ReadOnlySpan<byte> line)
    {
        return (line[0] | 0x20) == 'h'
            && (line[1] | 0x20) == 'o'
            && (line[2] | 0x20) == 's'
            && (line[3] | 0x20) == 't'
            && line[4] == ':';
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == ' ' || span[start] == '\t'))
        {
            start++;
        }

        int end = span.Length - 1;
        while (end >= start && (span[end] == ' ' || span[end] == '\t'))
        {
            end--;
        }

        return start <= end ? span.Slice(start, end - start + 1) : ReadOnlySpan<byte>.Empty;
    }

    private static bool TryDecodeAsciiHost(ReadOnlySpan<byte> bytes, out string host)
    {
        host = string.Empty;
        if (bytes.Length is < 3 or > 255) return false;

        Span<char> chars = stackalloc char[255];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b is <= 32 or > 126)
            {
                return false;
            }

            chars[i] = b is >= (byte)'A' and <= (byte)'Z'
                ? (char)(b + 32)
                : (char)b;
        }

        host = new string(chars.Slice(0, bytes.Length));
        return true;
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
