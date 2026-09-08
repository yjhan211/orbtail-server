using network.common;
using network.common.data;
using network.common.data.models;
using game_server.services;

namespace game_server.matches.states;

/// <summary>
///     바람 칼날의 틱·시동·피해자 면역·상처 상태. 이 객체 자체가 한 매치에 귀속되므로 내부 key에는
///     matching id를 반복하지 않는다. enclosing match execution gate가 모든 변경을 직렬화한다.
/// </summary>
public sealed class WindBladeState
{
    private readonly Dictionary<(long PlayerId, long ItemUid), DateTime> _nextTickAtUtc = new();
    private readonly Dictionary<(long PlayerId, long ItemUid), DateTime> _engagedAtUtc = new();
    private readonly Dictionary<long, DateTime> _victimImmuneUntilUtc = new();
    private readonly Dictionary<long, DateTime> _woundsUntilUtc = new();

    public bool TryBeginTick(long playerId, long itemUid, DateTime nowUtc, double intervalSeconds)
    {
        var key = (playerId, itemUid);
        if (_nextTickAtUtc.TryGetValue(key, out DateTime nextTickAtUtc) && nowUtc < nextTickAtUtc)
            return false;

        _nextTickAtUtc[key] = nowUtc.AddSeconds(intervalSeconds);
        return true;
    }

    public void ResetEngagement(long playerId, long itemUid) =>
        _engagedAtUtc.Remove((playerId, itemUid));

    public bool HasCompletedSpinup(long playerId, long itemUid, DateTime nowUtc, double durationSeconds)
    {
        var key = (playerId, itemUid);
        if (!_engagedAtUtc.TryGetValue(key, out DateTime engagedAtUtc))
        {
            engagedAtUtc = nowUtc;
            _engagedAtUtc[key] = engagedAtUtc;
        }

        return (nowUtc - engagedAtUtc).TotalSeconds >= durationSeconds;
    }

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
