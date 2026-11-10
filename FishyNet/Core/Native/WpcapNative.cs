using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FishyNet.Core.Native;

internal static unsafe partial class WpcapNative
{
    public const int PCAP_ERRBUF_SIZE = 256;

    [StructLayout(LayoutKind.Sequential)]
    public struct PcapPkthdr
    {
        public int TvSec;
        public int TvUsec;
        public uint CapLen;
        public uint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BpfProgram
    {
        public uint BfLen;
        public nint BfInsns;
    }

    [ModuleInitializer]
    internal static void InitializeNpcapSearchPath()
    {
        try
        {
            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string npcapDir = Path.Combine(systemDir, "Npcap");
            if (Directory.Exists(npcapDir))
            {
                SetDllDirectoryW(npcapDir);
            }
        }
        catch
        {
            // Ignore in restricted environments
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string lpPathName);

    [LibraryImport("wpcap.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pcap_open_live(string device, int snaplen, int promisc, int to_ms, byte* errbuf);

    [LibraryImport("wpcap.dll")]
    public static partial int pcap_next_ex(nint p, out PcapPkthdr* pkt_header, out byte* pkt_data);

    [LibraryImport("wpcap.dll")]
    public static partial int pcap_sendpacket(nint p, byte* buf, int size);

    [LibraryImport("wpcap.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pcap_compile(nint p, out BpfProgram fp, string str, int optimize, uint netmask);

    [LibraryImport("wpcap.dll")]
    public static partial int pcap_setfilter(nint p, ref BpfProgram fp);

    [LibraryImport("wpcap.dll")]
    public static partial void pcap_freecode(ref BpfProgram fp);

    [LibraryImport("wpcap.dll")]
    public static partial void pcap_close(nint p);

    [LibraryImport("wpcap.dll")]
    public static partial byte* pcap_lib_version();

    public static bool IsDriverAvailable(out string versionInfo)
    {
        try
        {
            byte* ptr = pcap_lib_version();
            if (ptr != null)
            {
                versionInfo = Marshal.PtrToStringUTF8((nint)ptr) ?? "Npcap / WinPcap Ready";
                return true;
            }
        }
        catch (Exception ex)
        {
            versionInfo = $"Packet driver unavailable: {ex.Message}";
            return false;
        }

        versionInfo = "Unknown packet driver status";
        return false;
    }

    public static nint OpenLiveDevice(string nicId, int snapLen, int promiscuous, int readTimeoutMs, out string? error)
    {
        error = null;
        string deviceName = nicId.StartsWith(@"\Device\NPF_", StringComparison.OrdinalIgnoreCase)
            ? nicId
            : $@"\Device\NPF_{nicId}";

        byte* errBuf = stackalloc byte[PCAP_ERRBUF_SIZE];
        errBuf[0] = 0;

        try
        {
            nint handle = pcap_open_live(deviceName, snapLen, promiscuous, readTimeoutMs, errBuf);
            if (handle == nint.Zero)
            {
                error = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(errBuf, PCAP_ERRBUF_SIZE)).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = $"Failed to open NPF adapter {deviceName}. Ensure Npcap is installed and app runs as Administrator.";
                }
            }
            return handle;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return nint.Zero;
        }
    }

    public static bool SetBpfFilter(nint handle, string filterExpression, uint netmask = 0xFFFFFFFF)
    {
        if (handle == nint.Zero)
        {
            return false;
        }

        if (pcap_compile(handle, out BpfProgram prog, filterExpression, 1, netmask) != 0)
        {
            return false;
        }

        try
        {
            return pcap_setfilter(handle, ref prog) == 0;
        }
        finally
        {
            pcap_freecode(ref prog);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SendPacket(nint handle, ReadOnlySpan<byte> frame)
    {
        if (handle == nint.Zero || frame.IsEmpty)
        {
            return false;
        }

        fixed (byte* ptr = frame)
        {
            return pcap_sendpacket(handle, ptr, frame.Length) == 0;
        }
    }
}
