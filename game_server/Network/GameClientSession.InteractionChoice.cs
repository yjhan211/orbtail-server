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
///     상호작용 선택지 (차기 재사용 보존): 질문/답변 선택지 전송, INTERACTION_ASK/ANSWER 핸들러, 조우 행동 선택지.
/// </summary>
public partial class GameClientSession
{
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

    private ChecklistTaskData? ResolveInteractionAnswerTask(long playerId)
    {
        return _checklistManager.GetNextActiveGeneralInteractTask(CurrentMapSubId, playerId);
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

        const int roundId = 1; // 라운드 시스템 퇴역(#246)
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

    private bool TryCreateTargetPlayerAnswer(int encodedAnswerIndex, out InteractionAnswer? selectedAnswer)
    {
        selectedAnswer = null;
        if (!PlayerId.HasValue) return false;

        int targetIndex = encodedAnswerIndex - TargetPlayerAnswerIndexOffset;
        if (targetIndex < 0) return false;

        var candidates = GetInteractionPlayerCandidates();
        if (targetIndex >= candidates.Count) return false;

        var candidate = candidates[targetIndex];
        var link = _matchRosterManager.GetEntry(CurrentMapSubId, PlayerId.Value);
        bool isTrue = link?.TargetPlayerId == candidate.playerId;

        selectedAnswer = new InteractionAnswer
        {
            IsTrue = isTrue,
            ClaimedJob = JobTitle.NONE,
            TextId = TargetPlayerAnswerTextId,
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
        if (msg.AnswerIndex >= TargetPlayerAnswerIndexOffset)
        {
            if (!TryCreateTargetPlayerAnswer(msg.AnswerIndex, out var manittoTargetAnswer) || manittoTargetAnswer == null)
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
