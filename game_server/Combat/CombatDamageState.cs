namespace game_server.combat;

/// <summary>
///     매치 하나의 착탄 대기 피해와 치명타 난수. MatchRuntime이 소유하며 매치 잠금 안에서만 접근한다.
///     적용 규칙은 MatchCombatDamageService에 있다.
/// </summary>
internal sealed class CombatDamageState
{
    public List<PendingMonsterHit> PendingMonsterHits { get; } = new();
    public List<(ProximityCombatAttack Attack, DateTime DueAtUtc)> PendingPvpHits { get; } = new();
    public Random CriticalRng { get; } = new();
}
