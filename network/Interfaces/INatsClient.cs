namespace network.interfaces;

public interface INatsClient
{
    public void Publish(string subject, byte[] message);
    public void Subscribe(string subject, Action<string, byte[]> messageHandler);
    public void Close();
}

public interface INatsClientFactory
{
    public void Initialize(string natsEndPoint);
    public INatsClient Create();
}
