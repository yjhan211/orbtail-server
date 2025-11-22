namespace network.interfaces;

public interface IServerConfig
{
    public string ServerType { get; }
    public int GameServerNum { get; }
    public int ServerId { get; }

    void Validate();
}