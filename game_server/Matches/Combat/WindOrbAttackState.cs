using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>
///     매치 내 바람 공격의 피해자 면역·상처 상태를 보관한다.
///     개인 발동 시각·시동 상태는 Player가 소유하며, 모든 변경은 매치 잠금 안에서 수행한다.
/// </summary>
public sealed class WindOrbAttackState
{
    private readonly Dictionary<long, DateTime> _victimImmuneUntilUtc = new();
    private readonly Dictionary<long, DateTime> _woundsUntilUtc = new();

    public bool TryClaimVictimShock(long victimId, DateTime nowUtc, double immunitySeconds)
    {
        if (_victimImmuneUntilUtc.TryGetValue(victimId, out DateTime immuneUntilUtc) && nowUtc < immuneUntilUtc)
            return false;

        _victimImmuneUntilUtc[victimId] = nowUtc.AddSeconds(immunitySeconds);
        return true;
    }

    public void ApplyWound(long victimId, DateTime untilUtc) =>
        _woundsUntilUtc[victimId] = untilUtc;

    public bool IsWounded(long victimId, DateTime nowUtc) =>
        _woundsUntilUtc.TryGetValue(victimId, out DateTime untilUtc) && nowUtc < untilUtc;
}
