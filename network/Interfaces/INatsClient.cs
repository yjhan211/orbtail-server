using network.contracts.messaging;

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
    public void EnsureDurableStream(NatsDurableStreamOptions options);
    public Task<NatsDurablePublishAck> PublishDurableAsync(
        string stream,
        string subject,
        string messageId,
        byte[] message,
        CancellationToken cancellationToken = default);
    public void SubscribeDurableQueue(
        NatsDurableConsumerOptions options,
        Func<NatsDurableMessage, CancellationToken, Task<NatsDurableMessageDisposition>> messageHandler);
    public Task CloseAsync(CancellationToken cancellationToken = default);
    public void Close();
}

public interface INatsClientFactory
{
    public void Initialize(string natsEndPoint);
    public INatsClient Create();
}
