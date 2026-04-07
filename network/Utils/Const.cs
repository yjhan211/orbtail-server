namespace network.utils;

public readonly struct Const<T>(T value)
{
    public T Value { get; } = value;
}
