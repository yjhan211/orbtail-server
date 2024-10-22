namespace network.utils
{
    public class LRUCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly LinkedList<TKey> _list;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _dict;
        private readonly Dictionary<TKey, TValue> _valueDict;
        private readonly object _syncLock = new();

        public LRUCache(int capacity)
        {
            _capacity = capacity;
            _list = new();
            _dict = new();
            _valueDict = new();
        }

        public void Add(TKey key, TValue value)
        {
            lock (_syncLock)
            {
                if (_dict.TryGetValue(key, out var latest))
                {
                    _list.Remove(latest);
                }
                else if (_dict.Count >= _capacity && _list.Last != null)
                {
                    TKey oldest = _list.Last.Value;
                    _list.RemoveLast();
                    _dict.Remove(oldest);
                    _valueDict.Remove(oldest);
                }

                LinkedListNode<TKey> node = _list.AddFirst(key);
                _dict[key] = node;
                _valueDict[key] = value;
            }
        }

        public bool TryGet(TKey key, out TValue? value)
        {
            lock (_syncLock)
            {
                if (_dict.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    value = _valueDict[key];
                    return true;
                }

                value = default;
                return false;
            }
        }
    }
}
