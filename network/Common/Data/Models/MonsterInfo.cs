using MessagePack;
using network.common;

namespace network.common.data.models
{
    /// <summary>
    /// 서버·클라이언트가 공유하는 몬스터 정보. 공간 정보는 ObjectInfo에 보관한다.
    /// 기존 전송 형식은 유지하며, 위치 X/Y는 같은 공간 정보에 위임한다.
    /// </summary>
    [MessagePackObject]
    public sealed class MonsterInfo
    {
        // 현재 스냅샷은 ID·월드 위치만 전달한다. 맵·셀·속도·회전은 아직 이 모델의 동기화 대상이 아니다.
        [IgnoreMember] public GameObjectInfo ObjectInfo { get; } = new GameObjectInfo { ObjectType = ObjectType.MONSTER };

        [Key("monsterId")] public int MonsterId { get => (int)ObjectInfo.ObjectId; set => ObjectInfo.ObjectId = value; }
        [Key("areaType")] public AreaType AreaType { get => ObjectInfo.Area; set => ObjectInfo.Area = value; }
        [Key("positionX")] public float PositionX { get => ObjectInfo.Position.X; set => ObjectInfo.Position.X = value; }
        [Key("positionY")] public float PositionY { get => ObjectInfo.Position.Y; set => ObjectInfo.Position.Y = value; }
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
        // 값은 MonsterKind(GameEnum)와 같다: 0 해골 · 1 다트 · 2 탈주 · 3 볼러
        // · 4 골렘 · 5 베이비드래곤 · 6 트리자이언트.
        [Key("kind")] public int Kind { get; set; }

        // 공급 페이즈 (2026-08-16): 0~4. 페이즈가 오를수록 몹이 세진다(HP 16/17/19/21/22,
        // 접촉 2/3/4/6/8). 클라는 이 값으로 몸집과 문양 수를 올려 "세졌다"를 눈으로 읽힌다.
        [Key("phase")] public int Phase { get; set; }
    }
}
