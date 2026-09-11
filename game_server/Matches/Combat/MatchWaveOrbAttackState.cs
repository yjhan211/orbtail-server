using network.common;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>매치 하나의 파도 기폭 대기열. 공격자의 생존 여부와 별개로 처리한다.</summary>
public sealed class MatchWaveOrbAttackState
{
    public readonly List<(long MatchingId, long OwnerId, AreaType Area, Vector3f Position, int Damage,
        float Radius, int SourceItemId, DateTime ExplodeAtUtc)> PendingAttacks = new();
}
