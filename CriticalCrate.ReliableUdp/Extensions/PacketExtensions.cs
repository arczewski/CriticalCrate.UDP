using System.Net;
using CriticalCrate.ReliableUdp.Channels;

namespace CriticalCrate.ReliableUdp.Extensions;

internal static class PacketExtensions
{
    public static Packet CreateUnreliable(this IPacketFactory packetFactory, Packet packet, ushort packetId, byte version = 1)
    {
        var newPacket = packetFactory.CreatePacket(packet.EndPoint, packet.Position - packet.Offset + UnreliableChannel.HeaderSize);
        var buffer = newPacket.Buffer;
        buffer[0] = (byte) PacketType.Unreliable;
        buffer[1] = version;
        BitConverter.TryWriteBytes(buffer[2..], packetId);
        packet.Buffer.CopyTo(buffer[4..]);
        return newPacket;
    }
    
    public static Packet CreatePing(this IPacketFactory packetFactory, EndPoint endPoint, ushort packetId, byte version = 1)
    {
        var newPacket = packetFactory.CreatePacket(endPoint, PingChannel.HeaderSize);
        var buffer = newPacket.Buffer;
        buffer[0] = (byte)PacketType.Ping;
        buffer[1] = version;
        BitConverter.TryWriteBytes(buffer[2..], packetId);
        return newPacket;
    }

    public static Packet CreatePong(this IPacketFactory packetFactory, EndPoint endPoint, ushort packetId, byte version = 1)
    {
        var newPacket = packetFactory.CreatePacket(endPoint, PingChannel.HeaderSize);
        var buffer = newPacket.Buffer;
        buffer[0] = (byte)(PacketType.PingAck);
        buffer[1] = version;
        BitConverter.TryWriteBytes(buffer[2..], packetId);
        return newPacket;
    }

    public static Packet CreateReliableAck(this IPacketFactory packetFactory, Packet packet, ushort ack)
    {
        var newPacket = packetFactory.CreatePacket(packet.EndPoint, ReliableChannel.HeaderSize);
        packet.Buffer[..ReliableChannel.HeaderSize].CopyTo(newPacket.Buffer);
        newPacket.Buffer[0] = (byte)(PacketType.Reliable | PacketType.Ack);
        BitConverter.TryWriteBytes(newPacket.Buffer[ReliableChannel.AckPosition..], ack);
        return newPacket;
    }
}