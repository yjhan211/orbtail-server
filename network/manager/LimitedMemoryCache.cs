using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace network
{
    public class LimitedMemoryCache<TKey, TValue>
    {
        private readonly MemoryCache cache;
        private readonly LinkedList<TKey> access_order;
        private readonly int max_num;
        private readonly int ttl;

        public LimitedMemoryCache(int max_num, int ttl)
        {
            this.cache = new(new MemoryCacheOptions());
            this.access_order = new();
            this.max_num = max_num;
            this.ttl = ttl;
        }

        public void Set(TKey key, TValue value)
        {
            if (this.access_order.Count >= this.max_num)
            {
                TKey old_key = this.access_order.First!.Value!;
                cache.Remove(old_key);
                this.access_order.RemoveFirst();
            }

            cache.Set(
                key!,
                value,
                new MemoryCacheEntryOptions
                {
                    Priority = CacheItemPriority.NeverRemove,
                    SlidingExpiration = TimeSpan.FromSeconds(ttl)
                }
            );

            this.access_order.AddLast(key);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (this.cache.TryGetValue(key!, out value!))
            {
                this.access_order.Remove(key);
                this.access_order.AddLast(key);
                return true;
            }

            return false;
        }
    }
}
