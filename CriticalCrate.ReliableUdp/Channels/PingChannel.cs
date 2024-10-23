using System.Net;
using CriticalCrate.ReliableUdp.Extensions;

namespace CriticalCrate.ReliableUdp.Channels;

public interface IPingChannel : IConnectionHandler, IPacketHandler
{
    void SendPendingPings(DateTime now);
}

public interface ILocalPingChannel : IPingChannel
{
    double GetPing();
}

public interface IServerPingChannel : IPingChannel
{
    double GetPing(EndPoint endPoint);
}

internal sealed class PingChannel(ISocket socket, IPacketFactory packetFactory, TimeSpan pingInterval)
: ILocalPingChannel, IServerPingChannel
{
    public const int HeaderSize = FlagSize + VersionSize + PacketIdSize;
    private const int FlagSize = sizeof(byte);
    private const int VersionSize = sizeof(byte);
    private const int PacketIdSize = sizeof(ushort);

    private readonly Dictionary<EndPoint, RingBuffer<double>> _trackedPings = [];
    private readonly Dictionary<EndPoint, PingData> _lastPing = [];

    public double GetPing(EndPoint endPoint)
    {
        if (_trackedPings.TryGetValue(endPoint, out var pingBuffer))
            return CalculatePing(pingBuffer);
        return -1;
    }

    public void HandlePacket(in Packet receivedPacket, in PacketType packetType, in ushort packetId)
    {
        if (packetType.HasFlag(PacketType.PingAck) &&
            _lastPing.TryGetValue(receivedPacket.EndPoint, out var pingData))
        {
            if (pingData.PingId == packetId)
                _trackedPings[receivedPacket.EndPoint].Add((DateTime.Now - pingData.LastSendTime).TotalMilliseconds);
            return;
        }

        if (!packetType.HasFlag(PacketType.Ping)) return;
        var packet = packetFactory.CreatePong(receivedPacket.EndPoint, packetId);
        socket.Send(packet);
    }

    public void SendPendingPings(DateTime now)
    {
        foreach (var endpoint in _lastPing.Keys)
        {
            var lastPingData = _lastPing[endpoint];
            if (lastPingData.LastSendTime.Add(pingInterval) > DateTime.Now)
                continue;

            lastPingData = new PingData(LastSendTime: now, PingId: (byte)(lastPingData.PingId + 1));
            var packet = packetFactory.CreatePing(endpoint, lastPingData.PingId);
            _lastPing[endpoint] = lastPingData;
            socket.Send(packet);
        }
    }

    public double GetPing()
    {
        var ringBuffer = _trackedPings.Values.SingleOrDefault();
        if (ringBuffer == null)
            return -1;
        return CalculatePing(ringBuffer);
    }

    public void HandleConnection(EndPoint endPoint)
    {
        _trackedPings.Add(endPoint, new RingBuffer<double>(100));
        _lastPing.Add(endPoint, new PingData(0, DateTime.MinValue));
    }

    public void HandleDisconnection(EndPoint endPoint)
    {
        _trackedPings.Remove(endPoint);
        _lastPing.Remove(endPoint);
    }

    private static double CalculatePing(RingBuffer<double> pingBuffer)
    {
        double accumulatedPing = 0;
        foreach (var ping in pingBuffer)
            accumulatedPing += ping;
        return accumulatedPing / pingBuffer.Capacity;
    }
}

internal record PingData(byte PingId, DateTime LastSendTime);