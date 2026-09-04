namespace network.infrastructure.messaging;

public interface INatsClient
{
    public void Publish(string subject, byte[] message);

    /// <summary>
    ///     <paramref name="queue" />를 주면 같은 큐 그룹의 구독자 중 하나만 메시지를 받는다 — 여러 프로세스가 같은
    ///     subject를 나눠 처리할 때 쓴다. 없으면 모든 구독자가 받는다.
    /// </summary>
    public void Subscribe(string subject, Action<string, byte[]> messageHandler, string? queue = null);

    public Task<byte[]> RequestAsync(
        string subject,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     핸들러가 null을 돌려주면 응답하지 않는다 — 요청 subject를 여러 프로세스가 구독하되 담당자만 답할 때 쓴다.
    /// </summary>
    public void SubscribeRequest(
        string subject,
        Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler,
        string? queue = null);
    public Task CloseAsync(CancellationToken cancellationToken = default);
    public void Close();
}

public interface INatsClientFactory
{
    public void Initialize(string natsEndPoint);
    public INatsClient Create();
}
