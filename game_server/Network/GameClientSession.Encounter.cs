using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
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
            candidatePositions.Select(entry => (entry.Item1, entry.Item2!)));
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
                out long targetPlayerId))
        {
            return false;
        }

        var targetSession = allSessions.FirstOrDefault(session => session.PlayerId == targetPlayerId);
        if (targetSession != null)
            SendEncounterEventToPair(targetSession, CurrentArea, EncounterRevealManager.RoomRevealEventType,
                EncounterRevealManager.PairCooldownSeconds);
        else
            SendEncounterEvent(targetPlayerId, CurrentArea, EncounterRevealManager.RoomRevealEventType,
                EncounterRevealManager.PairCooldownSeconds);

        _gameEventLogManager.LogInteraction(CurrentMapSubId, PlayerId.Value,
            $"Room encounter reveal: Target={targetPlayerId}, Area={CurrentArea}, InteractId={interactId}",
            isBot: false);

        Logger.LogInformation(
            "Room encounter reveal: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, InteractId={InteractId}",
            CurrentMapSubId,
            PlayerId.Value,
            targetPlayerId,
            CurrentArea,
            interactId);

        SendRngCollectResult(interactId, RngCollectEncounterResultType, 0, 0, 0);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        return true;
    }
}
