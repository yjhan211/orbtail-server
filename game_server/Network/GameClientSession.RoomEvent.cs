using System;
using System.Collections.Generic;
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
    private int _pendingRoomEventInteractId;

    private bool TryStartRoomExploreEvent(AreaType area, int interactId)
    {
        if (!PlayerId.HasValue || _pendingRoomEntryEventId != 0)
            return false;

        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null)
            return false;

        var roomEvent = SelectRoomExploreEvent(area, state);
        if (roomEvent == null)
            return false;

        lock (state.SyncRoot)
        {
            state.TriggeredRoomEventIds.Add(roomEvent.EventId);
        }

        _pendingRoomEntryEventId = roomEvent.EventId;
        _pendingRoomEventInteractId = interactId;

        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);

        using var packet = PacketMaker.G_TO_C_ROOM_ENTRY_EVENT(roomEvent.EventId);
        Send(packet);

        Logger.LogInformation(
            "Room explore event started: Matching={MatchingId}, Player={PlayerId}, Event={EventId}, Area={Area}, InteractId={InteractId}",
            CurrentMapSubId,
            PlayerId,
            roomEvent.EventId,
            area,
            interactId);

        return true;
    }

    private static RoomEventInfoData? SelectRoomExploreEvent(AreaType area, PlayerPartState state)
    {
        HashSet<int> triggeredRoomEventIds;
        lock (state.SyncRoot)
        {
            triggeredRoomEventIds = new HashSet<int>(state.TriggeredRoomEventIds);
        }

        foreach (var roomEvent in GameRoomEventData.GetByArea(area))
        {
            if (roomEvent.UsesWorldState)
                continue;

            if (triggeredRoomEventIds.Contains(roomEvent.EventId))
                continue;

            if (!string.Equals(roomEvent.TriggerType, "explore", StringComparison.OrdinalIgnoreCase))
                continue;

            int probability = Math.Clamp(roomEvent.ProbabilityPercent, 0, 100);
            if (probability <= 0)
                continue;

            if (Random.Shared.Next(100) < probability)
                return roomEvent;
        }

        return null;
    }

    private bool TryHandleRoomExploreEventChoice(C_TO_G_ROOM_ENTRY_EVENT_CHOICE msg)
    {
        if (!GameRoomEventData.TryGetChoice(msg.EventId, msg.ChoiceId, out var roomEvent, out var choice))
            return false;

        if (!PlayerId.HasValue)
            return true;

        RoomEventChoiceApplication? application = null;
        bool shouldRetry = false;
        lock (_roomEntryEventChoiceLock)
        {
            if (_pendingRoomEntryEventId != msg.EventId)
            {
                Logger.LogWarning(
                    "Room explore event choice ignored after atomic check: Player={PlayerId}, Pending={Pending}, Event={Event}, Choice={Choice}",
                    PlayerId,
                    _pendingRoomEntryEventId,
                    msg.EventId,
                    msg.ChoiceId);
                return true;
            }

            var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
            if (state == null)
            {
                _pendingRoomEntryEventId = 0;
                _pendingRoomEventInteractId = 0;
                Logger.LogWarning(
                    "Room explore event choice failed because mission state is missing: Matching={MatchingId}, Player={PlayerId}, Event={Event}, Choice={Choice}",
                    CurrentMapSubId,
                    PlayerId,
                    msg.EventId,
                    msg.ChoiceId);
                return true;
            }

            if (!MeetsRoomEventRequirement(choice, state))
                choice = ResolveDefaultRoomEventChoice(roomEvent) ?? choice;

            choice = ResolveLuckRoomEventChoice(roomEvent, choice);

            if (!TryApplyRoomEventChoice(choice, state, out application))
            {
                shouldRetry = true;
            }
            else
            {
                _pendingRoomEntryEventId = 0;
                _pendingRoomEventInteractId = 0;
            }
        }

        if (shouldRetry)
        {
            Logger.LogWarning(
                "Room explore event choice rejected before commit because item consumption failed: Matching={MatchingId}, Player={PlayerId}, Event={Event}, Choice={Choice}",
                CurrentMapSubId,
                PlayerId,
                roomEvent.EventId,
                choice.ChoiceId);
            using var retryPacket = PacketMaker.G_TO_C_ROOM_ENTRY_EVENT(roomEvent.EventId);
            Send(retryPacket);
            return true;
        }

        if (application == null)
            return true;

        PublishRoomEventChoiceApplication(application);
        SendMissionInfo();

        Logger.LogInformation(
            "Room explore event choice resolved: Matching={MatchingId}, Player={PlayerId}, Event={Event}, Choice={Choice}, MentalDelta={MentalDelta}, StaminaDelta={StaminaDelta}, RewardItem={RewardItem}, Clue={Clue}",
            CurrentMapSubId,
            PlayerId,
            roomEvent.EventId,
            application.Choice.ChoiceId,
            application.Choice.MentalDelta,
            application.Choice.StaminaDelta,
            application.Choice.RewardItemId,
            application.Choice.ClueRewardId);

        return true;
    }

    private RoomEventChoiceInfoData ResolveLuckRoomEventChoice(
        RoomEventInfoData roomEvent,
        RoomEventChoiceInfoData choice)
    {
        if (!string.Equals(choice.ChoiceType, "luck", StringComparison.OrdinalIgnoreCase))
            return choice;

        int successRate = Math.Clamp(choice.LuckSuccessRate, 0, 100);
        if (Random.Shared.Next(100) < successRate)
            return choice;

        if (choice.LuckFailureChoiceId > 0 &&
            GameRoomEventData.TryGetChoice(roomEvent.EventId, choice.LuckFailureChoiceId, out _, out var failureChoice))
        {
            return failureChoice;
        }

        return ResolveDefaultRoomEventChoice(roomEvent) ?? choice;
    }

    private static RoomEventChoiceInfoData? ResolveDefaultRoomEventChoice(RoomEventInfoData roomEvent)
    {
        if (roomEvent.DefaultChoiceId > 0 &&
            GameRoomEventData.TryGetChoice(roomEvent.EventId, roomEvent.DefaultChoiceId, out _, out var defaultChoice))
        {
            return defaultChoice;
        }

        return roomEvent.Choices.FirstOrDefault();
    }

    private bool MeetsRoomEventRequirement(RoomEventChoiceInfoData choice, PlayerPartState state)
    {
        string requirementType = choice.RequirementType?.Trim().ToLowerInvariant() ?? "";
        string requirementValue = choice.RequirementValue?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(requirementType) || requirementType == "none")
            return true;

        return requirementType switch
        {
            "item" => int.TryParse(requirementValue, out int itemId) &&
                      _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId!.Value)
                          .GetItemCount(itemId) > 0,
            "item_group" or "item_any" => ResolveRoomEventRequirementItemIds(requirementValue)
                .Any(candidateItemId => _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId!.Value)
                    .GetItemCount(candidateItemId) > 0),
            "trait" => !string.IsNullOrWhiteSpace(requirementValue) &&
                       state.OwnedClueTags.Contains(requirementValue),
            "stamina" => int.TryParse(requirementValue, out int staminaCost) &&
                         Stamina >= staminaCost,
            _ => true
        };
    }

    private bool TryApplyRoomEventChoice(
        RoomEventChoiceInfoData choice,
        PlayerPartState state,
        out RoomEventChoiceApplication? application)
    {
        application = null;
        if (!PlayerId.HasValue)
            return false;

        int requiredItemId = ResolveRequiredRoomEventItemId(choice);
        InGameItemInfo? consumedItemUpdate = null;
        InGameItemInfo? rewardItemUpdate = null;
        int interactId = _pendingRoomEventInteractId;

        if (choice.ConsumeItem)
        {
            if (requiredItemId <= 0 ||
                !_inGameInventoryManager.TryRemoveOneByItemId(
                    CurrentMapSubId,
                    PlayerId.Value,
                    requiredItemId,
                    out var updatedItem))
            {
                return false;
            }

            consumedItemUpdate = SnapshotRoomEventItemUpdate(updatedItem);
        }

        if (choice.RewardItemId > 0 && choice.RewardItemCount > 0)
        {
            var added = _inGameInventoryManager.AddItem(
                CurrentMapSubId,
                PlayerId.Value,
                choice.RewardItemId,
                choice.RewardItemCount);
            rewardItemUpdate = SnapshotRoomEventItemUpdate(added);
        }

        if (!string.IsNullOrWhiteSpace(choice.ClueRewardId))
        {
            lock (state.SyncRoot)
            {
                state.OwnedClueTags.Add(choice.ClueRewardId);
            }
        }

        if (choice.StaminaDelta != 0 || choice.MentalDelta != 0)
            ModifyStats(staminaDelta: choice.StaminaDelta, corruptionDelta: choice.MentalDelta);

        application = new RoomEventChoiceApplication(
            choice,
            consumedItemUpdate,
            rewardItemUpdate,
            interactId);
        return true;
    }

    private void PublishRoomEventChoiceApplication(RoomEventChoiceApplication application)
    {
        if (application.ConsumedItemUpdate != null)
            SendInGameInventoryUpdate(application.ConsumedItemUpdate);

        if (application.RewardItemUpdate != null)
            SendInGameInventoryUpdate(application.RewardItemUpdate);

        if (application.InteractId > 0)
        {
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, application.InteractId);
            BroadcastRngCollectCooldown(application.InteractId, 0);
        }

        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        CheckResourceElimination();
    }

    private static InGameItemInfo? SnapshotRoomEventItemUpdate(InGameItemInfo? item)
    {
        if (item == null)
            return null;

        return new InGameItemInfo
        {
            ItemUid = item.ItemUid,
            ItemId = item.ItemId,
            Count = item.Count,
            GiftState = item.GiftState
        };
    }

    private sealed record RoomEventChoiceApplication(
        RoomEventChoiceInfoData Choice,
        InGameItemInfo? ConsumedItemUpdate,
        InGameItemInfo? RewardItemUpdate,
        int InteractId);

    private static int ResolveRequiredRoomEventItemId(RoomEventChoiceInfoData choice)
    {
        return string.Equals(choice.RequirementType, "item", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(choice.RequirementValue, out int itemId)
            ? itemId
            : 0;
    }

    private static IEnumerable<int> ResolveRoomEventRequirementItemIds(string requirementValue)
    {
        if (string.IsNullOrWhiteSpace(requirementValue))
            return [];

        return requirementValue
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out int itemId) ? itemId : 0)
            .Where(itemId => itemId > 0);
    }
}
