using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    #region 플레이어 상호작용

    private const int InteractStaminaCost = 5;

    private Task HandlePlayerInteractRequest(C_TO_G_PLAYER_INTERACT_REQUEST msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 권고안 B 2026-05-05: Stamina 부족해도 ModifyStats가 Cor 1:2 변환 — 사전 차단 제거.

        // 쿨다운 체크 (거절/타임아웃 후 5초)
        if (DateTime.UtcNow - _lastInteractRejectTime < InteractCooldown)
        {
            using var cooldownPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(msg.PlayerId, ErrorCode.ACTION_COOLDOWN);
            Send(cooldownPacket);
            Logger.LogInformation("PlayerInteractRequest blocked by cooldown: requester={RequesterId}", PlayerId);
            return Task.CompletedTask;
        }

        long targetPlayerId = msg.PlayerId;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == targetPlayerId);

        // #26: 대상이 봇이면 자동 응답 처리 (휴리스틱)
        if (targetSession == null && BotPlayerManager.IsBotPlayerId(targetPlayerId))
        {
            return HandleBotInteractRequest(targetPlayerId);
        }

        // 대상 없으면 에러
        if (targetSession == null)
        {
            using var errorPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(targetPlayerId, ErrorCode.PLAYER_NOT_FOUND);
            Send(errorPacket);
            Logger.LogWarning(
                "PlayerInteractRequest failed: target PlayerId={TargetId} not found (requester={RequesterId})",
                targetPlayerId, PlayerId);
            return Task.CompletedTask;
        }

        // pending 저장 (요청자 세션에)
        _pendingInteractPlayerId = targetPlayerId;

        // 양쪽에 전송
        using (var requesterPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(targetPlayerId, ErrorCode.SUCCESS))
        {
            Send(requesterPacket); // 요청자(A)에게: 대기 시작
        }

        using (var targetPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(PlayerId.Value, ErrorCode.SUCCESS))
        {
            targetSession.Send(targetPacket); // 대상(B)에게: 수락 UI 표시
        }

        Logger.LogInformation("PlayerInteractRequest: requester={RequesterId} → target={TargetId}", PlayerId,
            targetPlayerId);

        // 10초 타이머 시작
        _interactTimeoutCts?.Cancel();
        _interactTimeoutCts?.Dispose();
        _interactTimeoutCts = new CancellationTokenSource();
        var cts = _interactTimeoutCts;
        long requesterPlayerId = PlayerId.Value;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(10000, cts.Token);

                // 타임아웃: 양쪽에 RESULT(accepted=false) 전송
                Logger.LogInformation("PlayerInteract timeout: requester={RequesterId}, target={TargetId}",
                    requesterPlayerId, targetPlayerId);

                using var requesterResult =
                    PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(false, targetPlayerId, ErrorCode.TIMEOUT);
                Send(requesterResult);

                if (targetSession.PlayerId.HasValue)
                {
                    using var targetResult =
                        PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(false, requesterPlayerId, ErrorCode.TIMEOUT);
                    targetSession.Send(targetResult);
                }

                _pendingInteractPlayerId = null;
                _interactTimeoutCts = null;
                _lastInteractRejectTime = DateTime.UtcNow;
            }
            catch (TaskCanceledException)
            {
                // 타이머 취소됨 (수락/거절로 인해)
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PlayerInteract timeout error: requester={RequesterId}, target={TargetId}",
                    requesterPlayerId, targetPlayerId);
            }
        }, cts.Token);

        return Task.CompletedTask;
    }

    private Task HandlePlayerInteractResponse(C_TO_G_PLAYER_INTERACT_RESPONSE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        long requesterPlayerId = msg.PlayerId;
        if (BotPlayerManager.IsBotPlayerId(requesterPlayerId))
            return HandleBotInteractResponse(requesterPlayerId, msg.Accepted);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var requesterSession = allSessions.FirstOrDefault(s => s.PlayerId == requesterPlayerId);

        if (requesterSession == null)
        {
            Logger.LogWarning("PlayerInteractResponse failed: requester PlayerId={RequesterId} not found",
                requesterPlayerId);
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(false, requesterPlayerId, ErrorCode.SESSION_NOT_FOUND);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // 요청자의 pending이 본인(응답자)인지 검증
        if (requesterSession._pendingInteractPlayerId != PlayerId.Value)
        {
            Logger.LogWarning("PlayerInteractResponse failed: pending mismatch (expected={Expected}, actual={Actual})",
                PlayerId.Value, requesterSession._pendingInteractPlayerId);
            return Task.CompletedTask;
        }

        // 타이머 취소
        requesterSession._interactTimeoutCts?.Cancel();
        requesterSession._interactTimeoutCts = null;

        // 양쪽에 RESULT 전송
        using (var requesterResult =
               PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(msg.Accepted, PlayerId.Value, ErrorCode.SUCCESS))
        {
            requesterSession.Send(requesterResult); // 요청자에게
        }

        using (var responderResult =
               PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(msg.Accepted, requesterPlayerId, ErrorCode.SUCCESS))
        {
            Send(responderResult); // 응답자에게
        }

        Logger.LogInformation(
            "PlayerInteractResponse: responder={ResponderId}, requester={RequesterId}, accepted={Accepted}",
            PlayerId, requesterPlayerId, msg.Accepted);

        // pending 클리어
        requesterSession._pendingInteractPlayerId = null;

        if (msg.Accepted)
        {
            requesterSession.ModifyStats(-InteractStaminaCost);

            // 수락 시 양쪽 세션에 활성 대화 상대 설정
            requesterSession._activeConversationPlayerId = PlayerId.Value;
            _activeConversationPlayerId = requesterPlayerId;

            // 마니또 상호작용 선택지 생성: 요청자가 질문자, 응답자가 답변자
            SendInteractionChoices(requesterSession, this);
        }
        else
        {
            // 거절 시 쿨다운 시작
            requesterSession._lastInteractRejectTime = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    private Task HandlePlayerInteractEnd(C_TO_G_PLAYER_INTERACT_END msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 봇 대상 대화 종료 — 5초 후 봇 정지 해제 (WalkStep이 InteractionStayUntil 시각 후 자동 해제).
        if (BotPlayerManager.IsBotPlayerId(msg.PlayerId))
        {
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, msg.PlayerId);
            if (bot != null)
            {
                bot.IsInInteraction = false;
                bot.InteractionStayUntil = DateTime.MinValue;
                bot.LoopWaitUntil = DateTime.UtcNow.AddSeconds(5);
                if (PlayerId.HasValue)
                    bot.TargetEncounterStartedAtByPlayerId[PlayerId.Value] = DateTime.UtcNow;
            }

            // active conversation 해제 → HandleAreaMove 차단 풀림.
            if (_activeConversationPlayerId == msg.PlayerId) _activeConversationPlayerId = null;

            using var endPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_END(msg.PlayerId);
            Send(endPacket);

            Logger.LogInformation(
                "봇 대화 종료: BotId={Bot}, requester={Requester}, 5초 후 walking 재개",
                msg.PlayerId, PlayerId);
            return Task.CompletedTask;
        }

        if (!_activeConversationPlayerId.HasValue)
        {
            Logger.LogWarning("PlayerInteractEnd failed: no active conversation for PlayerId={PlayerId}", PlayerId);
            using var errPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_END(PlayerId.Value);
            Send(errPacket);
            return Task.CompletedTask;
        }

        long partnerPlayerId = _activeConversationPlayerId.Value;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var partnerSession = allSessions.FirstOrDefault(s => s.PlayerId == partnerPlayerId);

        EndConversation(partnerPlayerId, partnerSession);

        Logger.LogInformation("PlayerInteractEnd: PlayerId={PlayerId} ended conversation with PlayerId={PartnerId}",
            PlayerId, partnerPlayerId);

        return Task.CompletedTask;
    }

    private Task HandlePlayerInteractUseItem(C_TO_G_PLAYER_INTERACT_USE_ITEM msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 활성 대화 검증
        if (!_activeConversationPlayerId.HasValue || _activeConversationPlayerId.Value != msg.PlayerId)
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(false, ErrorCode.INVALID_REQUEST, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // 권고안 B 2026-05-05: Stamina 부족해도 ModifyStats가 Cor 1:2 변환 — 사전 차단 제거.

        // 아이템 검증
        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        var itemInfo = inventory.GetItem(msg.ItemUid);
        if (itemInfo == null)
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(false, ErrorCode.ITEM_NOT_FOUND, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        int itemId = itemInfo.ItemId;
        var itemData = GameItemData.Get(itemId);
        if (!itemData.IsConsumable)
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(false, ErrorCode.ITEM_NOT_USABLE, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // 아이템 소모
        bool success =
            _inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid, 1, out var updatedItem);
        if (!success || updatedItem == null)
        {
            using var errPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(false, ErrorCode.FATAL, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // A: 인벤토리 업데이트 + 스태미나 차감
        SendInGameInventoryUpdate(updatedItem);
        ModifyStats(-InteractStaminaCost);

        // B: 아이템 버프 적용
        long targetPlayerId = msg.PlayerId;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == targetPlayerId);
        targetSession?.ApplyItemBuffs(itemId);

        // 양쪽에 사용 결과 전송 (targetPlayerId로 이펙트 대상 지정)
        using (var resultPacket =
               PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(true, ErrorCode.SUCCESS, itemId, targetPlayerId))
        {
            Send(resultPacket);
        }

        if (targetSession != null)
        {
            using var targetResultPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(true, ErrorCode.SUCCESS, itemId, targetPlayerId);
            targetSession.Send(targetResultPacket);
        }

        Logger.LogInformation("PlayerInteractUseItem: PlayerId={PlayerId} used item {ItemId} on PlayerId={TargetId}",
            PlayerId, itemId, targetPlayerId);

        // 대화 종료
        EndConversation(targetPlayerId, targetSession);

        return Task.CompletedTask;
    }

    private Task HandlePlayerInteractShareRule(C_TO_G_PLAYER_INTERACT_SHARE_RULE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 활성 대화 검증
        if (!_activeConversationPlayerId.HasValue || _activeConversationPlayerId.Value != msg.PlayerId)
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(false, ErrorCode.INVALID_REQUEST, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // 권고안 B 2026-05-05: Stamina 부족해도 ModifyStats가 Cor 1:2 변환 — 사전 차단 제거.

        // 수칙 소유 검증
        if (!_discoveredRules.ContainsKey(msg.RuleId))
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(false, ErrorCode.INVALID_REQUEST, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // 동일 대상에게 동일 수칙 중복 공유 방지
        if (!_sharedRules.Add((msg.RuleId, msg.PlayerId)))
        {
            using var errPacket =
                PacketMaker.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(false, ErrorCode.INVALID_REQUEST, 0);
            Send(errPacket);
            return Task.CompletedTask;
        }

        // A: 스태미나 차감
        ModifyStats(-InteractStaminaCost);

        // 최초 발견자 PlayerId 조회
        long originalDiscovererId = _discoveredRules[msg.RuleId];

        // B: 대상 플레이어에 수칙 추가 (최초 발견자 전파)
        long targetPlayerId = msg.PlayerId;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == targetPlayerId);
        targetSession?._discoveredRules.TryAdd(msg.RuleId, originalDiscovererId);
        // B가 A에게 같은 수칙을 되돌려 공유하지 않도록 기록
        targetSession?._sharedRules.Add((msg.RuleId, PlayerId!.Value));

        // 양쪽에 결과 전송
        using (var resultPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(true, ErrorCode.SUCCESS,
                   msg.RuleId, targetPlayerId, originalDiscovererId))
        {
            Send(resultPacket);
        }

        if (targetSession != null)
        {
            using var targetResultPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(true, ErrorCode.SUCCESS,
                msg.RuleId, targetPlayerId, originalDiscovererId);
            targetSession.Send(targetResultPacket);
        }

        Logger.LogInformation(
            "PlayerInteractShareRule: PlayerId={PlayerId} shared rule {RuleId} to PlayerId={TargetId}",
            PlayerId, msg.RuleId, targetPlayerId);

        // 대화 종료
        EndConversation(targetPlayerId, targetSession);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     #26: 봇이 대상일 때 1:1 상호작용 자동 응답.
    ///     - DecideAcceptInteraction으로 수락/거절 결정 (DemoMode 시)
    ///     - issue22 디버그(DemoMode 비활성): 무조건 수락 + 3초 지연 (실제 플레이어 응답 시뮬)
    ///     - 수락 시: 양쪽에 RESULT(accepted=true) 송신 → 즉시 종료(대화는 게임 진행상 0.x초 단위로 빈번)
    ///     - 거절 시: 양쪽에 RESULT(accepted=false) 송신
    ///     클라이언트 측 봇 대화 UX는 단순화 — 수락/거절 결과만 전달.
    /// </summary>
    private async Task HandleBotInteractRequest(long botPlayerId)
    {
        if (!PlayerId.HasValue) return;
        long requesterPlayerId = PlayerId.Value;

        // 상호작용 응답 대기/대화 중 봇 정지 — 실제 플레이어 정지 동작과 동등.
        // InteractionStayUntil은 사용자가 INTERACT_END를 안 보내고 끊어지는 등의 이상 케이스용 fallback.
        // 정상 종료 시 HandlePlayerInteractEnd가 +5초로 단축 → 5초 후 봇 walking 재개.
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot is not { IsEliminated: false } || bot.CurrentArea != CurrentArea)
        {
            using var errorPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(botPlayerId, ErrorCode.AREA_MISMATCH);
            Send(errorPacket);
            Logger.LogInformation(
                "봇 1:1 요청 실패: BotId={Bot}, Requester={Requester}, BotArea={BotArea}, RequesterArea={RequesterArea}",
                botPlayerId, requesterPlayerId, bot?.CurrentArea, CurrentArea);
            return;
        }

        bot.HoldForInteraction(TimeSpan.FromMinutes(5)); // 안전 fallback

        // pending 저장 — HandleAreaMove에서 영역 이동 차단 + 클라 InteractAlert UX와 정합.
        _pendingInteractPlayerId = botPlayerId;

        // 요청자에게 ack(REQUEST) 송신 → 클라이언트 InteractAlert(대기 UI) 표시.
        // 실제 플레이어 대상일 때 HandlePlayerInteractRequest 본 흐름에서 보내는 것과 동등.
        using (var requesterAck = PacketMaker.G_TO_C_PLAYER_INTERACT_REQUEST(botPlayerId, ErrorCode.SUCCESS))
        {
            Send(requesterAck);
        }

        bool accepted;
        if (DemoMode.IsActive)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            accepted = bot is { IsEliminated: false };
        }
        else
        {
            // issue22 디버그: 봇 응답 3초 지연 후 무조건 수락
            await Task.Delay(TimeSpan.FromSeconds(3));
            accepted = true;
        }

        // 봇은 응답 기록만 갱신 (직책 밝히기는 클라이언트 UX 미구현 — 추후 확장 지점)
        _botPlayerManager.NoteRespondedTo(CurrentMapSubId, botPlayerId, requesterPlayerId);

        // 요청자에게 봇이 결정한 결과 전송 (대화 UI 없이 결과만)
        using (var requesterResult =
               PacketMaker.G_TO_C_PLAYER_INTERACT_RESULT(accepted, botPlayerId, ErrorCode.SUCCESS))
        {
            Send(requesterResult);
        }

        Logger.LogInformation(
            "봇 1:1 자동 응답: BotId={Bot}, Requester={Requester}, Accepted={Acc}",
            botPlayerId, requesterPlayerId, accepted);

        // pending 해제 + 수락 시 active conversation으로 전환.
        _pendingInteractPlayerId = null;
        if (accepted)
        {
            ModifyStats(-InteractStaminaCost);

            _activeConversationPlayerId = botPlayerId;
            SendBotInteractionChoices(botPlayerId);
        }
        else
        {
            _lastInteractRejectTime = DateTime.UtcNow;
            // 거절 시 즉시 정지 해제
            if (bot != null)
            {
                bot.IsInInteraction = false;
                bot.InteractionStayUntil = DateTime.MinValue;
            }
        }
        // 수락 시: IsInInteraction는 InteractionStayUntil(5분)까지 유지 → 봇 정지.
        // 사용자가 INTERACT_END 보내면 HandlePlayerInteractEnd가 +5초로 단축.
    }

    /// <summary>
    ///     양쪽에 대화 종료 패킷 전송 + 상태 클리어
    /// </summary>
    private void EndConversation(long partnerPlayerId, GameClientSession? partnerSession)
    {
        using (var myPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_END(partnerPlayerId))
        {
            Send(myPacket);
        }

        if (partnerSession != null)
        {
            using var partnerPacket = PacketMaker.G_TO_C_PLAYER_INTERACT_END(PlayerId!.Value);
            partnerSession.Send(partnerPacket);
            partnerSession._activeConversationPlayerId = null;
        }

        _activeConversationPlayerId = null;
    }

    #endregion

    #region 문

    /// <summary>
    ///     문 열기 요청 처리
    /// </summary>
    private Task HandleDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
    {
        if (!PlayerId.HasValue)
        {
            Logger.LogWarning("HandleDoorOpenRequest: PlayerId not set");
            return Task.CompletedTask;
        }

        try
        {
            int doorId = msg.DoorId;
            var doorInfo = GameDoorData.Get(doorId);

            // 이미 열려있는지 확인
            if (_doorStateManager.IsDoorOpen(CurrentMapSubId, doorId))
            {
                Logger.LogDebug("Player {PlayerId} tried to open already open door: DoorId={DoorId}", PlayerId, doorId);
                using var alreadyOpenPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.DOOR_ALREADY_OPEN);
                Send(alreadyOpenPacket);
                return Task.CompletedTask;
            }

            // 열쇠 보유 확인 (required_item_id가 0이면 열쇠 불필요)
            if (doorInfo.RequiredItemId > 0)
            {
                var playerInventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                bool hasKey = playerInventory.GetItemCount(doorInfo.RequiredItemId) > 0;

                if (!hasKey)
                {
                    Logger.LogWarning(
                        "Player {PlayerId} missing key for door: DoorId={DoorId}, RequiredItemId={ItemId}",
                        PlayerId, doorId, doorInfo.RequiredItemId);
                    using var noKeyPacket =
                        PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.DOOR_KEY_MISSING);
                    Send(noKeyPacket);
                    return Task.CompletedTask;
                }
            }

            // 문 열기
            _doorStateManager.OpenDoor(CurrentMapSubId, doorId);
            Logger.LogInformation("Player {PlayerId} opened door: DoorId={DoorId}", PlayerId, doorId);

            // 같은 매칭의 모든 플레이어에게 브로드캐스트
            using var updatePacket =
                PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
            var matchingSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            foreach (var session in matchingSessions) session.Send(updatePacket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleDoorOpenRequest error for player {PlayerId}", PlayerId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     열린 문 목록 전송 (입장 시)
    /// </summary>
    private void SendDoorStateList()
    {
        if (!PlayerId.HasValue) return;

        var openDoors = _doorStateManager.GetOpenDoors(CurrentMapSubId);
        using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
        Send(packet);
        Logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
    }

    #endregion
}
