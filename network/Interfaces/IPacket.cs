namespace network.interfaces;

public interface IPacket : IDisposable
{
    byte[] ToBytes();
}
