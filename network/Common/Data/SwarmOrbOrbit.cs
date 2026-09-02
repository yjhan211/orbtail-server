using System;

namespace network.common.data
{
    /// <summary>
    ///     오브 궤도 위상 — 서버와 클라가 같은 식으로 같은 자리를 계산한다 (#232).
    ///     위상 = 소유자 시드 + 이동 거리 적산 × 도/단위 — 이동할 때 돌고 멈추면 선다.
    ///     서버가 검증 이동(사람)·봇 걸음으로 적산한 값이 권위이고 G_TO_C_MOVE.OrbOrbitPhaseDegrees로 내려간다.
    ///     현재 이 위상의 용도는 클라 표시 동기뿐이다 — 클라는 자기 트랜스폼 이동으로 같은 식을 적산하다가
    ///     서버 값으로 보정해 원격 플레이어의 궤도가 같은 자리에 보이게 한다. 서버 전투(표적 선정·판정)는
    ///     궤도 슬롯이 아니라 본체 위치를 원점으로 쓴다 (궤도 배치 공격은 #318에서 삭제).
    /// </summary>
    public static class SwarmOrbOrbit
    {
        public static float Radius => Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ORBIT_RADIUS_MULTIPLIER;

        /// <summary>소유자별 출발 위상(도) — 궤도가 서로 겹쳐 보이지 않게 흩는다.</summary>
        public static float InitialPhaseDegrees(long ownerPlayerId)
        {
            return ((ownerPlayerId * 47L) % 360L + 360L) % 360L;
        }

        /// <summary>
        ///     이동 거리만큼 위상을 돌린다. 텔레포트(구역 이동)급 점프는 이동이 아니다 — 무시한다.
        ///     서버(검증 이동)와 클라(트랜스폼 이동)가 같은 규칙으로 적산한다.
        /// </summary>
        public static float AdvancePhase(float phaseDegrees, float movedDistance)
        {
            if (movedDistance <= 0f || movedDistance > Config.SWARM_ORB_ORBIT_TELEPORT_DISTANCE)
                return phaseDegrees;

            float phase = (phaseDegrees + movedDistance * Config.SWARM_ORB_ORBIT_DEGREES_PER_UNIT) % 360f;
            return phase < 0f ? phase + 360f : phase;
        }

        /// <summary>두 위상의 최단 차이 (-180, 180] — 클라 보정이 짧은 쪽으로 돌기 위해.</summary>
        public static float DeltaDegrees(float fromDegrees, float toDegrees)
        {
            float delta = (toDegrees - fromDegrees) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta <= -180f) delta += 360f;
            return delta;
        }

        /// <summary>슬롯 각도(도): 위상 + 360/N 균등 분할 (클라 AssignOrbRingLayout과 같은 규칙).</summary>
        public static float SlotAngleDegrees(float phaseDegrees, int ordinal, int count)
        {
            int safeCount = Math.Max(1, count);
            return phaseDegrees + 360f * ordinal / safeCount;
        }

        /// <summary>궤도 중심에서 슬롯까지의 월드 오프셋 — 아이소 타원(y 절반).</summary>
        public static void SlotOffset(float angleDegrees, out float dx, out float dy)
        {
            double radians = angleDegrees * Math.PI / 180.0;
            dx = (float)(Math.Cos(radians) * Radius);
            dy = (float)(Math.Sin(radians) * Radius * Config.SWARM_ORB_ORBIT_ISO_Y_SCALE);
        }

        /// <summary>본체 월드 위치에서 슬롯 월드 위치 (x, y).</summary>
        public static void SlotPosition(
            float bodyX, float bodyY, float phaseDegrees, int ordinal, int count,
            out float x, out float y)
        {
            SlotOffset(SlotAngleDegrees(phaseDegrees, ordinal, count), out float dx, out float dy);
            x = bodyX + dx;
            y = bodyY + Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y + dy;
        }
    }
}
