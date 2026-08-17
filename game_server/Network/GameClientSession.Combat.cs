using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private const int CatPillowItemId = 401000003;
    private const int ChalkPowderItemId = 201000015;
    private const int ShortChalkItemId = 201000016;
    private const int LongChalkItemId = 201000017;
    private static readonly int[] RoomEncounterAttackItemIds =
    {
        LongChalkItemId,
        ChalkPowderItemId,
        ShortChalkItemId
    };
    private const int CatPillowRestDurationSeconds = 15;
    private DateTime _lastRoomEncounterItemUseUtc = DateTime.MinValue;

    private Task HandleAttack(C_TO_G_ATTACK msg)
    {
        if (!PlayerId.HasValue || msg == null)
            return Task.CompletedTask;

        if (Config.PROXIMITY_AUTO_COMBAT_P0_ENABLED)
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Manual attack is disabled during proximity auto combat P0");
            return Task.CompletedTask;
        }

        if (IsEliminated)
        {
            SendErrorResponse(ErrorCode.PLAYER_DEAD, "Eliminated players cannot attack");
            SendRoomEncounterItemUseResult(false, ErrorCode.PLAYER_DEAD, ChalkPowderItemId, 0);
            return Task.CompletedTask;
        }

        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            SendRoomEncounterItemUseResult(false, ErrorCode.INVALID_GAME_STATE, ChalkPowderItemId, 0);
            return Task.CompletedTask;
        }

        if (!long.TryParse(msg.TargetId, out long targetPlayerId) ||
            targetPlayerId == 0 ||
            targetPlayerId == PlayerId.Value)
        {
            SendErrorResponse(ErrorCode.INVALID_REQUEST, "Invalid attack target");
            SendRoomEncounterItemUseResult(false, ErrorCode.INVALID_REQUEST, ChalkPowderItemId, 0);
            return Task.CompletedTask;
        }

        var area = CurrentArea;
        if (CurrentMapSubId <= 0 || area == AreaType.None)
        {
            SendErrorResponse(ErrorCode.INVALID_AREA, "Invalid encounter area");
            SendRoomEncounterItemUseResult(false, ErrorCode.INVALID_AREA, ChalkPowderItemId, targetPlayerId);
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        if (now - _lastRoomEncounterItemUseUtc < RoomEncounterActionSuppressDuration)
        {
            SendErrorResponse(ErrorCode.ACTION_COOLDOWN, "Room encounter item use is in cooldown");
            SendRoomEncounterItemUseResult(false, ErrorCode.ACTION_COOLDOWN, ChalkPowderItemId, targetPlayerId);
            Logger.LogWarning(
                "Player {PlayerId} failed room encounter item use: Target={Target}, Matching={MatchingId}, Area={Area}, Error={Error}",
                PlayerId,
                targetPlayerId,
                CurrentMapSubId,
                area,
                ErrorCode.ACTION_COOLDOWN);
            return Task.CompletedTask;
        }

        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        int attackItemId = ResolveRoomEncounterAttackItemId(inventory);
        if (attackItemId == 0)
        {
            SendErrorResponse(ErrorCode.INSUFFICIENT_ITEM, "Encounter attack item is required");
            SendRoomEncounterItemUseResult(false, ErrorCode.INSUFFICIENT_ITEM, ChalkPowderItemId, targetPlayerId);
            Logger.LogWarning(
                "Player {PlayerId} failed room encounter item use: Target={Target}, Matching={MatchingId}, Area={Area}, Error={Error}",
                PlayerId,
                targetPlayerId,
                CurrentMapSubId,
                area,
                ErrorCode.INSUFFICIENT_ITEM);
            return Task.CompletedTask;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(session =>
            session.PlayerId == targetPlayerId &&
            !session.IsEliminated &&
            session.CurrentMapSubId == CurrentMapSubId &&
            session.CurrentArea == area);
        var targetBot = targetSession == null
            ? _botPlayerManager.GetBot(CurrentMapSubId, targetPlayerId)
            : null;

        if (targetSession == null &&
            (targetBot is not { IsEliminated: false } || targetBot.CurrentArea != area))
        {
            SendErrorResponse(ErrorCode.AREA_MISMATCH, "Room encounter target is not in the same area");
            SendRoomEncounterItemUseResult(false, ErrorCode.AREA_MISMATCH, ChalkPowderItemId, targetPlayerId);
            Logger.LogWarning(
                "Player {PlayerId} failed room encounter item use: Target={Target}, Matching={MatchingId}, Area={Area}, Error={Error}",
                PlayerId,
                targetPlayerId,
                CurrentMapSubId,
                area,
                ErrorCode.AREA_MISMATCH);
            return Task.CompletedTask;
        }

        if (!_inGameInventoryManager.TryRemoveOneByItemId(CurrentMapSubId, PlayerId.Value,
                attackItemId, out var updatedItem))
        {
            SendErrorResponse(ErrorCode.INSUFFICIENT_ITEM, "Encounter attack item is required");
            SendRoomEncounterItemUseResult(false, ErrorCode.INSUFFICIENT_ITEM, attackItemId, targetPlayerId);
            Logger.LogWarning(
                "Player {PlayerId} failed room encounter item use: Target={Target}, Matching={MatchingId}, Area={Area}, Error={Error}",
                PlayerId,
                targetPlayerId,
                CurrentMapSubId,
                area,
                ErrorCode.INSUFFICIENT_ITEM);
            return Task.CompletedTask;
        }

        if (updatedItem != null)
            SendInGameInventoryUpdate(updatedItem);

        _lastRoomEncounterItemUseUtc = now;

        if (targetSession != null)
        {
            targetSession.ApplyItemBuffs(attackItemId);
            targetSession.SendEncounterEvent(PlayerId.Value, area, EncounterRevealManager.RoomEncounterChalkHitEventType,
                EncounterRevealManager.PairCooldownSeconds);
        }
        else
        {
            ApplyItemBuffsToRoomEncounterBot(targetBot, attackItemId);
        }

        SendRoomEncounterItemUseResult(true, ErrorCode.SUCCESS, attackItemId, targetPlayerId);

        SuppressRoomEncounterBriefly();

        Logger.LogInformation(
            "Player {PlayerId} used room encounter item immediately: Target={Target}, Matching={MatchingId}, Area={Area}, ItemId={ItemId}",
            PlayerId,
            targetPlayerId,
            CurrentMapSubId,
            area,
            attackItemId);

        return Task.CompletedTask;
    }

    private void SendRoomEncounterItemUseResult(bool success, ErrorCode errorCode, int itemId, long targetPlayerId)
    {
        using var resultPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(
            success, errorCode, itemId, targetPlayerId);
        Send(resultPacket);
    }

    private static int ResolveRoomEncounterAttackItemId(PlayerInGameInventory inventory)
    {
        foreach (int itemId in RoomEncounterAttackItemIds)
        {
            if (inventory.GetItemCount(itemId) > 0)
                return itemId;
        }

        return 0;
    }

    internal void ApplyRoomEncounterChalkHitFrom(long sourcePlayerId, AreaType area, int itemId)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        ApplyItemBuffs(itemId);
        SendEncounterEvent(sourcePlayerId, area, EncounterRevealManager.RoomEncounterChalkHitEventType,
            EncounterRevealManager.PairCooldownSeconds);
    }

    private Task HandleInteract(C_TO_G_INTERACT msg)
    {
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return Task.CompletedTask;
        }

        // 스태미나 0 이하이면 상호작용 차단
        if (Stamina <= 0)
        {
            Logger.LogWarning("Player {PlayerId} cannot interact: Stamina={Stamina}", PlayerId, Stamina);
            SendErrorResponse(ErrorCode.INSUFFICIENT_STAMINA, "스태미나 부족");
            return Task.CompletedTask;
        }

        // 미구현 — 클라이언트에 에러 응답
        SendErrorResponse(ErrorCode.NOT_IMPLEMENTED, "상호작용 기능 미구현");
        return Task.CompletedTask;
    }

    #region 플레이어 상태

    private async Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue) return;
        if (IsRoundActionLocked(out _, out string lockReason))
        {
            // EXPLORE_1 is a collection-side state sync. The following collection ACK reports
            // the actionable result, so do not surface a second generic alert to the player.
            Logger.LogDebug("Ignored player state while gameplay is locked: PlayerId={PlayerId}, State={State}, Reason={Reason}",
                PlayerId, msg.State, lockReason);
            return;
        }

        Logger.LogInformation("Player {PlayerId} state change request: {State}", PlayerId, msg.State);

        bool isExploreState = msg.State == global::network.common.PlayerState.EXPLORE_1;
        if (!isExploreState &&
            msg.State != global::network.common.PlayerState.IDLE &&
            _pendingFinish.Count > 0)
        {
            Logger.LogDebug(
                "Ignored state change while RNG collect is pending: PlayerId={PlayerId}, State={State}",
                PlayerId, msg.State);
            return;
        }

        if (msg.State == global::network.common.PlayerState.SLEEP)
        {
            CancelPendingRngCollect("PlayerState:SLEEP");
            await HandleRestStateRequest();
            return;
        }

        // 서버 측 상태 저장
        await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
        if (!isExploreState)
            CancelPendingRngCollect($"PlayerState:{msg.State}");

        CurrentState = isExploreState
            ? PlayerState.Exploring
            : PlayerState.Idle;
        _exploreMoveGraceUntil = CurrentState == PlayerState.Exploring
            ? DateTime.UtcNow + ExploreMoveGracePeriod
            : DateTime.MinValue;


        // 같은 Area의 다른 플레이어들에게 상태 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea);

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, msg.State);
        foreach (var session in sameAreaSessions) session.Send(packet);

        Logger.LogDebug("Broadcasted PLAYER_STATE to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);

        // SLEEP 상태 추적
        _isSleeping = msg.State == global::network.common.PlayerState.SLEEP;

        // SLEEP 해제 시 주기적 버프 타이머 정리
        if (!_isSleeping) StopAllPeriodicBuffs();
    }

    private async Task HandleRestStateRequest()
    {
        if (_isSleeping) return;

        // #229 6단계: 스웜 수면은 스태미나 0 휴식이 아니라 본체 HP 회복 행동이다.
        // 조건은 하나 — 가해·피해 뒤 3초가 지났는가. 회복량 정산은 아레나 틱이 센다.
        if (Config.SWARM_P0_ENABLED)
        {
            // 교전 직후에는 조용히 무시한다 (#229): 실패 팝업을 띄우면 전투 중에 수면 버튼을
            // 잘못 누를 때마다 "유효하지 않은 게임 상태" 창이 화면을 막는다. 버튼이 이미 클릭
            // 피드백을 줬으므로 아무 일도 안 일어나는 것 자체가 답이다.
            if (!CanEnterSwarmSleep(DateTime.UtcNow))
                return;

            SwarmSleepStartedAtUtc = DateTime.MinValue;
            _swarmSleepGrantedTicks = 0;
            await BroadcastSleepState(true);
            return;
        }

        if (Stamina > 0)
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Rest is only available at zero stamina");
            return;
        }

        bool hasPeriodicBuff = ApplyItemBuffs(CatPillowItemId);
        if (!hasPeriodicBuff)
        {
            SendErrorResponse(ErrorCode.FATAL, "Rest buff data is missing");
            Logger.LogWarning("Player {PlayerId} failed to rest: rest buff data missing", PlayerId);
            return;
        }

        await BroadcastSleepState(true);
        Logger.LogInformation("Player {PlayerId} started zero-stamina rest", PlayerId);
    }

    private void AddPeriodicBuff(BuffSubType subType, int value, int intervalSeconds, int durationSeconds = 0)
    {
        _activePeriodicBuffs.RemoveAll(buff => buff.SubType == subType);
        _activePeriodicBuffs.Add(new PeriodicBuffEntry
        {
            SubType = subType,
            Value = value,
            IntervalSeconds = intervalSeconds,
            DurationSeconds = durationSeconds,
            RemainingSeconds = durationSeconds
        });

        // 마스터 타이머가 없으면 시작 (1초 틱)
        _periodicBuffTimer ??= new Timer(_ => OnPeriodicBuffTick(), null, 1000, 1000);
    }

    private void OnPeriodicBuffTick()
    {
        try
        {
            foreach (var buff in _activePeriodicBuffs.ToList())
            {
                buff.ElapsedSeconds += 1;
                if (buff.DurationSeconds > 0 && buff.RemainingSeconds > 0)
                    buff.RemainingSeconds -= 1;

                if (buff.ElapsedSeconds >= buff.IntervalSeconds)
                {
                    buff.ElapsedSeconds = 0;
                    switch (buff.SubType)
                    {
                        case BuffSubType.CONDITION_ADD:
                            if (Stamina < MaxStamina)
                                ModifyStats(buff.Value);
                            else if (buff.DurationSeconds <= 0)
                                _activePeriodicBuffs.Remove(buff);
                            break;
                        case BuffSubType.CORRUPTION_DOWN:
                            if (Corruption > 0)
                                ModifyStats(corruptionDelta: -buff.Value);
                            else if (buff.DurationSeconds <= 0)
                                _activePeriodicBuffs.Remove(buff);
                            break;
                        case BuffSubType.CORRUPTION_ADD:
                            if (Corruption < MaxCorruption)
                                ModifyStats(corruptionDelta: buff.Value);
                            else if (buff.DurationSeconds <= 0)
                                _activePeriodicBuffs.Remove(buff);
                            break;
                    }
                }

                if (buff.DurationSeconds > 0 && buff.RemainingSeconds <= 0)
                    _activePeriodicBuffs.Remove(buff);
            }

            if (_activePeriodicBuffs.Count == 0)
            {
                if (_isSleeping)
                    _ = BroadcastSleepState(false);
                else
                    StopAllPeriodicBuffs();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Periodic buff timer error, stopping all");
            StopAllPeriodicBuffs();
        }
    }

    // #229 6단계 수면 회복 → 2026-08-17 재조정: 준비 1초, 회복은 1초에 한 번.
    // 중단은 이동뿐이다 — 움직이지 않는 한 피격·폐쇄로는 깨지 않는다.
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private const int SwarmSleepRecoveryEventType = 28;

    /// <summary>
    ///     수면 중 본체 HP 회복. 진입 1초 뒤 첫 회복, 이후 1초마다 최대 HP 5%씩.
    ///     중단(이동)은 BreakSwarmSleep이 맡고 여기서는 회복만 센다.
    /// </summary>
    internal void TickSwarmSleepRecovery(DateTime nowUtc)
    {
        if (!Config.SWARM_P0_ENABLED || IsEliminated || !_isSleeping)
        {
            SwarmSleepStartedAtUtc = DateTime.MinValue;
            _swarmSleepGrantedTicks = 0;
            return;
        }

        if (SwarmSleepStartedAtUtc == DateTime.MinValue)
        {
            SwarmSleepStartedAtUtc = nowUtc;
            _swarmSleepGrantedTicks = 0;
            return;
        }

        // 준비 1초: 눌렀다 떼는 것만으로 피해를 무시할 수 없어야 한다.
        double asleepSeconds = (nowUtc - SwarmSleepStartedAtUtc).TotalSeconds;
        if (asleepSeconds < SwarmSleepWarmupSeconds)
            return;

        // 1초에 한 번 — 준비가 끝나는 순간이 첫 회복이다. 아레나 틱이 밀렸으면 한 번에 정산한다.
        int dueTicks = (int)Math.Floor(asleepSeconds - SwarmSleepWarmupSeconds) + 1;
        int pendingTicks = dueTicks - _swarmSleepGrantedTicks;
        if (pendingTicks <= 0)
            return;

        _swarmSleepGrantedTicks = dueTicks;
        if (Corruption <= 0)
            return;

        int perTick = Math.Max(1, (int)MathF.Round(MaxCorruption * SwarmSleepRecoveryRatioPerSecond));
        int recovered = Math.Min(perTick * pendingTicks, Corruption);
        ModifyStats(corruptionDelta: -recovered);
        // 회복량은 본인과 같은 구역 사람 모두 읽는다 — 수면은 남에게 보이는 표적이어야 한다.
        SendEncounterEvent(PlayerId ?? 0, CurrentArea, SwarmSleepRecoveryEventType, 0, 0, recovered);
    }

    /// <summary>수면 진입 가능 여부 (#229 6단계) — 가해·피해 뒤 3초는 눕지 못한다.</summary>
    internal bool CanEnterSwarmSleep(DateTime nowUtc) =>
        !Config.SWARM_P0_ENABLED ||
        (nowUtc - SwarmLastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds;

    /// <summary>
    ///     수면 중단 (2026-08-17 재조정): 부르는 곳은 이동뿐이다 — 누워서 도망칠 수 없다.
    ///     피격·폐쇄는 깨우지 않는다. 움직이지 않는 수면은 스스로 깨지 않는다.
    /// </summary>
    internal void BreakSwarmSleep()
    {
        if (!_isSleeping)
            return;

        SwarmSleepStartedAtUtc = DateTime.MinValue;
        _swarmSleepGrantedTicks = 0;
        _ = BroadcastSleepState(false);
    }

    /// <summary>교전 시각 기록 — 가해·피격 뒤 3초 수면 진입 잠금의 기준. 수면 자체는 깨지 않는다.</summary>
    internal void MarkSwarmCombat(DateTime nowUtc) => SwarmLastCombatAtUtc = nowUtc;

    private void StopAllPeriodicBuffs()
    {
        _activePeriodicBuffs.Clear();
        _periodicBuffTimer?.Dispose();
        _periodicBuffTimer = null;
    }

    /// <summary>
    ///     SLEEP 상태 변경을 서버에서 감지하여 브로드캐스트 (본인 포함)
    /// </summary>
    private async Task BroadcastSleepState(bool sleep)
    {
        if (!PlayerId.HasValue) return;

        _isSleeping = sleep;
        if (!sleep) StopAllPeriodicBuffs();

        var state = sleep ? global::network.common.PlayerState.SLEEP : global::network.common.PlayerState.IDLE;

        // 서버 측 상태 저장
        await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);

        // 같은 Area의 모든 플레이어에게 상태 브로드캐스트 (본인 포함)
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.Send(packet);

        Logger.LogInformation(
            "Server-driven SLEEP state={Sleep} for Player {PlayerId}, broadcasted to {Count} players in Area {Area}",
            sleep, PlayerId, sameAreaSessions.Count, CurrentArea);
    }

    #endregion

    #region 인게임 인벤토리

    /// <summary>
    ///     인게임 인벤토리 전체 목록 전송
    /// </summary>
    internal void SendInGameInventoryList()
    {
        if (!PlayerId.HasValue) return;

        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        var items = inventory.GetAllItems();
        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_LIST(items);
        Send(packet);

        var equippedItem = inventory.GetEquippedBattleItem();
        using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
            true, equippedItem?.ItemUid ?? 0, ErrorCode.SUCCESS);
        Send(equippedPacket);

        Logger.LogDebug("Sent InGameInventory list to PlayerId={PlayerId}, ItemCount={Count}", PlayerId, items.Count);
    }

    /// <summary>
    ///     인게임 인벤토리 업데이트 전송 (아이템 추가/제거 시)
    /// </summary>
    internal void SendInGameInventoryUpdate(InGameItemInfo item)
    {
        if (!PlayerId.HasValue) return;

        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([item]);
        Send(packet);

        Logger.LogDebug(
            "Sent InGameInventory update to PlayerId={PlayerId}, ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
            PlayerId, item.ItemUid, item.ItemId, item.Count);
    }

    /// <summary>
    ///     인게임 아이템 추가 (탐색 보상 등)
    /// </summary>
    private void AddInGameItem(int itemId, int count = 1)
    {
        if (!PlayerId.HasValue) return;

        var item = _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, itemId, count);
        SendInGameInventoryUpdate(item);
    }

    /// <summary>
    ///     인게임 아이템 사용 요청 처리
    /// </summary>
    private async Task HandleUseInGameItem(C_TO_G_USE_INGAME_ITEM msg)
    {
        if (!PlayerId.HasValue) return;
        if (IsRoundActionLocked(out _))
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                ErrorCode.INVALID_GAME_STATE);
            Send(failPacket);
            return;
        }

        // 아이템 정보 먼저 조회 (제거 전에 ItemId 확인 필요)
        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        var itemInfo = inventory.GetItem(msg.ItemUid);
        if (itemInfo == null)
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
            Send(failPacket);
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
                Send(failPacket);
                return;
            }

            using var resultPacket =
                PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, equippedItem!.ItemUid, ErrorCode.SUCCESS);
            Send(resultPacket);
            _gameEventLogManager.LogSurvivorOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, inventory.GetAllItems(), equippedItem.ItemId,
                CurrentArea.ToString(), "equip", isBot: false);
            Logger.LogInformation(
                "Player {PlayerId} equipped battle item: ItemUid={ItemUid}, ItemId={ItemId}",
                PlayerId, equippedItem.ItemUid, equippedItem.ItemId);
            var equippedCombatData = BattleItemCombatData.Get(equippedItem.ItemId);
            _gameEventLogManager.LogSurvivorTierReached(
                CurrentMapSubId, PlayerId.Value, equippedItem.ItemId, equippedCombatData?.Tier ?? 0, isBot: false);
            return;
        }

        if (msg.Count < 1)
        {
            using var failPacket =
                PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.INVALID_REQUEST);
            Send(failPacket);
            return;
        }

        var itemData = GameItemData.Get(itemId);
        if (System.Array.IndexOf(RoomEncounterAttackItemIds, itemId) >= 0)
        {
            using var failPacket =
                PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.ITEM_NOT_USABLE);
            Send(failPacket);
            Logger.LogWarning("Player {PlayerId} tried to use attack-only item outside encounter: ItemUid={ItemUid}, ItemId={ItemId}",
                PlayerId, msg.ItemUid, itemId);
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
            Send(resultPacket);

            Logger.LogInformation("Player {PlayerId} used reusable InGameItem: ItemUid={ItemUid}, ItemId={ItemId}",
                PlayerId, msg.ItemUid, itemId);
        }
        else
        {
            bool success = _inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid,
                msg.Count, out var updatedItem);

            if (success && updatedItem != null)
            {
                // 아이템 사용 성공 - 인벤토리 업데이트 전송
                SendInGameInventoryUpdate(updatedItem);

                // 버프 효과 적용
                bool hasPeriodicBuff = ApplyItemBuffs(itemId);

                // 주기적 버프 등록 시 SLEEP 상태로 전환 + 브로드캐스트
                if (hasPeriodicBuff) await BroadcastSleepState(true);

                // 행동 수칙 쪽지 아이템 처리 (202000003)
                int ruleId = 0;
                if (itemId == 202000003)
                {
                    ruleId = _areaRuleManager.GetRuleForNote(CurrentMapSubId);
                    if (ruleId != 0)
                        _discoveredRules[ruleId] = PlayerId!.Value;
                    Logger.LogInformation("Player {PlayerId} used manual item, got RuleId={RuleId}", PlayerId, ruleId);
                }

                // 사용 결과 전송
                using var resultPacket =
                    PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, msg.ItemUid, ErrorCode.SUCCESS, ruleId);
                Send(resultPacket);

                Logger.LogInformation(
                    "Player {PlayerId} used InGameItem: ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
                    PlayerId, msg.ItemUid, itemId, msg.Count);
            }
            else
            {
                // 아이템 사용 실패
                using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
                Send(resultPacket);

                Logger.LogWarning("Player {PlayerId} failed to use InGameItem: ItemUid={ItemUid}, Count={Count}",
                    PlayerId, msg.ItemUid, msg.Count);
            }
        }
    }

    #endregion

    #region 플레이어 스탯

    /// <summary>
    ///     아이템 버프 효과 적용. 주기적 버프가 등록되면 true 반환
    /// </summary>
    private bool ApplyItemBuffs(int itemId)
    {
        var itemData = GameItemData.Get(itemId);
        if (itemData.ConsumableBuffList.Count == 0) return false;

        int staminaDelta = 0;
        int corruptionDelta = 0;
        bool hasPeriodicBuff = false;

        foreach ((int buffId, int value, int interval) in itemData.ConsumableBuffList)
        {
            var buffData = GameBuffData.Get(buffId);

            // 주기적 버프 → 마스터 타이머에 등록
            if (buffData.Type == BuffType.PERIODIC && interval > 0)
            {
                int durationSeconds = itemId == CatPillowItemId ? CatPillowRestDurationSeconds : 0;
                AddPeriodicBuff(buffData.SubType, value, interval, durationSeconds);
                hasPeriodicBuff = true;
                continue;
            }

            // 즉시 버프
            switch (buffData.SubType)
            {
                case BuffSubType.CONDITION_ADD:
                    staminaDelta += PassiveBuffUtility.ApplyIncrease(
                        value,
                        ActiveBuffIds,
                        BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
                    break;

                case BuffSubType.CORRUPTION_DOWN:
                    corruptionDelta -= PassiveBuffUtility.ApplyIncrease(
                        value,
                        ActiveBuffIds,
                        BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
                    break;

                case BuffSubType.CORRUPTION_ADD:
                    corruptionDelta += value;
                    break;
            }
        }

        int corruptionBeforeBuffs = Corruption;
        if (staminaDelta != 0 || corruptionDelta != 0) ModifyStats(staminaDelta, corruptionDelta);
        int recoveredCorruption = Math.Max(0, corruptionBeforeBuffs - Corruption);
        if (PlayerId.HasValue && recoveredCorruption > 0)
            _gameEventLogManager.LogRecoveryUse(
                CurrentMapSubId, PlayerId.Value, itemId, recoveredCorruption,
                source: "inventory_consumable", isBot: false);

        return hasPeriodicBuff;
    }

    /// <summary>스태미나 부족 시 코럽션 대체 변환비 (1 stamina deficit = StaminaToCorruptionRatio cor). 권고안 B (2026-05-05).</summary>
    private const int StaminaToCorruptionRatio = 2;

    /// <summary>
    ///     스탯 변경 (외부에서 호출 가능 - 환경 효과 등).
    ///     2026-05-05 권고안 B: 스태미나 부족 시 부족분만큼 Corruption 1:2 변환.
    ///     단, 양수 staminaDelta(회복)는 그대로 처리.
    /// </summary>
    public void ModifyStats(int staminaDelta = 0, int corruptionDelta = 0, long attackerPlayerId = 0,
        bool isAreaClosureElimination = false, bool isOvertimeElimination = false, bool deferElimination = false)
    {
        int oldStamina = Stamina;
        int oldCorruption = Corruption;
        int conversionCor = 0;

        if (staminaDelta != 0)
        {
            int newStamina = Stamina + staminaDelta;
            if (newStamina < 0)
            {
                // 부족분만큼 Corruption 대체 (1:2 변환)
                int deficit = -newStamina;
                conversionCor = deficit * StaminaToCorruptionRatio;
                Stamina = 0;
            }
            else
            {
                Stamina = Math.Min(newStamina, MaxStamina);
            }
        }

        int totalCorDelta = corruptionDelta + conversionCor;
        if (totalCorDelta != 0) Corruption = Math.Clamp(Corruption + totalCorDelta, 0, MaxCorruption);

        // 값이 변경되지 않았으면 패킷 전송 안함
        if (Stamina == oldStamina && Corruption == oldCorruption) return;

        if (conversionCor > 0)
        {
            Logger.LogInformation(
                "Player {PlayerId} Stamina 부족 → Cor 대체: 요청 ΔSt={DeltaS}, 변환 ΔCor=+{ConvCor}",
                PlayerId, staminaDelta, conversionCor);
        }

        Logger.LogInformation(
            "Player {PlayerId} Stats: Stamina {OldS}→{NewS} ({DeltaS:+#;-#;0}), Corruption {OldC}→{NewC} ({DeltaC:+#;-#;0})",
            PlayerId, oldStamina, Stamina, staminaDelta, oldCorruption, Corruption, totalCorDelta);

        // 아이템 스펙 그대로 델타값 전송 (이펙트 표시용). 변환 발생 시 플래그 전달 (클라 경고 알럿용).
        SendPlayerStatsUpdate(staminaDelta, totalCorDelta, conversionCor > 0);

        // 운영툴 진행 로그
        if (PlayerId.HasValue)
        {
            int recoveredCorruption = Math.Max(0, oldCorruption - Corruption);
            if (recoveredCorruption > 0)
                _gameEventLogManager.RecordSurvivorRecovery(CurrentMapSubId, PlayerId.Value, recoveredCorruption);

            _gameEventLogManager.LogResource(CurrentMapSubId, PlayerId.Value,
                staminaDelta, totalCorDelta, Stamina, Corruption,
                conversionCor > 0, reason: "", isBot: false);
        }

        if (!deferElimination)
            CheckResourceElimination(attackerPlayerId, isAreaClosureElimination, isOvertimeElimination);
    }

    /// <summary>
    ///     스탯 업데이트 패킷 전송
    /// </summary>
    private void SendPlayerStatsUpdate(int staminaDelta, int corruptionDelta, bool staminaConverted = false)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Stamina, staminaDelta, Corruption, corruptionDelta,
            staminaConverted);
        Send(packet);
        Logger.LogDebug(
            "Sent PLAYER_STATS_UPDATE to Player {PlayerId}: Stamina={Stamina} ({StaminaDelta:+#;-#;0}), Corruption={Corruption} ({CorruptionDelta:+#;-#;0}), Converted={Converted}",
            PlayerId, Stamina, staminaDelta, Corruption, corruptionDelta, staminaConverted);
    }

    /// <summary>
    ///     Area 퇴장 불가 알림 전송 (위치 보정 포함)
    /// </summary>
    private void SendAreaExitBlocked(AreaType areaType, Cell correctedCell)
    {
        using var packet = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(areaType, correctedCell);
        Send(packet);
        Logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
            PlayerId, areaType, correctedCell.X, correctedCell.Y);
    }

    /// <summary>
    ///     인게임 스탯 초기화 (새 게임 시작 시)
    /// </summary>
    private void ResetInGameStats()
    {
        StopAllPeriodicBuffs();
        _isSleeping = false;
        Stamina = InitialStamina;
        Corruption = InitialCorruption;
        CurrentState = PlayerState.Idle;
        _exploreMoveGraceUntil = DateTime.MinValue;
        CurrentExploringInteractId = null;
        Logger.LogInformation("Player {PlayerId} in-game stats reset: Stamina={Stamina}, Corruption={Corruption}",
            PlayerId, Stamina, Corruption);

        // 클라이언트에 초기 스탯 푸시 — 변동 없는 상태에서도 UI가 시작값으로 갱신되도록
        SendPlayerStatsUpdate(0, 0);
    }

    #endregion
}
