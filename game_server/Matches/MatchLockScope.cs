namespace game_server.matches;

/// <summary>
///     매치 잠금의 사용 범위. Dispose 시 매치에 잠금 해제와 종료 정리를 맡긴다.
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
