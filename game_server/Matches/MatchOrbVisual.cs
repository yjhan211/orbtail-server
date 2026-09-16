using System.Collections.Immutable;
using game_server.players;
using network.common;

namespace game_server.matches;

/// <summary>
///     전투 액터에서 구성한 플레이어별 오브 표시 정보.
///     각 세션이 이전에 보낸 상태와 비교해 변경된 정보만 전송하는 데 사용한다.
/// </summary>
internal sealed record MatchOrbVisual(long ActorPlayerId, AreaType Area, int WeaponItemId, int BodyHealth, ImmutableArray<int> OrbItemIds)
{
    public bool HasSameState(MatchOrbVisual other) =>
        Area == other.Area && WeaponItemId == other.WeaponItemId && BodyHealth == other.BodyHealth && OrbItemIds.SequenceEqual(other.OrbItemIds);

    public static List<MatchOrbVisual> Build(MatchRuntime runtime, IReadOnlyCollection<ProximityCombatActor> actors)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb visual build requires the match lock.");
        }
        var playerOrder = new List<long>();
        var bodyActors = new Dictionary<long, ProximityCombatActor>();
        var orbActorsByPlayer = new Dictionary<long, List<ProximityCombatActor>>();
        foreach (var actor in actors)
        {
            if (!bodyActors.ContainsKey(actor.PlayerId))
            {
                playerOrder.Add(actor.PlayerId);
                bodyActors[actor.PlayerId] = actor;
                orbActorsByPlayer[actor.PlayerId] = new List<ProximityCombatActor>();
            }

            if (!actor.IsMonsterTarget && PlayerOrbState.GetOrbTier(actor.WeaponItemId) > 0)
            {
                orbActorsByPlayer[actor.PlayerId].Add(actor);
            }
        }

        var visuals = new List<MatchOrbVisual>(playerOrder.Count);
        foreach (long playerId in playerOrder)
        {
            var bodyActor = bodyActors[playerId];
            if (bodyActor.IsMonsterTarget)
            {
                continue;
            }

            var orbActors = orbActorsByPlayer[playerId];
            orbActors.Sort(CompareOrbActors);
            var orbItemIds = ImmutableArray.CreateBuilder<int>(orbActors.Count);
            foreach (var orbActor in orbActors)
            {
                orbItemIds.Add(orbActor.WeaponItemId);
            }

            var primaryActor = orbActors.Count > 0 ? orbActors[0] : bodyActor;
            int bodyHealth = runtime.GetParticipant(primaryActor.PlayerId)?.Health ?? -1;
            visuals.Add(new MatchOrbVisual(primaryActor.PlayerId, primaryActor.Area, primaryActor.WeaponItemId, bodyHealth, orbItemIds.MoveToImmutable()));
        }
        return visuals;
    }

    private static int CompareOrbActors(ProximityCombatActor left, ProximityCombatActor right)
    {
        int uidComparison = left.WeaponItemUid.CompareTo(right.WeaponItemUid);
        return uidComparison != 0 ? uidComparison : left.WeaponStackIndex.CompareTo(right.WeaponStackIndex);
    }
}
