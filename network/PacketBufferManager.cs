using System.Collections.Concurrent;

namespace network
{
    class PacketBufferManager
    {
        private static ObjectPool<Packet> pool;

        public static void initialize(int capacity)
        {
            pool = new ObjectPool<Packet>(() => new Packet(), capacity);
        }

        public static Packet pop()
        {
            return pool.pop();
        }

        public static void push(Packet packet)
        {
            pool.push(packet);
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
                throw new Exception($"fail initialize objectpool. {nameof(object_generator)}, {pool_capacity}");
            }

            this.objects = new ConcurrentBag<T>();
            this.object_generator = object_generator;

            foreach (var _ in Enumerable.Range(0, pool_capacity))
            {
                this.objects.Add(object_generator());
            }
        }

        public T pop()
        {
            if (this.objects.TryTake(out var item))
            {
                return item;
            }

            return this.object_generator();
        }

        public void push(T item)
        {
            this.objects.Add(item);
        }
    }
}
