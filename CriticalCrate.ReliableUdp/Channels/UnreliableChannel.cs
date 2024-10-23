
using CriticalCrate.ReliableUdp.Extensions;

namespace CriticalCrate.ReliableUdp.Channels;

public interface IUnreliableChannel : IChannel, IPacketHandler;

internal sealed class UnreliableChannel(ISocket socket, IPacketFactory packetFactory) : IUnreliableChannel
{
    public const int HeaderSize = FlagSize + VersionSize + PacketIdSize;
    private const int FlagSize = sizeof(byte);
    private const int VersionSize = sizeof(byte);
    private const int PacketIdSize = sizeof(ushort);

    public event Action<Packet>? OnPacketReceived;
    private ushort _packetId;
    public void Send(Packet packet)
    {
        socket.Send(packetFactory.CreateUnreliable(packet, _packetId));
        packetFactory.ReturnPacket(packet);
        _packetId++;
    }
    
    public void HandlePacket(in Packet receivedPacket, in PacketType packetType, in ushort seq)
    {
        OnPacketReceived?.Invoke(receivedPacket);
    }
}

