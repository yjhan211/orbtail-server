using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    // #219 3택 드래프트의 색 → 아이템 매핑. 클라 카드 순서(태양·파도·바람)와 일치해야 한다.
    private const int DraftSunOrbItemId = 107000010;
    private const int DraftWaveOrbItemId = 107000030;
    private const int DraftWindOrbItemId = 107000020;

    private Task HandleSummonOrb(C_TO_G_SUMMON_ORB request)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        // #219 M2 3택 드래프트: 개봉(RNG_COLLECT_FINISH)이 연 드래프트에서만 소환한다.
        // ChoiceIndex = 색 (0 태양, 1 파도, 2 바람). 비용은 개봉 시점에 확정된 값.
        if (!_hasPendingOrbDraft)
        {
            SendSummonOrbResult(false, ErrorCode.INVALID_GAME_STATE, 0, 0,
                GetSummonStoneSnapshot());
            return Task.CompletedTask;
        }

        ExecuteDraftOrbSummon(request.ChoiceIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     드래프트 소환 실행 (#219 M2 → #226 자동화): 색 인덱스로 티어 적용 아이템을 뽑아
    ///     소환한다. 열쇠 충전이 있으면 비용 0 + 성공 시 1 소비. 개봉 자동 소환과
    ///     (레거시) 선택 소환이 같은 경로를 쓴다.
    /// </summary>
    internal bool ExecuteDraftOrbSummon(int choiceIndex)
    {
        if (!PlayerId.HasValue)
            return false;

        // 상자 시간 등급 (#222 M3): 개전 후 80초/160초를 넘기면 같은 색의 T2/T3가 나온다.
        int draftItemId = OrbData.ApplyDraftTier(
            choiceIndex switch
            {
                1 => DraftWaveOrbItemId,
                2 => DraftWindOrbItemId,
                _ => DraftSunOrbItemId
            },
            GetSwarmDraftTier());
        // 열쇠 (#222 M4): 충전이 있으면 이번 소환 비용을 0으로 — 성공 시 1 소비.
        int draftCost = _pendingOrbDraftCost;
        bool useFreeSummon = FreeSummonCharges > 0 && draftCost > 0;
        if (useFreeSummon)
            draftCost = 0;
        var draftAttempt = ExecuteOrbSummon(
            choiceIndex, costOverride: draftCost, exactItemId: draftItemId);
        if (!draftAttempt.Success)
            return false;

        if (useFreeSummon)
        {
            FreeSummonCharges = Math.Max(0, FreeSummonCharges - 1);
            SendFreeSummonState();
        }

        _hasPendingOrbDraft = false;
        // 자동 머지: 오브열 실험(#226)에서는 끈다 — 성장 = 열 길이, 압축은 그 언어와 싸운다.
        if (Config.SWARM_ORB_MERGE_ENABLED)
            foreach (var mergedItem in _matchRuntimes.GetRequired(MatchingId).Inventory.AutoMergeOrbs(
                         PlayerId.Value))
                SendInGameInventoryUpdate(mergedItem);
        return true;
    }

    /// <summary>상자 시간 등급 (#222 M3): 개전 앵커 경과로 드래프트 티어 결정. 게이트 전엔 T1.</summary>
    private int GetSwarmDraftTier()
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(MatchingId);
        if (startedAtUtc == null)
            return 1;

        return OrbData.GetDraftTierByElapsed((DateTime.UtcNow - startedAtUtc.Value).TotalSeconds);
    }

    /// <summary>
    ///     소환 실행 코어. 버튼 소환과 수호물 오브젝트 개봉이 같은 경로(2택 후보·인벤토리
    ///     추가·결과 패킷·로그)를 쓴다. costOverride는 스웜 P0-c의 보유 오브 비례 비용.
    /// </summary>
    internal SummonOrbAttempt ExecuteOrbSummon(int choiceIndex, int? costOverride, int? exactItemId = null)
    {
        long playerId = PlayerId!.Value;
        var attempt = _matchRuntimes.GetRequired(MatchingId).SummonStones.TrySummon(
            playerId,
            itemId => _matchRuntimes.GetRequired(MatchingId).Inventory.TryAddItemWithCapacity(
                playerId,
                itemId,
                Config.SWARM_ORB_CAPACITY,
                out var addedItem)
                ? addedItem
                : null,
            choiceIndex,
            costOverride,
            exactItemId);

        if (attempt.Success && attempt.AddedItem != null)
        {
            SendInGameInventoryUpdate(attempt.AddedItem);
            var inventory = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(playerId);
            _gameEventLogManager.LogOrbBoardTransition(
                MatchingId,
                playerId,
                inventory.GetAllItems(),
                inventory.GetEquippedBattleItem()?.ItemId ?? 0,
                CurrentArea.ToString(),
                "summon",
                isBot: false);
        }

        SendSummonOrbResult(
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.AddedItem?.ItemUid ?? 0,
            attempt.State);
        _gameEventLogManager.LogOrbSummonAttempt(
            MatchingId,
            playerId,
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.State.StoneCount,
            attempt.State.NextCost,
            attempt.State.SuccessfulSummonCount,
            CurrentArea.ToString(),
            isBot: false);

        Logger.LogInformation(
            "Orb summon request: MatchingId={MatchingId}, PlayerId={PlayerId}, Success={Success}, Error={ErrorCode}, ItemId={ItemId}, Stones={StoneCount}, NextCost={NextCost}",
            MatchingId,
            playerId,
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.State.StoneCount,
            attempt.State.NextCost);
        return attempt;
    }

    /// <summary>절단 실험 더미 조종 (#226 실험장, 개발용) — 게임서버 훅으로 위임.</summary>
    private Task HandleDevDummyMove(C_TO_G_DEV_DUMMY_MOVE request)
    {
        if (PlayerId.HasValue && MatchingId > 0)
            SwarmDummyMoveCallback?.Invoke(MatchingId, request.DirX, request.DirY);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     성장 카드 선택 (#226 단계 C) — 매치 잠금 안에서 owning GameServer instance delegate로
    ///     오퍼 상태를 판정한다.
    /// </summary>
    private Task HandleSwarmGrowthPick(C_TO_G_SWARM_GROWTH_PICK request)
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
            return Task.CompletedTask;

        long matchingId = MatchingId;
        return RunUnderMatch(
            () =>
            {
                _handleSwarmGrowthPick(this, matchingId, request.OfferId, request.CardIndex);
                return Task.CompletedTask;
            },
            () => SendSwarmGrowthResult(request.OfferId, request.CardIndex, success: false));
    }

    /// <summary>
    ///     6칸 빌드 결정 (#232 4단계): 합성·예비 오브 교체·분해 — 매치 잠금 안에서
    ///     owning GameServer instance delegate로 판정한다.
    /// </summary>
    private Task HandleSwarmOrbDecision(C_TO_G_SWARM_ORB_DECISION request)
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || IsEliminated)
            return Task.CompletedTask;

        long matchingId = MatchingId;
        return RunUnderMatch(
            () =>
            {
                _handleSwarmOrbDecision(
                    this,
                    matchingId,
                    request.Action,
                    request.TargetItemUid,
                    request.SecondItemUid);
                return Task.CompletedTask;
            },
            () => SendSwarmOrbDecisionResult(
                request.Action,
                success: false,
                resultItemId: 0,
                targetItemUid: request.TargetItemUid,
                targetOrdinal: -1));
    }

    /// <summary>계열 공유 레벨 전송 (#232 4단계). 시작·강화·오브 증감 때 게임서버가 부른다.</summary>
    internal void SendSwarmFamilyLevels(
        int sunLevel, int windLevel, int waveLevel, int sunCost, int windCost, int waveCost)
    {
        if (!PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FAMILY_LEVELS, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FAMILY_LEVELS
        {
            SunLevel = sunLevel,
            WindLevel = windLevel,
            WaveLevel = waveLevel,
            SunCost = sunCost,
            WindCost = windCost,
            WaveCost = waveCost
        }));
        TrySend(packet);
    }

    /// <summary>6칸 빌드 결정 결과 (#232 4단계). targetOrdinal = 강화된 오브의 열 순번(없으면 -1).</summary>
    internal void SendSwarmOrbDecisionResult(
        int action, bool success, int resultItemId, long targetItemUid, int targetOrdinal = -1)
    {
        if (!PlayerId.HasValue)
            return;

        int stones = GetSummonStoneSnapshot().StoneCount;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ORB_DECISION_RESULT
        {
            Action = action,
            Success = success,
            ResultItemId = resultItemId,
            TargetItemUid = targetItemUid,
            StoneCount = stones,
            TargetOrdinal = targetOrdinal
        }));
        TrySend(packet);
    }

    /// <summary>성장 카드 오퍼 전송 (#226 단계 C) — 소환석 임계 도달 순간 게임서버가 부른다.</summary>
    internal void SendSwarmGrowthOffer(
        int offerId, int cost, int spawnItemId, int enhanceTargetTier, int armorCount,
        int costSummon = 0, int costAttack = 0, int costDefense = 0)
    {
        if (!PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_GROWTH_OFFER, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_GROWTH_OFFER
        {
            OfferId = offerId,
            Cost = cost,
            Costs = [costSummon, costAttack, costDefense],
            SpawnItemId = spawnItemId,
            EnhanceTargetTier = enhanceTargetTier,
            ArmorCount = armorCount
        }));
        TrySend(packet);
    }

    /// <summary>성장 카드 선택 결과 전송 (#226 단계 C). 실패 시 클라는 오퍼를 유지한다.</summary>
    internal void SendSwarmGrowthResult(int offerId, int cardIndex, bool success)
    {
        if (!PlayerId.HasValue)
            return;

        int stones = GetSummonStoneSnapshot().StoneCount;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_GROWTH_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_GROWTH_RESULT
        {
            OfferId = offerId,
            CardIndex = cardIndex,
            Success = success,
            StoneCount = stones
        }));
        TrySend(packet);
    }

    private Task HandleDestroyOrb(C_TO_G_DESTROY_ORB request)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        long playerId = PlayerId.Value;
        if (IsRoundActionLocked(out _))
        {
            SendDestroyOrbResult(false, ErrorCode.INVALID_GAME_STATE, request.ItemUid, 0,
                GetSummonStoneSnapshot());
            return Task.CompletedTask;
        }

        var inventory = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(playerId);
        var item = inventory.GetItem(request.ItemUid);
        if (item == null || item.Count <= 0)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_FOUND, request.ItemUid, 0,
                GetSummonStoneSnapshot());
            return Task.CompletedTask;
        }

        int tier;
        bool isDestroyableOrb =
            OrbData.TryGetColorAndTier(item.ItemId, out _, out tier) ||
            OrbData.TryGetRecoveryTier(item.ItemId, out tier);
        if (!isDestroyableOrb)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_USABLE, request.ItemUid, 0,
                GetSummonStoneSnapshot());
            return Task.CompletedTask;
        }

        if (!_matchRuntimes.GetRequired(MatchingId).Inventory.TryRemoveItem(
                playerId, request.ItemUid, 1, out var removedItem) ||
            removedItem == null)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_OWNED, request.ItemUid, 0,
                GetSummonStoneSnapshot());
            return Task.CompletedTask;
        }

        // 스웜 (#232 4단계): 계열 공유 레벨이 플레이어 귀속이라 표시 티어와 무관하게 1로 고정 —
        // 강화한 오브를 부숴도 레벨은 남으므로 티어 환급이면 강화-파괴 재판매가 성립한다.
        int refundedStones = Config.SWARM_ORB_DESTROY_REFUND_STONES;
        var state = _matchRuntimes.GetRequired(MatchingId).SummonStones.AddStones(playerId, refundedStones);
        SendInGameInventoryUpdate(removedItem);
        SendDestroyOrbResult(true, ErrorCode.SUCCESS, request.ItemUid, refundedStones, state);

        _gameEventLogManager.LogOrbBoardTransition(
            MatchingId,
            playerId,
            inventory.GetAllItems(),
            inventory.GetEquippedBattleItem()?.ItemId ?? 0,
            CurrentArea.ToString(),
            "destroy",
            isBot: false);
        Logger.LogInformation(
            "Orb destroyed for summon stones: MatchingId={MatchingId}, PlayerId={PlayerId}, ItemId={ItemId}, ItemUid={ItemUid}, Tier={Tier}, RefundedStones={RefundedStones}",
            MatchingId,
            playerId,
            item.ItemId,
            request.ItemUid,
            tier,
            refundedStones);
        return Task.CompletedTask;
    }
    internal void SendSummonStoneState(int awardedStones = 0, float awardSourceX = 0f, float awardSourceY = 0f)
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
            return;

        var state = GetSummonStoneSnapshot();
        var stateInfo = ToNetworkState(state);
        // NextCost = 성장 카드 최종 비용 (#226 C 잔여): N 기반 기본 + 오브 수 점수 할증,
        // 상한 10 — 클라 Mana 카운터가 이 서버 값을 그대로 표시한다(로컬 계산 퇴역).
        int orbCount = 0;
        foreach (var item in _matchRuntimes.GetRequired(MatchingId).Inventory
                     .GetPlayerInventory(PlayerId.Value).GetAllItems())
        {
            if (item.Count <= 0) continue;
            if (OrbData.TryGetColorAndTier(item.ItemId, out _, out _) ||
                OrbData.TryGetRecoveryTier(item.ItemId, out _))
                orbCount += item.Count;
        }

        stateInfo.NextCost = Config.GetSwarmGrowthCardCost(
            _matchRuntimes.GetRequired(MatchingId).SummonStones.GetGrowthSuccessCount(PlayerId.Value), orbCount);
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = stateInfo,
            AwardedStones = Math.Max(0, awardedStones),
            AwardSourceX = awardSourceX,
            AwardSourceY = awardSourceY
        }));
        TrySend(packet);
    }

    private void SendDestroyOrbResult(bool success, ErrorCode errorCode, long itemUid,
        int refundedStones, SummonStoneSnapshot state)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            ItemUid = itemUid,
            RefundedStones = refundedStones,
            State = ToNetworkState(state)
        }));
        TrySend(packet);
    }
    private void SendSummonOrbResult(bool success, ErrorCode errorCode, int summonedItemId,
        long summonedItemUid, SummonStoneSnapshot state)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            SummonedItemId = summonedItemId,
            SummonedItemUid = summonedItemUid,
            State = ToNetworkState(state)
        }));
        TrySend(packet);
    }

    // 매치가 이미 정리된 경우에도 실패 응답은 보낸다. 조회 때문에 상태를 다시 만들지 않는다.
    private SummonStoneSnapshot GetSummonStoneSnapshot() =>
        PlayerId.HasValue
            ? _matchRuntimes.Get(MatchingId)?.SummonStones.GetSnapshot(PlayerId.Value) ?? SummonStoneManager.EmptySnapshot
            : SummonStoneManager.EmptySnapshot;

    private SummonStoneStateInfo ToNetworkState(SummonStoneSnapshot state) => new()
    {
        StoneCount = state.StoneCount,
        SuccessfulSummonCount = state.SuccessfulSummonCount,
        NextCost = state.NextCost,
        PoolItemIds = _matchRuntimes.Get(MatchingId)?.SummonStones.PoolItemIds.ToList() ?? [],
        // 다음 소환의 2택 후보. 결정론적이라 상태 패킷마다 실어도 대기 상태가 필요 없다.
        NextCandidateItemIds = PlayerId.HasValue && _matchRuntimes.Get(MatchingId) is { IsTerminal: false } match
            ? match.SummonStones.GetSummonCandidates(PlayerId.Value).ToList()
            : []
    };

    internal void GrantSwarmArenaOrb(int itemId)
    {
        if (!PlayerId.HasValue)
            return;

        // 개별 스택 강제 (#226 단계 C 수리): AddItem은 같은 색·티어를 한 항목으로 합쳐
        // 오브별 ItemUid 정체성(열 순번·절단 래치·강화·철갑 대상)을 깨뜨렸다 — 봇 지급
        // 경로(TryAddItemWithCapacity)와 같은 규칙으로 오브 1개 = 항목 1개를 보장한다.
        _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value)
            .TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out _);
        SendInGameInventoryList();
    }
}
