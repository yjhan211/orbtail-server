namespace network.core.abstractions;

public interface IPacket : IDisposable
{
    public byte[] ToBytes();
}
