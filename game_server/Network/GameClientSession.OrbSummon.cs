using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

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

        if (Config.SWARM_P0_ENABLED)
        {
            // #219 M2 3택 드래프트: 개봉(RNG_COLLECT_FINISH)이 연 드래프트에서만 소환한다.
            // ChoiceIndex = 색 (0 태양, 1 파도, 2 바람). 비용은 개봉 시점에 확정된 값.
            if (!_hasPendingOrbDraft)
            {
                SendSummonOrbResult(false, ErrorCode.INVALID_GAME_STATE, 0, 0,
                    _summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId.Value));
                return Task.CompletedTask;
            }

            // 상자 시간 등급 (#222 M3): 개전 후 80초/160초를 넘기면 같은 색의 T2/T3가 나온다.
            int draftItemId = SurvivorOrbData.ApplyDraftTier(
                request.ChoiceIndex switch
                {
                    1 => DraftWaveOrbItemId,
                    2 => DraftWindOrbItemId,
                    _ => DraftSunOrbItemId
                },
                GetSwarmDraftTier());
            var draftAttempt = ExecuteOrbSummon(
                request.ChoiceIndex, costOverride: _pendingOrbDraftCost, exactItemId: draftItemId);
            if (!draftAttempt.Success)
                return Task.CompletedTask;

            _hasPendingOrbDraft = false;
            // 자동 머지: 드래프트로 같은 색·티어 3개가 되면 즉시 융합한다.
            foreach (var mergedItem in _inGameInventoryManager.AutoMergeSurvivorOrbs(
                         CurrentMapSubId, PlayerId.Value, Random.Shared))
                SendInGameInventoryUpdate(mergedItem);
            return Task.CompletedTask;
        }

        if (IsRoundActionLocked(out _) || IsSurvivorBoardActionLocked())
        {
            SendSummonOrbResult(false, ErrorCode.INVALID_GAME_STATE, 0, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId.Value));
            return Task.CompletedTask;
        }

        ExecuteOrbSummon(request.ChoiceIndex, costOverride: null);
        return Task.CompletedTask;
    }

    /// <summary>상자 시간 등급 (#222 M3): 개전 앵커 경과로 드래프트 티어 결정. 게이트 전엔 T1.</summary>
    private int GetSwarmDraftTier()
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(CurrentMapSubId);
        if (startedAtUtc == null)
            return 1;

        return SurvivorOrbData.GetDraftTierByElapsed((DateTime.UtcNow - startedAtUtc.Value).TotalSeconds);
    }

    /// <summary>
    ///     소환 실행 코어. 버튼 소환과 수호물 오브젝트 개봉이 같은 경로(2택 후보·인벤토리
    ///     추가·결과 패킷·로그)를 쓴다. costOverride는 스웜 P0-c의 보유 오브 비례 비용.
    /// </summary>
    internal SummonOrbAttempt ExecuteOrbSummon(int choiceIndex, int? costOverride, int? exactItemId = null)
    {
        long playerId = PlayerId!.Value;
        var attempt = _summonStoneManager.TrySummon(
            CurrentMapSubId,
            playerId,
            itemId => _inGameInventoryManager.TryAddItemWithCapacity(
                CurrentMapSubId,
                playerId,
                itemId,
                Config.SWARM_P0_ENABLED ? Config.SWARM_ORB_CAPACITY : Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                out var addedItem)
                ? addedItem
                : null,
            choiceIndex,
            costOverride,
            exactItemId);

        if (attempt.Success && attempt.AddedItem != null)
        {
            SendInGameInventoryUpdate(attempt.AddedItem);
            var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, playerId);
            _gameEventLogManager.LogSurvivorOrbBoardTransition(
                CurrentMapSubId,
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
            CurrentMapSubId,
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
            CurrentMapSubId,
            playerId,
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.State.StoneCount,
            attempt.State.NextCost);
        return attempt;
    }

    private Task HandleDestroyOrb(C_TO_G_DESTROY_ORB request)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        long playerId = PlayerId.Value;
        if (IsRoundActionLocked(out _) || IsSurvivorBoardActionLocked())
        {
            SendDestroyOrbResult(false, ErrorCode.INVALID_GAME_STATE, request.ItemUid, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, playerId));
            return Task.CompletedTask;
        }

        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, playerId);
        var item = inventory.GetItem(request.ItemUid);
        if (item == null || item.Count <= 0)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_FOUND, request.ItemUid, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, playerId));
            return Task.CompletedTask;
        }

        int tier;
        bool isDestroyableOrb =
            SurvivorOrbData.TryGetColorAndTier(item.ItemId, out _, out tier) ||
            SurvivorOrbData.TryGetRecoveryTier(item.ItemId, out tier);
        if (!isDestroyableOrb)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_USABLE, request.ItemUid, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, playerId));
            return Task.CompletedTask;
        }

        if (!_inGameInventoryManager.TryRemoveItem(
                CurrentMapSubId, playerId, request.ItemUid, 1, out var removedItem) ||
            removedItem == null)
        {
            SendDestroyOrbResult(false, ErrorCode.ITEM_NOT_OWNED, request.ItemUid, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, playerId));
            return Task.CompletedTask;
        }

        int refundedStones = Math.Clamp(tier, 1, 3);
        var state = _summonStoneManager.AddStones(CurrentMapSubId, playerId, refundedStones);
        SendInGameInventoryUpdate(removedItem);
        SendDestroyOrbResult(true, ErrorCode.SUCCESS, request.ItemUid, refundedStones, state);

        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            CurrentMapSubId,
            playerId,
            inventory.GetAllItems(),
            inventory.GetEquippedBattleItem()?.ItemId ?? 0,
            CurrentArea.ToString(),
            "destroy",
            isBot: false);
        Logger.LogInformation(
            "Orb destroyed for summon stones: MatchingId={MatchingId}, PlayerId={PlayerId}, ItemId={ItemId}, ItemUid={ItemUid}, Tier={Tier}, RefundedStones={RefundedStones}",
            CurrentMapSubId,
            playerId,
            item.ItemId,
            request.ItemUid,
            tier,
            refundedStones);
        return Task.CompletedTask;
    }
    internal void SendSummonStoneState(int awardedStones = 0, float awardSourceX = 0f, float awardSourceY = 0f)
    {
        if (!PlayerId.HasValue || CurrentMapSubId <= 0)
            return;

        var state = _summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId.Value);
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = ToNetworkState(state),
            AwardedStones = Math.Max(0, awardedStones),
            AwardSourceX = awardSourceX,
            AwardSourceY = awardSourceY
        }));
        Send(packet);
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
        Send(packet);
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
        Send(packet);
    }

    private SummonStoneStateInfo ToNetworkState(SummonStoneSnapshot state) => new()
    {
        StoneCount = state.StoneCount,
        SuccessfulSummonCount = state.SuccessfulSummonCount,
        NextCost = state.NextCost,
        PoolItemIds = _summonStoneManager.PoolItemIds.ToList(),
        // 다음 소환의 2택 후보. 결정론적이라 상태 패킷마다 실어도 대기 상태가 필요 없다.
        NextCandidateItemIds = PlayerId.HasValue && CurrentMapSubId > 0
            ? _summonStoneManager.GetSummonCandidates(CurrentMapSubId, PlayerId.Value).ToList()
            : []
    };
}
