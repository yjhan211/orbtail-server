namespace network.infrastructure.messaging;

/// <summary>
///     구현체는 하나지만, 매칭과 세션 라우팅을 실제 NATS 서버 없이 테스트하기 위해 인터페이스로 분리.
/// </summary>
public interface INatsClient
{
    public void Publish(string subject, byte[] message);
    public void Subscribe(string subject, Action<string, byte[]> messageHandler, string? queue = null);

    public Task<byte[]> RequestAsync(
        string subject,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    public void SubscribeRequest(
        string subject,
        Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler,
        string? queue = null);
    public Task CloseAsync(CancellationToken cancellationToken = default);
    public void Close();
}
