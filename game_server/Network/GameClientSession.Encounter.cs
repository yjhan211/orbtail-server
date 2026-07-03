using System;
using System.Threading.Tasks;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private static readonly TimeSpan RoomEncounterHoldDuration = TimeSpan.FromSeconds(13);
    private static readonly TimeSpan RoomDiscoveryDecisionDuration =
        TimeSpan.FromSeconds(EncounterRevealManager.RoomDiscoveryDecisionSeconds);

    private void SendEncounterEvent(long targetPlayerId, AreaType area, int eventType, int cooldownSeconds,
        int revealDelayMs = 0)
    {
        if (!PlayerId.HasValue || targetPlayerId == 0) return;

        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            targetPlayerId,
            area,
            eventType,
            cooldownSeconds,
            revealDelayMs);
        Send(packet);
    }

    private void SendEncounterEventToPair(
        GameClientSession otherSession,
        AreaType area,
        int eventType,
        int cooldownSeconds,
        int revealDelayMs = 0)
    {
        if (!PlayerId.HasValue || !otherSession.PlayerId.HasValue) return;

        SendEncounterEvent(otherSession.PlayerId.Value, area, eventType, cooldownSeconds, revealDelayMs);
        otherSession.SendEncounterEvent(PlayerId.Value, area, eventType, cooldownSeconds, revealDelayMs);
    }

    private void TrySendCorridorEncounterEvents(Vector3f actorPosition)
    {
        if (!PlayerId.HasValue || CurrentArea == AreaType.None || !CurrentArea.IsCorridor())
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var candidatePositions = allSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                session.PlayerId != PlayerId &&
                !session.IsEliminated &&
                session.CurrentMapSubId == CurrentMapSubId &&
                session.CurrentArea == CurrentArea &&
                session.LastValidatedPosition != null)
            .Select(session => (session.PlayerId!.Value, session.LastValidatedPosition!))
            .Concat(_botPlayerManager.GetBots(CurrentMapSubId)
                .Where(bot => !bot.IsEliminated && bot.CurrentArea == CurrentArea)
                .Select(bot => (bot.PlayerId, (Vector3f?)bot.Position)))
            .Where(entry => entry.Item2 != null)
            .Select(entry => (entry.Item1, entry.Item2!))
            .ToList();

        var decision = _encounterRevealManager.ResolveCorridorEncounter(
            CurrentMapSubId,
            PlayerId.Value,
            actorPosition,
            candidatePositions.Select(entry => (entry.Item1, entry.Item2!)),
            PassiveBuffUtility.GetValuePercent(ActiveBuffIds, BuffSubType.RISK_EVENT_CHANCE_DOWN),
            PassiveBuffUtility.GetValuePercent(ActiveBuffIds, BuffSubType.ENCOUNTER_ESCAPE_CHANCE_ADD));
        if (!decision.HasEvent) return;

        var targetSession = allSessions.FirstOrDefault(session => session.PlayerId == decision.TargetPlayerId);
        if (targetSession != null)
            SendEncounterEventToPair(targetSession, CurrentArea, decision.EventType, decision.CooldownSeconds,
                decision.RevealDelayMs);
        else
            SendEncounterEvent(decision.TargetPlayerId, CurrentArea, decision.EventType, decision.CooldownSeconds,
                decision.RevealDelayMs);

        Logger.LogInformation(
            "Corridor encounter event: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, EventType={EventType}",
            CurrentMapSubId,
            PlayerId.Value,
            decision.TargetPlayerId,
            CurrentArea,
            decision.EventType);
    }

    private bool TryHandleRoomEncounterBeforeCollect(int interactId, InteractableInfoData info)
    {
        if (!PlayerId.HasValue)
            return false;

        if (CurrentArea == AreaType.None || CurrentArea.IsCorridor())
        {
            Logger.LogDebug(
                "Room encounter skipped: invalid current area. Matching={MatchingId}, Actor={Actor}, Area={Area}, InteractId={InteractId}, ZoneId={ZoneId}",
                CurrentMapSubId,
                PlayerId.Value,
                CurrentArea,
                interactId,
                info.ZoneId);
            return false;
        }

        if (info.ZoneId != (int)CurrentArea)
        {
            Logger.LogDebug(
                "Room encounter skipped: interact area mismatch. Matching={MatchingId}, Actor={Actor}, CurrentArea={Area}, InteractId={InteractId}, ZoneId={ZoneId}",
                CurrentMapSubId,
                PlayerId.Value,
                CurrentArea,
                interactId,
                info.ZoneId);
            return false;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var candidateIds = allSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                session.PlayerId != PlayerId &&
                !session.IsEliminated &&
                session.CurrentMapSubId == CurrentMapSubId &&
                session.CurrentArea == CurrentArea)
            .Select(session => session.PlayerId!.Value)
            .Concat(_botPlayerManager.GetBots(CurrentMapSubId)
                .Where(bot => !bot.IsEliminated && bot.CurrentArea == CurrentArea)
                .Select(bot => bot.PlayerId))
            .ToList();

        Logger.LogDebug(
            "Room encounter candidates: Matching={MatchingId}, Actor={Actor}, Area={Area}, InteractId={InteractId}, CandidateCount={CandidateCount}, Candidates={Candidates}",
            CurrentMapSubId,
            PlayerId.Value,
            CurrentArea,
            interactId,
            candidateIds.Count,
            string.Join(",", candidateIds));

        if (!_encounterRevealManager.TryResolveRoomEncounter(
                CurrentMapSubId,
                PlayerId.Value,
                CurrentArea,
                candidateIds,
                out long targetPlayerId,
                PassiveBuffUtility.GetValuePercent(ActiveBuffIds, BuffSubType.RISK_EVENT_CHANCE_DOWN),
                PassiveBuffUtility.GetValuePercent(ActiveBuffIds, BuffSubType.ENCOUNTER_ESCAPE_CHANCE_ADD)))
        {
            return false;
        }

        var targetSession = allSessions.FirstOrDefault(session => session.PlayerId == targetPlayerId);
        var targetBot = targetSession == null
            ? _botPlayerManager.GetBot(CurrentMapSubId, targetPlayerId)
            : null;
        _encounterRevealManager.RegisterPendingRoomDiscovery(
            CurrentMapSubId,
            PlayerId.Value,
            targetPlayerId,
            CurrentArea);
        _ = ResolveRoomDiscoveryAfterDecisionDelay(PlayerId.Value, targetPlayerId, CurrentArea);

        SendEncounterEvent(targetPlayerId, CurrentArea, EncounterRevealManager.RoomDiscoveryEventType,
            EncounterRevealManager.PairCooldownSeconds);

        _gameEventLogManager.LogRoomEncounterReveal(CurrentMapSubId, PlayerId.Value, targetPlayerId,
            CurrentArea.ToString(), interactId, isBot: false);

        _gameEventLogManager.LogInteraction(CurrentMapSubId, PlayerId.Value,
            $"Room encounter reveal: Target={targetPlayerId}, Area={CurrentArea}, InteractId={interactId}",
            isBot: false);

        Logger.LogInformation(
            "Room discovery started: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, InteractId={InteractId}, EventType={EventType}",
            CurrentMapSubId,
            PlayerId.Value,
            targetPlayerId,
            CurrentArea,
            interactId,
            EncounterRevealManager.RoomDiscoveryEventType);

        SendRngCollectResult(interactId, RngCollectEncounterResultType, 0, 0, 0);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        return true;
    }

    private async Task ResolveRoomDiscoveryAfterDecisionDelay(long discovererPlayerId, long targetPlayerId,
        AreaType area)
    {
        try
        {
            await Task.Delay(RoomDiscoveryDecisionDuration);

            if (!PlayerId.HasValue || PlayerId.Value != discovererPlayerId || IsEliminated)
                return;

            if (!_encounterRevealManager.TryConsumePendingRoomDiscovery(
                    CurrentMapSubId,
                    discovererPlayerId,
                    targetPlayerId,
                    area,
                    out _))
            {
                return;
            }

            if (CurrentArea != area || CurrentMapSubId <= 0)
            {
                Logger.LogInformation(
                    "Room discovery expired but discoverer left: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}, CurrentArea={CurrentArea}",
                    CurrentMapSubId,
                    discovererPlayerId,
                    targetPlayerId,
                    area,
                    CurrentArea);
                return;
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
                Logger.LogInformation(
                    "Room discovery expired but target left: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}",
                    CurrentMapSubId,
                    discovererPlayerId,
                    targetPlayerId,
                    area);
                return;
            }

            HoldRoomEncounterBotTarget(targetBot);

            SendEncounterEvent(targetPlayerId, area, EncounterRevealManager.RoomEncounterEventType,
                EncounterRevealManager.PairCooldownSeconds);
            targetSession?.SendEncounterEvent(discovererPlayerId, area, EncounterRevealManager.RoomRevealEventType,
                EncounterRevealManager.PairCooldownSeconds);

            Logger.LogInformation(
                "Room discovery resolved by server timer: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}",
                CurrentMapSubId,
                discovererPlayerId,
                targetPlayerId,
                area);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Room discovery decision timer failed: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}",
                CurrentMapSubId,
                discovererPlayerId,
                targetPlayerId,
                area);
        }
    }

    private Task HandleRoomEncounterAvoid(C_TO_G_ROOM_ENCOUNTER_AVOID msg)
    {
        if (!PlayerId.HasValue || msg == null || msg.TargetPlayerId == 0)
            return Task.CompletedTask;

        var area = msg.AreaType == AreaType.None ? CurrentArea : msg.AreaType;
        bool consumed = _encounterRevealManager.TryConsumePendingRoomDiscovery(
            CurrentMapSubId,
            PlayerId.Value,
            msg.TargetPlayerId,
            area,
            out _);

        Logger.LogInformation(
            "Room discovery avoid: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, ActionType={ActionType}, Consumed={Consumed}",
            CurrentMapSubId,
            PlayerId.Value,
            msg.TargetPlayerId,
            area,
            msg.ActionType,
            consumed);
        return Task.CompletedTask;
    }

    private bool IsRoomEncounterTargetUnaware(GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        if (targetSession != null)
            return targetSession.CurrentState == PlayerState.Exploring ||
                   targetSession._pendingFinish.Count > 0 ||
                   targetSession._pendingChecklistActivityFinish.Count > 0;

        if (targetBot == null)
            return false;

        return targetBot.RngCollectProgressStartTime != DateTime.MinValue ||
               targetBot.ChecklistActivityProgressStartTime != DateTime.MinValue;
    }

    private void ResolvePendingRoomDiscoveriesAfterExploreFinished(AreaType area)
    {
        // Room discovery now resolves through the server-side decision timer or an explicit avoid request.
    }

    private static void HoldRoomEncounterBotTarget(BotPlayerState? bot)
    {
        if (bot == null)
            return;

        bot.HoldForInteraction(RoomEncounterHoldDuration);
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
    }
}
