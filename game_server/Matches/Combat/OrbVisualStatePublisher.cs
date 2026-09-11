using System.Collections.Immutable;
using game_server.matches;
using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>
///     행위자별 오브·체력·외피 표시 상태를 계산해 관찰자 세션마다 전달한다.
///     마지막 전송 상태는 각 세션이 갖고, 세션이 값이 같으면 보내지 않는다. 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class OrbVisualStatePublisher
{
    public void Publish(MatchRuntime runtime, IReadOnlyCollection<ProximityCombatActor> actors, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        var visuals = BuildOrbVisuals(runtime, actors, matchingSessions);
        var recipientSnapshot = matchingSessions.ToArray();
        foreach (var observer in recipientSnapshot)
        {
            // 탈락 관전자도 받는다 (#219): 오브 궤도·앞줄 HP가 관전 화면에서도 계속 갱신돼야 한다.
            if (!observer.PlayerId.HasValue)
                continue;

            foreach (var visual in visuals)
            {
                if (observer.Player.CurrentArea != visual.State.Area)
                {
                    observer.ForgetOrbVisualState(visual.ActorPlayerId);
                    continue;
                }

                observer.SendOrbVisualStateIfChanged(visual);
            }
        }
    }

    private List<OrbVisual> BuildOrbVisuals(MatchRuntime runtime, IReadOnlyCollection<ProximityCombatActor> actors, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        var visuals = new List<OrbVisual>();
        foreach (var group in actors.GroupBy(actor => actor.PlayerId))
        {
            var orbActors = group
                .Where(actor =>
                    OrbData.IsOrbItem(actor.WeaponItemId) ||
                    OrbData.IsRecoveryOrb(actor.WeaponItemId))
                .OrderBy(actor => actor.WeaponItemUid)
                .ThenBy(actor => actor.WeaponStackIndex)
                .ToList();
            var primaryActor = orbActors.FirstOrDefault(actor =>
                actor.OrbEffectActive &&
                OrbData.TryGetColorAndTier(actor.WeaponItemId, out var color, out _) &&
                color == OrbColor.Green);
            if (primaryActor.PlayerId == 0)
                primaryActor = orbActors.FirstOrDefault(actor => actor.OrbEffectActive);
            if (primaryActor.PlayerId == 0)
                primaryActor = orbActors.Count > 0 ? orbActors[0] : group.First();

            var orbItemIds = CaptureSwarmOrbVisualItemIds(orbActors.Select(actor => actor.WeaponItemId));
            // 앞줄 오브 HP·본체 체력을 상태에 포함 — 값 변화가 곧 상태 변화라 갱신이 전송된다.
            var state = new OrbVisualState(
                primaryActor.Area,
                primaryActor.WeaponItemId,
                primaryActor.OrbEffectActive,
                string.Join(",", orbItemIds),
                GetSwarmFrontOrbHp(runtime, primaryActor.PlayerId),
                GetSwarmBodyHealth(runtime, primaryActor.PlayerId, matchingSessions),
                GetSwarmArmorMask(runtime, primaryActor.PlayerId));
            visuals.Add(new OrbVisual(primaryActor.PlayerId, state, orbItemIds));
        }
        return visuals;
    }

    /// <summary>앞줄 오브 = 최저 티어·선입(ItemUid) — 피해·표시가 같은 기준을 읽는다.</summary>
    private static InGameItemInfo? FindSwarmFrontOrb(MatchRuntime runtime, long playerId)
    {
        return runtime.GetOrbs(playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => GetSquadOrbTier(item.ItemId))
            .ThenBy(item => item.ItemUid)
            .FirstOrDefault();
    }

    /// <summary>본체 체력 조회 (#226 가시화) — 세션·봇 공통. 못 찾으면 -1(클라 표시 유지).</summary>
    private static int GetSwarmBodyHealth(
        MatchRuntime runtime, long playerId, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var session in matchingSessions)
        {
            if (session.PlayerId == playerId)
                return session.Player.Health;
        }

        foreach (var bot in runtime.Bots.GetBots())
        {
            if (bot.PlayerId == playerId)
                return bot.Player.Health;
        }

        return -1;
    }

    /// <summary>
    ///     방어 강화(내구 2+) 오브 순번 마스크 — 클라 은백 링 표시용 (#226).
    ///     순서는 비주얼 브로드캐스트의 OrbItemIds와 동일한 ItemUid 오름차순 — 슬롯 인덱스 정합.
    /// </summary>
    private static long GetSwarmArmorMask(MatchRuntime runtime, long playerId)
    {
        long mask = 0;
        var orbs = runtime.GetOrbs(playerId).GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count && ordinal < 64; ordinal++)
            if (runtime.TrailCombat.OrbDurabilityBonus.ContainsKey((playerId, orbs[ordinal].ItemUid)))
                mask |= 1L << ordinal;
        return mask;
    }

    /// <summary>
    ///     앞줄 오브의 HP — 오브별 체력바 브로드캐스트용. 오브 HP 전투가 퇴역해 서버는 HP를 깎지 않으므로
    ///     항상 만충을 보낸다. 빈손은 -1.
    /// </summary>
    private static int GetSwarmFrontOrbHp(MatchRuntime runtime, long playerId)
    {
        var frontOrb = FindSwarmFrontOrb(runtime, playerId);
        return frontOrb == null ? -1 : OrbData.GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
    }

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

    private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(IEnumerable<int> orbItemIds) =>
        [.. orbItemIds];

    /// <summary>관찰자 한 명에게 마지막으로 보낸 행위자의 표시 상태. 값이 같으면 다시 보내지 않는다.</summary>
    internal readonly record struct OrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature,
        int FrontOrbHp,
        int BodyHealth,
        long ArmorMask);

    /// <summary>한 행위자의 표시 상태와 패킷 페이로드. OrbItemIds는 복사본이라 이후 컬렉션 변경에 영향받지 않는다.</summary>
    internal sealed record OrbVisual(long ActorPlayerId, OrbVisualState State, ImmutableArray<int> OrbItemIds);
}
