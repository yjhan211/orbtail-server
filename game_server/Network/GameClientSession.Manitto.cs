using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

/// <summary>
///     마니또 전용 핸들러: 색출, 흔적 배치, 타겟 위치 추적.
/// </summary>
public partial class GameClientSession
{
    private const int GiftRecallStaminaCost = 5;
    private const int ManittoTargetAnswerIndexOffset = 100000;
    private const int ManittoTargetAnswerTextId = 11044;

    // #159/#158: 핀(경계)을 켤 때 1회 소모하는 스태미나. 켤 때마다 큰 비용이라 같은 방 무료 스팸을 차단한다.
    // 따라가기와 공유 자원이라 의심에 쓸수록 따라갈 여력이 준다. 끄기는 무료, 재진입이 비싸 마이크로 토글도 막힌다. 튜닝 노브.
    private const int BookmarkActivationStaminaCost = 15;

    private static readonly int MissionInfoPacketBudget = Config.BUFFER_SIZE - Config.HEADER_SIZE - 4 - 8 - 128;
    private readonly HashSet<int> _pendingChecklistActivityFinish = new();

    /// <summary>
    ///     v0.2.0 — 미션 정보 전송 (게임 접속 시). 직책별 7부품 메타데이터 전체 송신.
    /// </summary>
    private void SendMissionInfo()
    {
        if (!PlayerId.HasValue) return;

        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null) return;

        var allParts = GameMissionData.GetPartsIncludingShared((short)MyJobTitle);
        var partInfos = allParts.Select(p => new MissionPartInfo
        {
            PartId = p.PartId,
            PartNameKr = p.PartNameKr,
            PartTier = (int)p.PartTier,
            TargetArea = p.TargetArea,
            TargetObjectType = p.TargetObjectType,
            PrerequisiteShareGroup = p.PrerequisiteShareGroup,
            IsCollected = state.CollectedParts.Contains(p.PartId),
            NarrativeKr = p.NarrativeKr ?? "",
            NarrativeEn = p.NarrativeEn ?? "",
            NarrativeJp = p.NarrativeJp ?? ""
        }).ToList();

        var graphNodes = BuildMissionGraphNodeProgress(state);
        var shortRewards = BuildMissionShortRewardInfoList(state);
        var chunks = BuildMissionInfoChunks(state, allParts.Count, partInfos, graphNodes, shortRewards);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkMsg = chunks[i];
            chunkMsg.ChunkIndex = i;
            chunkMsg.IsEnd = i == chunks.Count - 1;

