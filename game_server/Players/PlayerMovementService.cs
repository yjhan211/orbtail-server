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
///     전달받은 MatchPlayer에 검증 결과를 반영하며, 연결이 있으면 주변 플레이어에게 알린다.
///     세션을 보관하지 않는다. 이동 상태와 처리·응답 간격은 플레이어별로 관리하고 호출자가 매치 잠금을 잡는다.
/// </summary>
internal sealed class PlayerMovementService(
    MovementValidationService validationService,
    GameEventLogManager eventLog,
    ILogger logger)
{
    /// <summary>이전 이동 요청과의 처리 간격을 초 단위로 계산하고, 마지막 처리 시각을 갱신한다.</summary>
    public float CalculateMoveDeltaTime(MatchPlayer player, long timestamp)
    {
        if (player.LastMoveProcessedTimestamp == 0)
        {
            player.LastMoveProcessedTimestamp = timestamp;
            return MovementValidationPolicy.InitialReceiptDeltaSeconds;
        }

        double elapsedSeconds = (timestamp - player.LastMoveProcessedTimestamp) / (double)Stopwatch.Frequency;
        player.LastMoveProcessedTimestamp = timestamp;
        return MovementValidationPolicy.ClampReceiptDeltaSeconds(elapsedSeconds);
    }

    /// <summary>첫 이동 응답이거나, 마지막 응답 이후 전송 간격이 지났는지 확인한다.</summary>
    public bool ShouldSendMoveResponse(MatchPlayer player, long timestamp)
    {
        if (player.LastMoveResponseTimestamp == 0)
            return true;

        double elapsedSeconds = (timestamp - player.LastMoveResponseTimestamp) / (double)Stopwatch.Frequency;
        return elapsedSeconds >= MovementValidationPolicy.MovementAcknowledgementIntervalSeconds;
    }

    /// <summary>이동 응답을 전송한 시각을 기록한다. 즉시 보정 응답도 같은 간격에 반영한다.</summary>
    public void RecordMoveResponse(MatchPlayer player, long timestamp) => player.LastMoveResponseTimestamp = timestamp;

    /// <summary>입장 시 서버가 지정한 스폰으로 이동 상태를 초기화한다.</summary>
    public void InitializeSpawn(MatchPlayer player, Cell spawnCell)
    {
        player.LastValidatedCell = Cell.Clone(spawnCell);
        player.LastValidatedPosition = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell);
        player.LastValidatedVelocity = new Vector3f();
        player.LastValidatedRotation = 0f;
        player.CurrentArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, spawnCell);
        player.OrbOrbitPhaseDegrees = SwarmOrbOrbit.InitialPhaseDegrees(player.PlayerId);
    }

    /// <summary>검증된 이동 값을 함께 반영한다. 호출자는 매치 잠금을 잡아야 한다.</summary>
    internal void ApplyValidatedMovement(MatchPlayer player, ValidatedMovement movement, float rotation)
    {
        player.LastValidatedCell = movement.ValidCell;
        player.LastValidatedPosition = movement.Position;
        player.LastValidatedVelocity = movement.Velocity;
        player.LastValidatedRotation = rotation;
    }

    /// <summary>이동 정보와 전달받은 행동 상태를 복사한다. 호출자는 매치 잠금을 보유해야 한다.</summary>
    public GameObjectInfo CaptureGameObjectInfo(MatchRuntime match, MatchPlayer player, PlayerState state)
    {
        var position = player.LastValidatedPosition
            ?? throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        var cell = player.LastValidatedCell ?? ToCell(position);
        return new GameObjectInfo(ObjectType.PLAYER, player.PlayerId, Config.SWARM_MATCH_MAP, match.MatchingId, cell)
        {
            Position = new Vector3f(position.X, position.Y, position.Z),
            Velocity = new Vector3f(player.LastValidatedVelocity.X, player.LastValidatedVelocity.Y, player.LastValidatedVelocity.Z),
            Rotation = player.LastValidatedRotation,
            State = state
        };
    }

    private void AdvanceOrbOrbit(MatchPlayer player, Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        player.OrbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
            player.OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
    }

    public (ValidatedMovement Movement, Cell Cell, long ServerTimestamp)? Apply(MatchRuntime match, MatchPlayer player, C_TO_G_MOVE msg, float deltaTime)
    {
        var validation = validationService.ValidatePosition(
            player.PlayerId, Config.SWARM_MATCH_MAP, player.LastValidatedPosition, player.LastValidatedCell,
            msg.Position, msg.Velocity, deltaTime);
        var validatedPosition = validation.Position;
        // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
        var currentCell = ToCell(validatedPosition);
        var newArea = GameMapData.GetStableCurrentArea(Config.SWARM_MATCH_MAP, currentCell, player.CurrentArea);

        // 3. Area 변경 처리 (퇴장 조건 통과한 경우만)
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var previousCell = player.LastValidatedPosition != null
            ? ToCell(player.LastValidatedPosition)
            : currentCell;
        var blockedCell = validationService.GetBlockedTransitionCell(
            player.PlayerId, player.CurrentArea, newArea, previousCell, currentCell, match.Doors);
        if (blockedCell != null)
        {
            using var rejected = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(newArea, blockedCell);
            player.Session?.TrySend(rejected);
            logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
                player.PlayerId, newArea, blockedCell.X, blockedCell.Y);
            return null;
        }
        // 잠긴 문 검증이 끝난 뒤 위치를 게시한다. 폐쇄 구역도 문이 열려 있으면
        // 진입할 수 있으며, 체류 페널티는 ResourceTick에서 서버 권위로 적용한다.
        // #229 6단계: 이동 입력이 곧 수면 해제다 — 누워서 도망칠 수 없다.
        // 2026-08-17 재조정: 수면을 깨우는 건 이 이동뿐이다 (피격·폐쇄는 깨우지 않는다).
        if (player.TryStopSleep())
            player.Session?.SendPlayerState();
        // 오브 궤도 (#232): 검증된 이동 거리만큼 돈다 — 멈추면 이동 패킷이 없으니 저절로 선다.
        if (player.LastValidatedPosition != null)
            AdvanceOrbOrbit(player, player.LastValidatedPosition, validatedPosition);
        // 승인된 구간마다 후보를 기록한다. 구역을 넘으면 경로 위 좌표가 속한 구역도 확인한다.
        var pickupArea = newArea == AreaType.None ? player.CurrentArea : newArea;
        if (player.Session is { } session)
            GroundItemAutoPickupService.RecordMovement(session,
            player.LastValidatedPosition ?? validatedPosition, validatedPosition, pickupArea);
        ApplyValidatedMovement(player, validation, msg.Rotation);
        match.GroundItems.ReleaseSourcePickupBlocks(player.PlayerId, pickupArea, validatedPosition.X, validatedPosition.Y);

        if (newArea != player.CurrentArea && newArea != AreaType.None)
        {
            // 폐쇄 구역 진입 경고 (지속 페널티는 ResourceTick에서 처리)
            if (match.Closures.IsAreaClosed(newArea))
            {
                logger.LogInformation("폐쇄 구역 진입: PlayerId={PlayerId}, Area={Area} (체류 시 오염도 지속 증가)",
                    player.PlayerId, newArea);
            }

            logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                player.PlayerId, currentCell.X, currentCell.Y, player.CurrentArea, newArea);
            var oldArea = player.CurrentArea;
            player.CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
            eventLog.LogMove(match.MatchingId, player.PlayerId,
                oldArea.ToString(), newArea.ToString(), isBot: player.PlayerId < 0);
            if (player.Session is { } recipient)
                HandleAreaChange(recipient, oldArea, newArea);
        }

        return (validation, currentCell, serverTimestamp);
    }

    private void HandleAreaChange(GameClientSession player, AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!player.PlayerId.HasValue) return;

            logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", player.PlayerId, oldArea,
                newArea);

            var allSessions = player.Match.GetSessions();

            // 1. 이전 Area의 플레이어들에게 퇴장 알림 + 나에게 기존 플레이어 삭제 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = new List<GameClientSession>();
                foreach (var session in allSessions)
                {
                    if (!session.Player.IsEliminated && session.Player.CurrentArea == oldArea && session.PlayerId != player.PlayerId)
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
                    if (!session.Player.IsEliminated && session.Player.CurrentArea == newArea && session.PlayerId != player.PlayerId)
                        newAreaSessions.Add(session);
                }
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(CaptureGameObjectInfo(player.Match, player.Player, player.Player.State));

                foreach (var session in newAreaSessions) session.TrySend(enterPacket);

                logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송 (세션의 최신 Cell 사용)
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(session.PlayerMovement.CaptureGameObjectInfo(session.Match, session.Player, session.Player.State));
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
                SendInteractableList(player, newArea);
                GroundItemNotificationService.SendSnapshot(player, newArea);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", player.PlayerId);
        }
    }

    public void SendInteractableList(GameClientSession player, AreaType areaType)
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

    public void Broadcast(GameClientSession player, Packet packet)
    {
        var targetSessions = new List<GameClientSession>();
        foreach (var other in player.Match.GetSessions())
        {
            if (!other.Player.IsEliminated && other.Player.CurrentArea == player.Player.CurrentArea && other.PlayerId != player.PlayerId)
                targetSessions.Add(other);
        }
        foreach (var other in targetSessions)
            other.TrySend(packet);
    }

    private Cell ToCell(Vector3f position) =>
        MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position);
}
