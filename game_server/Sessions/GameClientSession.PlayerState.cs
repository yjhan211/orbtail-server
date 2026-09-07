using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     플레이어 상태 요청과 회복 결과의 패킷·로그·탈락 통지를 처리한다.
///     자원·수면·버프 계산은 PlayerCondition가 담당한다.
/// </summary>
public partial class GameClientSession
{

    private async Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue) return;
        await RunWithMatchLock(() => ProcessPlayerState(msg), () => { });
    }

    private async Task ProcessPlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue) return;
        if (IsRoundActionLocked(out string lockReason))
        {
            // EXPLORE_1 is a collection-side state sync. The following collection ACK reports
            // the actionable result, so do not surface a second generic alert to the player.
            Logger.LogDebug("Ignored player state while gameplay is locked: PlayerId={PlayerId}, State={State}, Reason={Reason}",
                PlayerId, msg.State, lockReason);
            return;
        }

        Logger.LogInformation("Player {PlayerId} state change request: {State}", PlayerId, msg.State);

        bool isExploreState = msg.State == PlayerState.EXPLORE_1;
        if (!isExploreState &&
            msg.State != PlayerState.IDLE &&
            _interactions.Count > 0)
        {
            Logger.LogDebug(
                "Ignored state change while RNG collect is pending: PlayerId={PlayerId}, State={State}",
                PlayerId, msg.State);
            return;
        }

        if (msg.State == PlayerState.SLEEP)
        {
            CancelPendingRngCollect("PlayerState:SLEEP");
            await HandleRestStateRequest();
            return;
        }

        if (!isExploreState)
            CancelPendingRngCollect($"PlayerState:{msg.State}");

        CurrentState = isExploreState ? PlayerState.EXPLORE_1 : PlayerState.IDLE;

        // 같은 Area의 다른 플레이어들에게 상태 브로드캐스트
        var allSessions = _getSessionsByMatch(MatchingId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea);

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, msg.State);
        foreach (var session in sameAreaSessions) session.TrySend(packet);

        Logger.LogDebug("Broadcasted PLAYER_STATE to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);

        // SLEEP 상태 추적
        _condition.IsSleeping = msg.State == PlayerState.SLEEP;

        // SLEEP 해제 시 주기적 버프 타이머 정리
        if (!_condition.IsSleeping) StopAllPeriodicBuffs();
    }

    private async Task HandleRestStateRequest()
    {
        if (_condition.IsSleeping) return;

        // #229 6단계: 스웜 수면은 스태미나 0 휴식이 아니라 본체 HP 회복 행동이다.
        // 조건은 하나 — 가해·피해 뒤 3초가 지났는가. 회복량 정산은 아레나 틱이 센다.
        // 교전 직후에는 조용히 무시한다 (#229): 실패 팝업을 띄우면 전투 중에 수면 버튼을
        // 잘못 누를 때마다 "유효하지 않은 게임 상태" 창이 화면을 막는다. 버튼이 이미 클릭
        // 피드백을 줬으므로 아무 일도 안 일어나는 것 자체가 답이다.
        if (!CanEnterSwarmSleep(DateTime.UtcNow))
            return;

        _condition.ResetSleep();
        await BroadcastSleepState(true);
        return;
    }

    private void OnPeriodicBuffTick()
    {
        _ = RunWithMatchLock(() =>
        {
            try
            {
                if (!Connection.IsAcceptingMessages)
                {
                    StopAllPeriodicBuffs();
                    return Task.CompletedTask;
                }
                _condition.TickPeriodicBuffs(Config.MAX_HEALTH, health => ModifyStats(health));
                if (!_condition.HasPeriodicBuffs)
                {
                    if (_condition.IsSleeping) _ = BroadcastSleepState(false);
                    else StopAllPeriodicBuffs();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Periodic buff timer error, stopping all");
                StopAllPeriodicBuffs();
            }
            return Task.CompletedTask;
        }, StopAllPeriodicBuffs);
    }

    // #229 6단계 수면 회복 → 2026-08-17 재조정: 준비 1초, 회복은 1초에 한 번.
    // 중단은 이동뿐이다 — 움직이지 않는 한 피격·폐쇄로는 깨지 않는다.

    private const int SwarmSleepRecoveryEventType = 28;

    /// <summary>
    ///     수면 중 본체 HP 회복. 진입 1초 뒤 첫 회복, 이후 1초마다 최대 HP 5%씩.
    ///     중단(이동)은 BreakSwarmSleep이 맡고 여기서는 회복만 센다.
    /// </summary>
    internal void TickSwarmSleepRecovery(DateTime nowUtc)
    {
        int recovered = _condition.GetSleepRecovery(nowUtc, IsEliminated, Config.MAX_HEALTH);
        if (recovered <= 0) return;
        ModifyStats(healthDelta: recovered);
        SendEncounterEvent(PlayerId ?? 0, CurrentArea, SwarmSleepRecoveryEventType, 0, 0, recovered);
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

        _condition.ResetSleep();
        _ = BroadcastSleepState(false);
    }

    /// <summary>교전 시각 기록 — 가해·피격 뒤 3초 수면 진입 잠금의 기준. 수면 자체는 깨지 않는다.</summary>
    internal void MarkSwarmCombat(DateTime nowUtc) => SwarmLastCombatAtUtc = nowUtc;

    private void StopAllPeriodicBuffs()
    {
        _condition.ClearPeriodicBuffs();
        _periodicBuffTimer?.Dispose();
        _periodicBuffTimer = null;
    }

    /// <summary>
    ///     SLEEP 상태 변경을 서버에서 감지하여 브로드캐스트 (본인 포함)
    /// </summary>
    private async Task BroadcastSleepState(bool sleep)
    {
        if (!PlayerId.HasValue) return;

        _condition.IsSleeping = sleep;
        if (!sleep) StopAllPeriodicBuffs();

        var state = sleep ? PlayerState.SLEEP : PlayerState.IDLE;

        // 같은 Area의 모든 플레이어에게 상태 브로드캐스트 (본인 포함)
        var allSessions = _getSessionsByMatch(MatchingId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);

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
    private async Task HandleUseInGameItem(C_TO_G_USE_INGAME_ITEM msg)
    {
        if (!PlayerId.HasValue) return;
        await RunWithMatchLock(() => ProcessUseInGameItem(msg), () =>
        {
            using var packet = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.INVALID_GAME_STATE);
            TrySend(packet);
        });
    }

    private async Task ProcessUseInGameItem(C_TO_G_USE_INGAME_ITEM msg)
    {
        if (!PlayerId.HasValue) return;
        if (IsRoundActionLocked(out _))
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                ErrorCode.INVALID_GAME_STATE);
            TrySend(failPacket);
            return;
        }

        // 아이템 정보 먼저 조회 (제거 전에 ItemId 확인 필요)
        var inventory = Match.Inventory.GetPlayerInventory(PlayerId.Value);
        var itemInfo = inventory.GetItem(msg.ItemUid);
        if (itemInfo == null)
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
            TrySend(failPacket);
            Logger.LogWarning("Player {PlayerId} item not found: ItemUid={ItemUid}", PlayerId, msg.ItemUid);
            return;
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
                return;
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
            return;
        }

        if (msg.Count < 1)
        {
            using var failPacket =
                PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.INVALID_REQUEST);
            TrySend(failPacket);
            return;
        }

        var itemData = GameItemData.Get(itemId);
        if (itemData?.ConsumableBuffList is not { Count: > 0 })
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                ErrorCode.ITEM_NOT_USABLE);
            TrySend(failPacket);
            return;
        }

        // Reusable 아이템은 소모하지 않음
        if (itemData.Reusable)
        {
            // 버프 효과 적용
            bool hasPeriodicBuff = ApplyItemBuffs(itemId);

            // 주기적 버프 등록 시 SLEEP 상태로 전환 + 브로드캐스트
            if (hasPeriodicBuff) await BroadcastSleepState(true);

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
                if (hasPeriodicBuff) await BroadcastSleepState(true);

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

    /// <summary>
    ///     아이템 버프 효과 적용. 주기적 버프가 등록되면 true 반환
    /// </summary>
    private bool ApplyItemBuffs(int itemId)
    {
        var effect = _condition.ApplyItemBuffs(itemId);
        if (effect.Periodic)
            _periodicBuffTimer ??= new Timer(_ => OnPeriodicBuffTick(), null, 1000, 1000);
        int before = Health;
        if (effect.Health != 0) ModifyStats(effect.Health);
        int recovered = Math.Max(0, Health - before);
        if (PlayerId.HasValue && recovered > 0)
            _gameEventLogManager.LogRecoveryUse(MatchingId, PlayerId.Value, itemId, recovered,
                source: "inventory_consumable", isBot: false);
        return effect.Periodic;
    }

    /// <summary>
    ///     체력을 변경하고 결과를 전송한다. 체력이 0이면 탈락 처리한다.
    /// </summary>
    public void ModifyStats(int healthDelta = 0, long attackerPlayerId = 0,
        bool isAreaClosureElimination = false, bool isOvertimeElimination = false, bool deferElimination = false)
    {
        int oldHealth = Health;
        _condition.ChangeHealth(healthDelta, Config.MAX_HEALTH);

        // 값이 변경되지 않았으면 패킷 전송 안함
        if (Health == oldHealth) return;

        Logger.LogInformation(
            "Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})",
            PlayerId, oldHealth, Health, healthDelta);

        // 효과 표시에는 요청한 변화량을, 상태에는 적용 후 체력을 보낸다.
        SendPlayerStatsUpdate(healthDelta);

        // 운영툴 진행 로그
        if (PlayerId.HasValue)
        {
            int recoveredHealth = Math.Max(0, Health - oldHealth);
            if (recoveredHealth > 0)
                _gameEventLogManager.RecordRecovery(MatchingId, PlayerId.Value, recoveredHealth);

            _gameEventLogManager.LogResource(MatchingId, PlayerId.Value,
                healthDelta, Health, reason: "", isBot: false);
        }

        if (!deferElimination)
            CheckResourceElimination(attackerPlayerId, isAreaClosureElimination, isOvertimeElimination);
    }

    /// <summary>
    ///     스탯 업데이트 패킷 전송
    /// </summary>
    private void SendPlayerStatsUpdate(int healthDelta)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Health, healthDelta);
        TrySend(packet);
    }

    /// <summary>
    ///     Area 퇴장 불가 알림 전송 (위치 보정 포함)
    /// </summary>
    private void SendAreaExitBlocked(AreaType areaType, Cell correctedCell)
    {
        using var packet = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(areaType, correctedCell);
        TrySend(packet);
        Logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
            PlayerId, areaType, correctedCell.X, correctedCell.Y);
    }


    private bool IsRoundActionLocked(out string reason)
    {
        reason = string.Empty;

        if (IsEliminated)
        {
            reason = "Eliminated players cannot act";
            return true;
        }

        if (Volatile.Read(ref _isGameEnded))
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
