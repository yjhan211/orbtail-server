namespace network.interfaces;

public interface INatsClient
{
    void Publish(string subject, byte[] message);
    void Subscribe(string subject, Action<string, byte[]> messageHandler);
    void Close();
}

public interface INatsClientFactory
{
    void Initialize(string natsEndPoint);
    INatsClient Create();
}
