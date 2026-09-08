using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                return Task.CompletedTask;
            }
            if (IsGameplayActionBlocked(out string lockReason))
            {
                Logger.LogWarning("Ignored player state while gameplay is locked: PlayerId={PlayerId}, State={State}, Reason={Reason}", PlayerId, msg.State, lockReason);
                return Task.CompletedTask;
            }

            bool isExploreState = msg.State == PlayerState.EXPLORE_1;
            if (!isExploreState && msg.State != PlayerState.IDLE && _interactions.Count > 0)
            {
                Logger.LogWarning("Ignored state change while RNG collect is pending: PlayerId={PlayerId}, State={State}", PlayerId, msg.State);
                return Task.CompletedTask;
            }

            if (msg.State == PlayerState.SLEEP)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, "PlayerState:SLEEP");
                if (!_condition.IsSleeping && _condition.TryStartSleep(DateTime.UtcNow))
                {
                    BroadcastSleepState(true);
                }
                return Task.CompletedTask;
            }

            if (!isExploreState)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, $"PlayerState:{msg.State}");
            }

            _condition.State = isExploreState ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
            var sameAreaSessions = Match.Sessions.GetInArea(CurrentArea, PlayerId);

            using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, _condition.State);
            foreach (var session in sameAreaSessions)
            {
                session.TrySend(packet);
            }
        }
        return Task.CompletedTask;
    }

    private void OnPeriodicBuffTick()
    {
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            StopAllPeriodicBuffs();
            return;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                StopAllPeriodicBuffs();
                return;
            }

            try
            {
                if (!Connection.IsAcceptingMessages)
                {
                    StopAllPeriodicBuffs();
                    return;
                }
                _condition.TickPeriodicBuffs(Config.MAX_HEALTH,
                    health => HandleHealthChanged(_condition.ChangeHealth(health, Config.MAX_HEALTH)));
                if (!_condition.HasPeriodicBuffs)
                {
                    StopAllPeriodicBuffs();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Periodic buff timer error, stopping all");
                StopAllPeriodicBuffs();
            }
        }
    }

    // #229 6단계 수면 회복 → 2026-08-17 재조정: 준비 1초, 회복은 1초에 한 번.
    // 중단은 이동뿐이다 — 움직이지 않는 한 피격·폐쇄로는 깨지 않는다.

    /// <summary>
    ///     수면 중 본체 HP 회복. 진입 1초 뒤 첫 회복, 이후 1초마다 최대 HP 5%씩.
    ///     중단(이동)은 BreakSwarmSleep이 맡고 여기서는 회복만 센다.
    /// </summary>
    internal void TickSwarmSleepRecovery(DateTime nowUtc)
    {
        int recovered = _condition.GetSleepRecovery(nowUtc, IsEliminated, Config.MAX_HEALTH);
        if (recovered <= 0) return;
        HandleHealthChanged(_condition.Recover(recovered));
        if (!PlayerId.HasValue) return;
        using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new()
        {
            PlayerId = PlayerId.Value,
            AreaType = CurrentArea,
            Amount = recovered,
            Source = HealthRecoveryKind.Sleep
        });
        TrySend(packet);
    }

    /// <summary>
    ///     수면 진입 가능 여부 (#229 6단계) — 가해·피해 뒤 3초는 눕지 못한다.
    ///     절단 치명상(#232) 8초 회복 차단 중에도 눕지 못한다 — 누워도 회복이 없다.
    /// </summary>
    internal bool CanEnterSwarmSleep(DateTime nowUtc) => _condition.CanSleep(nowUtc);

    /// <summary>
    ///     수면 중단 (2026-08-17 재조정): 부르는 곳은 이동뿐이다 — 누워서 도망칠 수 없다.
    ///     피격·폐쇄는 깨우지 않는다. 움직이지 않는 수면은 스스로 깨지 않는다.
    /// </summary>
    internal void BreakSwarmSleep()
    {
        if (!_condition.IsSleeping)
            return;

        BroadcastSleepState(false);
    }

    /// <summary>교전 시각 기록 — 가해·피격 뒤 3초 수면 진입 잠금의 기준. 수면 자체는 깨지 않는다.</summary>
    internal void MarkSwarmCombat(DateTime nowUtc) => _condition.LastCombatAtUtc = nowUtc;

    /// <summary>지정한 시각까지 수면 진입과 수면 회복을 차단한다.</summary>
    internal void BlockHealingUntil(DateTime untilUtc) => _condition.HealLockUntilUtc = untilUtc;

    private void StopAllPeriodicBuffs()
    {
        _condition.ClearPeriodicBuffs();
        _periodicBuffTimer?.Dispose();
        _periodicBuffTimer = null;
    }

    /// <summary>
    ///     SLEEP 상태 변경을 서버에서 감지하여 브로드캐스트 (본인 포함)
    /// </summary>
    private void BroadcastSleepState(bool sleep)
    {
        if (!PlayerId.HasValue) return;

        _condition.State = sleep ? PlayerState.SLEEP : PlayerState.IDLE;
        var state = _condition.State;

        // 같은 Area의 모든 플레이어에게 상태 브로드캐스트 (본인 포함)
        var sameAreaSessions = Match.Sessions.GetInArea(CurrentArea);

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.TrySend(packet);

        Logger.LogInformation(
            "Server-driven SLEEP state={Sleep} for Player {PlayerId}, broadcasted to {Count} players in Area {Area}",
            sleep, PlayerId, sameAreaSessions.Count, CurrentArea);
    }

    /// <summary>
    ///     인게임 인벤토리 전체 목록 전송
    /// </summary>
    internal void SendInGameInventoryList()
    {
        if (!PlayerId.HasValue) return;

        var inventory = Match.Inventory.GetPlayerInventory(PlayerId.Value);
        var items = inventory.GetAllItems();
        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_LIST(items);
        TrySend(packet);

        var equippedItem = inventory.GetEquippedBattleItem();
        using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
            true, equippedItem?.ItemUid ?? 0, ErrorCode.SUCCESS);
        TrySend(equippedPacket);

        Logger.LogDebug("Sent InGameInventory list to PlayerId={PlayerId}, ItemCount={Count}", PlayerId, items.Count);
    }

    /// <summary>
    ///     인게임 인벤토리 업데이트 전송 (아이템 추가/제거 시)
    /// </summary>
    internal void SendInGameInventoryUpdate(InGameItemInfo item)
    {
        if (!PlayerId.HasValue) return;

        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([item]);
        TrySend(packet);

        Logger.LogDebug(
            "Sent InGameInventory update to PlayerId={PlayerId}, ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
            PlayerId, item.ItemUid, item.ItemId, item.Count);
    }

    /// <summary>
    ///     인게임 아이템 사용 요청 처리
    /// </summary>

    private Task HandleUseInGameItem(C_TO_G_USE_INGAME_ITEM msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.INVALID_GAME_STATE);
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal || IsGameplayActionBlocked(out _))
            {
                using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                    ErrorCode.INVALID_GAME_STATE);
                TrySend(failPacket);
                return Task.CompletedTask;
            }

            // 아이템 정보 먼저 조회 (제거 전에 ItemId 확인 필요)
            var inventory = Match.Inventory.GetPlayerInventory(PlayerId.Value);
            var itemInfo = inventory.GetItem(msg.ItemUid);
            if (itemInfo == null)
            {
                using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
                TrySend(failPacket);
                Logger.LogWarning("Player {PlayerId} item not found: ItemUid={ItemUid}", PlayerId, msg.ItemUid);
                return Task.CompletedTask;
            }

            int itemId = itemInfo.ItemId;
            if (msg.Count == 0)
            {
                if (!BattleItemCombatData.IsCombatItem(itemId) ||
                    !inventory.TryEquipBattleItem(msg.ItemUid, out var equippedItem))
                {
                    using var failPacket =
                        PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.ITEM_NOT_USABLE);
                    TrySend(failPacket);
                    return Task.CompletedTask;
                }

                using var resultPacket =
                    PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, equippedItem!.ItemUid, ErrorCode.SUCCESS);
                TrySend(resultPacket);
                _gameEventLogManager.LogOrbBoardTransition(
                    MatchingId, PlayerId.Value, inventory.GetAllItems(), equippedItem.ItemId,
                    CurrentArea.ToString(), "equip", isBot: false);
                Logger.LogInformation(
                    "Player {PlayerId} equipped battle item: ItemUid={ItemUid}, ItemId={ItemId}",
                    PlayerId, equippedItem.ItemUid, equippedItem.ItemId);
                var equippedCombatData = BattleItemCombatData.Get(equippedItem.ItemId);
                _gameEventLogManager.LogTierReached(
                    MatchingId, PlayerId.Value, equippedItem.ItemId, equippedCombatData?.Tier ?? 0, isBot: false);
                return Task.CompletedTask;
            }

            if (msg.Count < 1)
            {
                using var failPacket =
                    PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.INVALID_REQUEST);
                TrySend(failPacket);
                return Task.CompletedTask;
            }

            var itemData = GameItemData.Get(itemId);
            if (itemData?.ConsumableBuffList is not { Count: > 0 })
            {
                using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                    ErrorCode.ITEM_NOT_USABLE);
                TrySend(failPacket);
                return Task.CompletedTask;
            }

            // Reusable 아이템은 소모하지 않음
            if (itemData.Reusable)
            {
                // 버프 효과 적용
                bool hasPeriodicBuff = ApplyItemBuffs(itemId);

                // 주기적 버프 등록 시 SLEEP 상태로 전환 + 브로드캐스트
                if (hasPeriodicBuff) BroadcastSleepState(true);

                // 사용 결과 전송
                using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, msg.ItemUid, ErrorCode.SUCCESS);
                TrySend(resultPacket);

                Logger.LogInformation("Player {PlayerId} used reusable InGameItem: ItemUid={ItemUid}, ItemId={ItemId}",
                    PlayerId, msg.ItemUid, itemId);
            }
            else
            {
                bool success = Match.Inventory.TryRemoveItem(PlayerId.Value, msg.ItemUid,
                    msg.Count, out var updatedItem);

                if (success && updatedItem != null)
                {
                    // 아이템 사용 성공 - 인벤토리 업데이트 전송
                    SendInGameInventoryUpdate(updatedItem);

                    // 버프 효과 적용
                    bool hasPeriodicBuff = ApplyItemBuffs(itemId);

                    // 주기적 버프 등록 시 SLEEP 상태로 전환 + 브로드캐스트
                    if (hasPeriodicBuff) BroadcastSleepState(true);

                    // 사용 결과 전송
                    using var resultPacket =
                        PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, msg.ItemUid, ErrorCode.SUCCESS);
                    TrySend(resultPacket);

                    Logger.LogInformation(
                        "Player {PlayerId} used InGameItem: ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
                        PlayerId, msg.ItemUid, itemId, msg.Count);
                }
                else
                {
                    // 아이템 사용 실패
                    using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
                    TrySend(resultPacket);

                    Logger.LogWarning("Player {PlayerId} failed to use InGameItem: ItemUid={ItemUid}, Count={Count}",
                        PlayerId, msg.ItemUid, msg.Count);
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     아이템 버프 효과 적용. 주기적 버프가 등록되면 true 반환
    /// </summary>
    private bool ApplyItemBuffs(int itemId)
    {
        var effect = _condition.ApplyItemBuffs(itemId);
        if (effect.Periodic)
            _periodicBuffTimer ??= new Timer(_ => OnPeriodicBuffTick(), null, 1000, 1000);
        var change = _condition.ChangeHealth(effect.Health, Config.MAX_HEALTH);
        HandleHealthChanged(change);
        if (PlayerId.HasValue && change.Recovered > 0)
            _gameEventLogManager.LogRecoveryUse(MatchingId, PlayerId.Value, itemId, change.Recovered,
                source: "inventory_consumable", isBot: false);
        return effect.Periodic;
    }

    /// <summary>
    ///     PlayerCondition이 확정한 체력 변경 결과를 전송·기록하고 필요하면 탈락 처리한다.
    ///     상태 변경 직후 같은 매치 잠금 안에서 호출한다. 여기서는 체력을 변경하지 않는다.
    /// </summary>
    internal void HandleHealthChanged(PlayerCondition.HealthChange change, long attackerPlayerId = 0,
        bool isAreaClosureElimination = false, bool isOvertimeElimination = false, bool deferElimination = false)
    {
        // 값이 변경되지 않았으면 패킷 전송 안함
        if (!change.Changed) return;

        Logger.LogInformation(
            "Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})",
            PlayerId, change.Before, change.After, change.RequestedDelta);

        // 효과 표시에는 요청한 변화량을, 상태에는 적용 후 체력을 보낸다.
        SendPlayerStatsUpdate(change);

        // 운영툴 진행 로그
        if (PlayerId.HasValue)
        {
            if (change.Recovered > 0)
                _gameEventLogManager.RecordRecovery(MatchingId, PlayerId.Value, change.Recovered);

            _gameEventLogManager.LogResource(MatchingId, PlayerId.Value,
                change.RequestedDelta, change.After, reason: "", isBot: false);
        }

        if (change.IsDepleted && !deferElimination &&
            PlayerId.HasValue && !IsGameEnded && !IsEliminated && Health <= 0)
        {
            _matchEliminations.Process(MatchingId, PlayerId.Value, EliminationReason.HEALTH_ZERO,
                attackerPlayerId: attackerPlayerId,
                isAreaClosureElimination: isAreaClosureElimination,
                isOvertimeElimination: isOvertimeElimination);
        }
    }

    /// <summary>
    ///     스탯 업데이트 패킷 전송
    /// </summary>
    private void SendPlayerStatsUpdate(PlayerCondition.HealthChange change)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(change.After, change.RequestedDelta);
        TrySend(packet);
    }

    private bool IsGameplayActionBlocked(out string reason)
    {
        reason = string.Empty;

        if (IsEliminated)
        {
            reason = "Eliminated players cannot act";
            return true;
        }

        if (IsGameEnded)
        {
            reason = "Game has already ended";
            return true;
        }

        if (MatchingId > 0 && !MatchStartGate.IsGameplayActive(MatchingId))
        {
            reason = "Waiting for match start";
            return true;
        }

        return false; // 라운드 시스템 퇴역(#246)
    }
}
