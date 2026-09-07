using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     플레이어 상태 파셜: C_TO_G_PLAYER_STATE(수면·휴식 요청)·스웜 수면 회복 틱·인게임 아이템
///     사용(C_TO_G_USE_INGAME_ITEM)·주기 버프 타이머·스탯 통지. (구 Combat.cs — 수동 공격 퇴역 후 #304 개명)
/// </summary>
public partial class GameClientSession
{
    /// <summary>고양이 베개 (401000003): 휴식 주기 버프의 지속 시간 특례.</summary>
    private const int CatPillowItemId = 401000003;
    private const int CatPillowRestDurationSeconds = 15;

    private async Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
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
            _pendingFinish.Count > 0)
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

        SetMovementLockState(isExploreState);


        // 같은 Area의 다른 플레이어들에게 상태 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea);

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, msg.State);
        foreach (var session in sameAreaSessions) session.TrySend(packet);

        Logger.LogDebug("Broadcasted PLAYER_STATE to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);

        // SLEEP 상태 추적
        _isSleeping = msg.State == PlayerState.SLEEP;

        // SLEEP 해제 시 주기적 버프 타이머 정리
        if (!_isSleeping) StopAllPeriodicBuffs();
    }

    /// <summary>이동 잠금 상태 갱신 — EXPLORE_1만 잠그고, 진입 직후 짧은 유예로 이동 패킷 경합을 흡수한다.</summary>
    private void SetMovementLockState(bool exploring)
    {
        CurrentState = exploring ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
        _exploreMoveGraceUntil = exploring
            ? DateTime.UtcNow + ExploreMoveGracePeriod
            : DateTime.MinValue;
    }

    private async Task HandleRestStateRequest()
    {
        if (_isSleeping) return;

        // #229 6단계: 스웜 수면은 스태미나 0 휴식이 아니라 본체 HP 회복 행동이다.
        // 조건은 하나 — 가해·피해 뒤 3초가 지났는가. 회복량 정산은 아레나 틱이 센다.
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
        if (IsEliminated || !_isSleeping)
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

        // 절단 치명상 회복 차단 (#232): 8초 동안은 틱이 지나도 회복이 없다 — 지난 틱은 소멸한다.
        if (nowUtc < SwarmHealLockUntilUtc)
        {
            _swarmSleepGrantedTicks = (int)Math.Floor(asleepSeconds - SwarmSleepWarmupSeconds) + 1;
            return;
        }

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

    /// <summary>
    ///     수면 진입 가능 여부 (#229 6단계) — 가해·피해 뒤 3초는 눕지 못한다.
    ///     절단 치명상(#232) 8초 회복 차단 중에도 눕지 못한다 — 누워도 회복이 없다.
    /// </summary>
    internal bool CanEnterSwarmSleep(DateTime nowUtc) =>
        (nowUtc - SwarmLastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds &&
        nowUtc >= SwarmHealLockUntilUtc;

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

        var state = sleep ? PlayerState.SLEEP : PlayerState.IDLE;


        // 같은 Area의 모든 플레이어에게 상태 브로드캐스트 (본인 포함)
        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
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

        var inventory = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
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
        if (IsRoundActionLocked(out _))
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid,
                ErrorCode.INVALID_GAME_STATE);
            TrySend(failPacket);
            return;
        }

        // 아이템 정보 먼저 조회 (제거 전에 ItemId 확인 필요)
        var inventory = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
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
            bool success = _matchRuntimes.GetRequired(MatchingId).Inventory.TryRemoveItem(PlayerId.Value, msg.ItemUid,
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
                MatchingId, PlayerId.Value, itemId, recoveredCorruption,
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
                _gameEventLogManager.RecordRecovery(MatchingId, PlayerId.Value, recoveredCorruption);

            _gameEventLogManager.LogResource(MatchingId, PlayerId.Value,
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
        TrySend(packet);
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
        TrySend(packet);
        Logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
            PlayerId, areaType, correctedCell.X, correctedCell.Y);
    }

}
