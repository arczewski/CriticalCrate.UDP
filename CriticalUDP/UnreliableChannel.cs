using System.Buffers;
using System.Net;

namespace CriticalCrate.UDP
{
    public class UnreliableChannel
    {
        public const int UnreliableHeaderSize = 2;
        private UDPAsyncSocket _asyncSocket;
        private PingManager _pingManager;

        public UnreliableChannel(UDPAsyncSocket asyncSocket, PingManager pingManager)
        {
            _asyncSocket = asyncSocket;
            _pingManager = pingManager;
        }

        public void Send(EndPoint endPoint, byte[] data, int offset, int size)
        {
            int sizeWithHeader = size + UnreliableHeaderSize;
            var packet = new Packet(sizeWithHeader, ArrayPool<byte>.Shared);
            packet.Assign(endPoint);
            packet.CopyFrom(data, offset, size, UnreliableHeaderSize);
            packet.Data[0] = (byte)PacketType.Unreliable;
            _pingManager.OnPacketSend(ref packet);
            _asyncSocket.Send(packet);
        }
    }
}