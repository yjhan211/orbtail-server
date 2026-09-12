#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;
using network.common;

namespace network.common.data.models
{
    /// <summary>
    /// Server-authoritative state for a fixed emotion-afterimage monster.
    /// The client uses this only to drive the scene prefab and never predicts HP.
    /// </summary>
    [MessagePackObject]
    public sealed class MonsterRuntimeInfo
    {
        [Key("monsterId")] public int MonsterId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("positionX")] public float PositionX { get; set; }
        [Key("positionY")] public float PositionY { get; set; }
        [Key("maxHealth")] public int MaxHealth { get; set; }
        [Key("currentHealth")] public int CurrentHealth { get; set; }
        [Key("isAlive")] public bool IsAlive { get; set; }
        [Key("rewardItemId")] public int RewardItemId { get; set; }
        [Key("isCore")] public bool IsCore { get; set; }
        [Key("summonStoneReward")] public int SummonStoneReward { get; set; }
        [Key("chaseTargetPlayerId")] public long ChaseTargetPlayerId { get; set; }

        // 종 식별자 (#229 4단계): 크기·몸체는 이 값으로 정한다.
        // 이전에는 MaxHealth가 종 식별자를 겸했는데, 폐쇄 단계별로 피통을 올리는 순간
        // 해골이 다트로 보이는 식으로 무너진다. 수치와 정체를 분리한다.
        // 값은 서버 MonsterKind와 같다: 0 해골 · 1 다트 · 2 탈주 · 3 볼러
        // · 4 골렘 · 5 베이비드래곤 · 6 트리자이언트.
        [Key("kind")] public int Kind { get; set; }

        // 공급 페이즈 (2026-08-16): 0~4. 페이즈가 오를수록 몹이 세진다(HP 16/17/19/21/22,
        // 접촉 2/3/4/6/8). 클라는 이 값으로 몸집과 문양 수를 올려 "세졌다"를 눈으로 읽힌다.
        [Key("phase")] public int Phase { get; set; }
    }

    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_SNAPSHOT : IMessagePackObject
    {
        [Key("monsters")] public List<MonsterRuntimeInfo> Monsters { get; set; } = new();
    }

    /// <summary>Visual-only confirmation that a monster's authoritative contact attack landed.</summary>
    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_ATTACK_VFX : IMessagePackObject
    {
        [Key("monsterId")] public int MonsterId { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
    }
}
