namespace game_server.matches;

/// <summary>
///     매치 진행 중 전투·구역 서비스가 한 번만 하거나 주기로 하는 일의 표시.
///     MatchRuntime이 소유하며 매치 잠금 안에서만 접근한다.
/// </summary>
internal sealed class MatchProgressState
{
    /// <summary>마지막으로 방송한 오브 순위표. 같으면 다시 보내지 않는다.</summary>
    public string? OrbRankingsSignature { get; set; }
    public bool TimeoutResultProcessed { get; set; }
    /// <summary>개전 게이트가 없는 봇 전용 매치의 시간 앵커. 스웜 첫 틱에 찍는다.</summary>
    public DateTime? FallbackStartedAtUtc { get; set; }
    public DateTime? NextContactLogAtUtc { get; set; }
    public bool InitialFieldStateSent { get; set; }
}
