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
    private static readonly TimeSpan RoomEncounterHoldDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RoomEncounterActionSuppressDuration = TimeSpan.FromSeconds(2);
    // Client visibility uses a light radius plus sprite/body overlap, so use a small server margin
    // to avoid edge cases where a player is visibly inside the mask but the center-point check misses.
    private const float RoomEncounterVisionDistance = 2.75f;
    private readonly Dictionary<int, List<long>> _roomEncounterStartCandidateIdsByInteractId = new();
    private DateTime _suppressRoomEncounterUntilUtc = DateTime.MinValue;

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

    internal void TrySendRoomEncounterEventsFromCurrentVision()
    {
        if (_lastValidatedPosition == null || IsRoomEncounterActorBlocked())
            return;

        TrySendRoomEncounterEventsFromVision(_lastValidatedPosition);
    }

    internal void TrySendRoomEncounterEventForObservedPlayer(long observedPlayerId, Vector3f observedPosition)
    {
        if (!PlayerId.HasValue ||
            observedPlayerId == 0 ||
            observedPlayerId == PlayerId.Value ||
            IsEliminated ||
            CurrentArea == AreaType.None ||
            CurrentArea.IsCorridor() ||
            IsRoomEncounterActorBlocked() ||
            _lastValidatedPosition == null ||
            !IsWithinRoomEncounterVision(_lastValidatedPosition, observedPosition))
        {
            return;
        }

        var observedBot = _botPlayerManager.GetBot(CurrentMapSubId, observedPlayerId);
        if (observedBot is { IsInInteraction: true } ||
            _encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, observedPlayerId))
        {
            return;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        if (!_encounterRevealManager.TryResolveRoomEncounter(
                CurrentMapSubId,
                PlayerId.Value,
                CurrentArea,
                [observedPlayerId],
                out long targetPlayerId,
                riskEventChanceDownPercent: 0,
                escapeChanceAddPercent: 0))
        {
            return;
        }

        StartRoomEncounterReveal(PlayerId.Value, targetPlayerId, allSessions, interactId: 0,
            sendCollectResult: false, forceEncounterEvent: true);
    }

    private void TrySendRoomEncounterEventsFromVision(Vector3f actorPosition)
    {
        var now = DateTime.UtcNow;
        if (!PlayerId.HasValue || CurrentArea == AreaType.None || CurrentArea.IsCorridor() ||
            IsRoomEncounterActorBlocked(now))
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var candidateIds = allSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                session.PlayerId != PlayerId &&
                !session.IsEliminated &&
                !session.IsRoomEncounterProcessingBlocked(now) &&
                session.CurrentMapSubId == CurrentMapSubId &&
                session.CurrentArea == CurrentArea &&
                IsWithinRoomEncounterVision(actorPosition, session.LastValidatedPosition))
            .Select(session => session.PlayerId!.Value)
            .Concat(_botPlayerManager.GetBots(CurrentMapSubId)
                .Where(bot =>
                    !bot.IsEliminated &&
                    !bot.IsInInteraction &&
                    !_encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, bot.PlayerId) &&
                    bot.CurrentArea == CurrentArea &&
                    IsWithinRoomEncounterVision(actorPosition, bot.Position))
                .Select(bot => bot.PlayerId))
            .ToList();

        if (candidateIds.Count == 0)
            return;

        long actorPlayerId = PlayerId.Value;
        if (!_encounterRevealManager.TryResolveRoomEncounter(
                CurrentMapSubId,
                actorPlayerId,
                CurrentArea,
                candidateIds,
                out long targetPlayerId,
                riskEventChanceDownPercent: 0,
                escapeChanceAddPercent: 0))
        {
            return;
        }

        StartRoomEncounterReveal(actorPlayerId, targetPlayerId, allSessions, interactId: 0,
            sendCollectResult: false, forceEncounterEvent: true);
    }

    private void SnapshotRoomEncounterStartCandidates(int interactId, InteractableInfoData info)
    {
        if (TryGetRoomEncounterCandidateIds(interactId, info, out _, out var candidateIds) &&
            candidateIds.Count > 0)
        {
            _roomEncounterStartCandidateIdsByInteractId[interactId] = candidateIds;
            return;
        }

        _roomEncounterStartCandidateIdsByInteractId.Remove(interactId);
    }

    private void ClearRoomEncounterStartCandidates(int interactId)
    {
        _roomEncounterStartCandidateIdsByInteractId.Remove(interactId);
    }

    private bool TryGetRoomEncounterCandidateIds(
        int interactId,
        InteractableInfoData info,
        out IReadOnlyCollection<GameClientSession> allSessions,
        out List<long> candidateIds)
    {
        allSessions = Array.Empty<GameClientSession>();
        candidateIds = new List<long>();

        if (!PlayerId.HasValue)
            return false;

        var now = DateTime.UtcNow;
        if (IsRoomEncounterActorBlocked(now))
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

        allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var actorPosition = _lastValidatedPosition;
        if (actorPosition == null)
        {
            Logger.LogDebug(
                "Room encounter skipped: actor position unavailable. Matching={MatchingId}, Actor={Actor}, Area={Area}, InteractId={InteractId}",
                CurrentMapSubId,
                PlayerId.Value,
                CurrentArea,
                interactId);
            return false;
        }

        candidateIds = allSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                session.PlayerId != PlayerId &&
                !session.IsEliminated &&
                !session.IsRoomEncounterProcessingBlocked(now) &&
                session.CurrentMapSubId == CurrentMapSubId &&
                session.CurrentArea == CurrentArea &&
                IsWithinRoomEncounterVision(actorPosition, session.LastValidatedPosition))
            .Select(session => session.PlayerId!.Value)
            .Concat(_botPlayerManager.GetBots(CurrentMapSubId)
                .Where(bot =>
                    !bot.IsEliminated &&
                    !bot.IsInInteraction &&
                    !_encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, bot.PlayerId) &&
                    bot.CurrentArea == CurrentArea &&
                    IsWithinRoomEncounterVision(actorPosition, bot.Position))
                .Select(bot => bot.PlayerId))
            .ToList();

        return true;
    }

    private bool TryHandleRoomEncounterFromExploreSpot(int interactId, InteractableInfoData info)
    {
        if (!TryGetRoomEncounterCandidateIds(interactId, info, out var allSessions, out var candidateIds))
        {
            ClearRoomEncounterStartCandidates(interactId);
            return false;
        }

        if (_roomEncounterStartCandidateIdsByInteractId.Remove(interactId, out var startCandidateIds))
            AddRoomEncounterStartCandidatesStillInVision(candidateIds, startCandidateIds, allSessions);

        long actorPlayerId = PlayerId.GetValueOrDefault();
        Logger.LogDebug(
            "Room encounter candidates: Matching={MatchingId}, Actor={Actor}, Area={Area}, InteractId={InteractId}, CandidateCount={CandidateCount}, Candidates={Candidates}",
            CurrentMapSubId,
            actorPlayerId,
            CurrentArea,
            interactId,
            candidateIds.Count,
            string.Join(",", candidateIds));

        if (!_encounterRevealManager.TryResolveRoomEncounter(
                CurrentMapSubId,
                actorPlayerId,
                CurrentArea,
                candidateIds,
                out long targetPlayerId,
                riskEventChanceDownPercent: 0,
                escapeChanceAddPercent: 0))
        {
            return false;
        }

        StartRoomEncounterReveal(actorPlayerId, targetPlayerId, allSessions, interactId,
            sendCollectResult: true, forceEncounterEvent: true);
        return true;
    }

    private void StartRoomEncounterReveal(
        long actorPlayerId,
        long targetPlayerId,
        IReadOnlyCollection<GameClientSession> allSessions,
        int interactId,
        bool sendCollectResult,
        bool forceEncounterEvent = false)
    {
        if (_encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, actorPlayerId) ||
            _encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, targetPlayerId))
        {
            Logger.LogDebug(
                "Room encounter skipped: participant already in encounter. Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, InteractId={InteractId}",
                CurrentMapSubId,
                actorPlayerId,
                targetPlayerId,
                CurrentArea,
                interactId);
            return;
        }

        var targetSession = allSessions.FirstOrDefault(session => session.PlayerId == targetPlayerId);
        var targetBot = targetSession == null
            ? _botPlayerManager.GetBot(CurrentMapSubId, targetPlayerId)
            : null;

        bool targetUnaware = IsRoomEncounterTargetUnaware(targetSession, targetBot);
        int eventType = targetUnaware
            ? EncounterRevealManager.RoomDiscoveryEventType
            : EncounterRevealManager.RoomEncounterEventType;

        if (targetUnaware)
        {
            _encounterRevealManager.RegisterPendingRoomDiscovery(
                CurrentMapSubId,
                actorPlayerId,
                targetPlayerId,
                CurrentArea);

            SendEncounterEvent(targetPlayerId, CurrentArea, EncounterRevealManager.RoomDiscoveryEventType,
                EncounterRevealManager.PairCooldownSeconds);
        }
        else
        {
            HoldRoomEncounterBotTarget(targetBot);
            StartRoomEncounterTurn(actorPlayerId, targetPlayerId, CurrentArea, targetSession);
        }

        _gameEventLogManager.LogRoomEncounterReveal(CurrentMapSubId, actorPlayerId, targetPlayerId,
            CurrentArea.ToString(), interactId, isBot: false);

        _gameEventLogManager.LogInteraction(CurrentMapSubId, actorPlayerId,
            $"Room encounter reveal: Target={targetPlayerId}, Area={CurrentArea}, InteractId={interactId}",
            isBot: false);

        Logger.LogInformation(
            "Room encounter reveal started: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, InteractId={InteractId}, TargetUnaware={TargetUnaware}, EventType={EventType}, Source={Source}",
            CurrentMapSubId,
            actorPlayerId,
            targetPlayerId,
            CurrentArea,
            interactId,
            targetUnaware,
            eventType,
            sendCollectResult ? "ExploreSpot" : "Vision");

        if (!sendCollectResult)
            return;

        SendRngCollectResult(interactId, RngCollectEncounterResultType, 0, 0, 0);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
    }

    private void AddRoomEncounterStartCandidatesStillInVision(
        List<long> candidateIds,
        IReadOnlyCollection<long> startCandidateIds,
        IReadOnlyCollection<GameClientSession> allSessions)
    {
        var actorPosition = _lastValidatedPosition;
        if (actorPosition == null)
            return;

        foreach (long candidateId in startCandidateIds)
        {
            if (candidateId == 0 || candidateId == PlayerId || candidateIds.Contains(candidateId))
                continue;

            if (IsRoomEncounterCandidateStillInVision(candidateId, allSessions, actorPosition))
                candidateIds.Add(candidateId);
        }
    }

    private bool IsRoomEncounterCandidateStillInVision(
        long candidateId,
        IReadOnlyCollection<GameClientSession> allSessions,
        Vector3f actorPosition)
    {
        var session = allSessions.FirstOrDefault(session =>
            session.PlayerId == candidateId &&
            !session.IsEliminated &&
            session.CurrentMapSubId == CurrentMapSubId &&
            session.CurrentArea == CurrentArea);
        if (session != null)
        {
            return !session.IsRoomEncounterProcessingBlocked() &&
                   IsWithinRoomEncounterVision(actorPosition, session.LastValidatedPosition);
        }

        var bot = _botPlayerManager.GetBot(CurrentMapSubId, candidateId);
        return bot is { IsEliminated: false, IsInInteraction: false } &&
               bot.CurrentArea == CurrentArea &&
               !_encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, bot.PlayerId) &&
               IsWithinRoomEncounterVision(actorPosition, bot.Position);
    }

    private void StartRoomEncounterTurn(long discovererPlayerId, long targetPlayerId, AreaType area,
        GameClientSession? targetSession)
    {
        _encounterRevealManager.RegisterPendingRoomEncounterTurn(CurrentMapSubId, discovererPlayerId, targetPlayerId,
            area);
        if (targetSession == null)
        {
            _encounterRevealManager.TrySubmitRoomEncounterChoice(
                CurrentMapSubId,
                targetPlayerId,
                discovererPlayerId,
                area,
                ResolveBotRoomEncounterAction(),
                out _);
        }

        SendEncounterEvent(targetPlayerId, area, EncounterRevealManager.RoomEncounterEventType,
            EncounterRevealManager.PairCooldownSeconds);
        targetSession?.SendEncounterEvent(discovererPlayerId, area, EncounterRevealManager.RoomEncounterEventType,
            EncounterRevealManager.PairCooldownSeconds);
    }

    private Task HandleRoomEncounterAvoid(C_TO_G_ROOM_ENCOUNTER_AVOID msg)
    {
        if (!PlayerId.HasValue || msg == null || msg.TargetPlayerId == 0)
            return Task.CompletedTask;

        var area = msg.AreaType == AreaType.None ? CurrentArea : msg.AreaType;
        if (msg.ActionType == EncounterRevealManager.RoomDiscoveryReadyAction)
        {
            bool pending = _encounterRevealManager.HasPendingRoomDiscovery(
                CurrentMapSubId,
                PlayerId.Value,
                msg.TargetPlayerId,
                area);

            Logger.LogInformation(
                "Room discovery ready: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, Pending={Pending}",
                CurrentMapSubId,
                PlayerId.Value,
                msg.TargetPlayerId,
                area,
                pending);
            return Task.CompletedTask;
        }

        if (msg.ActionType != EncounterRevealManager.RoomDiscoveryActionHidePresence)
            SuppressRoomEncounterBriefly();

        bool consumedDiscovery = _encounterRevealManager.TryConsumePendingRoomDiscovery(
            CurrentMapSubId,
            PlayerId.Value,
            msg.TargetPlayerId,
            area,
            out var discoveryResolution);

        bool discoveryResolvedToEncounter = false;
        if (consumedDiscovery)
        {
            discoveryResolvedToEncounter = HandleRoomDiscoveryAction(discoveryResolution, msg.ActionType);
        }

        bool submittedEncounterChoice = false;
        bool resolvedEncounterChoice = false;
        if (!consumedDiscovery)
        {
            submittedEncounterChoice = _encounterRevealManager.TrySubmitRoomEncounterChoice(
                CurrentMapSubId,
                PlayerId.Value,
                msg.TargetPlayerId,
                area,
                msg.ActionType,
                out var resolution);
            if (resolution.PlayerA != 0)
            {
                resolvedEncounterChoice = true;
                SendRoomEncounterTurnResult(resolution);
            }
        }

        Logger.LogInformation(
            "Room encounter action: Matching={MatchingId}, Actor={Actor}, Target={Target}, Area={Area}, ActionType={ActionType}, ConsumedDiscovery={ConsumedDiscovery}, DiscoveryEncounter={DiscoveryEncounter}, SubmittedEncounterChoice={SubmittedEncounterChoice}, Resolved={Resolved}",
            CurrentMapSubId,
            PlayerId.Value,
            msg.TargetPlayerId,
            area,
            msg.ActionType,
            consumedDiscovery,
            discoveryResolvedToEncounter,
            submittedEncounterChoice,
            resolvedEncounterChoice);
        return Task.CompletedTask;
    }

    private void SuppressRoomEncounterBriefly()
    {
        var until = DateTime.UtcNow.Add(RoomEncounterActionSuppressDuration);
        if (until > _suppressRoomEncounterUntilUtc)
            _suppressRoomEncounterUntilUtc = until;
    }

    private bool IsRoomEncounterSuppressed()
    {
        return IsRoomEncounterSuppressed(DateTime.UtcNow);
    }

    private bool IsRoomEncounterSuppressed(DateTime now)
    {
        return _suppressRoomEncounterUntilUtc > now;
    }

    private bool IsRoomEncounterProcessingBlocked()
    {
        return IsRoomEncounterProcessingBlocked(DateTime.UtcNow);
    }

    private bool IsRoomEncounterProcessingBlocked(DateTime now)
    {
        return IsRoomEncounterSuppressed(now) ||
               (PlayerId.HasValue &&
                _encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, PlayerId.Value));
    }

    private bool IsRoomEncounterActorBlocked()
    {
        return IsRoomEncounterActorBlocked(DateTime.UtcNow);
    }

    private bool IsRoomEncounterActorBlocked(DateTime now)
    {
        return IsRoomEncounterProcessingBlocked(now) || IsRoomEncounterActionInProgress();
    }

    private bool IsRoomEncounterActionInProgress()
    {
        return CurrentState == PlayerState.Exploring;
    }

    private void CancelPendingRoomEncounterTurnsForCurrentPlayer(string reason)
    {
        if (!PlayerId.HasValue || CurrentMapSubId <= 0)
            return;

        var resolutions = _encounterRevealManager.ConsumePendingRoomEncounterTurnsForPlayer(
            CurrentMapSubId,
            PlayerId.Value);
        foreach (var resolution in resolutions)
        {
            long otherPlayerId = resolution.GetOtherPlayer(PlayerId.Value);
            ReleaseRoomEncounterBotTarget(_botPlayerManager.GetBot(resolution.MatchingId, otherPlayerId));
            Logger.LogInformation(
                "Room encounter turn cancelled by player action: Matching={MatchingId}, Player={Player}, Other={Other}, Area={Area}, Reason={Reason}",
                resolution.MatchingId,
                PlayerId.Value,
                otherPlayerId,
                resolution.Area,
                reason);
        }
    }

    private bool HandleRoomDiscoveryAction(RoomDiscoveryResolution resolution, int actionType)
    {
        bool shouldStartEncounter = actionType switch
        {
            EncounterRevealManager.RoomDiscoveryActionFace => true,
            EncounterRevealManager.RoomDiscoveryActionHidePresence => false,
            EncounterRevealManager.RoomDiscoveryActionLeave => false,
            EncounterRevealManager.RoomEncounterActionLeave => false,
            _ => true
        };

        if (!shouldStartEncounter)
        {
            Logger.LogInformation(
                "Room discovery avoided: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}, ActionType={ActionType}",
                resolution.MatchingId,
                resolution.DiscovererPlayerId,
                resolution.TargetPlayerId,
                resolution.Area,
                actionType);
            return false;
        }

        if (CurrentArea != resolution.Area || CurrentMapSubId <= 0 || IsEliminated)
        {
            Logger.LogInformation(
                "Room discovery action ignored because discoverer left: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}, CurrentArea={CurrentArea}, ActionType={ActionType}",
                resolution.MatchingId,
                resolution.DiscovererPlayerId,
                resolution.TargetPlayerId,
                resolution.Area,
                CurrentArea,
                actionType);
            return false;
        }

        var allSessions = _getSessionsByInstance(CurrentMapId, resolution.MatchingId);
        var targetSession = allSessions.FirstOrDefault(session =>
            session.PlayerId == resolution.TargetPlayerId &&
            !session.IsEliminated &&
            session.CurrentMapSubId == resolution.MatchingId &&
            session.CurrentArea == resolution.Area);
        var targetBot = targetSession == null
            ? _botPlayerManager.GetBot(resolution.MatchingId, resolution.TargetPlayerId)
            : null;

        if (targetSession == null &&
            (targetBot is not { IsEliminated: false } || targetBot.CurrentArea != resolution.Area))
        {
            Logger.LogInformation(
                "Room discovery action ignored because target left: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}, ActionType={ActionType}",
                resolution.MatchingId,
                resolution.DiscovererPlayerId,
                resolution.TargetPlayerId,
                resolution.Area,
                actionType);
            return false;
        }

        HoldRoomEncounterBotTarget(targetBot);
        StartRoomEncounterTurn(resolution.DiscovererPlayerId, resolution.TargetPlayerId, resolution.Area,
            targetSession);

        Logger.LogInformation(
            "Room discovery resolved by choice: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}, ActionType={ActionType}",
            resolution.MatchingId,
            resolution.DiscovererPlayerId,
            resolution.TargetPlayerId,
            resolution.Area,
            actionType);
        return true;
    }

    private void SendRoomEncounterTurnResult(RoomEncounterTurnResolution resolution)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, resolution.MatchingId);
        SendRoomEncounterTurnResultToPlayer(allSessions, resolution, resolution.PlayerA);
        SendRoomEncounterTurnResultToPlayer(allSessions, resolution, resolution.PlayerB);
        ReleaseRoomEncounterBotTarget(_botPlayerManager.GetBot(resolution.MatchingId, resolution.PlayerA));
        ReleaseRoomEncounterBotTarget(_botPlayerManager.GetBot(resolution.MatchingId, resolution.PlayerB));

        Logger.LogInformation(
            "Room encounter turn resolved: Matching={MatchingId}, PlayerA={PlayerA}, ActionA={ActionA}, PlayerB={PlayerB}, ActionB={ActionB}, Area={Area}",
            resolution.MatchingId,
            resolution.PlayerA,
            resolution.PlayerAAction,
            resolution.PlayerB,
            resolution.PlayerBAction,
            resolution.Area);
    }

    private void SendRoomEncounterTurnResultToPlayer(IReadOnlyCollection<GameClientSession> allSessions,
        RoomEncounterTurnResolution resolution, long playerId)
    {
        var session = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
        if (session == null)
            return;

        long otherPlayerId = resolution.GetOtherPlayer(playerId);
        if (otherPlayerId == 0)
            return;

        int eventType = ResolveRoomEncounterResultEventType(
            resolution.GetActionFor(playerId),
            resolution.GetOtherActionFor(playerId));
        session.SendEncounterEvent(otherPlayerId, resolution.Area, eventType,
            EncounterRevealManager.PairCooldownSeconds);
    }

    private static int ResolveRoomEncounterResultEventType(int ownActionType, int otherActionType)
    {
        int ownAction = EncounterRevealManager.NormalizeRoomEncounterAction(ownActionType);
        int otherAction = EncounterRevealManager.NormalizeRoomEncounterAction(otherActionType);
        if (ownAction == EncounterRevealManager.RoomEncounterActionLeave)
            return EncounterRevealManager.RoomEncounterLeaveResultEventType;

        bool ownHid = ownAction == EncounterRevealManager.RoomEncounterActionHidePresence;
        bool otherHid = otherAction == EncounterRevealManager.RoomEncounterActionHidePresence;
        if (ownHid && otherHid)
            return EncounterRevealManager.RoomEncounterBothHidePresenceResultEventType;

        if (ownHid)
            return EncounterRevealManager.RoomEncounterOwnHidePresenceResultEventType;

        if (otherHid)
            return EncounterRevealManager.RoomEncounterHidePresenceResultEventType;

        return EncounterRevealManager.RoomEncounterInspectResultEventType;
    }

    private static int ResolveBotRoomEncounterAction()
    {
        return Random.Shared.Next(3) switch
        {
            0 => EncounterRevealManager.RoomEncounterActionHidePresence,
            1 => EncounterRevealManager.RoomEncounterActionLeave,
            _ => EncounterRevealManager.RoomEncounterActionInspect
        };
    }

    private static bool IsWithinRoomEncounterVision(Vector3f actorPosition, Vector3f? targetPosition)
    {
        if (targetPosition == null)
            return false;

        float dx = actorPosition.X - targetPosition.X;
        float dy = actorPosition.Y - targetPosition.Y;
        return dx * dx + dy * dy <= RoomEncounterVisionDistance * RoomEncounterVisionDistance;
    }

    private bool IsRoomEncounterTargetUnaware(GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        if (targetSession != null)
            return targetSession.IsRoomEncounterActionInProgress();

        if (targetBot == null)
            return false;

        return targetBot.RngCollectProgressStartTime != DateTime.MinValue ||
               targetBot.ChecklistActivityProgressStartTime != DateTime.MinValue;
    }

    private void ResolvePendingRoomDiscoveriesAfterExploreFinished(AreaType area)
    {
        if (!PlayerId.HasValue || CurrentMapSubId <= 0 || area == AreaType.None || CurrentArea != area ||
            IsEliminated)
        {
            return;
        }

        var discovererPlayerIds = _encounterRevealManager.ConsumePendingRoomDiscoverers(
            CurrentMapSubId,
            PlayerId.Value,
            area);
        if (discovererPlayerIds.Count == 0)
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        foreach (long discovererPlayerId in discovererPlayerIds)
        {
            if (discovererPlayerId == 0 || discovererPlayerId == PlayerId.Value)
                continue;

            if (_encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, PlayerId.Value) ||
                _encounterRevealManager.HasPendingRoomEncounterTurn(CurrentMapSubId, discovererPlayerId))
            {
                continue;
            }

            var discovererSession = allSessions.FirstOrDefault(session =>
                session.PlayerId == discovererPlayerId &&
                !session.IsEliminated &&
                session.CurrentMapSubId == CurrentMapSubId &&
                session.CurrentArea == area);
            if (discovererSession == null)
                continue;

            var discovererPosition = discovererSession.LastValidatedPosition;
            if (discovererPosition == null ||
                !IsWithinRoomEncounterVision(discovererPosition, LastValidatedPosition))
            {
                Logger.LogInformation(
                    "Pending room discovery expired after target explore finish: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}",
                    CurrentMapSubId,
                    discovererPlayerId,
                    PlayerId.Value,
                    area);
                continue;
            }

            discovererSession.StartRoomEncounterTurn(discovererPlayerId, PlayerId.Value, area, this);
            Logger.LogInformation(
                "Pending room discovery promoted after target explore finish: Matching={MatchingId}, Discoverer={Discoverer}, Target={Target}, Area={Area}",
                CurrentMapSubId,
                discovererPlayerId,
                PlayerId.Value,
                area);
            break;
        }
    }

    private static void HoldRoomEncounterBotTarget(BotPlayerState? bot)
    {
        if (bot == null)
            return;

        bot.HoldForInteraction(RoomEncounterHoldDuration);
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
    }

    private static void ReleaseRoomEncounterBotTarget(BotPlayerState? bot)
    {
        if (bot == null)
            return;

        bot.IsInInteraction = false;
        bot.InteractionStayUntil = DateTime.MinValue;
    }
}
