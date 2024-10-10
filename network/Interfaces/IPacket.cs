namespace network.interfaces
{
    public interface IPacket : IDisposable
    {
        int Position { get; }
        byte[] Buffer { get; }
        void CopyTo(IPacket target);
        void Overwrite(byte[] source, int position);
        int PopProtocolId();
        long PopPlayerId();
        void SetBody(byte[] serializedBuffer);
        byte[] PopBody();
        void RecordSize();
        byte[] ToBytes();
    }
}