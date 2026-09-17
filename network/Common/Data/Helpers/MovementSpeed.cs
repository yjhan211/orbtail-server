using System.Collections.Generic;
using network.common.data.models;

namespace network.common.data.helpers
{
    /// <summary>
    /// 사람과 봇이 공유하는 이동속도 배율. 각 실행 환경에서 판정한 버프 활성 여부를 받는다.
    /// 기본 속도, 시간 관리, 이동 검증 상한은 호출자가 담당한다.
    /// </summary>
    public static class MovementSpeed
    {
        public static float GetMultiplier(IEnumerable<InGameItemInfo> orbs,
            bool bootsActive, bool bareSpeedActive, bool waveSlowActive)
        {
            float wind = OrbData.GetMoveSpeedMultiplier(orbs);
            float boots = bootsActive ? Config.BOOTS_MOVE_SPEED_MULTIPLIER : 1f;
            float bare = bareSpeedActive ? Config.SWARM_BARE_MOVE_SPEED_MULTIPLIER : 1f;
            float waveSlow = waveSlowActive ? Config.SWARM_WAVE_SLOW_MOVE_SPEED_MULTIPLIER : 1f;
            return wind * boots * bare * waveSlow;
        }
    }
}
