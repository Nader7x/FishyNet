using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FishyNet.Core.Packets;

namespace FishyNet.Core.Native;

/// <summary>
/// Value-type snapshot entry representing an active OS TCP or UDP socket and its owning Windows PID.
/// </summary>
public readonly record struct SocketConnectionEntry(
    byte IpProtocol,
    bool IsIpv6,
    Ipv4Address LocalIpv4,
    ulong LocalIpv6High,
    ulong LocalIpv6Low,
    ushort LocalPort,
    Ipv4Address RemoteIpv4,
    ulong RemoteIpv6High,
    ulong RemoteIpv6Low,
    ushort RemotePort,
    int OwningPid,
    uint TcpState)
{
    public bool IsWildcardLocal => IsIpv6
        ? (LocalIpv6High == 0UL && LocalIpv6Low == 0UL)
        : LocalIpv4.IsZero;

    public bool IsIpv4MappedIpv6 =>
        IsIpv6 && LocalIpv6High == 0UL && (LocalIpv6Low >> 32) == 0xFFFFUL;

    public Ipv4Address EffectiveLocalIpv4 =>
        IsIpv6
            ? (IsIpv4MappedIpv6 ? new Ipv4Address((uint)(LocalIpv6Low & 0xFFFFFFFFUL)) : Ipv4Address.Zero)
            : LocalIpv4;

    public bool IsRemoteIpv4MappedIpv6 =>
        IsIpv6 && RemoteIpv6High == 0UL && (RemoteIpv6Low >> 32) == 0xFFFFUL;

    public Ipv4Address EffectiveRemoteIpv4 =>
        IsIpv6
            ? (IsRemoteIpv4MappedIpv6 ? new Ipv4Address((uint)(RemoteIpv6Low & 0xFFFFFFFFUL)) : Ipv4Address.Zero)
            : RemoteIpv4;
}

/// <summary>
/// Zero-allocation, Native-AOT-safe P/Invoke wrapper over iphlpapi.dll
/// (GetExtendedTcpTable / GetExtendedUdpTable) with reusable buffer and Marshal.OffsetOf table access.
/// </summary>
internal static unsafe partial class IpHelperNative
{
    public const int AfInet = 2;
    public const int AfInet6 = 23;
    public const int TcpTableOwnerPidAll = 5;
    public const int UdpTableOwnerPid = 1;

    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaxBufferBytes = 8 * 1024 * 1024;

    private static readonly int Tcp4TableOffset = (int)Marshal.OffsetOf<MibTcpTableOwnerPid>(nameof(MibTcpTableOwnerPid.table));
    private static readonly int Tcp6TableOffset = (int)Marshal.OffsetOf<MibTcp6TableOwnerPid>(nameof(MibTcp6TableOwnerPid.table));
    private static readonly int Udp4TableOffset = (int)Marshal.OffsetOf<MibUdpTableOwnerPid>(nameof(MibUdpTableOwnerPid.table));
    private static readonly int Udp6TableOffset = (int)Marshal.OffsetOf<MibUdp6TableOwnerPid>(nameof(MibUdp6TableOwnerPid.table));

