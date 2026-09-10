using game_server.items;
using game_server.logging;
using game_server.field;
using game_server.matches;
using System.Diagnostics;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players;

/// <summary>
///     플레이어 한 명의 이동 검증과 매치 내 반영을 조율한다.
///     참가자의 이동 상태에 검증 결과를 반영하고 주변 플레이어에게 알린다.
///     이동 처리·응답 간격도 관리한다. 이동 상태를 읽거나 변경할 때는 호출자가 매치 잠금을 잡는다.
/// </summary>
internal sealed class PlayerMovementService(
    GameClientSession player,
    MovementValidationService validationService,
    GameEventLogManager eventLog,
    ILogger logger)
{
    /// <summary>이전 이동 요청과의 처리 간격을 초 단위로 계산하고, 마지막 처리 시각을 갱신한다.</summary>
    public float CalculateMoveDeltaTime(long timestamp)
    {
        if (player._player.LastMoveProcessedTimestamp == 0)
        {
            player._player.LastMoveProcessedTimestamp = timestamp;
            return MovementValidationPolicy.InitialReceiptDeltaSeconds;
        }

        double elapsedSeconds = (timestamp - player._player.LastMoveProcessedTimestamp) / (double)Stopwatch.Frequency;
        player._player.LastMoveProcessedTimestamp = timestamp;
        return MovementValidationPolicy.ClampReceiptDeltaSeconds(elapsedSeconds);
    }

    /// <summary>첫 이동 응답이거나, 마지막 응답 이후 전송 간격이 지났는지 확인한다.</summary>
    public bool ShouldSendMoveResponse(long timestamp)
    {
        if (player._player.LastMoveResponseTimestamp == 0)
            return true;

        double elapsedSeconds = (timestamp - player._player.LastMoveResponseTimestamp) / (double)Stopwatch.Frequency;
        return elapsedSeconds >= MovementValidationPolicy.MovementAcknowledgementIntervalSeconds;
    }

    /// <summary>이동 응답을 전송한 시각을 기록한다. 즉시 보정 응답도 같은 간격에 반영한다.</summary>
    public void RecordMoveResponse(long timestamp) => player._player.LastMoveResponseTimestamp = timestamp;

    /// <summary>입장 시 서버가 지정한 스폰으로 이동 상태를 초기화한다.</summary>
    public void InitializeSpawn(Cell spawnCell)
    {
        player._player.LastValidatedCell = Cell.Clone(spawnCell);
        player._player.LastValidatedPosition = MapCoordinateConverter.CellToWorld(player._player.MapId, spawnCell);
        player._player.LastValidatedVelocity = new Vector3f();
        player._player.LastValidatedRotation = 0f;
        player._player.CurrentArea = GameMapData.GetCurrentArea(player._player.MapId, spawnCell);
        player._player.OrbOrbitPhaseDegrees = SwarmOrbOrbit.InitialPhaseDegrees(player.PlayerId ?? 0L);
    }

    /// <summary>검증된 이동 값을 함께 반영한다. 호출자는 매치 잠금을 잡아야 한다.</summary>
    internal void ApplyValidatedMovement(ValidatedMovement movement, float rotation)
    {
        player._player.LastValidatedCell = movement.ValidCell;
        player._player.LastValidatedPosition = movement.Position;
        player._player.LastValidatedVelocity = movement.Velocity;
        player._player.LastValidatedRotation = rotation;
    }

    /// <summary>이동 정보와 전달받은 행동 상태를 복사한다. 호출자는 매치 잠금을 보유해야 한다.</summary>
    public GameObjectInfo CaptureGameObjectInfo(PlayerState state)
    {
        var position = player._player.LastValidatedPosition
            ?? throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        var cell = player._player.LastValidatedCell ?? ToCell(position);
        return new GameObjectInfo(ObjectType.PLAYER, player.PlayerId!.Value, player._player.MapId, player.MatchingId, cell)
        {
            Position = new Vector3f(position.X, position.Y, position.Z),
            Velocity = new Vector3f(player._player.LastValidatedVelocity.X, player._player.LastValidatedVelocity.Y, player._player.LastValidatedVelocity.Z),
            Rotation = player._player.LastValidatedRotation,
            State = state
        };
    }

    private void AdvanceOrbOrbit(Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        player._player.OrbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
            player._player.OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
    }

    public (ValidatedMovement Movement, Cell Cell, long ServerTimestamp)? Apply(C_TO_G_MOVE msg, float deltaTime)
    {
        var validation = validationService.ValidatePosition(
            player.PlayerId.Value, player._player.MapId, player._player.LastValidatedPosition, player._player.LastValidatedCell,
            msg.Position, msg.Velocity, deltaTime);
        var validatedPosition = validation.Position;
        // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
        var currentCell = ToCell(validatedPosition);
        var newArea = GameMapData.GetStableCurrentArea(player._player.MapId, currentCell, player._player.CurrentArea);

        // 3. Area 변경 처리 (퇴장 조건 통과한 경우만)
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var previousCell = player._player.LastValidatedPosition != null
            ? ToCell(player._player.LastValidatedPosition)
            : currentCell;
        var blockedCell = validationService.GetBlockedTransitionCell(
            player.PlayerId.Value, player._player.CurrentArea, newArea, previousCell, currentCell, player.Match.Doors);
        if (blockedCell != null)
        {
            using var rejected = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(newArea, blockedCell);
            player.TrySend(rejected);
            logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
                player.PlayerId, newArea, blockedCell.X, blockedCell.Y);
            return null;
        }
        // 잠긴 문 검증이 끝난 뒤 위치를 게시한다. 폐쇄 구역도 문이 열려 있으면
        // 진입할 수 있으며, 체류 페널티는 ResourceTick에서 서버 권위로 적용한다.
        // #229 6단계: 이동 입력이 곧 수면 해제다 — 누워서 도망칠 수 없다.
        // 2026-08-17 재조정: 수면을 깨우는 건 이 이동뿐이다 (피격·폐쇄는 깨우지 않는다).
        if (player._player.TryStopSleep())
            player.SendPlayerState();
        // 오브 궤도 (#232): 검증된 이동 거리만큼 돈다 — 멈추면 이동 패킷이 없으니 저절로 선다.
        if (player._player.LastValidatedPosition != null)
            AdvanceOrbOrbit(player._player.LastValidatedPosition, validatedPosition);
        // 승인된 구간마다 후보를 기록한다. 구역을 넘으면 경로 위 좌표가 속한 구역도 확인한다.
        var pickupArea = newArea == AreaType.None ? player._player.CurrentArea : newArea;
        GroundItemAutoPickupService.RecordMovement(player,
            player._player.LastValidatedPosition ?? validatedPosition, validatedPosition, pickupArea);
        ApplyValidatedMovement(validation, msg.Rotation);
        player.Match.GroundItems.ReleaseSourcePickupBlocks(player.PlayerId.Value, pickupArea, validatedPosition.X, validatedPosition.Y);

        if (newArea != player._player.CurrentArea && newArea != AreaType.None)
        {
            // 폐쇄 구역 진입 경고 (지속 페널티는 ResourceTick에서 처리)
            if (player.Match.Closures.IsAreaClosed(newArea))
            {
                logger.LogInformation("폐쇄 구역 진입: PlayerId={PlayerId}, Area={Area} (체류 시 오염도 지속 증가)",
                    player.PlayerId, newArea);
            }

            logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                player.PlayerId, currentCell.X, currentCell.Y, player._player.CurrentArea, newArea);
            var oldArea = player._player.CurrentArea;
            player._player.CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
            eventLog.LogMove(player.MatchingId, player.PlayerId.Value,
                oldArea.ToString(), newArea.ToString(), isBot: false);
            HandleAreaChange(oldArea, newArea);
        }

        return (validation, currentCell, serverTimestamp);
    }

    private void HandleAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!player.PlayerId.HasValue) return;

            logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", player.PlayerId, oldArea,
                newArea);

            var allSessions = player.Match.Sessions.Values.ToList();

            // 1. 이전 Area의 플레이어들에게 퇴장 알림 + 나에게 기존 플레이어 삭제 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = new List<GameClientSession>();
                foreach (var session in allSessions)
                {
                    if (!session._player.IsEliminated && session._player.CurrentArea == oldArea && session.PlayerId != player.PlayerId)
                        oldAreaSessions.Add(session);
                }
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(player.PlayerId.Value);

                foreach (var session in oldAreaSessions)
                {
                    // 이전 Area 플레이어들에게 내 퇴장 알림
                    session.TrySend(leavePacket);

                    // 나에게 이전 Area 플레이어들 삭제 알림
                    if (session.PlayerId.HasValue)
                    {
                        using var removePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                        player.TrySend(removePacket);
                    }
                }

                // #79: 나에게 이전 Area의 봇들 삭제 알림 (봇은 TCP 세션이 없어 별도 처리)
                var oldAreaBots = player.Match.Bots.GetBots(player.MatchingId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == oldArea)
                    .ToList();
                foreach (var bot in oldAreaBots)
                {
                    using var botLeavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(bot.PlayerId);
                    player.TrySend(botLeavePacket);
                }

                logger.LogDebug("Sent LEAVE to {Count} players + {BotCount} bots in old Area {OldArea}",
                    oldAreaSessions.Count, oldAreaBots.Count, oldArea);
            }

            // 2. 새 Area의 플레이어들에게 진입 알림 (내 최신 Cell 포함)
            if (newArea != AreaType.None)
            {
                var newAreaSessions = new List<GameClientSession>();
                foreach (var session in allSessions)
                {
                    if (!session._player.IsEliminated && session._player.CurrentArea == newArea && session.PlayerId != player.PlayerId)
                        newAreaSessions.Add(session);
                }
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(CaptureGameObjectInfo(player._player.State));

                foreach (var session in newAreaSessions) session.TrySend(enterPacket);

                logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송 (세션의 최신 Cell 사용)
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(session._playerMovement.CaptureGameObjectInfo(session._player.State));
                    player.TrySend(otherEnterPacket);
                }

                logger.LogDebug("Sent {Count} existing players to Player {PlayerId}", newAreaSessions.Count, player.PlayerId);

                // 4. #125: 새 Area의 봇들 ENTER도 나에게 전송 (실제 플레이어 동등)
                var newAreaBots = player.Match.Bots.GetBots(player.MatchingId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == newArea)
                    .ToList();
                foreach (var bot in newAreaBots)
                {
                    var objectInfo = player.Match.Bots.SynthesizeGameObjectInfo(player.MatchingId, bot.PlayerId);
                    if (objectInfo == null) continue;
                    using var botEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(objectInfo);
                    player.TrySend(botEnterPacket);
                }
                if (newAreaBots.Count > 0)
                    logger.LogDebug("Sent {Count} bots in new Area {NewArea} to Player {PlayerId}",
                        newAreaBots.Count, newArea, player.PlayerId);

                // 5. 나에게 새 Area의 Interactable 목록 전송
                SendInteractableList(newArea);
                GroundItemNotificationService.SendSnapshot(player, newArea);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", player.PlayerId);
        }
    }

    public void SendInteractableList(AreaType areaType)
    {
        if (areaType == AreaType.None) return;

        var match = player.Match;
        if (match == null) return;

        using (match.Enter())
        {
            if (match.IsEnded) return;

            var objects = InteractableStateManager.GetAreaObjectStates(areaType);

            // #229 5단계: 스웜은 상자 탐색을 보내지 않는다 — 마커도 빈 상호작용 UI도 뜰 일이 없다.
            // 단 문 잠금해제(door_id > 0)는 예외다. 방을 여는 유일한 수단이라 스웜의 핵심 조작이다.
            if (Config.IsSwarmExploreDisabled())
                objects = objects.Where(state => GameInteractableData.Get(state.InteractId) is { DoorId: > 0 }).ToList();

            // 이미 열린 문의 마커는 보내지 않는다 — 열린 문 앞에서 게이지가 도는 그림은 거짓말이다.
            objects = objects
                .Where(state => GameInteractableData.Get(state.InteractId) is not { DoorId: > 0 } info ||
                                !match.Doors.IsDoorOpen(info.DoorId))
                .ToList();
            if (objects.Count == 0)
            {
                logger.LogDebug("No interactable objects in area {AreaType}", areaType);
                return;
            }

            // 각 오브젝트의 액션 개수 로그
            foreach (var obj in objects)
                logger.LogDebug("InteractableObject Id={InteractId}: {ActionCount} actions",
                    obj.InteractId, obj.Actions.Count);

            using var packet = PacketMaker.G_TO_C_INTERACTABLE_LIST(areaType, objects);
            player.TrySend(packet);

            logger.LogDebug(
                "Sent {Count} interactable objects for area {AreaType} to Player {PlayerId} (MatchingId={MatchingId})",
                objects.Count, areaType, player.PlayerId, player.MatchingId);
        }
    }

    public void Broadcast(Packet packet)
    {
        var targetSessions = new List<GameClientSession>();
        foreach (var other in player.Match.Sessions.Values.ToList())
        {
            if (!other._player.IsEliminated && other._player.CurrentArea == player._player.CurrentArea && other.PlayerId != player.PlayerId)
                targetSessions.Add(other);
        }
        foreach (var other in targetSessions)
            other.TrySend(packet);
    }

    private Cell ToCell(Vector3f position) =>
        MapCoordinateConverter.WorldToCell(player._player.MapId, position);
}
