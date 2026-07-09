using System;
using game_server.services;
using System.Collections.Generic;
using System.Linq;
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

        var roomEvent = SelectRoomExploreEvent(area);
        if (roomEvent == null)
            return false;

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

    private static RoomEventInfoData? SelectRoomExploreEvent(AreaType area)
    {
        foreach (var roomEvent in GameRoomEventData.GetByArea(area))
        {
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

        ApplyRoomEventChoice(choice, state);

        _pendingRoomEntryEventId = 0;
        _pendingRoomEventInteractId = 0;
        SendMissionInfo();

        Logger.LogInformation(
            "Room explore event choice resolved: Matching={MatchingId}, Player={PlayerId}, Event={Event}, Choice={Choice}, MentalDelta={MentalDelta}, StaminaDelta={StaminaDelta}, RewardItem={RewardItem}, Clue={Clue}",
            CurrentMapSubId,
            PlayerId,
            roomEvent.EventId,
            choice.ChoiceId,
            choice.MentalDelta,
            choice.StaminaDelta,
            choice.RewardItemId,
            choice.ClueRewardId);

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

    private void ApplyRoomEventChoice(RoomEventChoiceInfoData choice, PlayerPartState state)
    {
        if (!PlayerId.HasValue)
            return;

        int requiredItemId = ResolveRequiredRoomEventItemId(choice);
        if (choice.ConsumeItem && requiredItemId > 0 &&
            _inGameInventoryManager.TryRemoveOneByItemId(
                CurrentMapSubId,
                PlayerId.Value,
                requiredItemId,
                out var updatedItem) &&
            updatedItem != null)
        {
            SendInGameInventoryUpdate(updatedItem);
        }

        if (choice.RewardItemId > 0 && choice.RewardItemCount > 0)
        {
            var added = _inGameInventoryManager.AddItem(
                CurrentMapSubId,
                PlayerId.Value,
                choice.RewardItemId,
                choice.RewardItemCount);
            SendInGameInventoryUpdate(added);
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

        if (_pendingRoomEventInteractId > 0)
        {
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, _pendingRoomEventInteractId);
            BroadcastRngCollectCooldown(_pendingRoomEventInteractId, 0);
        }

        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        CheckResourceElimination();
    }

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
