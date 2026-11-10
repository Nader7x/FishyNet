using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FishyNet.Core;
using FishyNet.Core.Packets;

namespace FishyNet.Features;

/// <summary>
/// Provides active network utility tools: continuous ICMP Ping latency monitoring
/// and Wake-on-LAN (WoL) 102-byte Magic Packet broadcasting.
/// </summary>
public sealed class NetworkToolsService
{
    public static async Task<int> MeasurePingLatencyAsync(Ipv4Address targetIp, int timeoutMs = 1200)
    {
        if (targetIp.IsZero) return -1;

        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(targetIp.ToIPAddress(), timeoutMs).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                return (int)Math.Max(1L, reply.RoundtripTime);
            }
        }
        catch
        {
            // Host blocks ICMP or unreachable
        }

        return -1;
    }

    public static async Task SweepDeviceLatenciesAsync(IEnumerable<NetworkDevice> devices, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (NetworkDevice device in devices)
        {
            if (ct.IsCancellationRequested) break;
            if (device.IsLocalHost)
            {
                device.PingRttMs = 0;
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                int rtt = await MeasurePingLatencyAsync(device.Ip, 1000).ConfigureAwait(false);
                if (rtt >= 0)
                {
                    device.PingRttMs = rtt;
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation
        }
    }

    public static async Task<bool> SendWakeOnLanAsync(MacAddress targetMac)
    {
        if (targetMac.IsZero || targetMac.IsBroadcast) return false;

        byte[] magicPacket = new byte[PacketPrimitives.WakeOnLanPayloadLength];
        PacketPrimitives.WriteWakeOnLanMagicPacket(magicPacket, targetMac);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            await udp.SendAsync(magicPacket, magicPacket.Length, new IPEndPoint(IPAddress.Broadcast, 9)).ConfigureAwait(false);
            await udp.SendAsync(magicPacket, magicPacket.Length, new IPEndPoint(IPAddress.Broadcast, 7)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
