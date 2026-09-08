using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치 시작 시각으로 자기장 안전 반경을 계산하고 경계 밖 위치의 환경 피해를 구한다.
///     봇의 대피·스폰 판단과 환경 정산이 같은 수축 규칙을 사용한다.
///     매치 상태를 변경하지 않으며 현재 시각은 호출자가 전달한다.
/// </summary>
internal static class MatchPressureFieldPolicy
{
    internal static readonly bool Enabled = Config.SWARM_PRESSURE_FIELD_ENABLED;
    internal static double HoldSeconds => Config.SWARM_FIELD_HOLD_SECONDS;
    internal static double ShrinkSeconds => Config.SWARM_MATCH_DURATION_SECONDS - HoldSeconds;

    private static int BaseDamagePerTick =>
        SwarmConfigData.GetInt("SWARM_FIELD_BASE_DAMAGE_PER_TICK", 12);
    private static int DamagePerExtraCell =>
        SwarmConfigData.GetInt("SWARM_FIELD_DAMAGE_PER_EXTRA_CELL", 5);

    /// <summary>수축 전이거나 시작 시각이 없으면 전체 맵을 안전한 것으로 취급한다.</summary>
    internal static double GetSafeDistance(MatchRuntime match, DateTime nowUtc)
    {
        if (!Enabled)
            return double.MaxValue;
        var closureState = match.Closures.GetMatchingState();
        if (closureState == null)
            return double.MaxValue;

        double shrinkElapsed = (nowUtc - closureState.GameStartTime).TotalSeconds - HoldSeconds;
        if (shrinkElapsed <= 0)
            return double.MaxValue;

        double progress = Math.Min(1d, shrinkElapsed / ShrinkSeconds);
        return SwarmPressureField.GetSafeDistanceAtProgress(progress);
    }

    /// <summary>안전 경계 밖이면 5초 정산당 기본 피해에 초과 거리 비례 피해를 더한다.</summary>
    internal static int GetDamagePerTick(MatchRuntime match, Vector3f? worldPosition, DateTime nowUtc)
    {
        if (worldPosition == null)
            return 0;
        double safeDistance = GetSafeDistance(match, nowUtc);
        if (safeDistance >= double.MaxValue)
            return 0;

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, worldPosition);
        double over = SwarmPressureField.GetDistance(cell) - safeDistance;
        if (over <= 0)
            return 0;
        return BaseDamagePerTick + (int)(over * DamagePerExtraCell);
    }
}
