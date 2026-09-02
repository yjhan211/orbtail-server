namespace network.interfaces;

public interface INatsClient
{
    public void Publish(string subject, byte[] message);
    public void Subscribe(string subject, Action<string, byte[]> messageHandler);
    public Task<byte[]> RequestAsync(
        string subject,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
    public void SubscribeRequest(
        string subject,
        Func<string, byte[], CancellationToken, Task<byte[]>> messageHandler,
        string? queue = null);
    public Task CloseAsync(CancellationToken cancellationToken = default);
    public void Close();
}

public interface INatsClientFactory
{
    public void Initialize(string natsEndPoint);
    public INatsClient Create();
}
