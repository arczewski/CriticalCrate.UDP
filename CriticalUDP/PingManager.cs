using System.Buffers;
using System.Net;

namespace CriticalCrate.UDP;

public class PingManager
{
    private UDPSocket _socket;
    private Dictionary<EndPoint, PingData> _trackedPings = new Dictionary<EndPoint, PingData>();

    public PingManager(UDPSocket socket)
    {
        _socket = socket;
    }

    public long GetPing(EndPoint endPoint)
    {
        if (_trackedPings.TryGetValue(endPoint, out var pingData))
            return pingData.Ping;
        return -1;
    }

    internal void OnConnected(EndPoint endPoint)
    {
        _trackedPings.Add(endPoint, new PingData()
        {
            NextPing = DateTime.Now
        });
    }

    internal void OnDisconnected(EndPoint endPoint)
    {
        _trackedPings.Remove(endPoint);
    }

    internal void OnPacketSend(ref Packet packet)
    {
        if (!_trackedPings.TryGetValue(packet.EndPoint, out var pingData) || !pingData.ShouldPing(DateTime.Now))
            return;
        pingData.TrackSend(ref packet, DateTime.Now);
    }

    internal void OnPacketReceived(Packet receivedPacket)
    {
        if (((PacketType)receivedPacket.Data[0] & PacketType.Ping) == PacketType.Ping)
        {
            var packet = new Packet(UnreliableChannel.UnreliableHeaderSize, ArrayPool<byte>.Shared);
            packet.Data[0] = (byte)(PacketType.Unreliable | PacketType.Pong);
            packet.Data[1] = receivedPacket.Data[1];
            packet.Assign(receivedPacket.EndPoint);
            packet.ForcePosition(2);
            _socket.Send(packet);
            return;
        }

        if (((PacketType)receivedPacket.Data[0] & PacketType.Pong) != PacketType.Pong ||
            !_trackedPings.TryGetValue(receivedPacket.EndPoint, out var pingData))
            return;
        pingData.TrackReceive(receivedPacket, DateTime.Now);
    }

    internal void Update()
    {
        foreach (var keyValue in _trackedPings)
        {
            if (!keyValue.Value.ShouldPing(DateTime.Now))
                continue;
            var packet = new Packet(UnreliableChannel.UnreliableHeaderSize, ArrayPool<byte>.Shared);
            packet.Data[0] = (byte)(PacketType.Unreliable | PacketType.Ping);
            packet.Assign(keyValue.Key);
            packet.ForcePosition(2);
            OnPacketSend(ref packet);
            _socket.Send(packet);
        }
    }
}

public class PingData
{
    private const int _maxTrackedPings = 10;
    private const int _pingIntervalMs = 100;

    public DateTime NextPing;
    private Dictionary<byte, DateTime> _pings = new Dictionary<byte, DateTime>();
    private Queue<byte> _pingTimeout = new Queue<byte>();
    private Queue<long> _measuredPingsMs = new Queue<long>();
    private byte _seq = 0;
    private long _accumulated;
    public long Ping { get; private set; }

    public void TrackSend(ref Packet packet, DateTime now)
    {
        byte seq = _seq++;
        packet.Data[0] |= (byte)PacketType.Ping;
        packet.Data[1] = seq;
        NextPing = NextPing.AddMilliseconds(_pingIntervalMs);
        _pingTimeout.Enqueue(seq);
        _pings.Add(seq, now);
        if (_pingTimeout.Count > _maxTrackedPings)
            _pings.Remove(_pingTimeout.Dequeue());
    }

    public void TrackReceive(Packet packet, DateTime now)
    {
        if (!_pings.TryGetValue(packet.Data[1], out var pingDate))
            return;
        _pings.Remove(packet.Data[1]);
        long elapsedMs = (long)Math.Max(1, (now - pingDate).TotalMilliseconds);
        _accumulated += elapsedMs;
        _measuredPingsMs.Enqueue(elapsedMs);
        if (_measuredPingsMs.Count > _maxTrackedPings)
            _accumulated -= _measuredPingsMs.Dequeue();
        Ping = Math.Max(1, _accumulated / _measuredPingsMs.Count);
    }

    public bool ShouldPing(DateTime now)
    {
        return NextPing <= now;
    }
}