using System.Buffers;
using System.Net;

namespace CriticalCrate.ReliableUdp;

public interface IPacketFactory
{
    void ReturnPacket(Packet packet);
    Packet CreatePacket(EndPoint endPoint, byte[] buffer, int offset, int size);
    Packet CreatePacket(EndPoint endPoint, int size);
    Packet CreatePacket(Packet endPoint);
}

public class PacketFactory : IPacketFactory
{
    public void ReturnPacket(Packet packet)
    {
        packet.ReturnBorrowedMemory();
    }

    public Packet CreatePacket(EndPoint endPoint, byte[] buffer, int offset, int size)
    {
        var memory = MemoryPool<byte>.Shared.Rent(size);
        buffer.AsSpan(offset, size).CopyTo(memory.Memory.Span);
        return new Packet(endPoint, memory, 0, size);
    }

    public Packet CreatePacket(EndPoint endPoint, int size)
    {
        var memory = MemoryPool<byte>.Shared.Rent(size);
        return new Packet(endPoint, memory, 0, size);
    }

    public Packet CreatePacket(Packet packet)
    {
        var memory = MemoryPool<byte>.Shared.Rent(packet.Buffer.Length);
        packet.Buffer.CopyTo(memory.Memory.Span);
        return new Packet(packet.EndPoint, memory, 0, packet.Buffer.Length);
    }
}