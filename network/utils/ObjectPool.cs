using System.Collections.Concurrent;

namespace network.utils
{
    class ObjectPool<T>
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;

        public ObjectPool(Func<T> objectGenerator, int poolCapacity)
        {
            if (objectGenerator == null || poolCapacity <= 0)
            {
                throw new Exception($"fail initialize objectpool. {nameof(objectGenerator)}, {poolCapacity}");
            }

            _objects = new();
            _objectGenerator = objectGenerator;

            foreach (var _ in Enumerable.Range(0, poolCapacity))
            {
                _objects.Add(objectGenerator());
            }
        }

        public T Pop()
        {
            if (_objects.TryTake(out var item))
            {
                return item;
            }

            return _objectGenerator();
        }

        public void Push(T item)
        {
            _objects.Add(item);
        }
    }
}