using System.Collections.Immutable;
using game_server.players;
using network.common;

namespace game_server.matches.combat;

/// <summary>관찰자 한 명에게 마지막으로 보낸 행위자의 표시 상태. 값이 같으면 다시 보내지 않는다.</summary>
internal readonly record struct OrbVisualState(
    AreaType Area,
    int WeaponItemId,
    string OrbItemSignature,
    int BodyHealth);

/// <summary>
///     한 행위자의 표시 상태와 패킷 페이로드. 틱마다 액터 목록에서 만들고, 세션이 마지막 전송값과 비교해 변경분만 보낸다.
///     OrbItemIds는 복사본이라 이후 컬렉션 변경에 영향받지 않는다.
/// </summary>
internal sealed record OrbVisual(long ActorPlayerId, OrbVisualState State, ImmutableArray<int> OrbItemIds)
{
    /// <summary>참가자마다 하나씩. 매치 상태를 바꾸지 않으며 호출자는 매치 잠금을 보유한다.</summary>
    public static List<OrbVisual> Build(MatchRuntime runtime, IReadOnlyCollection<ProximityCombatActor> actors)
    {
        var visuals = new List<OrbVisual>();
        foreach (var group in actors.GroupBy(actor => actor.PlayerId))
        {
            var orbActors = group
                .Where(actor => !actor.IsMonsterTarget && PlayerOrbCollection.GetOrbTier(actor.WeaponItemId) > 0)
                .OrderBy(actor => actor.WeaponItemUid)
                .ThenBy(actor => actor.WeaponStackIndex)
                .ToList();
            var bodyActor = group.First();
            if (bodyActor.IsMonsterTarget)
            {
                continue;
            }

            // 표시 기준 액터: 첫 오브, 오브가 없으면 본체.
            var primaryActor = orbActors.Count > 0 ? orbActors[0] : bodyActor;
            var orbItemIds = ImmutableArray.CreateRange(orbActors.Select(actor => actor.WeaponItemId));
            // 본체 체력을 상태에 포함 — 값 변화가 곧 상태 변화라 갱신이 전송된다.
            var state = new OrbVisualState(
                primaryActor.Area,
                primaryActor.WeaponItemId,
                string.Join(",", orbItemIds),
                runtime.GetParticipant(primaryActor.PlayerId)?.Health ?? -1);
            visuals.Add(new OrbVisual(primaryActor.PlayerId, state, orbItemIds));
        }
        return visuals;
    }
}
