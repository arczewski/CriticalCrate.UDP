using CriticalCrate.ReliableUdp.Channels;
using CriticalCrate.ReliableUdp.Exceptions;

namespace CriticalCrate.ReliableUdp;

public enum SendMode
{
    Unreliable = 0,
    Reliable = 1,
}

public abstract class CriticalSocket : IDisposable
{
    public event Action<Packet>? OnPacketReceived;

    private readonly ISocket _socket;
    private readonly IUnreliableChannel _unreliableChannel;
    private readonly IReliableChannel _reliableChannel;
    private readonly IPingChannel _pingChannel;
    private readonly IPacketFactory _packetFactory;
    private readonly IConnectionManager _connectionManager;

    protected CriticalSocket(ISocket socket, IUnreliableChannel unreliableChannel, IReliableChannel reliableChannel,
        IPingChannel pingChannel, IPacketFactory packetFactory, IConnectionManager connectionManager)
    {
        _socket = socket;
        _pingChannel = pingChannel;
        _unreliableChannel = unreliableChannel;
        _reliableChannel = reliableChannel;
        _packetFactory = packetFactory;
        _connectionManager = connectionManager;
        _socket.OnPacketReceived += ReceivePacket;
        _reliableChannel.OnPacketReceived += packet => { OnPacketReceived?.Invoke(packet); };
        _pingChannel.OnPingUpdated += reliableChannel.OnPingUpdated;
    }

    private void ReceivePacket(Packet packet)
    {
        var packetType = (PacketType)packet.Buffer[0];
        var packetVersion = packet.Buffer[1];
        var packetId = BitConverter.ToUInt16(packet.Buffer[2..]);
        _connectionManager.HandlePacket(in packet, in packetType, in packetId);
        switch (packetType)
        {
            case PacketType.Connect:
            case PacketType.Disconnect:
            case PacketType.ServerFull:
            {
                break;
            }
            case PacketType.Ping:
            case PacketType.PingAck:
            {
                _pingChannel.HandlePacket(in packet, in packetType, in packetId);
                break;
            }
            case PacketType.Unreliable:
            {
                packet = packet with { Offset = UnreliableChannel.HeaderSize };
                OnPacketReceived?.Invoke(packet);
                break;
            }
            case PacketType.ReliableAck:
            {
                _reliableChannel.HandlePacket(in packet, in packetType, in packetId);
                break;
            }
            case PacketType.Reliable:
            {
                _reliableChannel.HandlePacket(in packet, in packetType, in packetId);
                break;
            }
            case PacketType.Ack:
            default:
                throw new ArgumentOutOfRangeException();
        }
    }


    public void Pool()
    {
        while (_socket.Pool())
        {
        }

        var now = DateTime.UtcNow;
        _pingChannel.SendPendingPings(now);
        _connectionManager.CheckConnectionTimeout(now);
        _reliableChannel.PushOutgoingPackets(now);
    }

    public void Send(Packet packet, SendMode sendMode = SendMode.Unreliable)
    {
        switch (sendMode)
        {
            case SendMode.Unreliable:
                if (packet.Buffer.Length >= ISocket.Mtu - UnreliableChannel.HeaderSize)
                    throw new PacketTooBigToSendException(
                        $"Packet too large. Maximum unreliable size {ISocket.Mtu - UnreliableChannel.HeaderSize}.");

                _unreliableChannel.Send(packet);
                break;
            case SendMode.Reliable:
                _reliableChannel.Send(packet);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sendMode), sendMode, null);
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}