namespace network.utils;

public struct Const<T>(T value)
{
    public T Value { get; private set; } = value;
}