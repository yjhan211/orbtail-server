using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

#pragma warning disable CS8714
#pragma warning disable CS8602
#pragma warning disable CS8600
#pragma warning disable CS8601

namespace network.manager
{
    public class LRUCache<TKey, TValue>
    {
        private readonly int capacity;
        private readonly LinkedList<TKey> list;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> dict;
        private readonly Dictionary<TKey, TValue> valueDict;
        private readonly object syncLock = new object();

        public LRUCache(int capacity)
        {
            this.capacity = capacity;
            list = new LinkedList<TKey>();
            dict = new Dictionary<TKey, LinkedListNode<TKey>>();
            valueDict = new Dictionary<TKey, TValue>();
        }

        public void Add(TKey key, TValue value)
        {
            lock (syncLock)
            {
                if (dict.ContainsKey(key))
                {
                    list.Remove(dict[key]);
                }
                else if (dict.Count >= capacity)
                {
                    TKey oldest = list.Last.Value;
                    list.RemoveLast();
                    dict.Remove(oldest);
                    valueDict.Remove(oldest);
                }

                LinkedListNode<TKey> node = list.AddFirst(key);
                dict[key] = node;
                valueDict[key] = value;
            }
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (syncLock)
            {
                if (dict.TryGetValue(key, out LinkedListNode<TKey> node))
                {
                    list.Remove(node);
                    list.AddFirst(node);
                    value = valueDict[key];
                    return true;
                }

                value = default;
                return false;
            }
        }
    }
}
