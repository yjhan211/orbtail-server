using System;

namespace network.common.data
{
    /// <summary>
    ///     오브 궤도 위치 (#232, 2026-08-17): 서버와 클라가 같은 식으로 같은 자리를 계산한다.
    ///     위상 = 소유자 시드 + 각속도 × 서버 시각 — 클라는 서버 시각 추정치(ServerClock)로 그리고,
    ///     서버는 그 자리를 오브별 발사 원점·표적 선정 기준으로 쓴다. 그래서 "각 오브가 제 자리에서
    ///     가장 가까운 몹을 노리고", 예고선이 그 오브에서 나가며, 판정선과 표시선이 같은 선이다.
    ///     이전(궤도 위상 = 클라 로컬 애니메이션)에는 서버가 본체 위치를 원점으로 써서, 표시선(오브)과
    ///     판정선(본체)이 원점 쪽에서 최대 궤도 반지름만큼 어긋났다 — "안 맞은 몹이 죽고, 지나간
    ///     자리의 봇이 안 맞는" 제보의 원인.
    /// </summary>
    public static class SwarmOrbOrbit
    {
        public static float Radius => Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ORBIT_RADIUS_MULTIPLIER;

        /// <summary>소유자별 공전 위상(도). 같은 시각이면 어디서 계산해도 같다.</summary>
        public static float PhaseDegrees(long ownerPlayerId, long unixMs)
        {
            // 소유자마다 출발 각을 흩어 궤도가 서로 겹쳐 보이지 않게 한다.
            double seed = ((ownerPlayerId * 47L) % 360L + 360L) % 360L;
            // double: 1.7e12 ms × 0.054 ≈ 9e10 — 소수 아래 5자리가 남아 각도 오차 1e-5° 수준.
            double phase = (seed + unixMs * (Config.SWARM_ORB_ORBIT_DEGREES_PER_SECOND / 1000.0)) % 360.0;
            return (float)phase;
        }

        /// <summary>슬롯 각도(도): 위상 + 360/N 균등 분할 (클라 AssignOrbRingLayout과 같은 규칙).</summary>
        public static float SlotAngleDegrees(long ownerPlayerId, long unixMs, int ordinal, int count)
        {
            int safeCount = Math.Max(1, count);
            return PhaseDegrees(ownerPlayerId, unixMs) + 360f * ordinal / safeCount;
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
            float bodyX, float bodyY, long ownerPlayerId, long unixMs, int ordinal, int count,
            out float x, out float y)
        {
            SlotOffset(SlotAngleDegrees(ownerPlayerId, unixMs, ordinal, count), out float dx, out float dy);
            x = bodyX + dx;
            y = bodyY + Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y + dy;
        }
    }
}
