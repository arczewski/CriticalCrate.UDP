using System;
using System.Buffers;
using System.Net;

namespace CriticalCrate.UDP
{
    public struct Packet : IDisposable
    {
        public EndPoint EndPoint { get; private set; }
        public byte[] Data { get; private set; }
        public int Position { get; private set; }
        
        private ArrayPool<byte> _pool;
        public bool SendDispose = true;

        internal Packet(int bufferSize, ArrayPool<byte> pool)
        {
            _pool = pool;
            EndPoint = null;
            Data = _pool.Rent(bufferSize);
            Position = 0;
        }

        public void Dispose()
        {
            _pool.Return(Data);
            EndPoint = null;
            Position = 0;
        }

        public void Assign(EndPoint endPoint)
        {
            EndPoint = endPoint;
        }

        public void CopyFrom(byte[] array, int offset, int length, int sourceOffset = 0)
        {
            Array.Copy(array, offset, Data, sourceOffset, length);
            Position = length + sourceOffset;
        }

        public void CopyTo(byte[] buffer, int offset, int sourceOffset = 0)
        {
            Array.Copy(Data, sourceOffset, buffer, offset, Position - sourceOffset);
        }

        public void ForcePosition(int position)
        {
            Position = position;
        }
    }
}