            using var chunkPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_INFO, PlayerId.Value);
            chunkPacket.SetBody(MessagePackSerializer.Serialize(chunkMsg));
            Send(chunkPacket);
        }

        Logger.LogDebug(
            "Sent mission info to PlayerId={PlayerId} in {ChunkCount} packets: Parts={PartCount}, Nodes={NodeCount}, Rewards={RewardCount}",
            PlayerId, chunks.Count, partInfos.Count, graphNodes.Count, shortRewards.Count);
    }

    private void SendChecklistInfo()
    {
        if (!PlayerId.HasValue) return;

        int roundNumber = !Config.ROUND_SYSTEM_ENABLED
            ? 1
            : GameRoundStates.TryGetValue(CurrentMapSubId, out var roundState)
            ? roundState.RoundNumber
            : 0;
        var contribution = _checklistManager.GetPlayerContributions(CurrentMapSubId, new[] { PlayerId.Value })
            .FirstOrDefault();
        var msg = new G_TO_C_CHECKLIST_INFO
        {
            MatchingId = CurrentMapSubId,
            RoundNumber = roundNumber,
            ActiveTaskIds = _checklistManager.GetActiveTasks(CurrentMapSubId, PlayerId.Value)
                .Select(task => task.TaskId)
                .ToList(),
            CompletedTaskIds = _checklistManager.GetCompletedTaskIds(CurrentMapSubId, PlayerId.Value),
            ActiveTaskProgresses = _checklistManager.GetActiveTaskProgresses(CurrentMapSubId, PlayerId.Value),
            GeneralJobScore = contribution?.GeneralJobScore ?? 0f,
            ManittoRoleScore = contribution?.ManittoRoleScore ?? 0f,
            BonusScore = contribution?.BonusScore ?? 0f,
            Contribution = contribution?.Contribution ?? 0
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_INFO, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    public bool AdvanceTargetProximityChecklistProgress(float deltaSeconds)
    {
        if (!PlayerId.HasValue) return false;

        var result = _checklistManager.AdvanceActiveTaskProgress(
            CurrentMapSubId,
            PlayerId.Value,
            "MANITTO_STAY_NEAR_TARGET_20",
            deltaSeconds);
        if (!result.Changed)
            return false;

        if (result.Completion?.CompletedTask != null)
            Logger.LogInformation(
                "Checklist proximity task completed: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}",
                CurrentMapSubId, PlayerId.Value, result.Completion.CompletedTask.TaskId);

        SendChecklistInfo();
        return true;
    }

    public bool CompleteTargetGiftChecklist()
    {
        if (!PlayerId.HasValue) return false;

        var task = _checklistManager.GetActiveTasks(CurrentMapSubId, PlayerId.Value)
            .FirstOrDefault(activeTask =>
                activeTask.TaskKey.Equals("MANITTO_TARGET_DISCOVERS_GIFT", StringComparison.OrdinalIgnoreCase));
        if (task == null)
            return false;

        var result = _checklistManager.TryCompleteTask(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            CurrentArea,
            interactId: 0,
            _inGameInventoryManager);
        if (result.ErrorCode != ErrorCode.SUCCESS)
            return false;

        SendChecklistInfo();
        SendChecklistActivityResult(0, ErrorCode.SUCCESS, result.AwardedScore, result.AwardedContribution);
        return true;
    }

    private bool TryGetActiveInteractObjectChecklistTask(InteractableInfoData info, out ChecklistTaskData? task)
    {
        task = null;
        if (!PlayerId.HasValue || info == null) return false;

        task = _checklistManager.GetActiveTasks(CurrentMapSubId, PlayerId.Value)
            .FirstOrDefault(activeTask =>
                activeTask.Category == ChecklistTaskCategory.GeneralJob &&
                activeTask.CompletionEvent.Equals("interact_object", StringComparison.OrdinalIgnoreCase) &&
                (activeTask.AreaType <= 0 || activeTask.AreaType == info.ZoneId) &&
                (activeTask.ObjectType <= 0 || activeTask.ObjectType == (int)info.ObjectType) &&
                (activeTask.InteractId <= 0 || activeTask.InteractId == info.Id));
        return task != null;
    }

    private bool TryCompleteInteractObjectChecklist(InteractableInfoData info)
    {
        return TryCompleteInteractObjectChecklist(info, out _, out _, out _) == ErrorCode.SUCCESS;
    }

    private ErrorCode TryCompleteInteractObjectChecklist(
        InteractableInfoData info,
        out ChecklistTaskData? completedTask,
        out float awardedScore,
        out int awardedContribution)
    {
        completedTask = null;
        awardedScore = 0f;
        awardedContribution = 0;
        if (!PlayerId.HasValue) return ErrorCode.INVALID_GAME_STATE;
        if (!TryGetActiveInteractObjectChecklistTask(info, out var task) || task == null)
            return ErrorCode.INTERACTABLE_NOT_AVAILABLE;

        var result = _checklistManager.TryCompleteTask(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            (AreaType)info.ZoneId,
            info.Id,
            _inGameInventoryManager);
        completedTask = result.CompletedTask;
        if (result.ErrorCode != ErrorCode.SUCCESS)
        {
            Logger.LogInformation(
                "Checklist interact_object completion skipped: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}, Error={ErrorCode}",
                CurrentMapSubId, PlayerId.Value, task.TaskId, info.Id, result.ErrorCode);
            return result.ErrorCode;
        }

        awardedScore = result.AwardedScore;
        awardedContribution = result.AwardedContribution;
        _gameEventLogManager.LogSchoolActivityComplete(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            ((AreaType)info.ZoneId).ToString(),
            info.Id,
            result.AwardedScore,
            result.AwardedContribution,
            task.TitleKr,
            isBot: false);

        if (result.ConsumedItemUpdate != null)
            SendInGameInventoryUpdate(result.ConsumedItemUpdate);

        SendChecklistInfo();
        Logger.LogInformation(
            "Checklist interact_object completed: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}",
            CurrentMapSubId, PlayerId.Value, task.TaskId, info.Id);
        return ErrorCode.SUCCESS;
    }

    private Task HandleChecklistActivityStart(C_TO_G_CHECKLIST_ACTIVITY_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated)
        {
            SendChecklistActivityAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (IsRoundActionLocked(out _))
        {
            SendChecklistActivityAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        CancelPendingRoomEncounterTurnsForCurrentPlayer("ChecklistActivityStart");

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("Checklist activity START unknown InteractId: {InteractId}", msg.InteractId);
            SendChecklistActivityAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (!TryGetActiveInteractObjectChecklistTask(info, out var task) || task == null)
        {
            Logger.LogDebug(
                "Checklist activity START unavailable: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            SendChecklistActivityAck(msg.InteractId, ErrorCode.INTERACTABLE_NOT_AVAILABLE, 0);
            return Task.CompletedTask;
        }

        int staminaCost = ApplyDutyStaminaSaverToCost(Math.Max(0, task.StaminaCost));
        if (staminaCost > 0)
            ModifyStats(-staminaCost);

        var currentState = _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
        if (currentState == (int)InteractableStateType.SABOTAGE)
            _sabotageManager.OnActionCompleted(CurrentMapSubId, msg.InteractId, 0);

        _pendingChecklistActivityFinish.Add(msg.InteractId);
        _gameEventLogManager.LogSchoolActivityStart(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            ((AreaType)info.ZoneId).ToString(),
            msg.InteractId,
            task.TitleKr,
            isBot: false);

        Logger.LogInformation(
            "Checklist activity START: PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}, StaminaCost={StaminaCost}",
            PlayerId, task.TaskId, msg.InteractId, staminaCost);

        SendChecklistActivityAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        BroadcastPlayerState(global::network.common.PlayerState.EXPLORE_1);
        return Task.CompletedTask;
    }

    private Task HandleChecklistActivityFinish(C_TO_G_CHECKLIST_ACTIVITY_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        if (!_pendingChecklistActivityFinish.Remove(msg.InteractId))
        {
            Logger.LogWarning(
                "Checklist activity FINISH without START or duplicated: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        ErrorCode errorCode = ErrorCode.SUCCESS;
        float awardedScore = 0f;
        int awardedContribution = 0;
        if (info == null)
        {
            Logger.LogWarning("Checklist activity FINISH unknown InteractId: {InteractId}", msg.InteractId);
            errorCode = ErrorCode.FATAL;
        }
        else
        {
            errorCode = TryCompleteInteractObjectChecklist(info, out _, out awardedScore, out awardedContribution);
        }

        Logger.LogInformation(
            "Checklist activity FINISH: PlayerId={PlayerId}, InteractId={InteractId}, ErrorCode={ErrorCode}",
            PlayerId, msg.InteractId, errorCode);

        SendChecklistActivityResult(msg.InteractId, errorCode, awardedScore, awardedContribution);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        ResolvePendingRoomDiscoveriesAfterExploreFinished(info != null ? (AreaType)info.ZoneId : CurrentArea);
        return Task.CompletedTask;
    }

    private void SendChecklistActivityAck(int interactId, ErrorCode errorCode, int cooldownRemain)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_CHECKLIST_ACTIVITY_ACK
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            CooldownRemainSeconds = cooldownRemain
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_ACTIVITY_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private void SendChecklistActivityResult(
        int interactId,
        ErrorCode errorCode,
        float awardedScore,
        int awardedContribution)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_CHECKLIST_ACTIVITY_RESULT
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            AwardedScore = errorCode == ErrorCode.SUCCESS ? awardedScore : 0f,
            AwardedContribution = errorCode == ErrorCode.SUCCESS ? Math.Max(0, awardedContribution) : 0
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_ACTIVITY_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private List<G_TO_C_MISSION_INFO> BuildMissionInfoChunks(
        PlayerPartState state,
        int totalSteps,
        List<MissionPartInfo> parts,
        List<MissionGraphNodeProgressInfo> graphNodes,
        List<MissionShortRewardInfo> shortRewards)
    {
        var chunks = new List<G_TO_C_MISSION_INFO>();
        var current = CreateMissionInfoChunk(state, totalSteps, includeStoryletSnapshot: true);

        void CommitCurrent()
        {
            chunks.Add(current);
            current = CreateMissionInfoChunk(state, totalSteps, includeStoryletSnapshot: false);
        }

        void AddItem<T>(
            T item,
            Action<G_TO_C_MISSION_INFO, T> add,
            Action<G_TO_C_MISSION_INFO, T> remove)
        {
            add(current, item);
            if (GetMissionInfoPayloadSize(current) <= MissionInfoPacketBudget) return;

            remove(current, item);
            if (!IsMissionInfoChunkEmpty(current)) CommitCurrent();

            add(current, item);
            int payloadSize = GetMissionInfoPayloadSize(current);
            if (payloadSize > MissionInfoPacketBudget)
            {
                Logger.LogWarning(
                    "Single mission info entry exceeds packet budget: PlayerId={PlayerId}, Size={Size}, Budget={Budget}",
                    PlayerId, payloadSize, MissionInfoPacketBudget);
            }
        }

        foreach (var part in parts)
            AddItem(part, (msg, item) => msg.Parts.Add(item), (msg, item) => msg.Parts.Remove(item));

        foreach (var node in graphNodes)
            AddItem(node, (msg, item) => msg.GraphNodes.Add(item), (msg, item) => msg.GraphNodes.Remove(item));

        foreach (var reward in shortRewards)
            AddItem(reward, (msg, item) => msg.ShortRewards.Add(item), (msg, item) => msg.ShortRewards.Remove(item));

        if (!IsMissionInfoChunkEmpty(current) || chunks.Count == 0) chunks.Add(current);
        return chunks;
    }

    private G_TO_C_MISSION_INFO CreateMissionInfoChunk(PlayerPartState state, int totalSteps, bool includeStoryletSnapshot)
    {
        var msg = new G_TO_C_MISSION_INFO
        {
            JobTitle = MyJobTitle,
            CurrentStep = state.CollectedParts.Count,
            TotalSteps = totalSteps,
            TargetArea = 0,
            TargetInteractId = 0,
            TargetActionId = 0,
            Parts = [],
            GraphNodes = [],
            ShortRewards = []
        };

        if (!includeStoryletSnapshot) return msg;

        lock (state.SyncRoot)
        {
            msg.DiscoveredStoryletIds = state.DiscoveredStoryletIds.ToList();
            msg.TrackedStoryletIds = state.TrackedStoryletIds.ToList();
            msg.ClaimedStoryletIds = state.ClaimedStoryletIds.ToList();
            msg.LostStoryletIds = state.LostStoryletIds.ToList();
            msg.OwnedClueTags = state.OwnedClueTags.ToList();
            msg.CraftedFunctionItemIds = state.CraftedFunctionItems.ToList();
            msg.VisibleVictoryTraceIds = state.VisibleVictoryTraceIds.ToList();
        }

        return msg;
    }

    private static int GetMissionInfoPayloadSize(G_TO_C_MISSION_INFO msg) =>
        MessagePackSerializer.Serialize(msg).Length;

    private static bool IsMissionInfoChunkEmpty(G_TO_C_MISSION_INFO msg) =>
        msg.Parts.Count == 0 &&
        msg.GraphNodes.Count == 0 &&
        msg.ShortRewards.Count == 0 &&
        msg.DiscoveredStoryletIds.Count == 0 &&
        msg.TrackedStoryletIds.Count == 0 &&
        msg.ClaimedStoryletIds.Count == 0 &&
        msg.LostStoryletIds.Count == 0 &&
        msg.OwnedClueTags.Count == 0 &&
        msg.CraftedFunctionItemIds.Count == 0 &&
        msg.VisibleVictoryTraceIds.Count == 0;

    private Task HandleRecallGift(C_TO_G_RECALL_GIFT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendRecallGiftResult(ErrorCode.INVALID_GAME_STATE, new RecallGiftResult
            {
                InteractId = msg.InteractId
            });
            return Task.CompletedTask;
        }

        var result = _missionManager.TryRecallGift(CurrentMapSubId, PlayerId.Value, msg.InteractId);
        if (!result.Success)
        {
            SendRecallGiftResult(result.ErrorCode, result);
            return Task.CompletedTask;
        }

        var updatedItem = _inGameInventoryManager.AddItem(
            CurrentMapSubId,
            PlayerId.Value,
            result.ItemId,
            1,
            GiftState.Prepared);

        using (var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([updatedItem]))
        {
            Send(inventoryPacket);
        }

        result.ItemUid = updatedItem.ItemUid;
        ModifyStats(staminaDelta: -GiftRecallStaminaCost);
        SendRecallGiftResult(ErrorCode.SUCCESS, result);

        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            $"비밀 선물 회수: ItemId={result.ItemId}, InteractId={result.InteractId}, Target={result.TargetPlayerId}",
            isBot: false);

        return Task.CompletedTask;
    }

    private void SendRecallGiftResult(ErrorCode errorCode, RecallGiftResult result)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_RECALL_GIFT_RESULT, PlayerId.Value);
        var msg = new G_TO_C_RECALL_GIFT_RESULT
        {
            ErrorCode = errorCode,
            ItemUid = result.ItemUid,
            ItemId = result.ItemId,
            InteractId = result.InteractId,
            AreaType = result.AreaType,
            HasPlacedGiftAtInteract = result.HasPlacedGiftAtInteract,
            HasPlacedGiftInArea = result.HasPlacedGiftInArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     색출 요청 처리 (1회 한정)
    /// </summary>
    private Task HandleDetectManitto(C_TO_G_DETECT_MANITTO msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            using var lockedPacket = Packet.Create((int)Protocol.G_TO_C_DETECT_RESULT, PlayerId.Value);
            var lockedResult = new G_TO_C_DETECT_RESULT
            {
                ErrorCode = ErrorCode.INVALID_GAME_STATE,
                IsCorrect = false,
                TargetPlayerId = msg.TargetPlayerId
            };
            lockedPacket.SetBody(MessagePackSerializer.Serialize(lockedResult));
            Send(lockedPacket);
            return Task.CompletedTask;
        }

        var (isCorrect, errorCode) = _manittoChainManager.TryDetect(CurrentMapSubId, PlayerId.Value, msg.TargetPlayerId);

        // 결과 전송
        using var packet = Packet.Create((int)Protocol.G_TO_C_DETECT_RESULT, PlayerId.Value);
        var result = new G_TO_C_DETECT_RESULT
        {
            ErrorCode = errorCode,
            IsCorrect = isCorrect,
            TargetPlayerId = msg.TargetPlayerId
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 지목을 실제로 소비한 경우만 결과를 판정한다(SUCCESS). ALREADY_USED/NOT_AVAILABLE 등은 막기만 하고 페널티 없음.
        if (errorCode != ErrorCode.SUCCESS) return Task.CompletedTask;

        // 적중 — 내 마니또(스토커)를 즉시 탈락시킨다. 프로토 0은 정신력 모델이라 v0.2.0 부품 전이는 적용하지 않는다.
        if (isCorrect)
        {
            // 마니또의 모든 흔적 함정 무효화 (§2.5.1)
            _traceManager.InvalidateTracesByPlacer(CurrentMapSubId, msg.TargetPlayerId);

            _ = ProcessElimination(msg.TargetPlayerId, EliminationReason.DETECTED, PlayerId.Value);
            return Task.CompletedTask;
        }

        Logger.LogInformation("색출 오발: DetecterId={PlayerId}, Target={Target}", PlayerId, msg.TargetPlayerId);

        return Task.CompletedTask;
    }

    private Task HandleSettlementNominate(C_TO_G_SETTLEMENT_NOMINATE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (CurrentMapSubId <= 0 || !GameRoundStates.TryGetValue(CurrentMapSubId, out var state))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement is not active");
            return Task.CompletedTask;
        }

        lock (state.SyncRoot)
        {
            if (state.IsSessionEnded || state.Phase != RoundPhase.SettlementNomination)
            {
                SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round nomination phase is not active");
                return Task.CompletedTask;
            }

            if (!IsSettlementNominationTargetAvailable(CurrentMapSubId, msg.TargetPlayerId))
            {
                SendErrorResponse(ErrorCode.DETECT_TARGET_NOT_FOUND, "Settlement nomination target not found");
                return Task.CompletedTask;
            }

            state.SettlementNominations[PlayerId.Value] = msg.TargetPlayerId;
        }

        Logger.LogInformation("Settlement nomination saved: MatchingId={MatchingId}, Nominator={Nominator}, Target={Target}",
            CurrentMapSubId, PlayerId.Value, msg.TargetPlayerId);
        return Task.CompletedTask;
    }

    private bool IsSettlementNominationTargetAvailable(long matchingId, long targetPlayerId)
    {
        if (!PlayerId.HasValue || targetPlayerId == 0 || targetPlayerId == PlayerId.Value)
            return false;

        var targetSession = _getSessionsByInstance(CurrentMapId, matchingId)
            .FirstOrDefault(session => session.PlayerId == targetPlayerId);
        if (targetSession != null)
            return !targetSession.IsEliminated && targetSession.ManittoStatus != ManittoStatus.SPECTATING;

        var bot = _botPlayerManager.GetBot(matchingId, targetPlayerId);
        return bot != null && !bot.IsEliminated && bot.ManittoStatus != ManittoStatus.SPECTATING;
    }

    private Task HandleBookmarkPresence(C_TO_G_BOOKMARK_PRESENCE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return Task.CompletedTask;
        }

        long previousBookmarkPlayerId = PresenceBookmarkPlayerId;
        var myManitto = _manittoChainManager.FindManittoOf(CurrentMapSubId, PlayerId.Value);
        long newBookmarkPlayerId = msg.TargetPlayerId;
        if (previousBookmarkPlayerId != 0 && previousBookmarkPlayerId != newBookmarkPlayerId &&
            myManitto?.PlayerId == previousBookmarkPlayerId)
        {
            SendSharpGazeMarkUpdate(previousBookmarkPlayerId, false);
        }

        bool shouldConsumeActivationCost =
            ShouldConsumePresenceBookmarkActivationCost(previousBookmarkPlayerId, newBookmarkPlayerId);
        PresenceBookmarkPlayerId = newBookmarkPlayerId;
        bool isManitto = PresenceBookmarkPlayerId != 0 && myManitto?.PlayerId == PresenceBookmarkPlayerId;

        using var packet = Packet.Create((int)Protocol.G_TO_C_BOOKMARK_PRESENCE_RESULT, PlayerId.Value);
        var result = new G_TO_C_BOOKMARK_PRESENCE_RESULT
        {
            TargetPlayerId = PresenceBookmarkPlayerId
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 켤 때(새로 켜거나 대상 변경) 1회 스태미나 소모. 끄기는 무료지만 재진입이 비싸 스팸/마이크로 토글을 막는다.
        // 맞든 틀리든 동일 소모라 정답을 누설하지 않고, 스태미나 고갈 시 ModifyStats가 정신력으로 1:2 전환한다.
        if (shouldConsumeActivationCost) ConsumePresenceBookmarkActivationCost(PresenceBookmarkPlayerId);

        if (isManitto) SendSharpGazeMarkUpdate(PresenceBookmarkPlayerId, true);

        Logger.LogInformation(
            "Presence bookmark updated: PlayerId={PlayerId}, Bookmark={Bookmark}, IsManitto={IsManitto}",
            PlayerId, PresenceBookmarkPlayerId, isManitto);

        return Task.CompletedTask;
    }

    private static bool ShouldConsumePresenceBookmarkActivationCost(long previousBookmarkPlayerId,
        long newBookmarkPlayerId)
    {
        if (newBookmarkPlayerId == 0) return false;
        return newBookmarkPlayerId != previousBookmarkPlayerId;
    }

    private void ConsumePresenceBookmarkActivationCost(long bookmarkPlayerId)
    {
        ModifyStats(staminaDelta: -BookmarkActivationStaminaCost);
        Logger.LogInformation(
            "Presence bookmark activation cost consumed: PlayerId={PlayerId}, Bookmark={Bookmark}, StaminaCost={Cost}",
            PlayerId, bookmarkPlayerId, BookmarkActivationStaminaCost);
    }

    private void SendSharpGazeMarkUpdate(long targetPlayerId, bool isActive)
    {
        if (targetPlayerId == 0) return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == targetPlayerId);
        if (targetSession == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SHARP_GAZE_MARK_UPDATE, targetPlayerId);
        var msg = new G_TO_C_SHARP_GAZE_MARK_UPDATE { IsActive = isActive };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        targetSession.Send(packet);
    }

    /// <summary>
    ///     플레이어 탈락 처리 + 체인 단절 브로드캐스트
    /// </summary>
    private Task ProcessElimination(long eliminatedPlayerId, EliminationReason reason, long? causePlayerId = null,
        bool deferGameOver = false, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var eliminatedSession = allSessions.FirstOrDefault(session => session.PlayerId == eliminatedPlayerId);
        var eliminatedBot = _botPlayerManager.GetBot(CurrentMapSubId, eliminatedPlayerId);
        AreaType eliminatedArea = eliminatedSession?.CurrentArea ?? eliminatedBot?.CurrentArea ?? AreaType.None;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        _groundItemManager.ReleaseClaimReservationsForPlayer(CurrentMapSubId, eliminatedPlayerId);
        _gameEventLogManager.LogElimination(CurrentMapSubId, eliminatedPlayerId, reason.ToString(), isBot: false);
        int finalOrbTier = ResolveFinalOrbTier(CurrentMapSubId, eliminatedPlayerId);
        var affected = _manittoChainManager.EliminatePlayer(CurrentMapSubId, eliminatedPlayerId, reason,
            resolvedAttackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank, finalOrbTier);

        if (eliminatedSession != null)
            eliminatedSession.DropAllInventoryAtCurrentPosition();
        else
            DropBotInventoryAtCurrentPosition(eliminatedPlayerId);

        // 1. 전체에게 탈락 알림. 탈락자에게만 결과표를 고정 패킷 예산 안에서 나눠 보낸다.
        var eliminatedResultPlayers = BuildGameResultPlayers(allSessions, CurrentMapSubId, 0);
        var eliminatedResultChunks = GameResultPacketChunker.CreateEliminationChunks(
            eliminatedPlayerId,
            resolvedAttackerPlayerId,
            reason,
            eliminatedResultPlayers);

        foreach (var session in allSessions)
        {
            if (session.PlayerId == eliminatedPlayerId)
            {
                foreach (var resultChunk in eliminatedResultChunks)
                {
                    using var resultPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
                    resultPacket.SetBody(MessagePackSerializer.Serialize(resultChunk));
                    session.Send(resultPacket);
                }

                continue;
            }

            using var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
            var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
            {
                PlayerId = eliminatedPlayerId,
                AttackerPlayerId = resolvedAttackerPlayerId,
                Reason = reason,
                ResultPlayers = [],
                ResultChunkIndex = 0,
                IsResultEnd = true
            };
            eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
            session.Send(eliminatedPacket);
        }

        // 세션 ManittoStatus 동기화 (탈락자 → SPECTATING으로 관전 전환)
        // #26: 봇 상태도 함께 동기화 (BotPlayerManager) — 시한부 진입 시 사보타주 트리거 등
        foreach (var (playerId, newStatus) in affected)
        {
            var s = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (s != null)
            {
                s.ManittoStatus = newStatus == ManittoStatus.ELIMINATED
                    ? ManittoStatus.SPECTATING
                    : newStatus;
                continue;
            }

            // 봇 상태 동기화
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, playerId);
            if (bot != null)
            {
                if (newStatus == ManittoStatus.ELIMINATED)
                {
                    bot.IsEliminated = true;
                    bot.ManittoStatus = ManittoStatus.SPECTATING;
                }
                else
                {
                    bot.ManittoStatus = newStatus;
                }
            }
        }

        foreach (var (playerId, newStatus) in affected)
        {
            if (newStatus != ManittoStatus.TERMINAL) continue;
            _missionManager.NotifyTargetLost(CurrentMapSubId, playerId, eliminatedPlayerId, reason, causePlayerId);
        }

        // 2. 영향받는 플레이어에게 개별 상태 변경 알림
        foreach (var (playerId, newStatus) in affected)
        {
            if (newStatus == ManittoStatus.ELIMINATED) continue; // 탈락자 본인은 이미 알림됨

            var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (targetSession == null) continue;

            using var chainPacket = Packet.Create((int)Protocol.G_TO_C_CHAIN_BREAK, playerId);
            var chainMsg = new G_TO_C_CHAIN_BREAK
            {
                EliminatedPlayerId = eliminatedPlayerId,
                NewStatus = newStatus
            };
            chainPacket.SetBody(MessagePackSerializer.Serialize(chainMsg));
            targetSession.Send(chainPacket);
        }

        // 3. 게임 종료 판정
        var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(CurrentMapSubId);
        if (!deferGameOver && isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            SendGameResult(allSessions, winnerId ?? 0, false, CurrentMapSubId);
        }

        return Task.CompletedTask;
    }
    internal void EliminateForSettlement(
        long eliminatedPlayerId,
        bool isAreaClosureElimination,
        bool isOvertimeElimination,
        int forcedRank)
    {
        _ = ProcessElimination(
            eliminatedPlayerId,
            EliminationReason.MENTAL_ZERO,
            deferGameOver: true,
            isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination,
            forcedRank: forcedRank);
    }

    internal void TryEndSurvivorMatch(long winnerId, string criterion)
    {
        if (_isGameEnded || CurrentMapSubId <= 0)
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        Logger.LogInformation(
            "Survivor match resolved: MatchingId={MatchingId}, WinnerId={WinnerId}, Criterion={Criterion}",
            CurrentMapSubId, winnerId, criterion);
        _gameEventLogManager.LogSystem(
            CurrentMapSubId,
            $"survivor_settlement winner={winnerId} criterion={criterion}");
        SendGameResult(allSessions, winnerId, false, CurrentMapSubId, "overtime_settlement", criterion);
    }

    private int ResolveFinalOrbTier(long matchingId, long playerId)
    {
        var equippedItem = _inGameInventoryManager.GetEquippedBattleItem(matchingId, playerId);
        return equippedItem == null ? 0 : BattleItemCombatData.Get(equippedItem.ItemId)?.Tier ?? 0;
    }

    /// <summary>
    ///     게임 결과 패킷 전송 (체인 전체 공개)
    /// </summary>
    private void SendGameResult(List<GameClientSession> allSessions, long winnerId, bool isTimeout, long matchingId,
        string endReason = "last_survivor", string tieBreakCriterion = "not_required")
    {
        if (allSessions.Any(session => session.IsGameEnded))
            return;

        var players = BuildGameResultPlayers(allSessions, matchingId, winnerId);
        _gameEventLogManager.LogMatchEnded(
            matchingId,
            winnerId,
            endReason,
            tieBreakCriterion,
            players.Select(player => new SurvivorFinalPlayerStats(
                player.PlayerId,
                player.Rank,
                player.SurvivalTimeSeconds,
                player.KillCount,
                player.TotalDamageDealt,
                player.TotalRecovery)).ToList());

        var resultChunks = GameResultPacketChunker.CreateGameResultChunks(winnerId, isTimeout, players);
        foreach (var resultChunk in resultChunks)
        {
            using var resultPacket = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
            resultPacket.SetBody(MessagePackSerializer.Serialize(resultChunk));
            foreach (var session in allSessions) session.Send(resultPacket);
        }

        // 기존 게임 종료 패킷도 전송 (클라이언트 호환)
        foreach (var session in allSessions)
        {
            bool isEscaped = !isTimeout && session.PlayerId == winnerId;
            using var endPacket = PacketMaker.G_TO_C_GAME_END(matchingId, isEscaped);
            session.Send(endPacket);
        }

        // 결과 화면 이후 퇴장은 페널티 면제
        foreach (var session in allSessions) session.MarkGameEnded();

        // 게임 타이머 정리 — race 완주/색출로 종료되었을 때 타임아웃이 후행 발사되지 않도록 (#87)
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
        GameRoundStates.TryRemove(matchingId, out _);
        _areaClosureManager.CleanupMatching(matchingId);
        _presenceTracker?.Remove(matchingId);

        MatchStartGate.RemoveMatching(matchingId);
        RngCollectCooldownStore.ClearMatching(matchingId);
        _checklistManager.RemoveMatchingState(matchingId);
        _areaItemStockManager.RemoveMatchingState(matchingId);
        _groundItemManager.RemoveMatchingState(matchingId);
        _emotionAfterimageMonsterManager.RemoveMatchingState(matchingId);
        _inGameInventoryManager.RemoveMatchingState(matchingId);
        _interactableStateManager.RemoveMatchingState(matchingId);
        _areaRuleManager.RemoveMatchingState(matchingId);
        _itemPoolManager.RemoveMatchingState(matchingId);
        _doorStateManager.ClearMatching(matchingId);
        _sabotageManager.RemoveMatchingState(matchingId);
        _missionManager.CleanupMatching(matchingId);
        _traceManager.CleanupMatching(matchingId);
        _interactionChoiceService.CleanupMatching(matchingId);
        _encounterRevealManager.CleanupMatching(matchingId);
        _manittoChainManager.CleanupMatching(matchingId);
        _gameEventLogManager.Clear(matchingId);
        // 매치 전송용 봇/버프 데이터는 결과를 보낸 뒤 더 이상 재접속에 필요하지 않다.
        _botPlayerManager.CleanupMatching(matchingId);
        _ = CleanupRedisMatchingTransientStateAsync(matchingId);
    }

    private List<GameResultPlayerInfo> BuildGameResultPlayers(List<GameClientSession> allSessions, long matchingId,
        long winnerId)
    {
        DateTime endedAtUtc = DateTime.UtcNow;
        DateTime startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId) ?? endedAtUtc;
        var resultRows = _manittoChainManager.BuildGameResult(matchingId);
        var killCountsByPlayerId = resultRows
            .Where(row => row.attackerPlayerId != 0 && row.reason == EliminationReason.MENTAL_ZERO)
            .GroupBy(row => row.attackerPlayerId)
            .ToDictionary(group => group.Key, group => group.Count());

        var rows = resultRows
            .Select(d =>
            {
                var playerInfo = ResolveResultPlayerInfo(matchingId, d.playerId);
                var session = allSessions.FirstOrDefault(s => s.PlayerId == d.playerId);
                var bot = _botPlayerManager.GetBot(matchingId, d.playerId);
                var stats = _gameEventLogManager.GetSurvivorResultStats(matchingId, d.playerId);
                DateTime survivalEndUtc = d.eliminatedAt ?? endedAtUtc;
                int survivalSeconds = Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds));

                return new
                {
                    Info = new GameResultPlayerInfo
                    {
                        PlayerId = d.playerId,
                        Name = ResolveResultPlayerName(d.playerId, playerInfo, bot),
                        JobTitle = d.job,
                        TargetPlayerId = d.targetId,
                        ManittoPlayerId = d.manittoId,
                        EliminationReason = d.reason,
                        FinalStatus = d.finalStatus,
                        Corruption = session?.Corruption ?? bot?.Corruption ?? 0,
                        MaxCorruption = MaxCorruption,
                        WearItemIdList = playerInfo?.WearItemIdList != null
                            ? new List<int>(playerInfo.WearItemIdList)
                            : new List<int>(),
                        SurvivalTimeSeconds = survivalSeconds,
                        // 실제 탈락 결과를 기준으로 집계해 전투 로그 누락/중복과 무관하게 결과표를 맞춘다.
                        KillCount = killCountsByPlayerId.TryGetValue(d.playerId, out int killCount) ? killCount : 0,
                        TotalDamageDealt = stats.TotalDamageDealt,
                        TotalRecovery = stats.TotalRecovery,
                        AttackerPlayerId = d.attackerPlayerId,
                        EliminatedArea = d.eliminatedArea,
                        IsAreaClosureElimination = d.isAreaClosureElimination,
                        IsOvertimeElimination = d.isOvertimeElimination,
                        Rank = d.playerId == winnerId ? 1 : d.eliminationRank,
                        FinalOrbTier = d.playerId == winnerId ? ResolveFinalOrbTier(matchingId, d.playerId) : d.finalOrbTier
                    },
                    EliminatedAt = d.eliminatedAt
                };
            })
            .ToList();

        return GameResultRankingResolver.Resolve(rows.Select(row => row.Info), winnerId);
    }
    private PlayerInfo? ResolveResultPlayerInfo(long matchingId, long playerId)
    {
        if (BotPlayerManager.IsBotPlayerId(playerId))
            return _botPlayerManager.SynthesizePlayerInfo(matchingId, playerId);

        try
        {
            return PlayerInfo.Load(CacheHelper, playerId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "결과 프로필 PlayerInfo 조회 실패: PlayerId={PlayerId}", playerId);
            return null;
        }
    }

    private static string ResolveResultPlayerName(long playerId, PlayerInfo? playerInfo, BotPlayerState? bot)
    {
        if (!string.IsNullOrEmpty(playerInfo?.Name)) return playerInfo.Name;
        if (!string.IsNullOrEmpty(bot?.Name)) return bot.Name;
        return BotPlayerManager.IsBotPlayerId(playerId) ? $"Player{Math.Abs(playerId)}" : $"Player{playerId}";
    }

    /// <summary>매치 완료 뒤 재접속용 Redis handoff 데이터를 제거한다.</summary>
    private async Task CleanupRedisMatchingTransientStateAsync(long matchingId)
    {
        try
        {
            await CacheHelper.HashDeleteAsync("matching_bots", matchingId);
            string prefix = $"{matchingId}:";
            var fields = (await CacheHelper.HashGetAllAsync(PlayerBuffInfoKey))
                .Select(entry => entry.Name.ToString())
                .Where(field => field.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            foreach (string field in fields)
                await CacheHelper.HashDeleteAsync(PlayerBuffInfoKey, field);

            Logger.LogInformation(
                "Redis matching handoff 정리: MatchingId={MatchingId}, BuffFields={BuffFieldCount}",
                matchingId, fields.Length);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Redis matching handoff 정리 실패: MatchingId={MatchingId}", matchingId);
        }
    }

    /// <summary>
    ///     흔적 배치 요청 처리 (마니또 전용)
    /// </summary>
    private Task HandlePlaceTrace(C_TO_G_PLACE_TRACE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            using var lockedPacket = Packet.Create((int)Protocol.G_TO_C_PLACE_TRACE_RESULT, PlayerId.Value);
            var lockedResult = new G_TO_C_PLACE_TRACE_RESULT
            {
                ErrorCode = ErrorCode.INVALID_GAME_STATE,
                StaminaCost = 0
            };
            lockedPacket.SetBody(MessagePackSerializer.Serialize(lockedResult));
            Send(lockedPacket);
            return Task.CompletedTask;
        }

        const int placeTraceCost = 5; // 스태미나 소모 (패키지 Y: -10 → -5, #24)

        // 권고안 B 2026-05-05: Stamina 부족해도 ModifyStats가 Cor 1:2 변환 — 사전 차단 제거.
        ModifyStats(staminaDelta: -placeTraceCost);

        // 성공 응답
        using var packet = Packet.Create((int)Protocol.G_TO_C_PLACE_TRACE_RESULT, PlayerId.Value);
        var result = new G_TO_C_PLACE_TRACE_RESULT
        {
            ErrorCode = ErrorCode.SUCCESS,
            StaminaCost = placeTraceCost
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 흔적 저장 (탐색 시 발견됨)
        StoreTrace(CurrentArea, msg.InteractId, "누군가 무언가를 남겼다...", false);

        return Task.CompletedTask;
    }

    private Task HandlePlaceGift(C_TO_G_PLACE_GIFT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendPlaceGiftResult(ErrorCode.INVALID_GAME_STATE, msg, CurrentArea, TargetPlayerId);
            return Task.CompletedTask;
        }

        var inventoryItem = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value)
            .GetItem(msg.ItemUid);
        bool canPlaceGiftItem = inventoryItem != null &&
                                (inventoryItem.GiftState == GiftState.Prepared ||
                                 GameItemData.GetItemType(inventoryItem.ItemId) == ItemType.CONSUMABLE);
        if (inventoryItem == null || inventoryItem.ItemId != msg.ItemId || inventoryItem.Count <= 0 ||
            !canPlaceGiftItem)
        {
            SendPlaceGiftResult(ErrorCode.ITEM_NOT_FOUND, msg, CurrentArea, TargetPlayerId);
            return Task.CompletedTask;
        }

        var result = _missionManager.TryPlaceGift(
            CurrentMapSubId,
            PlayerId.Value,
            TargetPlayerId,
            msg.ItemUid,
            msg.ItemId,
            CurrentArea,
            msg.InteractId);

        if (!result.Success)
        {
            SendPlaceGiftResult(result.ErrorCode, msg, result.AreaType, result.TargetPlayerId);
            return Task.CompletedTask;
        }

        if (!_inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid, 1,
                out var updatedItem) || updatedItem == null)
        {
            _missionManager.RollbackPlacedGift(CurrentMapSubId, PlayerId.Value, msg.ItemUid);
            SendPlaceGiftResult(ErrorCode.ITEM_NOT_FOUND, msg, result.AreaType, result.TargetPlayerId);
            return Task.CompletedTask;
        }

        using (var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([updatedItem]))
        {
            Send(inventoryPacket);
        }

        SendPlaceGiftResult(ErrorCode.SUCCESS, msg, result.AreaType, result.TargetPlayerId);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, msg.InteractId);
        BroadcastRngCollectCooldown(msg.InteractId, 0);
        SendTargetBotToPlacedGift(result.TargetPlayerId, result.AreaType, result.InteractId);

        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            $"비밀 선물 설치: ItemId={msg.ItemId}, InteractId={msg.InteractId}, Target={TargetPlayerId}",
            isBot: false);

        return Task.CompletedTask;
    }

    private void SendTargetBotToPlacedGift(long targetPlayerId, AreaType areaType, int interactId)
    {
        if (!BotPlayerManager.IsBotPlayerId(targetPlayerId)) return;

        bool started = _botPlayerManager.TrySendBotToInteract(
            CurrentMapSubId,
            targetPlayerId,
            areaType,
            interactId,
            _areaClosureManager);

        if (started)
            Logger.LogInformation("선물 설치 후 타겟 봇 회수 이동: BotId={Bot}, InteractId={InteractId}",
                targetPlayerId, interactId);
    }

    private void SendPlaceGiftResult(ErrorCode errorCode, C_TO_G_PLACE_GIFT request, AreaType areaType,
        long targetPlayerId)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PLACE_GIFT_RESULT, PlayerId.Value);
        var msg = new G_TO_C_PLACE_GIFT_RESULT
        {
            ErrorCode = errorCode,
            ItemUid = request.ItemUid,
            ItemId = request.ItemId,
            InteractId = request.InteractId,
            TargetPlayerId = targetPlayerId,
            AreaType = areaType
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     마니또 → 타겟 구역 위치 전송
    /// </summary>
    public void SendTargetLocation()
    {
        if (!PlayerId.HasValue || TargetPlayerId == 0) return;
        // 1인 매칭으로 본인이 본인을 타겟으로 가지는 케이스 방어
        if (TargetPlayerId == PlayerId.Value) return;

        AreaType targetArea;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (targetSession != null)
        {
            targetArea = targetSession.CurrentArea;
        }
        else
        {
            // #26: 타겟이 봇인 경우 BotPlayerManager에서 위치 조회
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, TargetPlayerId);
            if (bot == null || bot.IsEliminated) return;
            targetArea = bot.CurrentArea;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_TARGET_LOCATION, PlayerId.Value);
        var msg = new G_TO_C_TARGET_LOCATION
        {
            TargetPlayerId = TargetPlayerId,
            AreaType = targetArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
        Logger.LogDebug("Target location sent: MatchingId={MatchingId}, PlayerId={PlayerId}, Target={TargetPlayerId}, Area={Area}",
            CurrentMapSubId, PlayerId, TargetPlayerId, targetArea);
    }

    /// <summary>
    ///     프로토 0 기척 갱신 (#159) — 타겟 제외 후보별 최근 25초 조우 강도(0~5) 전송
    /// </summary>
    public void SendPresenceUpdate(List<(long playerId, float presence, string name, List<int> wear)> candidates)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PRESENCE_UPDATE, PlayerId.Value);
        var msg = new G_TO_C_PRESENCE_UPDATE
        {
            Candidates = candidates
                .Select(c => new PresenceCandidate
                {
                    PlayerId = c.playerId,
                    Presence = c.presence,
                    Name = c.name,
                    WearItemIds = c.wear
                })
                .ToList()
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>Send accumulated presence records for the student notebook.</summary>
    public void SendPresenceNotebookUpdate(long matchingId, int roundNumber, List<PresenceNotebookRecord> records)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PRESENCE_NOTEBOOK_UPDATE, PlayerId.Value);
        var msg = new G_TO_C_PRESENCE_NOTEBOOK_UPDATE
        {
            MatchingId = matchingId,
            RoundNumber = roundNumber,
            Entries = records
                .Select(r => new PresenceNotebookEntry
                {
                    PlayerId = r.PlayerId,
                    LastSeenArea = r.LastSeenArea,
                    TotalOverlapSeconds = r.TotalOverlapSeconds,
                    LongestOverlapSeconds = r.LongestOverlapSeconds,
                    CurrentOverlapSeconds = r.CurrentOverlapSeconds,
                    OverlapStartCount = r.OverlapStartCount,
                    EnterAfterObserverCount = r.EnterAfterObserverCount,
                    AlreadyThereWhenObserverArrivedCount = r.AlreadyThereWhenObserverArrivedCount,
                    UnclassifiedOverlapStartCount = r.UnclassifiedOverlapStartCount,
                    IsCurrentlyOverlapping = r.IsCurrentlyOverlapping
                })
                .ToList()
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     #87: 블러프 우회(off-pool 오브젝트 사용) 시 추가 스태미나 비용. 기본 액션 비용에 더한다.
    ///     기존 -3 또는 0 → 총 -8이 되도록 추가량을 산정.
    /// </summary>
    private const int BluffBypassExtraStaminaCost = 5;

    /// <summary>
    ///     v0.2.0 — 탐색 완료 시 부품/선행 아이템 회수 체크.
    ///     object_action.csv의 result_type=1 (REWARD_POOL) 액션 선택 시 호출됨.
    ///     1) 자기 직책 발견 풀 매칭 → 부품 회수 (G_TO_C_PART_COLLECTED)
    ///     2) prerequisite_item.csv 매칭 → 선행 아이템 회수 (G_TO_C_PREREQUISITE_COLLECTED)
    ///     3) 둘 다 매칭 안되면 일반 탐색 — 미션 진행 없음
    ///         #87: 자기 직책 발견 풀에 없는 오브젝트 사용 시 우회 비용 -5 스태미나 추가 차감(N11).
    /// </summary>
    public void CheckMissionProgress(AreaType area, int interactId, int actionId)
    {
        if (!PlayerId.HasValue) return;

        // object_type 매핑 — interactId 기반으로 GameInteractableData에서 object_type 조회
        var interactable = GameInteractableData.Get(interactId);
        if (interactable == null) return;
        int objectType = (int)interactable.ObjectType;

        // 1. 부품 회수 시도 (자기 직책 소재 풀 매칭)
        var collectResult = _missionManager.TryCollectPart(CurrentMapSubId, PlayerId.Value, area, objectType, interactId);
        if (collectResult != null && collectResult.Success && collectResult.Part != null)
        {
            // 부품 회수 stamina 보상 제거 (#135)

            using var partPacket = Packet.Create((int)Protocol.G_TO_C_PART_COLLECTED, PlayerId.Value);
            var partMsg = new G_TO_C_PART_COLLECTED
            {
                PartId = collectResult.Part.PartId,
                PartNameKr = collectResult.Part.PartNameKr,
                PartTier = (int)collectResult.Part.PartTier,
                StaminaReward = 0
            };
            partPacket.SetBody(MessagePackSerializer.Serialize(partMsg));
            Send(partPacket);

            _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
                $"부품 회수: {collectResult.Part.PartNameKr} (체력+{collectResult.StaminaReward})", isBot: false);

            // 호환: 단계 완료 패킷도 송신 (구 클라이언트 호환용)
            using var legacyPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var legacyMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = collectResult.Part.PartId,
                StaminaReward = collectResult.StaminaReward,
                NextTargetArea = 0,
                NextTargetInteractId = 0,
                NextTargetActionId = 0
            };
            legacyPacket.SetBody(MessagePackSerializer.Serialize(legacyMsg));
            Send(legacyPacket);

            // 미션 흔적 저장 (탐색 시 다른 플레이어가 발견 가능)
            StoreTrace(area, interactId, GetMissionCollectTraceDescription(collectResult.CompletedMissionNodeIds), true);
            return;
        }

        // 2. 선행 아이템 회수 시도 (장갑/드라이버 등 share_group)
        if (TryCollectPrerequisiteWithNotice(area, objectType))
            return;

        // 3. 블러프 우회(off-pool) 비용 적용 (#87 N11)
        ApplyBluffBypassCost(area, objectType);
    }

    private string GetMissionCollectTraceDescription(IEnumerable<int> completedMissionNodeIds)
    {
        foreach (int nodeId in completedMissionNodeIds)
        {
            var node = GameMissionGraphData.GetNode(nodeId);
            string trace = node?.VisibleTrace?.Kr ?? "";
            if (!string.IsNullOrWhiteSpace(trace))
                return trace;
        }

        return "여기서 무언가 회수된 흔적이 남아있다.";
    }

    /// <summary>
    ///     #87: off-pool 오브젝트 탐색 시 추가 스태미나 차감.
    ///     자기 직책 1단계 발견 풀(소재 + 선행 아이템) 어느 것도 매칭되지 않은 (area, objectType) 사용에 적용.
    /// </summary>
    private void ApplyBluffBypassCost(AreaType area, int objectType)
    {
        if (!PlayerId.HasValue) return;

        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null) return;

        // 자기 직책의 모든 소재(Tier 0) (area, object_type) 풀 검사
        var materials = GameMissionData.GetMaterials((short)state.JobTitle);
        bool inMaterialPool = materials.Any(p =>
            p.TargetArea == (int)area && p.TargetObjectType == objectType);
        if (inMaterialPool) return;

        // 자기 직책의 선행 아이템 위치 풀 검사
        bool inPrereqPool = false;
        foreach (var part in materials)
        {
            if (part.PrerequisiteShareGroup <= 0) continue;
            var prereq = PrerequisiteItemData.GetForPart(part.PartId);
            if (prereq == null) continue;
            if (prereq.LocationArea == (int)area && prereq.LocationObjectType == objectType)
            {
                inPrereqPool = true;
                break;
            }
        }
        if (inPrereqPool) return;

        // 우회 비용 차감 + 클라이언트 동기화
        int prevStamina = Stamina;
        ModifyStats(staminaDelta: -BluffBypassExtraStaminaCost);
        int delta = Stamina - prevStamina;
        Logger.LogInformation(
            "블러프 우회 비용: PlayerId={PlayerId}, Area={Area}, ObjType={ObjType}, -{Cost}=({Delta})",
            PlayerId, area, objectType, BluffBypassExtraStaminaCost, delta);
    }

    /// <summary>
    ///     선행 아이템 회수 시도 + 클라이언트 알림. 매칭 시 true.
    /// </summary>
    private bool TryCollectPrerequisiteWithNotice(AreaType area, int objectType)
    {
        if (!PlayerId.HasValue) return false;

        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null) return false;

        // 매칭 prereq 직접 찾기 (이름/share_group 알림용)
        var materials = GameMissionData.GetMaterials((short)state.JobTitle);
        PrerequisiteItem? matchedPrereq = null;
        foreach (var part in materials)
        {
            if (part.PrerequisiteShareGroup <= 0) continue;
            var prereq = PrerequisiteItemData.GetForPart(part.PartId);
            if (prereq == null) continue;
            if (prereq.LocationArea == (int)area && prereq.LocationObjectType == objectType)
            {
                matchedPrereq = prereq;
                break;
            }
        }
        if (matchedPrereq == null) return false;

        bool success = _missionManager.TryCollectPrerequisite(CurrentMapSubId, PlayerId.Value, area, objectType);
        if (!success) return false;

        // 선행 아이템 회수 stamina 보상 제거 (#135)
        const int prerequisiteStaminaReward = 0;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PREREQUISITE_COLLECTED, PlayerId.Value);
        var msg = new G_TO_C_PREREQUISITE_COLLECTED
        {
            ShareGroup = matchedPrereq.ShareGroup,
            ItemNameKr = matchedPrereq.ItemNameKr,
            StaminaReward = prerequisiteStaminaReward
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);

        return true;
    }

    private Task HandleBotInteractResponse(long botPlayerId, bool accepted)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        if (_pendingBotRequesterPlayerId != botPlayerId)
        {
            Logger.LogWarning(
                "BotInteractResponse failed: pending mismatch (Expected={Expected}, Actual={Actual}, Player={Player})",
                _pendingBotRequesterPlayerId, botPlayerId, PlayerId.Value);
            using var errPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(
                false,
                botPlayerId,
                ErrorCode.INVALID_REQUEST);
            Send(errPacket);
            return Task.CompletedTask;
        }

        CancelTargetBotInterrogationTimeout();
        _pendingBotRequesterPlayerId = null;

        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null || bot.IsEliminated || bot.CurrentArea != CurrentArea)
        {
            ReleaseTargetBotInterrogation(botPlayerId, resetEncounterDelay: true);
            using var errPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(
                false,
                botPlayerId,
                bot == null || bot.IsEliminated ? ErrorCode.PLAYER_NOT_FOUND : ErrorCode.AREA_MISMATCH);
            Send(errPacket);
            return Task.CompletedTask;
        }

        using (var resultPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(accepted, botPlayerId, ErrorCode.SUCCESS))
        {
            Send(resultPacket);
        }

        if (!accepted)
        {
            ReleaseTargetBotInterrogation(botPlayerId, resetEncounterDelay: true);
            _lastInteractRejectTime = DateTime.UtcNow;
            Logger.LogInformation(
                "타겟 봇 선심문 거절: BotId={Bot}, PlayerId={Player}",
                botPlayerId, PlayerId.Value);
            return Task.CompletedTask;
        }

        SendTargetBotInterrogationAnswerChoices(bot);
        return Task.CompletedTask;
    }

    private void SendTargetBotInterrogationAnswerChoices(BotPlayerState bot)
    {
        if (!PlayerId.HasValue) return;

        var questionSet = _interactionChoiceService.GenerateQuestionSet(
            CurrentMapSubId,
            bot.PlayerId,
            PlayerId.Value,
            bot.CurrentArea,
            _previousArea);
        var questions = questionSet.Questions;
        var question = questions.FirstOrDefault(q => q.QuestionType == InteractionQuestionType.ASK_LOCATION)
                       ?? questions.FirstOrDefault();
        if (question == null) return;

        _activeConversationPlayerId = bot.PlayerId;
        _lastAskedQuestion = question.QuestionType;
        _pendingQuestions = null;
        _pendingQuestionContexts = questionSet.Contexts;
        var answerTask = ResolveInteractionAnswerTask(PlayerId.Value);
        var answerSet = _interactionChoiceService.GenerateAnswerSet(
            CurrentMapSubId,
            PlayerId.Value,
            bot.PlayerId,
            _lastAskedQuestion,
            CurrentArea,
            questionSet.Contexts.FirstOrDefault(c => c.QuestionType == _lastAskedQuestion),
            ResolveTaskArea(answerTask),
            answerTask?.TaskId ?? 0);
        _pendingAnswers = answerSet.Answers;
        _pendingAnswerContexts = answerSet.Contexts;

        bot.HoldForInteraction(TimeSpan.FromMinutes(5));
        bot.LoopWaitUntil = DateTime.MinValue;

        using var answerChoicesPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ANSWER_CHOICES, PlayerId.Value);
        var answerChoices = new G_TO_C_INTERACTION_ANSWER_CHOICES
        {
            QuestionType = _lastAskedQuestion,
            QuestionTextId = question.TextId,
            QuestionArgs = question.Args,
            Answers = _pendingAnswers
        };
        answerChoicesPacket.SetBody(MessagePackSerializer.Serialize(answerChoices));
        Send(answerChoicesPacket);

        Logger.LogInformation(
            "타겟 봇 선심문 시작: BotId={Bot}, PlayerId={Player}, Area={Area}",
            bot.PlayerId, PlayerId.Value, bot.CurrentArea);
    }

    private void StartTargetBotInterrogationTimeout(long botPlayerId)
    {
        CancelTargetBotInterrogationTimeout();

        _botInteractTimeoutCts = new CancellationTokenSource();
        var cts = _botInteractTimeoutCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                if (_pendingBotRequesterPlayerId != botPlayerId) return;

                _pendingBotRequesterPlayerId = null;
                ReleaseTargetBotInterrogation(botPlayerId, resetEncounterDelay: true);
                _lastInteractRejectTime = DateTime.UtcNow;

                using var timeoutPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(
                    false,
                    botPlayerId,
                    ErrorCode.TIMEOUT);
                Send(timeoutPacket);

                Logger.LogInformation(
                    "타겟 봇 선심문 타임아웃: BotId={Bot}, PlayerId={Player}",
                    botPlayerId, PlayerId);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "타겟 봇 선심문 타임아웃 처리 오류: BotId={Bot}, PlayerId={Player}",
                    botPlayerId, PlayerId);
            }
        }, cts.Token);
    }

    private void CancelTargetBotInterrogationTimeout()
    {
        _botInteractTimeoutCts?.Cancel();
        _botInteractTimeoutCts?.Dispose();
        _botInteractTimeoutCts = null;
    }

    private void StartTargetBotInterrogationChoiceDelay(long botPlayerId)
    {
        CancelTargetBotInterrogationTimeout();

        _botInteractTimeoutCts = new CancellationTokenSource();
        var cts = _botInteractTimeoutCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2), cts.Token);
                if (_activeConversationPlayerId != botPlayerId) return;

                var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
                if (bot == null || bot.IsEliminated || bot.CurrentArea != CurrentArea)
                {
                    if (_activeConversationPlayerId == botPlayerId) _activeConversationPlayerId = null;
                    ReleaseTargetBotInterrogation(botPlayerId, resetEncounterDelay: true);
                    using var errorPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(
                        false,
                        botPlayerId,
                        bot == null || bot.IsEliminated ? ErrorCode.PLAYER_NOT_FOUND : ErrorCode.AREA_MISMATCH);
                    Send(errorPacket);
                    return;
                }

                SendTargetBotInterrogationAnswerChoices(bot);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Target bot interrogation choice delay failed: BotId={Bot}, PlayerId={Player}",
                    botPlayerId, PlayerId);
            }
        }, cts.Token);
    }

    private void ReleaseTargetBotInterrogation(long botPlayerId, bool resetEncounterDelay = false)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null) return;

        bot.IsInInteraction = false;
        bot.InteractionStayUntil = DateTime.MinValue;
        bot.LoopWaitUntil = DateTime.UtcNow.AddSeconds(5);

        if (resetEncounterDelay && PlayerId.HasValue)
            bot.TargetEncounterStartedAtByPlayerId[PlayerId.Value] = DateTime.UtcNow;
    }

    /// <summary>
    ///     스태미나 보상 적용 + 클라이언트 동기화.
    /// </summary>
    private void ApplyStaminaReward(int reward)
    {
        if (reward <= 0) return;
        int prevStamina = Stamina;
        Stamina = Math.Min(Stamina + reward, MaxStamina);
        int staminaDelta = Stamina - prevStamina;
        using var statsPacket = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Stamina, staminaDelta, Corruption, 0);
        Send(statsPacket);
    }

    /// <summary>
    ///     #135 — 결합 후 인벤토리 sync: input 부품 제거(Count=0 알림) + output 부품 추가 한 번에 broadcast.
    /// </summary>
    private void SyncInventoryAfterCombine(int inputA, int inputB, int outputPartId)
    {
        if (!PlayerId.HasValue) return;

        var items = new List<InGameItemInfo>();

        foreach (int input in new[] { inputA, inputB })
        {
            int inputItemId = GameMissionData.GetPartItemId(input);
            if (inputItemId == 0) continue;

            var removed = _inGameInventoryManager.RemoveItemByItemId(CurrentMapSubId, PlayerId.Value, inputItemId);
            if (removed != null)
                items.Add(new InGameItemInfo { ItemUid = removed.ItemUid, ItemId = removed.ItemId, Count = 0 });
        }

        if (outputPartId > 0)
        {
            int outputItemId = GameMissionData.GetPartItemId(outputPartId);
            if (outputItemId == 0) return;

            var added = _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, outputItemId, 1,
                GiftState.Prepared);
            items.Add(added);
        }

        if (items.Count > 0)
        {
            using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(items);
            Send(packet);
        }
    }

    /// <summary>
    ///     v0.2.0 — 부품 결합 요청 처리. 두 부품 결합 시도 → 결과 송신.
    ///     #87: 최종 결합(IsRaceComplete) 시 즉시 게임 종료 — 다른 생존자 RACE_LOST 처리.
    /// </summary>
    private Task HandleCombineParts(C_TO_G_COMBINE_PARTS msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendCombinePartsFailure(msg.PartA, msg.PartB, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }
        if (!HasInGamePartItem(msg.PartA) || !HasInGamePartItem(msg.PartB))
        {
            if (TryHandleBattleItemCombine(msg))
                return Task.CompletedTask;

            SendCombinePartsFailure(msg.PartA, msg.PartB, ErrorCode.INSUFFICIENT_ITEM);
            return Task.CompletedTask;
        }

        var result = _missionManager.TryCombineParts(
            CurrentMapSubId, PlayerId.Value, msg.PartA, msg.PartB, msg.ClientStartUnixMs,
            requireCollectedParts: false);
        if (!result.Success)
        {
            SendCombinePartsFailure(msg.PartA, msg.PartB, result.ErrorCode);
            return Task.CompletedTask;
        }

        // 결합 stamina 보상 제거 (#135)

        // 결합 결과 송신
        using var packet = Packet.Create((int)Protocol.G_TO_C_PART_COMBINED, PlayerId.Value);
        var combinedMsg = new G_TO_C_PART_COMBINED
        {
            RecipeId = result.Recipe?.Id ?? 0,
            InputPartA = msg.PartA,
            InputPartB = msg.PartB,
            OutputPartId = result.OutputPart?.PartId ?? 0,
            OutputPartNameKr = result.OutputPart?.PartNameKr ?? "",
            StaminaReward = result.StaminaReward,
            IsRaceComplete = result.IsRaceComplete
        };
        packet.SetBody(MessagePackSerializer.Serialize(combinedMsg));
        Send(packet);

        // #135 — 인벤토리 동기화: input 부품 제거 + output 선물 파트 추가
        SyncInventoryAfterCombine(msg.PartA, msg.PartB, result.OutputPart?.PartId ?? 0);

        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            result.IsRaceComplete
                ? $"최종 결합 완성! ({result.OutputPart?.PartNameKr ?? ""}) — race 완주"
                : $"부품 결합: {result.OutputPart?.PartNameKr ?? ""} (체력+{result.StaminaReward})",
            isBot: false);

        // 호환: 최종 결합 시 ALL_COMPLETE 패킷 송신 (구 클라이언트 호환)
        if (result.IsRaceComplete)
        {
            using var allCompletePacket = Packet.Create((int)Protocol.G_TO_C_MISSION_ALL_COMPLETE, PlayerId.Value);
            var allCompleteMsg = new G_TO_C_MISSION_ALL_COMPLETE { JobTitle = MyJobTitle };
            allCompletePacket.SetBody(MessagePackSerializer.Serialize(allCompleteMsg));
            Send(allCompletePacket);

            Logger.LogInformation("race 완주: PlayerId={PlayerId}, 직책={Job} — 즉시 게임 종료", PlayerId, MyJobTitle);

            // #87: 30초 봉쇄 폐기 — race 완주 즉시 게임 종료
            EndGameByRaceCompletion(PlayerId.Value);
        }

        return Task.CompletedTask;
    }

    private bool HasInGamePartItem(int partId)
    {
        int itemId = GameMissionData.GetPartItemId(partId);
        if (itemId == 0) return false;

        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId!.Value);
        return inventory.GetItemCount(itemId) > 0;
    }

    private bool TryHandleBattleItemCombine(C_TO_G_COMBINE_PARTS msg)
    {
        if (!PlayerId.HasValue) return false;

        bool isSurvivorOrbRequest = SurvivorOrbData.IsSurvivorOrb(msg.PartA) ||
                                    SurvivorOrbData.IsSurvivorOrb(msg.PartB);
        if (isSurvivorOrbRequest)
        {
            var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            bool hadResonance = inventory.TryGetActiveSurvivorOrbPair(out SurvivorOrbColor previousResonanceColor,
                out int previousSupportTier);
            if (!_inGameInventoryManager.TryCombineSurvivorOrbs(
                    CurrentMapSubId,
                    PlayerId.Value,
                    msg.PartA,
                    msg.PartB,
                    Random.Shared,
                    out int outputItemId,
                    out var changedItems))
            {
                SendCombinePartsFailure(msg.PartA, msg.PartB, ErrorCode.INVALID_PARAMETER);
                return true;
            }

            SendBattleItemCombineResult(msg, outputItemId, changedItems, recipeId: 0);

            bool resonanceActive = inventory.TryGetActiveSurvivorOrbPair(out SurvivorOrbColor resonanceColor,
                out int supportTier);
            SurvivorOrbData.TryGetColorAndTier(outputItemId, out SurvivorOrbColor outputColor, out int outputTier);
            _gameEventLogManager.LogSurvivorOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, inventory.GetAllItems(),
                inventory.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "merge", isBot: false);
            _gameEventLogManager.LogMission(
                CurrentMapSubId,
                PlayerId.Value,
                $"SURVIVOR_ORB_MERGE inputs=[{msg.PartA},{msg.PartB}] output={outputItemId} " +
                $"outputColor={outputColor} outputTier={outputTier} " +
                $"resonanceBefore={(hadResonance ? previousResonanceColor.ToString() : "off")}/T{previousSupportTier} " +
                $"resonanceAfter={(resonanceActive ? resonanceColor.ToString() : "off")}/T{supportTier} " +
                $"area={CurrentArea} nextArea=pending",
                isBot: false);

            var outputCombatData = BattleItemCombatData.Get(outputItemId);
            _gameEventLogManager.LogSurvivorTierReached(
                CurrentMapSubId,
                PlayerId.Value,
                outputItemId,
                outputCombatData?.Tier ?? 0,
                isBot: false);
            return true;
        }

        var recipe = BattleItemRecipeData.PickRandomRecipe(
            new[] { msg.PartA, msg.PartB },
            CurrentArea,
            Random.Shared);
        if (recipe == null)
            return false;

        if (!_inGameInventoryManager.TryCombineItems(
                CurrentMapSubId,
                PlayerId.Value,
                recipe.InputItemIds,
                recipe.OutputItemId,
                out var legacyChangedItems))
        {
            SendCombinePartsFailure(msg.PartA, msg.PartB, ErrorCode.INSUFFICIENT_ITEM);
            return true;
        }

        SendBattleItemCombineResult(msg, recipe.OutputItemId, legacyChangedItems, recipe.RecipeId);
        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            $"Battle item combine: {msg.PartA} + {msg.PartB} => {recipe.OutputItemId}",
            isBot: false);

        var combinedCombatData = BattleItemCombatData.Get(recipe.OutputItemId);
        _gameEventLogManager.LogSurvivorTierReached(
            CurrentMapSubId,
            PlayerId.Value,
            recipe.OutputItemId,
            combinedCombatData?.Tier ?? 0,
            isBot: false);
        return true;
    }

    private void SendBattleItemCombineResult(C_TO_G_COMBINE_PARTS msg, int outputItemId,
        IReadOnlyCollection<InGameItemInfo> changedItems, int recipeId)
    {
        var outputItem = changedItems.LastOrDefault(item => item.ItemId == outputItemId && item.Count > 0);
        var equippedBattleItem = _inGameInventoryManager.GetEquippedBattleItem(CurrentMapSubId, PlayerId!.Value);
        bool shouldReplaceEquippedItem = outputItem != null && equippedBattleItem?.ItemUid == outputItem.ItemUid;

        using var combinePacket = Packet.Create((int)Protocol.G_TO_C_PART_COMBINED, PlayerId.Value);
        var itemData = GameItemData.Get(outputItemId);
        var combinedMsg = new G_TO_C_PART_COMBINED
        {
            RecipeId = recipeId,
            InputPartA = msg.PartA,
            InputPartB = msg.PartB,
            OutputPartId = outputItemId,
            OutputPartNameKr = itemData?.Name?.Kr ?? "",
            StaminaReward = 0,
            IsRaceComplete = false
        };
        combinePacket.SetBody(MessagePackSerializer.Serialize(combinedMsg));
        Send(combinePacket);

        // Inventory update follows the result packet so the client reveals the server-authoritative outcome.
        using var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(changedItems.ToList());
        Send(inventoryPacket);

        if (shouldReplaceEquippedItem)
        {
            using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
                true, outputItem!.ItemUid, ErrorCode.SUCCESS);
            Send(equippedPacket);
        }
    }
    private void SendCombinePartsFailure(int partA, int partB, ErrorCode errorCode)
    {
        using var failPacket = Packet.Create((int)Protocol.G_TO_C_PART_COMBINED, PlayerId!.Value);
        var failMsg = new G_TO_C_PART_COMBINED
        {
            RecipeId = 0,
            InputPartA = partA,
            InputPartB = partB,
            OutputPartId = 0,
            OutputPartNameKr = "",
            StaminaReward = 0,
            IsRaceComplete = false
        };
        failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
        Send(failPacket);

        SendErrorResponse(errorCode, "부품 결합 실패");
    }

    /// <summary>
    ///     #26: 봇 race 완주 시 게임 즉시 종료. 임의 세션에서 호출되어 winner는 봇 PlayerId.
    /// </summary>
    public void EndGameByBotRaceCompletion(long botWinnerId)
    {
        EndGameByRaceCompletion(botWinnerId);
    }

    /// <summary>
    ///     #26: 봇 탈락에 의한 체인 단절 영향을 본 세션에 반영.
    ///     ManittoStatus 갱신 + ELIMINATED가 아닌 경우 G_TO_C_CHAIN_BREAK 송신.
    /// </summary>
    public void ApplyChainBreakStatus(ManittoStatus newStatus, long eliminatedPlayerId)
    {
        ManittoStatus = newStatus == ManittoStatus.ELIMINATED
            ? ManittoStatus.SPECTATING
            : newStatus;

        if (newStatus == ManittoStatus.ELIMINATED) return;
        if (!PlayerId.HasValue) return;

        using var chainPacket = Packet.Create((int)Protocol.G_TO_C_CHAIN_BREAK, PlayerId.Value);
        var chainMsg = new G_TO_C_CHAIN_BREAK
        {
            EliminatedPlayerId = eliminatedPlayerId,
            NewStatus = newStatus
        };
        chainPacket.SetBody(MessagePackSerializer.Serialize(chainMsg));
        Send(chainPacket);
    }

    /// <summary>
    ///     #26: 봇 색출 적중 시 마니또(피탈자) 탈락 처리. 임의 세션이 트리거 역할만 수행.
    /// </summary>
    public void ProcessBotDetectedElimination(long manittoPlayerId, long? detecterBotId = null)
    {
        _ = ProcessElimination(manittoPlayerId, EliminationReason.DETECTED, detecterBotId);
    }

    /// <summary>
    ///     #87: race 완주에 의한 게임 즉시 종료.
    ///     완주자를 winner로 하고, 그 외 모든 생존자를 RACE_LOST 사유로 탈락 처리한 뒤 결과 패킷을 전송한다.
    /// </summary>
    private void EndGameByRaceCompletion(long winnerId)
    {
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning("[DEV] 게임 종료 차단됨 (DISABLE_GAME_END=1): EndGameByRaceCompletion winner={Winner}",
                winnerId);
            return;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        // 완주자 외 모든 생존자 탈락 (RACE_LOST)
        foreach (var session in allSessions)
        {
            if (!session.PlayerId.HasValue) continue;
            if (session.PlayerId.Value == winnerId) continue;
            if (session.IsEliminated) continue;

            // 체인 매니저에 탈락 등록 (chain break 브로드캐스트는 생략 — 어차피 즉시 게임 종료)
            _manittoChainManager.EliminatePlayer(CurrentMapSubId, session.PlayerId.Value, EliminationReason.RACE_LOST);
            session.ManittoStatus = ManittoStatus.SPECTATING;
        }

        // 완주자 본인은 ELIMINATED가 아니므로 별도 처리 없음 (BuildGameResult에서 정상 노출)
        Logger.LogInformation("게임 즉시 종료(race 완주): MatchingId={MatchingId}, Winner={WinnerId}",
            CurrentMapSubId, winnerId);

        SendGameResult(allSessions, winnerId, isTimeout: false, CurrentMapSubId, "race_completion");
    }

    /// <summary>
    ///     흔적 저장 (TraceManager에 등록, 오브젝트 탐색 시 발견됨)
    /// </summary>
    private void StoreTrace(AreaType area, int interactId, string description, bool isMissionTrace)
    {
        if (!PlayerId.HasValue) return;

        _traceManager.AddTrace(CurrentMapSubId, area, interactId, description, PlayerId.Value, isMissionTrace);
        Logger.LogInformation("흔적 저장: PlayerId={PlayerId}, Area={Area}, InteractId={InteractId}, Mission={IsMission}",
            PlayerId, area, interactId, isMissionTrace);
    }

    /// <summary>
    ///     오브젝트 탐색 시 흔적 발견 체크.
    ///     해당 오브젝트에 미발견 흔적이 있으면 발견 처리 + 정신력 효과 적용.
    /// </summary>
    public void CheckTraceDiscovery(int interactId)
    {
        if (!PlayerId.HasValue) return;

        var undiscovered = _traceManager.GetUndiscoveredTraces(CurrentMapSubId, interactId, PlayerId.Value);
        if (undiscovered.Count == 0) return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        foreach (var stored in undiscovered)
        {
            _traceManager.MarkDiscovered(CurrentMapSubId, interactId, PlayerId.Value, stored.TraceId);

            // 발견자에게 흔적 알림
            var traceInfo = new TraceInfo
            {
                TraceId = stored.TraceId,
                AreaType = stored.AreaType,
                InteractId = stored.InteractId,
                Description = stored.Description,
                PlacedByPlayerId = stored.PlacedByPlayerId,
                IsMissionTrace = stored.IsMissionTrace
            };

            using var packet = Packet.Create((int)Protocol.G_TO_C_TRACE_CREATED);
            var msg = new G_TO_C_TRACE_CREATED { Trace = traceInfo };
            packet.SetBody(MessagePackSerializer.Serialize(msg));
            Send(packet);

            // 마니또 배치 흔적만 정신력 효과 적용 (미션 흔적은 단서 역할만)
            if (!stored.IsMissionTrace)
            {
                // GDD 2.3.2: 마니또(배치자) 정신력 회복
                var placerSession = allSessions.FirstOrDefault(s => s.PlayerId == stored.PlacedByPlayerId);
                if (placerSession != null)
                {
                    placerSession.ModifyStats(corruptionDelta: -GameServer.TraceFoundManittoRecovery);
                    Logger.LogInformation("흔적 발견 → 마니또 회복: PlayerId={Placer}, -오염도{Amount}",
                        stored.PlacedByPlayerId, GameServer.TraceFoundManittoRecovery);
                }

                // GDD 2.3.2: 발견자(▓▓) 오염도 증가 ("누군가 당신을 지켜보고 있습니다")
                ModifyStats(corruptionDelta: GameServer.TraceFoundTargetDecay);
                Logger.LogInformation("흔적 발견 → 발견자 오염도 증가: PlayerId={Discoverer}, +오염도{Amount}",
                    PlayerId, GameServer.TraceFoundTargetDecay);
                CheckResourceElimination();
            }

            Logger.LogInformation("흔적 발견: PlayerId={Discoverer}, TraceId={TraceId}, 배치자={Placer}",
                PlayerId, stored.TraceId, stored.PlacedByPlayerId);
        }
    }

    private const int GiftFoundCorruptionDelta = 30;

    public void CheckGiftDiscovery(int interactId)
    {
        if (!PlayerId.HasValue) return;
        if (!_missionManager.TryDiscoverGift(CurrentMapSubId, PlayerId.Value, interactId, out var result)) return;

        if (result.DiscoveryType == GiftDiscoveryType.Other)
        {
            SendGiftDiscovered(result, 0);
            return;
        }

        ModifyStats(corruptionDelta: GiftFoundCorruptionDelta);
        SendGiftDiscovered(result, GiftFoundCorruptionDelta);
        SendGiftProgressToOwner(result);

        CheckResourceElimination();
    }

    private void SendGiftDiscovered(GiftDiscoveryResult result, int corruptionDelta)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_GIFT_DISCOVERED, PlayerId.Value);
        var msg = new G_TO_C_GIFT_DISCOVERED
        {
            DiscoveryType = result.DiscoveryType,
            InteractId = result.InteractId,
            ItemId = result.ItemId,
            CorruptionDelta = corruptionDelta
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private void SendGiftProgressToOwner(GiftDiscoveryResult result)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var ownerSession = sessions.FirstOrDefault(s => s.PlayerId == result.OwnerPlayerId);
        if (ownerSession == null)
        {
            if (BotPlayerManager.IsBotPlayerId(result.OwnerPlayerId))
                _checklistManager.TryCompleteTargetGiftTask(CurrentMapSubId, result.OwnerPlayerId);
            return;
        }

        ownerSession.CompleteTargetGiftChecklist();

        using var packet = Packet.Create((int)Protocol.G_TO_C_GIFT_PROGRESS, result.OwnerPlayerId);
        var msg = new G_TO_C_GIFT_PROGRESS
        {
            DeliveredCount = result.DeliveredCount,
            RequiredCount = result.RequiredCount,
            FinalPartId = result.FinalPartId,
            IsRaceComplete = result.IsRaceComplete,
            InteractId = result.InteractId,
            AreaType = result.AreaType,
            HasPlacedGiftAtInteract = result.HasPlacedGiftAtInteract,
            HasPlacedGiftInArea = result.HasPlacedGiftInArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        ownerSession.Send(packet);
    }

    /// <summary>
    ///     정신력 100 도달 시 탈락 체크. 권고안 B(2026-05-05): Stamina 0 단독으로는 탈락 트리거 안 됨
    ///     (대신 ModifyStats가 Stamina 부족분을 Corruption 1:2 변환).
    /// </summary>
    public void CheckResourceElimination(long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false)
    {
        if (!PlayerId.HasValue || _isGameEnded || IsEliminated) return;
        if (Corruption < MaxCorruption) return;

        Logger.LogInformation(
            "[Resource] Mental depleted: PlayerId={PlayerId}, Corruption={Corruption}/{MaxCorruption}. Eliminating player.",
            PlayerId.Value, Corruption, MaxCorruption);
        _ = ProcessElimination(PlayerId.Value, EliminationReason.MENTAL_ZERO,
            attackerPlayerId: attackerPlayerId, isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);
    }

    // ===== 시한부 사보타주 (GDD 2.5.4, 패키지 Y 4B, #24) =====

    private const int SabotageStaminaCost = 25; // 스태미나 소모 (-25, GDD 확정)
    private const int SabotageExposeSeconds = 5; // ▓▓ 위치 공개 지속 시간 (4B 옵션)

    /// <summary>
    ///     시한부 전용: 사보타주 처리 (패키지 Y 4B).
    ///     대상 1명 지정 → 현재 미션 단계 무효화(보상 없음) + ▓▓ 위치를 모든 생존자에게 5초 공개.
    ///     GDD 2.5.4: "단계 무효화 + ▓▓ 위치 5초 공개"
    /// </summary>
    private Task HandleSabotageMission(C_TO_G_SABOTAGE_MISSION msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendSabotageResult(ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        // 시한부만 사보타주 가능
        if (ManittoStatus != ManittoStatus.TERMINAL)
        {
            SendSabotageResult(ErrorCode.SABOTAGE_NOT_TERMINAL, SabotageStaminaCost);
            return Task.CompletedTask;
        }

        // 권고안 B 2026-05-05: 시한부 사보타주도 Cor 1:2 변환 허용 (-25 → +50 cor = 자가 탈락 위험으로 자연 균형).

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        // 대상 세션 확인 (targetPlayerId가 없으면 레거시 interactId 모드로 폴백)
        GameClientSession? targetSession = null;
        if (msg.TargetPlayerId != 0)
            targetSession = allSessions.FirstOrDefault(s => s.PlayerId == msg.TargetPlayerId);

        // E3 사전 체크: 대상이 이미 미션을 완료한 경우 스태미나 미차감 + 실패 응답 (GDD §2.5.4, #56)
        if (targetSession?.PlayerId.HasValue == true)
        {
            var targetState = _missionManager.GetState(CurrentMapSubId, targetSession.PlayerId.Value);
            if (targetState?.IsCompleted == true)
            {
                SendSabotageResult(ErrorCode.MISSION_ALREADY_COMPLETED, SabotageStaminaCost);
                return Task.CompletedTask;
            }
        }

        // 스태미나 차감
        ModifyStats(staminaDelta: -SabotageStaminaCost);

        // 성공 응답 (요청자에게)
        SendSabotageResult(ErrorCode.SUCCESS, SabotageStaminaCost);

        // === v0.2.0: 대상의 가장 가치 높은 부품 1개 무효화 ===
        if (targetSession != null && targetSession.PlayerId.HasValue)
        {
            int? invalidatedPartId = _missionManager.InvalidateHighestPart(
                CurrentMapSubId, targetSession.PlayerId.Value);

            if (invalidatedPartId.HasValue)
            {
                var part = GameMissionData.GetPart(invalidatedPartId.Value);
                using var invalidatedPacket = Packet.Create(
                    (int)Protocol.G_TO_C_PART_INVALIDATED, targetSession.PlayerId.Value);
                var invalidatedMsg = new G_TO_C_PART_INVALIDATED
                {
                    PartId = invalidatedPartId.Value,
                    PartNameKr = part?.PartNameKr ?? "",
                    PartTier = part != null ? (int)part.PartTier : 0,
                    TargetPlayerId = targetSession.PlayerId.Value
                };
                invalidatedPacket.SetBody(MessagePackSerializer.Serialize(invalidatedMsg));
                targetSession.Send(invalidatedPacket);

                Logger.LogInformation("사보타주 부품 무효화: Terminal={Terminal}, Target={Target}, PartId={PartId}",
                    PlayerId, targetSession.PlayerId, invalidatedPartId.Value);
            }
        }

        // === 4B Step 2: ▓▓ 위치를 모든 생존자에게 5초 공개 ===
        // 시한부 본인의 타겟(▓▓) 위치 공개
        var myTargetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (myTargetSession != null)
        {
            using var exposePacket = Packet.Create((int)Protocol.G_TO_C_SABOTAGE_TARGET_EXPOSED);
            var exposeMsg = new G_TO_C_SABOTAGE_TARGET_EXPOSED
            {
                TerminalPlayerId = PlayerId.Value,
                TargetPlayerId = TargetPlayerId,
                TargetAreaType = myTargetSession.CurrentArea,
                ExposeDurationSeconds = SabotageExposeSeconds
            };
            exposePacket.SetBody(MessagePackSerializer.Serialize(exposeMsg));
            // 모든 생존자에게 브로드캐스트
            foreach (var s in allSessions.Where(s => !s.IsEliminated))
                s.Send(exposePacket);

            Logger.LogInformation(
                "사보타주 4B — ▓▓ 위치 공개: Terminal={PlayerId}, Target={Target}, Area={Area}, {Sec}초",
                PlayerId, TargetPlayerId, myTargetSession.CurrentArea, SabotageExposeSeconds);
        }

        Logger.LogInformation("사보타주: PlayerId={PlayerId}, TargetPlayerId={Target}, InteractId={InteractId}",
            PlayerId, msg.TargetPlayerId, msg.InteractId);

        // 사보타주 후 스태미나 탈락 체크
        CheckResourceElimination();

        return Task.CompletedTask;
    }

    private void SendSabotageResult(ErrorCode errorCode, int cost)
    {
        if (!PlayerId.HasValue) return;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SABOTAGE_RESULT, PlayerId.Value);
        var msg = new G_TO_C_SABOTAGE_RESULT { ErrorCode = errorCode, StaminaCost = cost };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    // ===== 상호작용 선택지 =====

    /// <summary>
    ///     대화 수락 시 양쪽에 선택지 전송 (질문자=requester, 답변자=responder)
    /// </summary>
    private void SendInteractionChoices(GameClientSession askerSession, GameClientSession answererSession)
    {
        if (!askerSession.PlayerId.HasValue || !answererSession.PlayerId.HasValue) return;

        if (TrySendRoomEncounterActionChoices(askerSession, answererSession))
            return;

        // 질문 선택지 생성
        var questionSet = _interactionChoiceService.GenerateQuestionSet(
            CurrentMapSubId,
            askerSession.PlayerId.Value,
            answererSession.PlayerId.Value,
            CurrentArea,
            answererSession._previousArea);
        var questions = questionSet.Questions;

        askerSession._pendingQuestions = questions;
        askerSession._pendingQuestionContexts = questionSet.Contexts;

        // 질문자에게 선택지 전송
        using var askerPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_CHOICES, askerSession.PlayerId.Value);
        var askerMsg = new G_TO_C_INTERACTION_CHOICES
        {
            PartnerPlayerId = answererSession.PlayerId.Value,
            IsAsker = true,
            Questions = questions
        };
        askerPacket.SetBody(MessagePackSerializer.Serialize(askerMsg));
        askerSession.Send(askerPacket);

        // 답변자에게 대기 알림 (질문 선택 대기)
        using var answererPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_CHOICES, answererSession.PlayerId.Value);
        var answererMsg = new G_TO_C_INTERACTION_CHOICES
        {
            PartnerPlayerId = askerSession.PlayerId.Value,
            IsAsker = false,
            Questions = new List<InteractionQuestion>() // 빈 목록 (대기 상태)
        };
        answererPacket.SetBody(MessagePackSerializer.Serialize(answererMsg));
        answererSession.Send(answererPacket);
    }

    private void SendBotInteractionChoices(long botPlayerId)
    {
        if (!PlayerId.HasValue) return;

        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        var area = bot?.CurrentArea ?? CurrentArea;
        var roomEncounterLogIds = GetRecentRoomEncounterLogIds(PlayerId.Value, botPlayerId, area);
        if (roomEncounterLogIds.Count > 0)
        {
            SendEncounterActionChoices(this, botPlayerId, area, roomEncounterLogIds);
            return;
        }

        var questionSet = _interactionChoiceService.GenerateQuestionSet(
            CurrentMapSubId,
            PlayerId.Value,
            botPlayerId,
            area,
            null);
        var questions = questionSet.Questions;
        _pendingQuestions = questions;
        _pendingQuestionContexts = questionSet.Contexts;

        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_CHOICES, PlayerId.Value);
        var msg = new G_TO_C_INTERACTION_CHOICES
        {
            PartnerPlayerId = botPlayerId,
            IsAsker = true,
            Questions = questions
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private bool TrySendRoomEncounterActionChoices(GameClientSession askerSession, GameClientSession answererSession)
    {
        if (!askerSession.PlayerId.HasValue || !answererSession.PlayerId.HasValue)
            return false;

        var roomEncounterLogIds = GetRecentRoomEncounterLogIds(
            askerSession.PlayerId.Value,
            answererSession.PlayerId.Value,
            askerSession.CurrentArea);
        if (roomEncounterLogIds.Count == 0)
            return false;

        SendEncounterActionChoices(
            askerSession,
            answererSession.PlayerId.Value,
            askerSession.CurrentArea,
            roomEncounterLogIds);
        SendEncounterActionChoices(
            answererSession,
            askerSession.PlayerId.Value,
            askerSession.CurrentArea,
            roomEncounterLogIds);

        Logger.LogInformation(
            "Room encounter action choices sent: PlayerA={PlayerA}, PlayerB={PlayerB}, Area={Area}, LinkedLogs={Logs}",
            askerSession.PlayerId.Value,
            answererSession.PlayerId.Value,
            askerSession.CurrentArea,
            string.Join(",", roomEncounterLogIds));

        return true;
    }

    private List<long> GetRecentRoomEncounterLogIds(long playerA, long playerB, AreaType area)
    {
        if (playerA == 0 || playerB == 0 || playerA == playerB || area == AreaType.None)
            return new List<long>();

        string areaName = area.ToString();
        long cutoffUnixMs = DateTimeOffset.UtcNow.AddSeconds(-15).ToUnixTimeMilliseconds();

        return _gameEventLogManager.GetRecent(CurrentMapSubId, 100)
            .Where(entry => entry.TimestampUnixMs >= cutoffUnixMs)
            .Where(entry => entry.Type == "ROOM_ENCOUNTER_REVEAL")
            .Where(entry => string.Equals(entry.Area, areaName, StringComparison.Ordinal))
            .Where(entry =>
                (entry.ActorPlayerId == playerA && entry.EncounteredPlayerIds?.Contains(playerB) == true)
                || (entry.ActorPlayerId == playerB && entry.EncounteredPlayerIds?.Contains(playerA) == true))
            .Select(entry => entry.Seq)
            .Distinct()
            .ToList();
    }

    private void SendEncounterActionChoices(
        GameClientSession session,
        long partnerPlayerId,
        AreaType area,
        IReadOnlyCollection<long> linkedLogIds)
    {
        if (!session.PlayerId.HasValue)
            return;

        var inventoryItems = _inGameInventoryManager.GetAllItems(CurrentMapSubId, session.PlayerId.Value);
        var answerSet = _interactionChoiceService.GenerateEncounterActionAnswerSet(
            area,
            inventoryItems,
            linkedLogIds);

        session._lastAskedQuestion = InteractionQuestionType.ENCOUNTER_ACTION;
        session._pendingQuestions = null;
        session._pendingQuestionContexts = null;
        session._pendingAnswers = answerSet.Answers;
        session._pendingAnswerContexts = answerSet.Contexts;

        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ANSWER_CHOICES, session.PlayerId.Value);
        var msg = new G_TO_C_INTERACTION_ANSWER_CHOICES
        {
            QuestionType = InteractionQuestionType.ENCOUNTER_ACTION,
            QuestionTextId = InteractionChoiceService.EncounterActionQuestionTextId,
            QuestionArgs = new List<TextArg>(),
            Answers = answerSet.Answers
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        session.Send(packet);
    }

    public bool TryStartTargetBotInterrogation(BotPlayerState bot)
    {
        if (!PlayerId.HasValue) return false;
        if (IsEliminated
            || _activeConversationPlayerId.HasValue
            || _pendingInteractPlayerId.HasValue
            || _pendingBotRequesterPlayerId.HasValue)
            return false;
        if (CurrentState != PlayerState.Idle || _isSleeping) return false;
        if (bot.IsEliminated || bot.IsInInteraction) return false;
        if (bot.CurrentArea == AreaType.None || bot.CurrentArea != CurrentArea) return false;

        _activeConversationPlayerId = bot.PlayerId;
        bot.HoldForInteraction(TimeSpan.FromSeconds(13));
        bot.LoopWaitUntil = DateTime.MinValue;

        using (var requestPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(bot.PlayerId, ErrorCode.SUCCESS))
        {
            Send(requestPacket);
        }

        using (var resultPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(true, bot.PlayerId, ErrorCode.SUCCESS))
        {
            Send(resultPacket);
        }

        StartTargetBotInterrogationChoiceDelay(bot.PlayerId);

        Logger.LogInformation(
            "타겟 봇 선심문 시작: BotId={Bot}, PlayerId={Player}, Area={Area}",
            bot.PlayerId, PlayerId.Value, bot.CurrentArea);

        return true;
    }

    /// <summary>
    ///     질문자가 질문 선택
    /// </summary>
    private async Task HandleInteractionAsk(C_TO_G_INTERACTION_ASK msg)
    {
        if (!PlayerId.HasValue || !_activeConversationPlayerId.HasValue) return;
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return;
        }

        long partnerPlayerId = _activeConversationPlayerId.Value;
        _lastAskedQuestion = msg.QuestionType;

        if (BotPlayerManager.IsBotPlayerId(partnerPlayerId))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            SendBotInteractionResult(partnerPlayerId);
            return;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var partnerSession = allSessions.FirstOrDefault(s => s.PlayerId == partnerPlayerId);
        if (partnerSession == null) return;
        var questionContext = _pendingQuestionContexts?
            .FirstOrDefault(context => context.QuestionType == msg.QuestionType);

        // 답변 선택지 생성
        var answerTask = ResolveInteractionAnswerTask(partnerPlayerId);
        var answerSet = _interactionChoiceService.GenerateAnswerSet(
            CurrentMapSubId,
            partnerPlayerId,
            PlayerId.Value,
            msg.QuestionType,
            partnerSession.CurrentArea,
            questionContext,
            ResolveTaskArea(answerTask),
            answerTask?.TaskId ?? 0);
        var answers = answerSet.Answers;

        partnerSession._pendingAnswers = answers;
        partnerSession._pendingAnswerContexts = answerSet.Contexts;

        // 질문 textId/args 찾기 — 양쪽 화면에 같은 질문 표시
        var pendingQuestion = _pendingQuestions?.FirstOrDefault(q => q.QuestionType == msg.QuestionType);

        // 답변자에게 답변 선택지 전송
        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ANSWER_CHOICES, partnerPlayerId);
        var answerMsg = new G_TO_C_INTERACTION_ANSWER_CHOICES
        {
            QuestionType = msg.QuestionType,
            QuestionTextId = pendingQuestion?.TextId ?? 0,
            QuestionArgs = pendingQuestion?.Args ?? new List<TextArg>(),
            Answers = answers
        };
        packet.SetBody(MessagePackSerializer.Serialize(answerMsg));
        partnerSession.Send(packet);

        Logger.LogInformation("상호작용 질문: Asker={Asker}, Answerer={Answerer}, Type={Type}",
            PlayerId, partnerPlayerId, msg.QuestionType);

        return;
    }

    private void SendBotInteractionResult(long botPlayerId)
    {
        if (!PlayerId.HasValue) return;
        if (_activeConversationPlayerId != botPlayerId) return;

        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null) return;

        var (selectedAnswer, selectedAnswerContext) = ResolveBotInteractionAnswer(bot);
        int answerTextId = selectedAnswer?.TextId ?? InteractionChoiceService.CoincidenceAnswerTextId;
        var answerArgs = selectedAnswer?.Args ?? new List<TextArg>();

        var result = new G_TO_C_INTERACTION_RESULT
        {
            PartnerPlayerId = botPlayerId,
            QuestionType = _lastAskedQuestion,
            ClaimedJob = selectedAnswer?.ClaimedJob ?? JobTitle.NONE,
            ClaimedArea = bot.CurrentArea,
            IsFakeDetected = false,
            ConflictTextId = 0,
            ConflictArgs = new List<TextArg>(),
            AnswerTextId = answerTextId,
            AnswerArgs = answerArgs
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        LogStatementIfNeeded(botPlayerId, PlayerId.Value, selectedAnswerContext, isBot: true);

        _pendingQuestions = null;
        _pendingQuestionContexts = null;

        Logger.LogInformation(
            "봇 심문 응답: BotId={Bot}, Asker={Asker}, AnswerTextId={AnswerTextId}",
            botPlayerId, PlayerId.Value, answerTextId);
    }

    private (InteractionAnswer? Answer, InteractionAnswerContext? Context) ResolveBotInteractionAnswer(BotPlayerState bot)
    {
        var questionContext = _pendingQuestionContexts?
            .FirstOrDefault(context => context.QuestionType == _lastAskedQuestion);
        var answerTask = ResolveBotAnswerTask(bot);
        var answerSet = _interactionChoiceService.GenerateAnswerSet(
            CurrentMapSubId,
            bot.PlayerId,
            PlayerId ?? 0,
            _lastAskedQuestion,
            bot.CurrentArea,
            questionContext,
            ResolveTaskArea(answerTask),
            answerTask?.TaskId ?? 0);
        var answers = answerSet.Answers;
        if (answers.Count == 0) return (null, null);

        int answerIndex = _botPlayerManager.PickAnswerIndex(answers.Count);
        answerIndex = Math.Clamp(answerIndex, 0, answers.Count - 1);
        return (answers[answerIndex], answerSet.Contexts.ElementAtOrDefault(answerIndex));
    }

    private ChecklistTaskData? ResolveInteractionAnswerTask(long playerId)
    {
        return _checklistManager.GetNextActiveGeneralInteractTask(CurrentMapSubId, playerId);
    }

    private ChecklistTaskData? ResolveBotAnswerTask(BotPlayerState bot)
    {
        if (bot.PendingChecklistTaskId > 0)
        {
            var pendingTask = GameChecklistData.GetTask(bot.PendingChecklistTaskId);
            if (pendingTask != null)
                return pendingTask;
        }

        return ResolveInteractionAnswerTask(bot.PlayerId);
    }

    private static AreaType? ResolveTaskArea(ChecklistTaskData? task)
    {
        if (task?.AreaType > 0 && Enum.IsDefined(typeof(AreaType), task.AreaType))
            return (AreaType)task.AreaType;

        return null;
    }

    private void LogStatementIfNeeded(
        long speakerPlayerId,
        long listenerPlayerId,
        InteractionAnswerContext? answerContext,
        bool isBot)
    {
        if (answerContext == null) return;
        if (!string.Equals(answerContext.QuestionId, InteractionChoiceService.NearbyReasonQuestionId,
                StringComparison.Ordinal)
            && !string.Equals(answerContext.QuestionId, InteractionChoiceService.EncounterActionQuestionId,
                StringComparison.Ordinal))
            return;

        int roundId = GameRoundStates.TryGetValue(CurrentMapSubId, out var roundState)
            ? roundState.RoundNumber
            : 0;
        var statement = _gameEventLogManager.LogStatement(
            CurrentMapSubId,
            roundId,
            speakerPlayerId,
            listenerPlayerId,
            answerContext.Area.ToString(),
            answerContext.QuestionId,
            answerContext.QuestionText,
            answerContext.AnswerType,
            answerContext.AnswerText,
            answerContext.LinkedLogIds,
            isBot);

        Logger.LogInformation("[STATEMENT] {Description}", statement.Description);
    }

    private static (int TextId, List<TextArg> Args) CreateDemoBotAnswer(BotPlayerState bot, bool isPlayersManitto)
    {
        if (isPlayersManitto)
            return (InteractionChoiceService.DemoRecordAnswerTextId, new List<TextArg>());

        return Random.Shared.Next(3) switch
        {
            0 => (InteractionChoiceService.DemoPassingAnswerTextId, new List<TextArg>()),
            1 => (InteractionChoiceService.DemoMissionAnswerTextId, new List<TextArg>
            {
                new() { Type = TextArgType.JOB_TITLE, IntValue = (int)bot.MyJobTitle }
            }),
            _ => (InteractionChoiceService.DemoStaminaAnswerTextId, new List<TextArg>())
        };
    }

    private string ResolveDemoInteractionPlayerName(long playerId)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, playerId);
        if (bot != null && !string.IsNullOrEmpty(bot.Name)) return bot.Name;

        if (!BotPlayerManager.IsBotPlayerId(playerId))
        {
            try
            {
                var playerInfo = PlayerInfo.Load(CacheHelper, playerId).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(playerInfo?.Name)) return playerInfo.Name;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Interaction player name lookup failed: PlayerId={PlayerId}", playerId);
            }
        }

        return BotPlayerManager.IsBotPlayerId(playerId)
            ? $"Player{Math.Abs(playerId)}"
            : $"Player{playerId}";
    }

    private bool TryCreateManittoTargetAnswer(int encodedAnswerIndex, out InteractionAnswer? selectedAnswer)
    {
        selectedAnswer = null;
        if (!PlayerId.HasValue) return false;

        int targetIndex = encodedAnswerIndex - ManittoTargetAnswerIndexOffset;
        if (targetIndex < 0) return false;

        var candidates = GetInteractionPlayerCandidates();
        if (targetIndex >= candidates.Count) return false;

        var candidate = candidates[targetIndex];
        var link = _manittoChainManager.GetLink(CurrentMapSubId, PlayerId.Value);
        bool isTrue = link?.TargetPlayerId == candidate.playerId;

        selectedAnswer = new InteractionAnswer
        {
            IsTrue = isTrue,
            ClaimedJob = JobTitle.NONE,
            TextId = ManittoTargetAnswerTextId,
            Args = new List<TextArg>
            {
                new() { Type = TextArgType.RAW_STRING, StringValue = candidate.name }
            }
        };
        return true;
    }

    private List<(long playerId, string name)> GetInteractionPlayerCandidates()
    {
        var candidates = new Dictionary<long, string>();

        void AddCandidate(long playerId, string name)
        {
            if (playerId == 0) return;
            if (PlayerId.HasValue && playerId == PlayerId.Value) return;
            if (candidates.ContainsKey(playerId)) return;

            candidates[playerId] = !string.IsNullOrWhiteSpace(name)
                ? name
                : ResolveDemoInteractionPlayerName(playerId);
        }

        foreach (var session in _getSessionsByInstance(CurrentMapId, CurrentMapSubId))
        {
            if (!session.PlayerId.HasValue) continue;
            AddCandidate(session.PlayerId.Value, ResolveDemoInteractionPlayerName(session.PlayerId.Value));
        }

        foreach (var bot in _botPlayerManager.GetBots(CurrentMapSubId).Where(b => !b.IsEliminated))
            AddCandidate(bot.PlayerId, bot.Name);

        return candidates
            .OrderBy(candidate => candidate.Key)
            .Select(candidate => (candidate.Key, candidate.Value))
            .ToList();
    }

    /// <summary>
    ///     답변자가 답변 선택
    /// </summary>
    private Task HandleInteractionAnswer(C_TO_G_INTERACTION_ANSWER msg)
    {
        if (!PlayerId.HasValue || !_activeConversationPlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return Task.CompletedTask;
        }
        if (_pendingAnswers == null || msg.AnswerIndex < 0)
            return Task.CompletedTask;

        long askerPlayerId = _activeConversationPlayerId.Value;
        InteractionAnswer selectedAnswer;
        InteractionAnswerContext? selectedAnswerContext = null;
        if (msg.AnswerIndex >= ManittoTargetAnswerIndexOffset)
        {
            if (!TryCreateManittoTargetAnswer(msg.AnswerIndex, out var manittoTargetAnswer) || manittoTargetAnswer == null)
                return Task.CompletedTask;
            selectedAnswer = manittoTargetAnswer;
        }
        else
        {
            if (msg.AnswerIndex >= _pendingAnswers.Count)
                return Task.CompletedTask;
            selectedAnswer = _pendingAnswers[msg.AnswerIndex];
            selectedAnswerContext = _pendingAnswerContexts?.ElementAtOrDefault(msg.AnswerIndex);
        }

        if (BotPlayerManager.IsBotPlayerId(askerPlayerId))
        {
            _interactionChoiceService.ProcessAnswer(
                CurrentMapSubId,
                askerPlayerId,
                PlayerId.Value,
                selectedAnswer.ClaimedJob,
                CurrentArea,
                selectedAnswer.IsTrue);

            Logger.LogInformation(
                "타겟 봇 선심문 응답: BotId={Bot}, Answerer={Answerer}, TextId={TextId}",
                askerPlayerId, PlayerId.Value, selectedAnswer.TextId);

            LogStatementIfNeeded(PlayerId.Value, askerPlayerId, selectedAnswerContext, isBot: false);

            if (string.Equals(selectedAnswerContext?.QuestionId, InteractionChoiceService.EncounterActionQuestionId,
                    StringComparison.Ordinal))
            {
                var result = new G_TO_C_INTERACTION_RESULT
                {
                    PartnerPlayerId = askerPlayerId,
                    QuestionType = _lastAskedQuestion,
                    ClaimedJob = selectedAnswer.ClaimedJob,
                    ClaimedArea = CurrentArea,
                    IsFakeDetected = false,
                    ConflictTextId = 0,
                    ConflictArgs = new List<TextArg>(),
                    AnswerTextId = selectedAnswer.TextId,
                    AnswerArgs = selectedAnswer.Args
                };

                using var resultPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_RESULT, PlayerId.Value);
                resultPacket.SetBody(MessagePackSerializer.Serialize(result));
                Send(resultPacket);
            }

            _pendingAnswers = null;
            _pendingAnswerContexts = null;
            _pendingQuestionContexts = null;
            return Task.CompletedTask;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var askerSession = allSessions.FirstOrDefault(s => s.PlayerId == askerPlayerId);
        if (askerSession == null) return Task.CompletedTask;

        // 로그 기록 + 사칭 발각 체크
        var (isFakeDetected, conflictTextId, conflictArgs) = _interactionChoiceService.ProcessAnswer(
            CurrentMapSubId,
            askerPlayerId,
            PlayerId.Value,
            selectedAnswer.ClaimedJob,
            CurrentArea,
            selectedAnswer.IsTrue);

        // 양쪽에 결과 전송
        var resultForAsker = new G_TO_C_INTERACTION_RESULT
        {
            PartnerPlayerId = PlayerId.Value,
            QuestionType = askerSession._lastAskedQuestion,
            ClaimedJob = selectedAnswer.ClaimedJob,
            ClaimedArea = CurrentArea,
            IsFakeDetected = isFakeDetected,
            ConflictTextId = conflictTextId,
            ConflictArgs = conflictArgs,
            AnswerTextId = selectedAnswer.TextId,
            AnswerArgs = selectedAnswer.Args
        };

        using (var askerPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_RESULT, askerPlayerId))
        {
            askerPacket.SetBody(MessagePackSerializer.Serialize(resultForAsker));
            askerSession.Send(askerPacket);
        }

        var resultForAnswerer = new G_TO_C_INTERACTION_RESULT
        {
            PartnerPlayerId = askerPlayerId,
            QuestionType = askerSession._lastAskedQuestion,
            ClaimedJob = selectedAnswer.ClaimedJob,
            ClaimedArea = CurrentArea,
            IsFakeDetected = isFakeDetected,
            ConflictTextId = conflictTextId,
            ConflictArgs = conflictArgs,
            AnswerTextId = selectedAnswer.TextId,
            AnswerArgs = selectedAnswer.Args
        };

        using (var answererPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_RESULT, PlayerId.Value))
        {
            answererPacket.SetBody(MessagePackSerializer.Serialize(resultForAnswerer));
            Send(answererPacket);
        }

        Logger.LogInformation("상호작용 답변: Answerer={Answerer}, Asker={Asker}, ClaimedJob={Job}, Fake={Fake}",
            PlayerId, askerPlayerId, selectedAnswer.ClaimedJob, isFakeDetected);

        LogStatementIfNeeded(PlayerId.Value, askerPlayerId, selectedAnswerContext, isBot: false);

        // 선택지 상태 클리어
        _pendingAnswers = null;
        if (string.Equals(selectedAnswerContext?.QuestionId, InteractionChoiceService.EncounterActionQuestionId,
                StringComparison.Ordinal))
        {
            askerSession._pendingAnswers = null;
            askerSession._pendingAnswerContexts = null;
        }

        _pendingAnswerContexts = null;
        askerSession._pendingQuestions = null;
        askerSession._pendingQuestionContexts = null;

        return Task.CompletedTask;
    }
}
