using System.Buffers;
using System.Net;

namespace CriticalCrate.UDP;

public interface IChannel
{
    void Send(EndPoint endPoint, byte[] data, int offset, int size);
}

public class UnreliableChannel
{
    public const int UnreliableHeaderSize = 2;
    private UDPSocket _socket;

    public UnreliableChannel(UDPSocket socket)
    {
        _socket = socket;
    }

    public void Send(EndPoint endPoint, byte[] data, int offset, int size)
    {
        int sizeWithHeader = size + UnreliableHeaderSize;
        var packet = new Packet(sizeWithHeader, ArrayPool<byte>.Shared);
        packet.Assign(endPoint);
        packet.CopyFrom(data, offset, size, UnreliableHeaderSize);
        packet.Data[0] = (byte)PacketType.Unreliable;
        _socket.Send(packet);
    }
}