    private static readonly Lock BufferLock = new();
    private static byte[] _reusableBuffer = new byte[32768];

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpTableOwnerPid
    {
        public uint dwNumEntries;
        public MibTcpRowOwnerPid table;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcp6RowOwnerPid
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcp6TableOwnerPid
    {
        public uint dwNumEntries;
        public MibTcp6RowOwnerPid table;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdpRowOwnerPid
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdpTableOwnerPid
    {
        public uint dwNumEntries;
        public MibUdpRowOwnerPid table;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdp6RowOwnerPid
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdp6TableOwnerPid
    {
        public uint dwNumEntries;
        public MibUdp6RowOwnerPid table;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        nint pTcpTable,
        ref uint dwOutBufLen,
        int sort,
        int ipVersion,
        int tblClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(
        nint pUdpTable,
        ref uint dwOutBufLen,
        int sort,
        int ipVersion,
        int tblClass,
        uint reserved);

    /// <summary>
    /// Captures all active IPv4/IPv6 TCP and UDP socket-to-PID mappings into a caller-reused list.
    /// Zero heap allocations once buffers and destination capacity warm up.
    /// </summary>
    public static bool TrySnapshotSockets(List<SocketConnectionEntry> destination, bool includeIpv6 = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        bool anySucceeded = false;

        lock (BufferLock)
        {
            anySucceeded |= TryAppendTcpTable(AfInet, destination);
            anySucceeded |= TryAppendUdpTable(AfInet, destination);

            if (includeIpv6)
            {
                anySucceeded |= TryAppendTcpTable(AfInet6, destination);
                anySucceeded |= TryAppendUdpTable(AfInet6, destination);
            }
        }

        return anySucceeded;
    }

    private static bool TryAppendTcpTable(int addressFamily, List<SocketConnectionEntry> destination)
    {
        try
        {
            if (!TryQueryTable(isTcp: true, addressFamily, out int bytesRead))
            {
                return false;
            }

            ReadOnlySpan<byte> span = _reusableBuffer.AsSpan(0, bytesRead);
            if (addressFamily == AfInet)
            {
                ParseTcp4Table(span, destination);
            }
            else
            {
                ParseTcp6Table(span, destination);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAppendUdpTable(int addressFamily, List<SocketConnectionEntry> destination)
    {
        try
        {
            if (!TryQueryTable(isTcp: false, addressFamily, out int bytesRead))
            {
                return false;
            }

            ReadOnlySpan<byte> span = _reusableBuffer.AsSpan(0, bytesRead);
            if (addressFamily == AfInet)
            {
                ParseUdp4Table(span, destination);
            }
            else
            {
                ParseUdp6Table(span, destination);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryQueryTable(bool isTcp, int addressFamily, out int bytesRead)
    {
        bytesRead = 0;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint size = (uint)_reusableBuffer.Length;
            uint status;
            fixed (byte* ptr = _reusableBuffer)
            {
                status = isTcp
                    ? GetExtendedTcpTable((nint)ptr, ref size, sort: 0, addressFamily, TcpTableOwnerPidAll, reserved: 0)
                    : GetExtendedUdpTable((nint)ptr, ref size, sort: 0, addressFamily, UdpTableOwnerPid, reserved: 0);
            }

            if (status == NoError)
            {
                bytesRead = (int)Math.Min(size, (uint)_reusableBuffer.Length);
                return bytesRead >= sizeof(uint);
            }

            if (status != ErrorInsufficientBuffer)
            {
                return false;
            }

            int needed = (int)Math.Min((long)Math.Max(size, (uint)_reusableBuffer.Length * 2U), MaxBufferBytes);
            if (needed <= _reusableBuffer.Length)
            {
                return false;
            }

            _reusableBuffer = new byte[needed];
        }

        return false;
    }

    internal static void ParseTcp4Table(ReadOnlySpan<byte> tableBytes, List<SocketConnectionEntry> destination)
    {
        if (tableBytes.Length < Tcp4TableOffset) return;

        fixed (byte* basePtr = tableBytes)
        {
            uint numEntries = Unsafe.ReadUnaligned<uint>(basePtr);
            int rowSize = sizeof(MibTcpRowOwnerPid);
            int maxRows = (tableBytes.Length - Tcp4TableOffset) / rowSize;
            int count = (int)Math.Min(numEntries, (uint)Math.Max(0, maxRows));

            byte* rowPtr = basePtr + Tcp4TableOffset;
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibTcpRowOwnerPid row = Unsafe.ReadUnaligned<MibTcpRowOwnerPid>(rowPtr);
                Ipv4Address localIp = Ipv4FromNetworkDword(row.dwLocalAddr);
                Ipv4Address remoteIp = Ipv4FromNetworkDword(row.dwRemoteAddr);
                ushort localPort = NetworkToHostPort(row.dwLocalPort);
                ushort remotePort = NetworkToHostPort(row.dwRemotePort);

                destination.Add(new SocketConnectionEntry(
                    IpProtocol: 6,
                    IsIpv6: false,
                    LocalIpv4: localIp,
                    LocalIpv6High: 0UL,
                    LocalIpv6Low: 0UL,
                    LocalPort: localPort,
                    RemoteIpv4: remoteIp,
                    RemoteIpv6High: 0UL,
                    RemoteIpv6Low: 0UL,
                    RemotePort: remotePort,
                    OwningPid: unchecked((int)row.dwOwningPid),
                    TcpState: row.dwState));
            }
        }
    }

    internal static void ParseTcp6Table(ReadOnlySpan<byte> tableBytes, List<SocketConnectionEntry> destination)
    {
        if (tableBytes.Length < Tcp6TableOffset) return;

        fixed (byte* basePtr = tableBytes)
        {
            uint numEntries = Unsafe.ReadUnaligned<uint>(basePtr);
            int rowSize = sizeof(MibTcp6RowOwnerPid);
            int maxRows = (tableBytes.Length - Tcp6TableOffset) / rowSize;
            int count = (int)Math.Min(numEntries, (uint)Math.Max(0, maxRows));

            byte* rowPtr = basePtr + Tcp6TableOffset;
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibTcp6RowOwnerPid row = Unsafe.ReadUnaligned<MibTcp6RowOwnerPid>(rowPtr);
                var localSpan = new ReadOnlySpan<byte>(row.ucLocalAddr, 16);
                var remoteSpan = new ReadOnlySpan<byte>(row.ucRemoteAddr, 16);

                destination.Add(new SocketConnectionEntry(
                    IpProtocol: 6,
                    IsIpv6: true,
                    LocalIpv4: Ipv4Address.Zero,
                    LocalIpv6High: BinaryPrimitives.ReadUInt64BigEndian(localSpan.Slice(0, 8)),
                    LocalIpv6Low: BinaryPrimitives.ReadUInt64BigEndian(localSpan.Slice(8, 8)),
                    LocalPort: NetworkToHostPort(row.dwLocalPort),
                    RemoteIpv4: Ipv4Address.Zero,
                    RemoteIpv6High: BinaryPrimitives.ReadUInt64BigEndian(remoteSpan.Slice(0, 8)),
                    RemoteIpv6Low: BinaryPrimitives.ReadUInt64BigEndian(remoteSpan.Slice(8, 8)),
                    RemotePort: NetworkToHostPort(row.dwRemotePort),
                    OwningPid: unchecked((int)row.dwOwningPid),
                    TcpState: row.dwState));
            }
        }
    }

    internal static void ParseUdp4Table(ReadOnlySpan<byte> tableBytes, List<SocketConnectionEntry> destination)
    {
        if (tableBytes.Length < Udp4TableOffset) return;

        fixed (byte* basePtr = tableBytes)
        {
            uint numEntries = Unsafe.ReadUnaligned<uint>(basePtr);
            int rowSize = sizeof(MibUdpRowOwnerPid);
            int maxRows = (tableBytes.Length - Udp4TableOffset) / rowSize;
            int count = (int)Math.Min(numEntries, (uint)Math.Max(0, maxRows));

            byte* rowPtr = basePtr + Udp4TableOffset;
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibUdpRowOwnerPid row = Unsafe.ReadUnaligned<MibUdpRowOwnerPid>(rowPtr);
                destination.Add(new SocketConnectionEntry(
                    IpProtocol: 17,
                    IsIpv6: false,
                    LocalIpv4: Ipv4FromNetworkDword(row.dwLocalAddr),
                    LocalIpv6High: 0UL,
                    LocalIpv6Low: 0UL,
                    LocalPort: NetworkToHostPort(row.dwLocalPort),
                    RemoteIpv4: Ipv4Address.Zero,
                    RemoteIpv6High: 0UL,
                    RemoteIpv6Low: 0UL,
                    RemotePort: 0,
                    OwningPid: unchecked((int)row.dwOwningPid),
                    TcpState: 0));
            }
        }
    }

    internal static void ParseUdp6Table(ReadOnlySpan<byte> tableBytes, List<SocketConnectionEntry> destination)
    {
        if (tableBytes.Length < Udp6TableOffset) return;

        fixed (byte* basePtr = tableBytes)
        {
            uint numEntries = Unsafe.ReadUnaligned<uint>(basePtr);
            int rowSize = sizeof(MibUdp6RowOwnerPid);
            int maxRows = (tableBytes.Length - Udp6TableOffset) / rowSize;
            int count = (int)Math.Min(numEntries, (uint)Math.Max(0, maxRows));

            byte* rowPtr = basePtr + Udp6TableOffset;
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibUdp6RowOwnerPid row = Unsafe.ReadUnaligned<MibUdp6RowOwnerPid>(rowPtr);
                var localSpan = new ReadOnlySpan<byte>(row.ucLocalAddr, 16);

                destination.Add(new SocketConnectionEntry(
                    IpProtocol: 17,
                    IsIpv6: true,
                    LocalIpv4: Ipv4Address.Zero,
                    LocalIpv6High: BinaryPrimitives.ReadUInt64BigEndian(localSpan.Slice(0, 8)),
                    LocalIpv6Low: BinaryPrimitives.ReadUInt64BigEndian(localSpan.Slice(8, 8)),
                    LocalPort: NetworkToHostPort(row.dwLocalPort),
                    RemoteIpv4: Ipv4Address.Zero,
                    RemoteIpv6High: 0UL,
                    RemoteIpv6Low: 0UL,
                    RemotePort: 0,
                    OwningPid: unchecked((int)row.dwOwningPid),
                    TcpState: 0));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort NetworkToHostPort(uint rawPort)
    {
        // Win32 stores htons(port) in the low 16 bits of the DWORD (byte 0 = MSB, byte 1 = LSB on little-endian Windows).
        return BitConverter.IsLittleEndian
            ? (ushort)(((rawPort & 0xFFu) << 8) | ((rawPort >> 8) & 0xFFu))
            : (ushort)(rawPort & 0xFFFFu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Ipv4Address Ipv4FromNetworkDword(uint rawAddr)
    {
        // Network byte order in DWORD: on little-endian, byte 0 is LSB, byte 3 is MSB -> ReverseEndianness produces host-order uint.
        uint hostOrder = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReverseEndianness(rawAddr)
            : rawAddr;
        return new Ipv4Address(hostOrder);
    }
}
