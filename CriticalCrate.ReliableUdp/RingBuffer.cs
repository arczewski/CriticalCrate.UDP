using System.Collections;

namespace CriticalCrate.ReliableUdp;

internal class RingBuffer<T>(int capacity) : IEnumerable<T>
{
   private readonly Queue<T> _queue = [];
   public long Capacity { get; init; } = capacity; 

   public void Add(T item)
   {
      _queue.Enqueue(item);
      if (_queue.Count > Capacity)
         _queue.Dequeue();
   }

   public IEnumerator<T> GetEnumerator()
   {
      return _queue.GetEnumerator();
   }

   IEnumerator IEnumerable.GetEnumerator()
   {
      return GetEnumerator();
   }
}