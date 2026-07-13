using System;
using System.Linq;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     구역 이동 처리 (GDD v0.0.8: 문/계단 마커 방식)
///     클라이언트가 마커 클릭 → 오토무브 도착 시 C_TO_G_AREA_MOVE 전송 →
///     서버가 인접/스태미나 검증 후 목적지 스폰 셀과 함께 G_TO_C_AREA_MOVE_RESULT 응답.
/// </summary>
public partial class GameClientSession
{
    private async Task HandleAreaMove(C_TO_G_AREA_MOVE msg)
    {
        if (PlayerId == null) return;
        if (IsEliminated)
        {
            LogAreaMoveError(ErrorCode.PLAYER_DEAD, msg.TargetArea);
            SendAreaMoveError(ErrorCode.PLAYER_DEAD, msg.TargetArea);
            return;
        }

        if (IsRoundActionLocked(out _))
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        CancelPendingRoomEncounterTurnsForCurrentPlayer("AreaMove");

        if (CurrentState == PlayerState.Exploring)
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        if (_isSleeping)
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        bool roomEncounterLeaveAreaMoveCostExempt = _roomEncounterLeaveAreaMoveCostExemptUntilUtc > DateTime.UtcNow;

        // 1:1 상호작용 요청 중 또는 대화 진행 중에는 영역 이동 차단 (실제 플레이어 정지 동작과 동등).
        // 조우 이탈은 같은 입력에서 대화 종료와 구역 이동이 연달아 들어오므로 예외로 통과시킨다.
        if (!roomEncounterLeaveAreaMoveCostExempt &&
            (_pendingInteractPlayerId.HasValue || _activeConversationPlayerId.HasValue))
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        // 1. 인접 그래프 검증
        if (!GameAreaConnectionData.IsAdjacent(CurrentMapId, CurrentArea, msg.TargetArea))
        {
            Logger.LogWarning(
                "Player {PlayerId} requested non-adjacent area move: {From} → {To}",
                PlayerId, CurrentArea, msg.TargetArea);
            LogAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            return;
        }

        // 2. 연결 타입 검증 (요청 메타와 실제 그래프 일치 확인 — 변조 방지)
        var actualType = GameAreaConnectionData.GetConnectionType(CurrentMapId, CurrentArea, msg.TargetArea);
        if (actualType == ConnectionType.None || actualType != msg.ConnectionType)
        {
            Logger.LogWarning(
                "Player {PlayerId} ConnectionType mismatch: requested {Req}, actual {Act}",
                PlayerId, msg.ConnectionType, actualType);
            LogAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            return;
        }

        // 3. 이동 비용 산정 (모든 area 이동은 Door — 계단 개념 폐지)
        // 강제 미행은 정신오염도 한계치로 제어권을 잃은 상태이므로 구역 이동 비용을 면제한다.
        bool forcedFollowActive = Corruption >= MaxCorruption;
        int staminaCost = forcedFollowActive || roomEncounterLeaveAreaMoveCostExempt ? 0 : GameServer.MoveStaminaCost;

        // 4. 스태미나 부족 시 ModifyStats가 Cor 1:2 변환 (권고안 B 2026-05-05) — 사전 차단 제거.

        // 5. 목적지 스폰 셀 조회 — CSV 연결별 스폰(from→to 방향, 계단은 서/동 구분) 우선,
        //    없으면 영역 중심 fallback
        var spawnCell = GameAreaConnectionData.GetSpawnCell(CurrentMapId, CurrentArea, msg.TargetArea,
                            msg.StairSide)
                        ?? GameMapData.GetAreaSpawnCell(CurrentMapId, msg.TargetArea);
        if (ReferenceEquals(spawnCell, null))
        {
            Logger.LogWarning(
                "Player {PlayerId} AreaMove failed: spawn cell not found, CurrentArea={CurrentArea}, RequestedArea={RequestedArea}, StairSide={StairSide}",
                PlayerId, CurrentArea, msg.TargetArea, msg.StairSide);
            LogAreaMoveError(ErrorCode.SERVER_INTERNAL_ERROR, msg.TargetArea);
            SendAreaMoveError(ErrorCode.SERVER_INTERNAL_ERROR, msg.TargetArea);
            return;
        }

        // 6. 위치/구역 정보 영구 저장 (스태미나는 인-게임 한정이라 Redis 갱신 안 함)
        await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
        var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

        var spawnPos = CellToWorldPosition(spawnCell);
        float rotation = _lastValidatedRotation;
        if (playerInfo != null)
        {
            playerInfo.ObjectInfo.Cell = spawnCell;
            playerInfo.ObjectInfo.Position = spawnPos;
            playerInfo.ObjectInfo.Velocity = new Vector3f(0f, 0f, 0f);
            playerInfo.ObjectInfo.MoveTimestamp = DateTime.UtcNow;
            rotation = playerInfo.ObjectInfo.Rotation;
            await playerInfo.Save(CacheHelper);
        }
        else
        {
            Logger.LogWarning(
                "Player {PlayerId} AreaMove: PlayerInfo missing in Redis, proceeding with session state only. CurrentArea={CurrentArea}, RequestedArea={RequestedArea}",
                PlayerId, CurrentArea, msg.TargetArea);
        }

        // 7. 세션 상태 갱신 + Area 변경 후처리
        var oldArea = CurrentArea;
        _previousArea = oldArea;
        CurrentArea = msg.TargetArea;
        _presenceTracker?.SetPlayerArea(CurrentMapSubId, PlayerId.Value, msg.TargetArea,
            countAsEntry: true);
        _lastValidatedPosition = spawnPos;
        _lastValidCell = spawnCell;

        // 8. 스태미나 차감 + G_TO_C_PLAYER_STATS_UPDATE 송신 (다른 스태미나 변경 흐름과 동일 경로)
        if (roomEncounterLeaveAreaMoveCostExempt)
            _roomEncounterLeaveAreaMoveCostExemptUntilUtc = DateTime.MinValue;

        ModifyStats(staminaDelta: -staminaCost);

        Logger.LogInformation(
            "Player {PlayerId} AreaMove: {From} → {To} (cost {Cost}, type {Type}, forcedFollow={ForcedFollow}, encounterLeaveExempt={EncounterLeaveExempt})",
            PlayerId, oldArea, msg.TargetArea, staminaCost, actualType, forcedFollowActive,
            roomEncounterLeaveAreaMoveCostExempt);

        // HandleAreaChange는 Movement에 정의됨 — 폐쇄 알림, 동선 추적 등 공통 처리
        _gameEventLogManager.LogMove(CurrentMapSubId, PlayerId.Value,
            oldArea.ToString(), msg.TargetArea.ToString(), isBot: false);

        await HandleAreaChange(oldArea, msg.TargetArea);
        TrySendCorridorEncounterEvents(spawnPos);

        // 9. 응답 (요청자에게만 — 다른 플레이어는 G_TO_C_MOVE 브로드캐스트로 위치 동기화)
        using var resultPacket = PacketMaker.G_TO_C_AREA_MOVE_RESULT(
            ErrorCode.SUCCESS,
            msg.TargetArea,
            spawnCell.X,
            spawnCell.Y,
            staminaCost,
            Stamina);
        Send(resultPacket);

        TrySendRoomEntryEvent(msg.TargetArea);

        // 10. 위치 텔레포트 브로드캐스트 (같은 새 Area의 플레이어들에게)
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            PlayerId.Value,
            spawnPos,
            new Vector3f(0f, 0f, 0f),
            rotation,
            spawnCell,
            lastProcessedInput: 0u,
            serverTimestamp);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, msg.TargetArea);
        foreach (var session in sameAreaSessions) session.Send(movePacket);
    }

    private void SendAreaMoveError(ErrorCode errorCode, AreaType requestedArea)
    {
        if (PlayerId == null) return;
        using var packet = PacketMaker.G_TO_C_AREA_MOVE_RESULT(
            errorCode,
            requestedArea,
            spawnCellX: 0,
            spawnCellY: 0,
            staminaCost: 0,
            remainingStamina: 0);
        Send(packet);
    }

    private void LogAreaMoveError(ErrorCode errorCode, AreaType requestedArea)
    {
        Logger.LogWarning(
            "Player {PlayerId} AreaMove failed: {ErrorCode}, CurrentArea={CurrentArea}, RequestedArea={RequestedArea}, State={State}, Sleeping={Sleeping}, PendingInteract={PendingInteract}, ActiveConversation={ActiveConversation}",
            PlayerId,
            errorCode,
            CurrentArea,
            requestedArea,
            CurrentState,
            _isSleeping,
            _pendingInteractPlayerId,
            _activeConversationPlayerId);
    }

    private void TrySendRoomEntryEvent(AreaType area)
    {
        if (!PlayerId.HasValue || _pendingRoomEntryEventId != 0) return;
        if (HasInitialRoomEntryTrait()) return;
        if (!GameRoomEntryEventData.TryGetByArea(area, out var entryEvent)) return;

        _pendingRoomEntryEventId = entryEvent.Id;

        using var packet = PacketMaker.G_TO_C_ROOM_ENTRY_EVENT(entryEvent.Id);
        Send(packet);

        Logger.LogInformation(
            "Player {PlayerId} initial room entry event: eventId={EventId}, area={Area}",
            PlayerId,
            entryEvent.Id,
            area);
    }

    private void ResendPendingRoomEntryEvent(string reason)
    {
        if (!PlayerId.HasValue || _pendingRoomEntryEventId == 0) return;

        using var packet = PacketMaker.G_TO_C_ROOM_ENTRY_EVENT(_pendingRoomEntryEventId);
        Send(packet);

        Logger.LogInformation(
            "Pending room entry event resent: Player={PlayerId}, Event={EventId}, Reason={Reason}",
            PlayerId,
            _pendingRoomEntryEventId,
            reason);
    }

    private async Task HandleRoomEntryEventChoice(C_TO_G_ROOM_ENTRY_EVENT_CHOICE msg)
    {
        if (!PlayerId.HasValue) return;
        if (_pendingRoomEntryEventId != msg.EventId)
        {
            Logger.LogWarning(
                "Room entry event choice ignored: Player={PlayerId}, Pending={Pending}, Event={Event}, Choice={Choice}",
                PlayerId,
                _pendingRoomEntryEventId,
                msg.EventId,
                msg.ChoiceId);
            return;
        }

        if (TryHandleRoomExploreEventChoice(msg))
            return;

        if (!GameRoomEntryEventData.TryGetChoice(msg.EventId, msg.ChoiceId, out var entryEvent, out var choice))
        {
            Logger.LogWarning(
                "Room entry event choice missing: Player={PlayerId}, Event={Event}, Choice={Choice}",
                PlayerId,
                msg.EventId,
                msg.ChoiceId);
            return;
        }

        int buffId = ResolveInitialRoomEntryTraitBuffId(choice.GrantedTraitId);
        bool traitGranted = false;
        bool buffGranted = false;
        lock (_roomEntryEventChoiceLock)
        {
            if (_pendingRoomEntryEventId != msg.EventId)
            {
                Logger.LogWarning(
                    "Room entry event choice ignored after atomic check: Player={PlayerId}, Pending={Pending}, Event={Event}, Choice={Choice}",
                    PlayerId,
                    _pendingRoomEntryEventId,
                    msg.EventId,
                    msg.ChoiceId);
                return;
            }

            var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
            if (state == null)
            {
                _pendingRoomEntryEventId = 0;
                Logger.LogWarning(
                    "Room entry event choice failed because mission state is missing: Matching={MatchingId}, Player={PlayerId}",
                    CurrentMapSubId,
                    PlayerId);
                return;
            }

            lock (state.SyncRoot)
            {
                if (!HasInitialRoomEntryTrait(state) && !string.IsNullOrWhiteSpace(choice.GrantedTraitId))
                {
                    traitGranted = state.OwnedClueTags.Add(choice.GrantedTraitId);
                    if (traitGranted)
                        buffGranted = AddActiveBuffId(buffId);
                }
            }

            _pendingRoomEntryEventId = 0;
        }

        if (buffGranted)
            await SaveActiveBuffIds(CurrentMapSubId, PlayerId.Value);

        SendMissionInfo();

        Logger.LogInformation(
            "Room entry event choice resolved: Matching={MatchingId}, Player={PlayerId}, Event={Event}, Choice={Choice}, Trait={Trait}, TraitGranted={TraitGranted}, Buff={Buff}, BuffGranted={BuffGranted}",
            CurrentMapSubId,
            PlayerId,
            entryEvent.Id,
            choice.ChoiceId,
            choice.GrantedTraitId,
            traitGranted,
            buffId,
            buffGranted);
    }

    private bool HasInitialRoomEntryTrait()
    {
        if (!PlayerId.HasValue) return false;
        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        return state != null && HasInitialRoomEntryTrait(state);
    }

    private static bool HasInitialRoomEntryTrait(PlayerPartState state)
    {
        return state.OwnedClueTags.Any(IsInitialRoomEntryTrait);
    }

    private static bool IsInitialRoomEntryTrait(string tag)
    {
        return ResolveInitialRoomEntryTraitBuffId(tag) > 0;
    }

    private static int ResolveInitialRoomEntryTraitBuffId(string? traitId)
    {
        return traitId switch
        {
            "trait_observation" => GameBuffData.PersonaSecretCollectorBuffId,
            "trait_calm" => GameBuffData.PersonaNocturnalBuffId,
            "trait_execution" => GameBuffData.PersonaPhysicalSolverBuffId,
            "trait_survival" => GameBuffData.PersonaCowardBuffId,
            "trait_courage" => GameBuffData.PersonaGuardianAngelBuffId,
            _ => 0
        };
    }
}
