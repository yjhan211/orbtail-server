using System.Collections.Concurrent;

namespace network.packets;

/// <summary>
///     사용이 끝난 객체를 보관했다가 다시 빌려주는 풀.
///     생성 시 지정한 개수만큼 미리 만들고, 빌릴 객체가 없으면 새로 만든다.
///     지정한 개수는 초기 개수이며, 보관 개수에 상한은 없다.
///
///     여러 스레드에서 객체를 빌리고 반납할 수 있지만,
///     빌린 객체 자체의 동시 사용까지 보호하지는 않는다.
///
///     객체 초기화는 호출자가 담당한다.
///     같은 객체를 중복 반납하거나 반납한 객체를 다시 사용하면 안 된다.
/// </summary>
internal class ObjectPool<T>
{
    private readonly Func<T> _objectGenerator;
    private readonly ConcurrentBag<T> _objects;

    public ObjectPool(Func<T> objectGenerator, int poolCapacity)
    {
        ArgumentNullException.ThrowIfNull(objectGenerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poolCapacity);

        _objects = [];
        _objectGenerator = objectGenerator;

        foreach (int _ in Enumerable.Range(0, poolCapacity)) _objects.Add(objectGenerator());
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
