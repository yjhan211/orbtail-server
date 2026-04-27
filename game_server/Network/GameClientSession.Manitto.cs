using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     마니또 전용 핸들러: 색출, 흔적 배치, 타겟 위치 추적.
/// </summary>
public partial class GameClientSession
{
    /// <summary>
    ///     미션 정보 전송 (게임 접속 시)
    /// </summary>
    private void SendMissionInfo()
    {
        if (!PlayerId.HasValue) return;

        var currentStep = _missionManager.GetCurrentStep(CurrentMapSubId, PlayerId.Value);
        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_MISSION_INFO, PlayerId.Value);
        var msg = new G_TO_C_MISSION_INFO
        {
            JobTitle = MyJobTitle,
            CurrentStep = state.CurrentStepOrder,
            TotalSteps = state.TotalSteps,
            TargetArea = currentStep?.TargetArea ?? 0,
            TargetInteractId = currentStep?.TargetInteractId ?? 0,
            TargetActionId = currentStep?.TargetActionId ?? 0
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

        // 적중 시 마니또 탈락 처리
        if (isCorrect && errorCode == ErrorCode.SUCCESS)
        {
            _ = ProcessElimination(msg.TargetPlayerId, EliminationReason.DETECTED);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     플레이어 탈락 처리 + 체인 단절 브로드캐스트
    /// </summary>
    private Task ProcessElimination(long eliminatedPlayerId, EliminationReason reason)
    {
        var affected = _manittoChainManager.EliminatePlayer(CurrentMapSubId, eliminatedPlayerId, reason);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        // 1. 전체에게 탈락 알림
        using var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
        var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
        {
            PlayerId = eliminatedPlayerId,
            Reason = reason
        };
        eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
        foreach (var session in allSessions) session.Send(eliminatedPacket);

        // 세션 ManittoStatus 동기화 (탈락자 → SPECTATING으로 관전 전환)
        foreach (var (playerId, newStatus) in affected)
        {
            var s = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (s == null) continue;
            s.ManittoStatus = newStatus == ManittoStatus.ELIMINATED
                ? ManittoStatus.SPECTATING
                : newStatus;
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
        if (isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            SendGameResult(allSessions, winnerId ?? 0, false);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     게임 결과 패킷 전송 (체인 전체 공개)
    /// </summary>
    private void SendGameResult(List<GameClientSession> allSessions, long winnerId, bool isTimeout)
    {
        var chainData = _manittoChainManager.BuildGameResult(CurrentMapSubId);
        var gameStartTime = DateTime.UtcNow; // 근사값 (AreaClosureManager의 GameStartTime 참조)

        var players = chainData.Select(d => new GameResultPlayerInfo
        {
            PlayerId = d.playerId,
            JobTitle = d.job,
            TargetPlayerId = d.targetId,
            ManittoPlayerId = d.manittoId,
            EliminationReason = d.reason,
            FinalStatus = d.finalStatus,
            SurvivalTimeSeconds = 0 // 근사값; 추후 정확한 타이밍 필요 시 개선
        }).ToList();

        using var resultPacket = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
        var resultMsg = new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = isTimeout,
            Players = players
        };
        resultPacket.SetBody(MessagePackSerializer.Serialize(resultMsg));
        foreach (var session in allSessions) session.Send(resultPacket);

        // 기존 게임 종료 패킷도 전송 (클라이언트 호환)
        using var endPacket = PacketMaker.G_TO_C_GAME_END(CurrentMapSubId, !isTimeout);
        foreach (var session in allSessions) session.Send(endPacket);
    }

    /// <summary>
    ///     흔적 배치 요청 처리 (마니또 전용)
    /// </summary>
    private Task HandlePlaceTrace(C_TO_G_PLACE_TRACE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        const int placeTraceCost = 5; // 스태미나 소모 (패키지 Y: -10 → -5, #24)

        // 스태미나 부족
        if (Stamina < placeTraceCost)
        {
            using var failPacket = Packet.Create((int)Protocol.G_TO_C_PLACE_TRACE_RESULT, PlayerId.Value);
            var failMsg = new G_TO_C_PLACE_TRACE_RESULT
            {
                ErrorCode = ErrorCode.INSUFFICIENT_STAMINA,
                StaminaCost = placeTraceCost
            };
            failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
            Send(failPacket);
            return Task.CompletedTask;
        }

        // 스태미나 소모
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

    /// <summary>
    ///     마니또 → 타겟 구역 위치 전송
    /// </summary>
    public void SendTargetLocation()
    {
        if (!PlayerId.HasValue || TargetPlayerId == 0) return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (targetSession == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_TARGET_LOCATION, PlayerId.Value);
        var msg = new G_TO_C_TARGET_LOCATION
        {
            TargetPlayerId = TargetPlayerId,
            AreaType = targetSession.CurrentArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     탐색 완료 시 미션 진행 체크
    /// </summary>
    public void CheckMissionProgress(AreaType area, int interactId, int actionId)
    {
        if (!PlayerId.HasValue) return;

        var result = _missionManager.TryCompleteStep(CurrentMapSubId, PlayerId.Value, area, interactId, actionId);
        if (result == null) return;

        // 스태미나 보상
        int prevStamina = Stamina;
        Stamina = Math.Min(Stamina + result.StaminaReward, MaxStamina);
        int staminaDelta = Stamina - prevStamina;
        using var statsPacket = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Stamina, staminaDelta, Corruption, 0);
        Send(statsPacket);

        if (result.IsAllCompleted)
        {
            // 미션 전체 완료
            using var completePacket = Packet.Create((int)Protocol.G_TO_C_MISSION_ALL_COMPLETE, PlayerId.Value);
            var completeMsg = new G_TO_C_MISSION_ALL_COMPLETE { JobTitle = MyJobTitle };
            completePacket.SetBody(MessagePackSerializer.Serialize(completeMsg));
            Send(completePacket);
        }
        else
        {
            // 단계 완료 + 다음 정보
            using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = result.CompletedStep,
                StaminaReward = result.StaminaReward,
                NextTargetArea = result.NextStep?.TargetArea ?? 0,
                NextTargetInteractId = result.NextStep?.TargetInteractId ?? 0,
                NextTargetActionId = result.NextStep?.TargetActionId ?? 0
            };
            stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
            Send(stepPacket);
        }

        // 흔적 저장 (탐색 시 발견됨)
        StoreTrace(result.TraceArea, result.TraceInteractId, result.TraceDescription, true);
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

    /// <summary>
    ///     스태미나/정신력이 0 이하일 때 탈락 체크
    /// </summary>
    public void CheckResourceElimination()
    {
        if (!PlayerId.HasValue) return;

        if (Stamina <= 0)
            _ = ProcessElimination(PlayerId.Value, EliminationReason.STAMINA_ZERO);
        else if (Corruption >= MaxCorruption)
            _ = ProcessElimination(PlayerId.Value, EliminationReason.MENTAL_ZERO);
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

        // 시한부만 사보타주 가능
        if (ManittoStatus != ManittoStatus.TERMINAL)
        {
            SendSabotageResult(ErrorCode.SABOTAGE_NOT_TERMINAL, SabotageStaminaCost);
            return Task.CompletedTask;
        }

        if (Stamina < SabotageStaminaCost)
        {
            SendSabotageResult(ErrorCode.INSUFFICIENT_STAMINA, SabotageStaminaCost);
            return Task.CompletedTask;
        }

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

        // === 4B Step 1: 대상 미션 단계 무효화 ===
        if (targetSession != null && targetSession.PlayerId.HasValue)
        {
            var (success, invalidatedStep, nextStep) =
                _missionManager.InvalidateCurrentStep(CurrentMapSubId, targetSession.PlayerId.Value);

            if (success)
            {
                // 무효화 알림 (대상에게)
                using var redirectPacket = Packet.Create(
                    (int)Protocol.G_TO_C_MISSION_REDIRECTED, targetSession.PlayerId.Value);
                var redirectMsg = new G_TO_C_MISSION_REDIRECTED
                {
                    CurrentStep = invalidatedStep,
                    NewTargetArea = nextStep?.TargetArea ?? 0,
                    NewTargetInteractId = nextStep?.TargetInteractId ?? 0,
                    NewTargetActionId = nextStep?.TargetActionId ?? 0
                };
                redirectPacket.SetBody(MessagePackSerializer.Serialize(redirectMsg));
                targetSession.Send(redirectPacket);
            }
        }
        else if (msg.InteractId != 0)
        {
            // 레거시: interactId 기반 재설정
            var affected = _missionManager.RedirectMissionsByInteractId(
                CurrentMapSubId, msg.InteractId, _areaClosureManager);

            foreach (var (playerId, step, newArea, newInteractId, newActionId) in affected)
            {
                var victim = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
                if (victim == null) continue;

                using var packet = Packet.Create((int)Protocol.G_TO_C_MISSION_REDIRECTED, playerId);
                var redirectMsg = new G_TO_C_MISSION_REDIRECTED
                {
                    CurrentStep = step,
                    NewTargetArea = newArea,
                    NewTargetInteractId = newInteractId,
                    NewTargetActionId = newActionId
                };
                packet.SetBody(MessagePackSerializer.Serialize(redirectMsg));
                victim.Send(packet);
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

        // 질문 선택지 생성
        var questions = _interactionChoiceService.GenerateQuestions(
            CurrentMapSubId,
            askerSession.PlayerId.Value,
            answererSession.PlayerId.Value,
            CurrentArea,
            answererSession._previousArea);

        askerSession._pendingQuestions = questions;

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

    /// <summary>
    ///     질문자가 질문 선택
    /// </summary>
    private Task HandleInteractionAsk(C_TO_G_INTERACTION_ASK msg)
    {
        if (!PlayerId.HasValue || !_activeConversationPlayerId.HasValue) return Task.CompletedTask;

        long partnerPlayerId = _activeConversationPlayerId.Value;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var partnerSession = allSessions.FirstOrDefault(s => s.PlayerId == partnerPlayerId);
        if (partnerSession == null) return Task.CompletedTask;

        _lastAskedQuestion = msg.QuestionType;

        // 답변 선택지 생성
        var answers = _interactionChoiceService.GenerateAnswers(
            CurrentMapSubId,
            partnerPlayerId,
            msg.QuestionType);

        partnerSession._pendingAnswers = answers;

        // 질문 텍스트 찾기
        string questionText = _pendingQuestions?
            .FirstOrDefault(q => q.QuestionType == msg.QuestionType)?.Text ?? "질문";

        // 답변자에게 답변 선택지 전송
        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ANSWER_CHOICES, partnerPlayerId);
        var answerMsg = new G_TO_C_INTERACTION_ANSWER_CHOICES
        {
            QuestionType = msg.QuestionType,
            QuestionText = questionText,
            Answers = answers
        };
        packet.SetBody(MessagePackSerializer.Serialize(answerMsg));
        partnerSession.Send(packet);

        Logger.LogInformation("상호작용 질문: Asker={Asker}, Answerer={Answerer}, Type={Type}",
            PlayerId, partnerPlayerId, msg.QuestionType);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     답변자가 답변 선택
    /// </summary>
    private Task HandleInteractionAnswer(C_TO_G_INTERACTION_ANSWER msg)
    {
        if (!PlayerId.HasValue || !_activeConversationPlayerId.HasValue) return Task.CompletedTask;
        if (_pendingAnswers == null || msg.AnswerIndex < 0 || msg.AnswerIndex >= _pendingAnswers.Count)
            return Task.CompletedTask;

        long askerPlayerId = _activeConversationPlayerId.Value;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var askerSession = allSessions.FirstOrDefault(s => s.PlayerId == askerPlayerId);
        if (askerSession == null) return Task.CompletedTask;

        var selectedAnswer = _pendingAnswers[msg.AnswerIndex];

        // 로그 기록 + 사칭 발각 체크
        var (isFakeDetected, conflictInfo) = _interactionChoiceService.ProcessAnswer(
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
            ConflictInfo = conflictInfo
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
            ConflictInfo = conflictInfo
        };

        using (var answererPacket = Packet.Create((int)Protocol.G_TO_C_INTERACTION_RESULT, PlayerId.Value))
        {
            answererPacket.SetBody(MessagePackSerializer.Serialize(resultForAnswerer));
            Send(answererPacket);
        }

        Logger.LogInformation("상호작용 답변: Answerer={Answerer}, Asker={Asker}, ClaimedJob={Job}, Fake={Fake}",
            PlayerId, askerPlayerId, selectedAnswer.ClaimedJob, isFakeDetected);

        // 선택지 상태 클리어
        _pendingAnswers = null;
        askerSession._pendingQuestions = null;

        return Task.CompletedTask;
    }
}
