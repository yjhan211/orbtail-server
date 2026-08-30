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
///     봇 심문(타겟 봇 질문·답변 선택지·타임아웃) — 상호작용 선택지 시스템의 봇 측.
/// </summary>
public partial class GameClientSession
{
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
                // Expected when the conversation ends before the delayed choices are sent.
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

    private string ResolveDemoInteractionPlayerName(long playerId)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, playerId);
        if (bot != null && !string.IsNullOrEmpty(bot.Name)) return bot.Name;

        var profile = _matchRosterManager.GetPlayerProfile(CurrentMapSubId, playerId);
        if (!string.IsNullOrWhiteSpace(profile?.Name)) return profile.Name;

        return BotPlayerManager.IsBotPlayerId(playerId)
            ? $"Player{Math.Abs(playerId)}"
            : $"Player{playerId}";
    }
}
