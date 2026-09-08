namespace game_server.matches;

/// <summary>
///     매치 하나의 구역 폐쇄(1초)와 환경 정산(5초) 실행 시각을 관리한다.
///     타이머를 직접 실행하지 않으며, 호출자가 매치 잠금 안에서 실행 차례인지 확인한다.
///     지연된 틱을 몰아서 실행하지 않고 다음 실행 시각으로 넘긴다.
/// </summary>
internal sealed class MatchTickSchedule(object matchLock)
{
    public const int EnvironmentalTickIntervalSeconds = 5;
    private readonly object _matchLock = matchLock ?? throw new ArgumentNullException(nameof(matchLock));

    public DateTime? NextAreaClosureTickAtUtc { get; private set; }
    public DateTime? NextEnvironmentalTickAtUtc { get; private set; }

    public bool TryBeginAreaClosureTick(DateTime utcNow, DateTime? gameplayStartedAtUtc, bool isEnded)
    {
        if (!Monitor.IsEntered(_matchLock))
        {
            throw new InvalidOperationException("Area closure tick requires the match monitor to be held.");
        }

        if (isEnded || gameplayStartedAtUtc is not { } startedAt || utcNow < startedAt.AddSeconds(1))
        {
            return false;
        }

        if (NextAreaClosureTickAtUtc is { } next && utcNow < next)
        {
            return false;
        }
        NextAreaClosureTickAtUtc = utcNow.AddSeconds(1);
        return true;
    }

    /// <summary>
    ///     매치 시작 기준 5초마다 환경 정산을 한 번 허용한다. 반드시 매치 잠금 안에서 호출한다.
    ///     지연된 구간은 몰아서 정산하지 않고 다음 5초 경계로 건너뛴다.
    ///     실행 전에 시각을 넘겨 예외가 나더라도 매 50ms마다 같은 정산을 반복하지 않는다.
    /// </summary>
    public bool TryBeginEnvironmentalTick(DateTime utcNow, DateTime? gameplayStartedAtUtc, bool isEnded)
    {
        if (!Monitor.IsEntered(_matchLock))
            throw new InvalidOperationException("Environmental tick requires the match monitor to be held.");
        if (isEnded || gameplayStartedAtUtc is not { } startedAt || utcNow < startedAt)
            return false;

        NextEnvironmentalTickAtUtc ??= startedAt.AddSeconds(EnvironmentalTickIntervalSeconds);
        if (utcNow < NextEnvironmentalTickAtUtc.Value)
            return false;

        long intervalTicks = TimeSpan.TicksPerSecond * EnvironmentalTickIntervalSeconds;
        long nextInterval = (utcNow.Ticks - startedAt.Ticks) / intervalTicks + 1;
        NextEnvironmentalTickAtUtc = startedAt.AddTicks(nextInterval * intervalTicks);
        return true;
    }
}
