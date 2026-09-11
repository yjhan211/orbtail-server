using game_server.combat;
using game_server.matches;
using game_server.orbs;
using network.common;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>
///     봇 투사체 회피 반사 (#232 §9, 2026-08-18 유저 제보 "봇이 투사체를 안 피하고 그대로 맞고 있어").
///     봇 이동 틱(50ms)이 매 걸음 전에 묻는다: "지금 이 자리를 지나갈 태양 투사체가 있는가?"
///     있으면 그 직선의 수직으로 한 걸음 비켜선다 — 경로는 버리지 않고 다음 틱에 이어 걷는다.
///     판정 기하는 교차사격 서버 판정(바닥면 dy×2, 반폭 + 플레이어 반경)과 같다.
///     대상은 태양 투사체뿐이다: 바람 몸통박치기는 표적의 착지 순간 자리에 떨어져(호밍) 자리로는 못 피하고,
///     파도 물폭탄은 잔상 전용이다.
///     상태 없는 순수 정책 (#297) — 매치별 스냅샷 발행(SunOrbAttackState)과 분리.
/// </summary>
public static class SwarmBotDodgePolicy
{
    // 봇이 반응하는 착탄 예상 시간 상한 — 이보다 멀면 아직 안 움직인다(예고 0.55초 + 비행이라 대개 1초 안팎).
    private const float SwarmBotDodgeHorizonSeconds = 1.5f;
    // 판정 띠 밖으로 이만큼 여유를 더 벌린다 — 띠 가장자리에서 멈추면 몸통 반경만큼 다시 맞는다.
    private const float SwarmBotDodgeMargin = 0.2f;
    // 회피 커밋 여유 — 앞머리가 지난 뒤 이만큼 더 선 자리에 있다가 경로로 돌아간다.
    private const float SwarmBotDodgeHoldSlackSeconds = 0.15f;

    public readonly record struct SwarmCrossfireDodgeThreat(
        long MatchingId,
        AreaType Area,
        long OwnerId,
        float OriginX,
        float OriginY,
        float AxisX,
        float AxisY,
        float GroundLength,
        float HalfWidth,
        float SweepSpeed,
        DateTime ArmedAtUtc,
        DateTime ExpiresAtUtc);

    /// <summary>
    ///     이 봇이 지금 비켜서야 할 방향(월드 단위 벡터). 위협이 없으면 null.
    ///     가장 먼저 닿을 투사체 하나만 본다 — 여러 개가 겹치면 다음 틱에 다시 묻는다.
    /// </summary>
    public static SwarmBotDodgeAdvice? ResolveSwarmBotDodgeDirection(
        IReadOnlyList<SwarmCrossfireDodgeThreat> threats,
        long matchingId, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc)
    {
        if (threats.Count == 0)
            return null;

        float bestTime = float.MaxValue;
        SwarmBotDodgeAdvice? bestDirection = null;
        foreach (var threat in threats)
        {
            if (threat.MatchingId != matchingId || threat.Area != area || threat.OwnerId == botPlayerId)
                continue;
            if (nowUtc >= threat.ExpiresAtUtc)
                continue;

            float rx = position.X - threat.OriginX;
            float ry = (position.Y - threat.OriginY) * SwarmCombatGeometry.GroundYScale;
            float along = rx * threat.AxisX + ry * threat.AxisY;
            float perp = rx * -threat.AxisY + ry * threat.AxisX;
            float band = threat.HalfWidth + SwarmCombatGeometry.PlayerRadius + SwarmBotDodgeMargin;
            if (MathF.Abs(perp) > band)
                continue;
            if (along < -threat.HalfWidth - SwarmCombatGeometry.PlayerRadius ||
                along > threat.GroundLength + threat.HalfWidth + SwarmCombatGeometry.PlayerRadius)
                continue;

            // 앞머리 위치: 예고 중이면 원점 앞 캡, 발동 뒤면 속도 × 경과.
            double sinceArmed = (nowUtc - threat.ArmedAtUtc).TotalSeconds;
            float front = sinceArmed <= 0d
                ? -threat.HalfWidth
                : -threat.HalfWidth + (float)sinceArmed * threat.SweepSpeed;
            // 이미 지나간 투사체는 위협이 아니다.
            if (front > along + SwarmCombatGeometry.PlayerRadius)
                continue;

            float timeToHit = (float)Math.Max(0d, -sinceArmed) +
                              Math.Max(0f, along - front) / Math.Max(0.01f, threat.SweepSpeed);
            if (timeToHit > SwarmBotDodgeHorizonSeconds || timeToHit >= bestTime)
                continue;

            bestTime = timeToHit;
            // 이미 치우친 쪽으로 빠진다 — 축 위에 딱 서 있으면 봇 id 홀짝으로 갈라 무리가 한쪽으로 몰리지 않게.
            float side = MathF.Abs(perp) < 0.02f ? ((botPlayerId & 1) == 0 ? 1f : -1f) : MathF.Sign(perp);
            // 바닥면 수직 (-ay, ax) → 월드로 되돌린다 (Y는 ÷2).
            float wx = -threat.AxisY * side;
            float wy = threat.AxisX * side / SwarmCombatGeometry.GroundYScale;
            float wl = MathF.Sqrt(wx * wx + wy * wy);
            if (wl < 0.001f)
                continue;
            // 유지 시간 = 앞머리가 내 자리를 지나 몸 반경만큼 더 간 뒤 한 박자. 그동안은 띠 밖에 서서 기다린다.
            float holdSeconds = timeToHit +
                                (2f * SwarmCombatGeometry.PlayerRadius) / Math.Max(0.01f, threat.SweepSpeed) +
                                SwarmBotDodgeHoldSlackSeconds;
            bestDirection = new SwarmBotDodgeAdvice(wx / wl, wy / wl, holdSeconds);
        }

        return bestDirection;
    }
}
