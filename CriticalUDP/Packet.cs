using System.Net;

namespace CriticalCrate.UDP
{
    public struct Packet : IDisposable
    {
        public EndPoint EndPoint { get; private set; }
        public RawArray<byte> Data { get; private set; }

        public Packet(int size)
        {
            Data = new RawArray<byte>(size);
            EndPoint = null;
        }

        public void Dispose()
        {
            Data.Dispose();
        }

        public void Assign(EndPoint endPoint)
        {
            EndPoint = endPoint;
        }

        public void CopyFrom(byte[] array, int offset, int length)
        {
            Data.CopyFrom(array, offset, length);
        }

        public void CopyTo(byte[] buffer, int offset)
        {
            Data.CopyTo(buffer, offset);
        }
    }
}