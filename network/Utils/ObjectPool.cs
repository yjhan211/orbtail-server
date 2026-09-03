using System.Collections.Concurrent;

namespace network.utils;

/// <summary>
///     스레드별 로컬 목록을 가진 ConcurrentBag 풀. 빌린 스레드가 곧 돌려주는 패킷 버퍼 패턴에서 전역 잠금 없이 돈다.
///     <paramref name="poolCapacity" />는 초기 개수일 뿐 상한이 아니다 — 비면 새로 만들고, 돌아온 건 전부 받는다.
///     같은 객체를 두 번 Push하면 두 번 Pop되므로, 반납은 한 번만 해야 한다.
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
