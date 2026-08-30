using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

// EmotionAfterimageMonsterManager(#274에서 삭제된 빈 껍데기)에 정의돼 있던 공용 타입 중
// 살아 있는 소비처가 남은 것만 보존한다.
// - MonsterCombatTarget: 봇 잔상 사냥 경로(BotPlayerManager.Movement, MONSTER_SUMMON_ECONOMY_ENABLED 동결)의 입력 타입
// - 나머지: GameEventLogManager 몬스터 텔레메트리 시그니처

public readonly record struct MonsterCombatTarget(
    int MonsterId,
    MapId MapId,
    AreaType Area,
    Vector3f Position,
    int RewardItemId,
    int ClusterId = 0,
    int ClusterMemberIndex = 0,
    int ClusterSize = 1,
    bool IsCore = false);

public readonly record struct MonsterRewardAreaState(
    AreaType Area,
    int AliveMonsterCount,
    int AliveCoreCount,
    int RemainingSummonStoneReward);

public readonly record struct MonsterRewardAreaSnapshot(
    int PhaseIndex,
    IReadOnlyList<MonsterRewardAreaState> Areas)
{
    public static MonsterRewardAreaSnapshot Empty => new(0, []);
}

public readonly record struct MonsterReinforcementRelease(
    AreaType Area,
    int PhaseIndex,
    int ReleasedCount,
    int RemainingBudget,
    int AliveCountAfterRelease);

public readonly record struct MonsterDensitySample(
    AreaType Area,
    int PhaseIndex,
    int AliveMonsterCount,
    int ReinforcementRemainingBudget,
    int GlobalAliveMonsterCount,
    bool HasAttackableMonster);
