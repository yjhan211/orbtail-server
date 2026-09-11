using System.Collections.Concurrent;

namespace game_server.matches.combat;

/// <summary>매치 하나의 오브별 다음 회복 시각. PlayerOrbService가 갱신하며 매치와 함께 폐기한다.</summary>
internal sealed class MatchOrbRecoveryState
{
    public ConcurrentDictionary<(long PlayerId, long ItemUid, int StackIndex), DateTime> ReadyAtUtc { get; } = new();
}
