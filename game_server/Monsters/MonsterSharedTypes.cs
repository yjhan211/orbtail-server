using game_server.bots;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.monsters;

// 봇 잔상 사냥 경로(BotPlayerManager.Movement, MONSTER_SUMMON_ECONOMY_ENABLED)의 입력 타입.
// 공급원이던 SwarmAfterimageMonsterManager는 #274에서 삭제 — 부활 시 SwarmMonsterDirector에서 공급할 것.
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
