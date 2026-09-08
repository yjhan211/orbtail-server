namespace game_server.matches;

/// <summary>
///     매치 잠금을 잡고 있는 범위를 나타낸다.
///     using 범위를 벗어나면 잠금을 해제하며,
///     매치가 종료됐다면 가장 바깥쪽 범위가 끝날 때 종료 정리도 수행한다.
/// </summary>
internal readonly struct MatchLockScope : IDisposable
{
    internal MatchLockScope(MatchRuntime runtime)
    {
        Runtime = runtime;
    }

    public MatchRuntime Runtime { get; }

    public void Dispose() => Runtime.Exit();
}
