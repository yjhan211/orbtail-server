namespace network.interfaces;

public interface IPacket : IDisposable
{
    public byte[] ToBytes();
}
