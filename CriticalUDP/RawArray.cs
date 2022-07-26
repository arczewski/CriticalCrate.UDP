using System.Runtime.InteropServices;

namespace CriticalCrate.UDP
{
    public class RawArray<T> where T : unmanaged
    {
        private IntPtr _startPointer;
        private int _tSize;
        public int Length { get; private set; }

        public T this[int index]
        {
            get { return Marshal.PtrToStructure<T>(_startPointer + (index * _tSize)); }
            set { Marshal.StructureToPtr(value, _startPointer + (index * _tSize), false); }
        }

        public RawArray(int length)
        {
            Length = length;
            unsafe
            {
                _tSize = sizeof(T);
                _startPointer = Marshal.AllocHGlobal(length * _tSize);
            }
        }

        public void Dispose()
        {
            unsafe
            {
                Marshal.FreeHGlobal(_startPointer);
            }
        }

        public void CopyFrom(byte[] array, int offset, int length)
        {
            Marshal.Copy(array, offset, _startPointer, length);
        }

        public void CopyTo(byte[] array, int offset)
        {
            Marshal.Copy(_startPointer, array, offset, Length);
        }
    }
}