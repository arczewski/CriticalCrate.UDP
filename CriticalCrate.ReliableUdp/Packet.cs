using System.Buffers;
using System.Net;

namespace CriticalCrate.ReliableUdp
{
    public struct Packet
    {
        public EndPoint EndPoint { get; private set; }
        public Span<byte> Buffer => _owner.Memory.Span[Offset..Position];
        internal bool DisposeOnSocketSend { get; set; } = true;

        private readonly IMemoryOwner<byte> _owner;
        public int Position { get; set; }
        public int Offset { get; set; }
        
        internal Packet(EndPoint endPoint, IMemoryOwner<byte> owner, int offset, int position)
        {
            EndPoint = endPoint;
            Offset = offset;
            Position = position;
            _owner = owner;
        }

        internal void ReturnBorrowedMemory()
        {
            _owner.Dispose();
        }

        internal Memory<byte> ToMemoryBuffer()
        {
            return _owner.Memory.Slice(Offset,Position);
        }
    }
}