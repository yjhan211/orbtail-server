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
    // Used only for resolving already-pending discovery transitions after the target finishes exploring.

    private void SendEncounterEvent(long targetPlayerId, AreaType area, int eventType, int cooldownSeconds,
        int revealDelayMs = 0, int damageValue = 0)
    {
        if (!PlayerId.HasValue || targetPlayerId == 0) return;

        int targetCorruption = -1;
        if (eventType == ProximityAutoAttackDealtEventType || eventType == ProximityAutoAttackTakenEventType)
        {
            var targetSession = _getSessionsByInstance(CurrentMapId, CurrentMapSubId)
                .FirstOrDefault(session => session.PlayerId == targetPlayerId);
            if (targetSession != null)
                targetCorruption = targetSession.Corruption;
            else
                targetCorruption = _botPlayerManager.GetBot(CurrentMapSubId, targetPlayerId)?.Corruption ?? -1;
        }

        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            targetPlayerId,
            area,
            eventType,
            cooldownSeconds,
            revealDelayMs,
            damageValue,
            targetCorruption);
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
        // 복도 조우는 프로토0 레거시 — 스웜 모드에서는 발화하지 않는다 (#236)
        if (Config.SWARM_P0_ENABLED) return;
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
}
