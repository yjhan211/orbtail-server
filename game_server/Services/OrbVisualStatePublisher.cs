using OrbVisualState = game_server.services.MatchPresentationState.OrbVisualState;
using System.Collections.Immutable;
using game_server.network;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>
///     관전자별 오브·오염·외피 표시 상태를 계산하고 변경된 항목만 전송한다.
///     캐시는 매치의 Presentation이 소유한다. 호출자는 매치 잠금을 보유하며
///     항목별 캐시 반영 직후 전송하는 순서와 전송 실패 경계를 유지한다.
/// </summary>
internal sealed class OrbVisualStatePublisher(MatchRuntimeStore matchRuntimes)
{
    public void Publish(long matchingId, IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        DispatchOrbVisualStatePublications(
            PrepareOrbVisualStatePublications(matchingId, actors, matchingSessions));
    }
    /// <summary>앞줄 오브 = 최저 티어·선입(ItemUid) — 피해·표시가 같은 기준을 읽는다.</summary>
    private InGameItemInfo? FindSwarmFrontOrb(long matchingId, long playerId)
    {
        return matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => GetSquadOrbTier(item.ItemId))
            .ThenBy(item => item.ItemUid)
            .FirstOrDefault();
    }

    /// <summary>잼 보유량 조회 (#222 M3) — 사람은 세션, 봇은 봇 상태에서. 머리 위 공개 표시용.</summary>
    private int GetSwarmJamCount(
        long matchingId, long playerId, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var session in matchingSessions)
        {
            if (session.PlayerId == playerId)
                return session.JamCount;
        }

        foreach (var bot in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.JamCount;
        }

        return 0;
    }

    /// <summary>본체 오염 조회 (#226 가시화) — 세션·봇 공통. 못 찾으면 -1(클라 표시 유지).</summary>
    private int GetSwarmBodyCorruption(
        long matchingId, long playerId, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var session in matchingSessions)
        {
            if (session.PlayerId == playerId)
                return session.CurrentCorruption;
        }

        foreach (var bot in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.Corruption;
        }

        return -1;
    }

    /// <summary>
    ///     방어 강화(내구 2+) 오브 순번 마스크 — 클라 은백 링 표시용 (#226).
    ///     순서는 비주얼 브로드캐스트의 OrbItemIds와 동일한 ItemUid 오름차순 — 슬롯 인덱스 정합.
    /// </summary>
    private long GetSwarmArmorMask(long matchingId, long playerId)
    {
        long mask = 0;
        var orbs = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count && ordinal < 64; ordinal++)
            if (matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, orbs[ordinal].ItemUid)))
                mask |= 1L << ordinal;
        return mask;
    }




    /// <summary>
    ///     앞줄 오브의 HP — 오브별 체력바 브로드캐스트용. 오브 HP 전투가 퇴역해 서버는 HP를 깎지 않으므로
    ///     항상 만충을 보낸다. 빈손은 -1.
    /// </summary>
    private int GetSwarmFrontOrbHp(long matchingId, long playerId)
    {
        var frontOrb = FindSwarmFrontOrb(matchingId, playerId);
        return frontOrb == null ? -1 : OrbData.GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
    }

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

    /// <summary>
    ///     현재 observer→actor 순서의 cache remove 또는 publication 후보를 불변 값으로 고정한다.
    ///     cache commit은 아직 하지 않는다. 각 Send 직전 commit이라는 failure boundary는
    ///     <see cref="DispatchOrbVisualStatePublications"/>가 지킨다.
    /// </summary>
    private ImmutableArray<SwarmOrbVisualPublication> PrepareOrbVisualStatePublications(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        if (matchRuntimes.Get(matchingId)?.Presentation is not { } presentation)
            return [];
        var visualStates = presentation.OrbVisuals;
        GameClientSession[] recipientSnapshot = matchingSessions.ToArray();
        var publications = ImmutableArray.CreateBuilder<SwarmOrbVisualPublication>();
        var visualActors = actors
            .GroupBy(actor => actor.PlayerId)
            .Select(group =>
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

                var orbItemIds = orbActors.Select(actor => actor.WeaponItemId).ToList();
                return new
                {
                    Actor = primaryActor,
                    OrbItemIds = orbItemIds,
                    OrbItemSignature = string.Join(",", orbItemIds)
                };
            })
            .ToList();

        foreach (var observer in recipientSnapshot)
        {
            // 탈락 관전자도 받는다 (#219): 오브 궤도·앞줄 HP가 관전 화면에서도 계속 갱신돼야 한다.
            if (!observer.PlayerId.HasValue)
                continue;

            foreach (var visualActor in visualActors)
            {
                var actor = visualActor.Actor;
                var key = (observer.PlayerId.Value, actor.PlayerId);
                if (observer.CurrentArea != actor.Area)
                {
                    publications.Add(SwarmOrbVisualPublication.Remove(
                        matchingId, observer.PlayerId.Value, actor.PlayerId));
                    continue;
                }

                // 앞줄 오브 HP·잼·본체 오염을 시그니처에 포함 — 값 변화가 곧 상태 변화라 갱신이 전송된다.
                int frontOrbHp = GetSwarmFrontOrbHp(matchingId, actor.PlayerId);
                int jamCount = GetSwarmJamCount(matchingId, actor.PlayerId, matchingSessions);
                int bodyCorruption = GetSwarmBodyCorruption(matchingId, actor.PlayerId, matchingSessions);
                long armorMask = GetSwarmArmorMask(matchingId, actor.PlayerId);
                var state = new OrbVisualState(
                    actor.Area,
                    actor.WeaponItemId,
                    actor.OrbEffectActive,
                    visualActor.OrbItemSignature,
                    frontOrbHp,
                    jamCount,
                    bodyCorruption,
                    armorMask);
                if (visualStates.TryGetValue(key, out var previousState) &&
                    previousState == state)
                {
                    continue;
                }

                publications.Add(SwarmOrbVisualPublication.Publish(
                    matchingId,
                    observer.PlayerId.Value,
                    observer,
                    actor.PlayerId,
                    state,
                    actor.WeaponItemId,
                    actor.OrbEffectActive,
                    CaptureSwarmOrbVisualItemIds(visualActor.OrbItemIds),
                    frontOrbHp,
                    jamCount,
                    bodyCorruption,
                    armorMask));
            }
        }

        return publications.ToImmutable();
    }

    /// <summary>
    ///     cache remove/update와 바로 뒤 Send를 항목마다 붙여 실행한다 (매치 잠금 안). Send가 실패한 key는
    ///     이미 commit돼 재시도하지 않지만, 아직 방문하지 않은 항목은 cache가 그대로라 다음 틱에 다시 후보가 된다.
    /// </summary>
    private void DispatchOrbVisualStatePublications(
        ImmutableArray<SwarmOrbVisualPublication> publications)
    {
        foreach (SwarmOrbVisualPublication publication in publications)
            CommitAndDispatchOrbVisualStatePublication(publication);
    }

    private void CommitAndDispatchOrbVisualStatePublication(
        SwarmOrbVisualPublication publication)
    {
        if (matchRuntimes.Get(publication.MatchingId)?.Presentation is not { } presentation)
            return;
        var visualStates = presentation.OrbVisuals;
        var key = (publication.ObserverPlayerId, publication.ActorPlayerId);
        if (publication.State is { } state)
            visualStates[key] = state;
        else
            visualStates.TryRemove(key, out _);

        if (publication.State is null)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_EFFECT_STATE
        {
            PlayerId = publication.ActorPlayerId,
            WeaponItemId = publication.WeaponItemId,
            IsActive = publication.IsActive,
            OrbItemIds = publication.OrbItemIds.ToList(),
            FrontOrbHp = publication.FrontOrbHp,
            JamCount = publication.JamCount,
            BodyCorruption = publication.BodyCorruption,
            ArmorMask = publication.ArmorMask
        }));
        publication.Recipient!.TrySend(packet);
    }

    private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(IEnumerable<int> orbItemIds) =>
        [.. orbItemIds];

    private sealed record SwarmOrbVisualPublication(
        long MatchingId,
        long ObserverPlayerId,
        long ActorPlayerId,
        OrbVisualState? State,
        GameClientSession? Recipient,
        int WeaponItemId,
        bool IsActive,
        ImmutableArray<int> OrbItemIds,
        int FrontOrbHp,
        int JamCount,
        int BodyCorruption,
        long ArmorMask)
    {
        public static SwarmOrbVisualPublication Remove(
            long matchingId, long observerPlayerId, long actorPlayerId) =>
            new(
                matchingId, observerPlayerId, actorPlayerId, null, null,
                0, false, ImmutableArray<int>.Empty, 0, 0, 0, 0);

        public static SwarmOrbVisualPublication Publish(
            long matchingId,
            long observerPlayerId,
            GameClientSession recipient,
            long actorPlayerId,
            OrbVisualState state,
            int weaponItemId,
            bool isActive,
            ImmutableArray<int> orbItemIds,
            int frontOrbHp,
            int jamCount,
            int bodyCorruption,
            long armorMask) =>
            new(
                matchingId, observerPlayerId, actorPlayerId, state, recipient,
                weaponItemId, isActive, orbItemIds, frontOrbHp, jamCount, bodyCorruption, armorMask);
    }

}

