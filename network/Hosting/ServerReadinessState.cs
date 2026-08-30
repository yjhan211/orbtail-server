namespace network.hosting;

/// <summary>
///     Process-local readiness gate shared by the hosted server and its health endpoint.
///     Liveness remains independent so an unready process can still report diagnostics.
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
