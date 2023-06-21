#pragma warning disable CS8604
#pragma warning disable CS8622
#pragma warning disable CS8618

using System.Collections.Concurrent;

namespace network
{
    public class PacketBufferManager
    {
        private static ObjectPool<Packet> pool;

        public static void Initialize(int capacity)
        {
            pool = new ObjectPool<Packet>(() => new Packet(), capacity);
        }

        public static Packet Pop()
        {
            return pool.Pop();
        }

        public static void Push(Packet packet)
        {
            pool.Push(packet);
        }
    }

    class ObjectPool<T>
    {
        private readonly ConcurrentBag<T> objects;
        private readonly Func<T> object_generator;

        public ObjectPool(Func<T> object_generator, int pool_capacity)
        {
            if (object_generator is null || pool_capacity <= 0)
            {
                throw new Exception(
                    $"fail initialize objectpool. {nameof(object_generator)}, {pool_capacity}"
                );
            }

            this.objects = new ConcurrentBag<T>();
            this.object_generator = object_generator;

            foreach (var _ in Enumerable.Range(0, pool_capacity))
            {
                this.objects.Add(object_generator());
            }
        }

        public T Pop()
        {
            if (this.objects.TryTake(out var item))
            {
                return item;
            }

            return this.object_generator();
        }

        public void Push(T item)
        {
            this.objects.Add(item);
        }
    }
}
