using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

/// <summary>매치 하나의 오브 전송 캐시·회복 시각·몬스터 위치 전송 시각. 매치와 함께 폐기한다.</summary>
internal sealed class MatchPresentationState
{
    public ConcurrentDictionary<(long ObserverPlayerId, long ActorPlayerId), OrbVisualState> OrbVisuals { get; } = new();
    public ConcurrentDictionary<(long PlayerId, long ItemUid, int StackIndex), DateTime> OrbRecoveryReadyAtUtc { get; } = new();
    // 매치 틱의 잠금 안에서만 읽고 변경한다.
    public DateTime NextMonsterPositionBroadcastAtUtc { get; set; }

    internal readonly record struct OrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature,
        int FrontOrbHp,
        int BodyHealth,
        long ArmorMask);
}
