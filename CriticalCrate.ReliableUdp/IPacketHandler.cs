using System.Net;

namespace CriticalCrate.ReliableUdp;

public interface IPacketHandler
{
    void HandlePacket(in Packet receivedPacket, in PacketType packetType, in ushort packetId);
}