namespace network.hosting;

/// <summary>
///     /health/ready 응답을 통해 Docker와 같은 외부 운영 환경에
///     서버가 실제 요청을 받을 준비가 되었는지 알린다.
/// </summary>
public sealed class ServerReadinessState
{
    private int _isReady;
    private string _status = "starting";

    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public string Status => Volatile.Read(ref _status);

    public void MarkReady()
    {
        Volatile.Write(ref _status, "ready");
        Volatile.Write(ref _isReady, 1);
    }

    public void MarkNotReady(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Volatile.Write(ref _isReady, 0);
        Volatile.Write(ref _status, status);
    }
}
