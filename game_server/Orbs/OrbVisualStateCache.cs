using network.common;
using System.Collections.Concurrent;

namespace game_server.orbs;

/// <summary>매치 하나의 관찰자별 오브 전송 캐시. 마지막 전송 상태를 보관하며 매치와 함께 폐기한다.</summary>
internal sealed class OrbVisualStateCache
{
    public ConcurrentDictionary<(long ObserverPlayerId, long ActorPlayerId), OrbVisualState> States { get; } = new();

    internal readonly record struct OrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature,
        int FrontOrbHp,
        int BodyHealth,
        long ArmorMask);
}
