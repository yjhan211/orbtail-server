using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private const int CatPillowItemId = 401000003;
    private const int CatPillowRestDurationSeconds = 15;

    private Task HandleAttack(C_TO_G_ATTACK msg)
    {
        // 미구현 — 클라이언트에 에러 응답
        SendErrorResponse(ErrorCode.NOT_IMPLEMENTED, "공격 기능 미구현");
        return Task.CompletedTask;
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
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return;
        }

        Logger.LogInformation("Player {PlayerId} state change request: {State}", PlayerId, msg.State);

        if (msg.State == global::network.common.PlayerState.SLEEP)
        {
            await HandleRestStateRequest();
            return;
        }

        // 서버 측 상태 저장
        await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
        var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);
        if (playerInfo != null)
        {
            playerInfo.State = msg.State;
            await playerInfo.Save(CacheHelper);
        }

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
        var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);
        if (playerInfo != null)
        {
            playerInfo.State = state;
            await playerInfo.Save(CacheHelper);
        }

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
    private void SendInGameInventoryList()
    {
        if (!PlayerId.HasValue) return;

        var items = _inGameInventoryManager.GetAllItems(CurrentMapSubId, PlayerId.Value);
        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_LIST(items);
        Send(packet);

        Logger.LogDebug("Sent InGameInventory list to PlayerId={PlayerId}, ItemCount={Count}", PlayerId, items.Count);
    }

    /// <summary>
    ///     인게임 인벤토리 업데이트 전송 (아이템 추가/제거 시)
    /// </summary>
    private void SendInGameInventoryUpdate(InGameItemInfo item)
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
        var itemData = GameItemData.Get(itemId);

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
                    staminaDelta += value;
                    break;

                case BuffSubType.CORRUPTION_DOWN:
                    corruptionDelta -= value;
                    break;

                case BuffSubType.CORRUPTION_ADD:
                    corruptionDelta += value;
                    break;
            }
        }

        if (staminaDelta != 0 || corruptionDelta != 0) ModifyStats(staminaDelta, corruptionDelta);

        return hasPeriodicBuff;
    }

    /// <summary>스태미나 부족 시 코럽션 대체 변환비 (1 stamina deficit = StaminaToCorruptionRatio cor). 권고안 B (2026-05-05).</summary>
    private const int StaminaToCorruptionRatio = 2;

    /// <summary>
    ///     스탯 변경 (외부에서 호출 가능 - 환경 효과 등).
    ///     2026-05-05 권고안 B: 스태미나 부족 시 부족분만큼 Corruption 1:2 변환.
    ///     단, 양수 staminaDelta(회복)는 그대로 처리.
    /// </summary>
    public void ModifyStats(int staminaDelta = 0, int corruptionDelta = 0)
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
            _gameEventLogManager.LogResource(CurrentMapSubId, PlayerId.Value,
                staminaDelta, totalCorDelta, Stamina, Corruption,
                conversionCor > 0, reason: "", isBot: false);
        }
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
        CurrentExploringInteractId = null;
        Logger.LogInformation("Player {PlayerId} in-game stats reset: Stamina={Stamina}, Corruption={Corruption}",
            PlayerId, Stamina, Corruption);

        // 클라이언트에 초기 스탯 푸시 — 변동 없는 상태에서도 UI가 시작값으로 갱신되도록
        SendPlayerStatsUpdate(0, 0);
    }

    #endregion
}
