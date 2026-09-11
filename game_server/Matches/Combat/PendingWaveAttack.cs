using network.common;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>예고 뒤 기폭할 파도 소용돌이. 발동 시 확정한 자리·피해·반경을 기폭 시각까지 보관한다.</summary>
public readonly record struct PendingWaveAttack(
    long OwnerId,
    AreaType Area,
    Vector3f Position,
    int Damage,
    float Radius,
    int SourceItemId,
    DateTime ExplodeAtUtc);
