using System.Collections.Concurrent;
using System.Net;
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
    private readonly ConcurrentQueue<Packet> _pendingPackets;

    protected CriticalSocket(ISocket socket, IUnreliableChannel unreliableChannel, IReliableChannel reliableChannel,
        IPingChannel pingChannel, IPacketFactory packetFactory, IConnectionManager connectionManager)
    {
        _socket = socket;
        _pingChannel = pingChannel;
        _unreliableChannel = unreliableChannel;
        _reliableChannel = reliableChannel;
        _packetFactory = packetFactory;
        _connectionManager = connectionManager;
        _pendingPackets = new ConcurrentQueue<Packet>();
        _socket.OnPacketReceived += ReceivePacket;
        _socket.OnPacketSent += OnPacketSent;
        _reliableChannel.OnPacketReceived += (packet) => { OnPacketReceived?.Invoke(packet); };
    }

    private void OnPacketSent(Packet packet)
    {
        if (packet.DisposeOnSocketSend)
            _packetFactory.ReturnPacket(packet);
    }

    public bool Pool()
    {
        _reliableChannel.PushOutgoingPackets(DateTime.Now);
        _pingChannel.SendPendingPings(DateTime.Now);
        _connectionManager.CheckConnectionTimeout();

        if (!_pendingPackets.TryDequeue(out var packet))
        {
            return false;
        }

        try
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
                    _packetFactory.ReturnPacket(packet);
                    break;
                }
                case PacketType.Ping:
                case PacketType.PingAck:
                {
                    _pingChannel.HandlePacket(in packet, in packetType, in packetId);
                    _packetFactory.ReturnPacket(packet);
                    break;
                }
                case PacketType.Unreliable:
                {
                    packet = packet with { Offset = UnreliableChannel.HeaderSize };
                    OnPacketReceived?.Invoke(packet);
                    _packetFactory.ReturnPacket(packet);
                    break;
                }
                case PacketType.ReliableAck:
                {
                    _reliableChannel.HandlePacket(in packet, in packetType, in packetId);
                    _packetFactory.ReturnPacket(packet);
                    break;
                }
                case PacketType.Reliable:
                {
                    _reliableChannel.HandlePacket(in packet, in packetType, in packetId);
                    _packetFactory.ReturnPacket(packet);
                    break;
                }
                case PacketType.Ack:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        finally
        {
            _packetFactory.ReturnPacket(packet);
        }

        return _pendingPackets.Count > 0;
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

    private void ReceivePacket(Packet packet)
    {
        _pendingPackets.Enqueue(packet);
    }
}