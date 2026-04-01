using System.Collections.Concurrent;

namespace network.utils;

internal class ObjectPool<T>
{
    private readonly Func<T> _objectGenerator;
    private readonly ConcurrentBag<T> _objects;

    public ObjectPool(Func<T> objectGenerator, int poolCapacity)
    {
        if (objectGenerator == null || poolCapacity <= 0)
            throw new Exception($"fail initialize ObjectPool. {nameof(objectGenerator)}, {poolCapacity}");

        _objects = [];
        _objectGenerator = objectGenerator;

        foreach (var _ in Enumerable.Range(0, poolCapacity)) _objects.Add(objectGenerator());
    }

    public T Pop()
    {
        return _objects.TryTake(out var item) ? item : _objectGenerator();
    }

    public void Push(T item)
    {
        _objects.Add(item);
    }
}
