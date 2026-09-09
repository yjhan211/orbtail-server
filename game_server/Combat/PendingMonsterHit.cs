using network.common.data.models;

namespace game_server.combat;

/// <summary>착탄 시각에 적용할 몬스터 피해. 대상·공격자·피해량과 발사 시 확정한 위치를 보관한다.</summary>
public readonly record struct PendingMonsterHit(
    long MatchingId,
    long CombatTargetId,
    long AttackerId,
    int Damage,
    DateTime ApplyAtUtc,
    int WeaponItemId = 0,
    long AttackerItemUid = 0,
    Vector3f? Origin = null,
    Vector3f? AnchorPosition = null);
