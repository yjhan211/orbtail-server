using System.Collections.Immutable;
using game_server.network;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;
using static game_server.network.SessionSnapshotDelivery;

namespace game_server.services;

/// <summary>
///     자기장 스폰 위치와 폐쇄 시간표를 계산하고 구역·문·잔류 오브를 정리한다.
///     매치 잠금 안에서 상태 변경을 완료한 뒤 확정된 순서로 패킷을 전송한다.
///     전송 실패 시 이미 적용한 상태를 되돌리지 않는다.
/// </summary>
internal sealed class MatchFieldService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    OrbTrailService orbTrails,
    ILogger<MatchFieldService> logger)
{
    public void Process(long matchingId, GameClientSession[] sessionSnapshot)
    {
        var plan = PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);
        if (plan != null)
            DispatchSwarmClosurePublicationPlan(plan, sessionSnapshot);
    }

    // #272 경계 토출 스폰: 구역별 walkable 셀을 중심 거리 오름차순으로 캐시 — 리졸버가 띠를 자른다.
    // Config 초기화 뒤 첫 접근까지 계산을 미루되, 서로 다른 매치의 동시 최초 접근은 한 번만 게시한다.
    private static readonly Lazy<IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>>
        _swarmAreaCellsByDistance = new(
            BuildSwarmAreaCellsByDistance,
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>
        BuildSwarmAreaCellsByDistance()
    {
        var byArea = new Dictionary<AreaType, List<(Cell Cell, int Distance)>>();
        foreach (var pair in SwarmPressureField.DistancesByCell)
        {
            var cell = new Cell(pair.Key.X, pair.Key.Y);
            var cellArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
            if (cellArea == AreaType.None) continue;
            if (!byArea.TryGetValue(cellArea, out var list))
                byArea[cellArea] = list = new List<(Cell, int)>();
            list.Add((cell, pair.Value));
        }

        foreach (var list in byArea.Values)
            list.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        return byArea.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<(Cell Cell, int Distance)>)pair.Value.AsReadOnly());
    }

    internal static IReadOnlyList<(Cell Cell, int Distance)> GetSwarmAreaCellsByDistance(AreaType area)
    {
        return _swarmAreaCellsByDistance.Value.TryGetValue(area, out var cells)
            ? cells
            : [];
    }

    // 경계 토출 띠 폭 (셀): 스폰은 경계 바로 밖, 복귀 앵커는 경계 바로 안 — 태어나서 걸어 들어온다.
    private const int SwarmFieldSpawnBandCells = 6;

    /// <summary>
    ///     #272 경계 토출 스폰 ("안전 구역 예외 제거" 결정 포함): 캠프
    ///     신규 스폰은 항상 바깥(자기장이 올 방향)에서 태어나 안쪽 앵커로 걸어 들어온다 —
    ///     경계가 구역을 관통 중이면 경계 밖 빨간 띠, 아직 온전히 안전한 구역이면 그 구역의
    ///     가장 바깥 띠. 저작 캠프 앵커는 자기장 모드에서 쓰지 않는다 (단일 문법).
    ///     온전히 밖인 구역은 폐쇄 스폰 정지 규칙이 이미 막는다.
    /// </summary>
    public (Cell Spawn, Cell Anchor)? ResolveSpawn(long matchingId, AreaType area)
    {
        if (!MatchPressureFieldPolicy.Enabled) return null;
        double safeDistance = MatchPressureFieldPolicy.GetSafeDistance(matchRuntimes.GetRequired(matchingId), DateTime.UtcNow);

        var cells = GetSwarmAreaCellsByDistance(area);
        if (cells.Count == 0) return null;

        bool boundaryCrossing = safeDistance < cells[^1].Distance;
        double spawnMin = boundaryCrossing ? safeDistance : cells[^1].Distance - SwarmFieldSpawnBandCells;
        double spawnMax = boundaryCrossing ? safeDistance + SwarmFieldSpawnBandCells : cells[^1].Distance;

        var spawnBand = cells
            .Where(entry => entry.Distance > spawnMin && entry.Distance <= spawnMax)
            .ToList();
        if (spawnBand.Count == 0)
            spawnBand = boundaryCrossing
                ? cells.Where(entry => entry.Distance > safeDistance).ToList()
                : [cells[^1]];

        // 앵커(도착지)는 구역에서 자기장 중심에 가장 가까운 띠 (#269-A):
        // 스폰 띠 바로 안쪽으로 잡으면 복도처럼 좁은 구역에서 스폰과 도착이 사실상 같은 자리라
        // "즉시 젠 후 제자리"로 읽힌다 — 구역을 최대로 가로질러 걸어 들어오게 한다.
        var anchorBand = cells
            .Where(entry => entry.Distance < cells[0].Distance + SwarmFieldSpawnBandCells)
            .ToList();
        if (anchorBand.Count == 0)
            anchorBand = [cells[0]];

        return (
            spawnBand[Random.Shared.Next(spawnBand.Count)].Cell,
            anchorBand[Random.Shared.Next(anchorBand.Count)].Cell);
    }

    // 자기장 파생 웨이브 (#272): 계산은 AreaClosureManager.BuildSwarmFieldWaves가 담당한다.
    // 거리 필드·상수가 프로세스 수명 동안 불변이라 한 번만 계산해 캐시한다.
    private static readonly Lazy<IReadOnlyList<ClosureWaveDefinition>> _swarmFieldDerivedWaves =
        new(
            () => AreaClosureManager
                .BuildSwarmFieldWaves(MatchPressureFieldPolicy.HoldSeconds, MatchPressureFieldPolicy.ShrinkSeconds)
                .AsReadOnly(),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyList<ClosureWaveDefinition> GetSwarmFieldWaves() =>
        _swarmFieldDerivedWaves.Value;

    // 자기장 상태 패킷은 매칭당 개전 1회 브로드캐스트 (재접속은 스냅샷이 복원). Timer 콜백은
    // 겹칠 수 있으므로 authoritative commit은 match monitor, outbound 순서는 field FIFO가 맡는다.

    /// <summary>
    ///     #272 자기장 폐쇄: 구역 웨이브는 자기장에서 파생한 시간표로 닫는다 (MatchPressureFieldPolicy.Enabled=false면
    ///     폐쇄 없음 — 레거시 DefaultP0Waves 폴백은 #310에서 제거). 경고 15초 → 폐쇄 브로드캐스트. 폐쇄 구역 오염은 자기장
    ///     경사(정산 틱의 MatchPressureFieldPolicy.GetCorruptionPerTick)가 전담하고, 신규 몹 스폰 정지는 캠프
    ///     리졸버, 봇·스팟 제외는 IsSwarmAreaOutside가 담당한다.
    /// </summary>
    /// <summary>
    ///     Commits closure, door, inventory, and event-log state under the match monitor, then
    ///     freezes its ordered best-effort packet projection. Transport failure never rolls back
    ///     these authoritative changes.
    /// </summary>
    private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(
        long matchingId,
        IReadOnlyList<GameClientSession> sessions)
    {
        var outbound = ImmutableArray.CreateBuilder<SwarmClosureOutbound>();
        ImmutableArray<int> allRecipients = CaptureSwarmClosureRecipientOrdinals(
            sessions,
            static _ => true);
        var closureState = matchRuntimes.GetRequired(matchingId).Closures.InitializeMatching(
            wavesOverride: MatchPressureFieldPolicy.Enabled ? GetSwarmFieldWaves() : null);
        if (MatchPressureFieldPolicy.Enabled && matchRuntimes.GetRequired(matchingId).Swarm.Pacing.FieldStateAnnounced.Add(matchingId))
        {
            outbound.Add(new SwarmFieldStateOutbound(
                new DateTimeOffset(closureState.GameStartTime).ToUnixTimeMilliseconds(),
                allRecipients));
        }

        var closureTick = matchRuntimes.GetRequired(matchingId).Closures.CheckClosureSchedule();
        foreach (var area in closureTick.WarningAreas)
        {
            outbound.Add(new SwarmClosureWarningOutbound(
                area,
                closureTick.WarningSeconds,
                closureTick.ClosureAtUnixMs,
                allRecipients));
        }

        foreach (var area in closureTick.ClosedAreas)
        {
            eventLogs.LogClosure(matchingId, area.ToString());
            outbound.Add(new SwarmAreaClosedOutbound(area, allRecipients));
        }

        if (closureTick.ClosedAreas.Count > 0)
        {
            // 폐쇄 = 문 잠금 + 틱 오염 (즉사 없음, #227): 닫히는 순간 안에 있어도 죽지
            // 않는다. 정산 틱(GetClosedAreaCorruptionPerTick)이 5초마다 오염을 얹고, 안에 있는 사람은 자기 구역
            // 문을 게이지로 따고 나갈 수 있다(밖에서 들어오는 문 따기는 여전히 거절). 자기 구역 문이 잠기는 것은
            // 그대로다 — "지금 나가야 하는가"의 판단은 경고 15초와 잠긴 문이 만든다.
            // 폐쇄·경고도 수면을 깨우지 않는다 — 수면 중단은 이동뿐이다.
            IReadOnlyList<int> lockedDoorIds =
                matchRuntimes.Get(matchingId)?.Doors.CloseDoorsForAreas(closureTick.ClosedAreas) ?? [];
            foreach (int doorId in lockedDoorIds.Distinct())
                outbound.Add(new SwarmDoorStateOutbound(doorId, allRecipients));

            // 꼬리 파괴: 본인은 밖에 있고 꼬리만 남은 경우가 무보상 파괴 대상이다. 안에 있는 사람의 꼬리는
            // 본인과 함께 남는다 — 틱 오염이 그 사람의 비용이다.
            PrepareDestroySwarmOrbsInClosedAreas(
                matchingId,
                closureTick.ClosedAreas,
                sessions,
                outbound);
        }

        return outbound.Count == 0
            ? null
            : new SwarmClosurePublicationPlan(matchingId, outbound.ToImmutable());
    }

    private static ImmutableArray<int> CaptureSwarmClosureRecipientOrdinals(
        IReadOnlyList<GameClientSession> sessions,
        Func<GameClientSession, bool> predicate)
    {
        var recipients = ImmutableArray.CreateBuilder<int>();
        for (int ordinal = 0; ordinal < sessions.Count; ordinal++)
        {
            if (predicate(sessions[ordinal]))
                recipients.Add(ordinal);
        }

        return recipients.ToImmutable();
    }

    /// <summary>
    ///     Dispatches one frozen closure plan in legacy packet order. The first transport exception
    ///     aborts the remaining projection; the match tick releases its lock in the surrounding scope.
    /// </summary>
    private void DispatchSwarmClosurePublicationPlan(
        SwarmClosurePublicationPlan plan,
        IReadOnlyList<GameClientSession> sessions)
    {
        foreach (SwarmClosureOutbound outbound in plan.Outbound)
        {
            switch (outbound)
            {
                case SwarmFieldStateOutbound fieldState:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
                        {
                            StartedAtUnixMs = fieldState.StartedAtUnixMs
                        }));
                        SendToCapturedRecipients(packet, fieldState.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmClosureWarningOutbound warning:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
                        {
                            AreaType = warning.Area,
                            SecondsRemaining = warning.SecondsRemaining,
                            ClosureAtUnixMs = warning.ClosureAtUnixMs
                        }));
                        SendToCapturedRecipients(packet, warning.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmAreaClosedOutbound closed:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
                        {
                            AreaType = closed.Area,
                            IsClosed = true
                        }));
                        SendToCapturedRecipients(packet, closed.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmDoorStateOutbound door:
                    {
                        using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(
                            door.DoorId,
                            false,
                            ErrorCode.SUCCESS,
                            0);
                        SendToCapturedRecipients(packet, door.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmInventoryUpdateOutbound inventory:
                    {
                        foreach (int ordinal in inventory.RecipientOrdinals)
                        {
                            if (TryGetCapturedValue(sessions, ordinal, out GameClientSession session))
                                session.SendInGameInventoryUpdate(inventory.Item.ToModel());
                        }

                        break;
                    }
                case SwarmRingVfxOutbound ring:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ENCIRCLE_VFX);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ENCIRCLE_VFX
                        {
                            OwnerPlayerId = ring.OwnerPlayerId,
                            CenterX = ring.CenterX,
                            CenterY = ring.CenterY,
                            Radius = ring.Radius,
                            Kind = ring.Kind,
                            VictimPlayerId = ring.VictimPlayerId,
                            FromOrdinal = ring.FromOrdinal
                        }));
                        SendToCapturedRecipients(packet, ring.RecipientOrdinals, sessions);
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown closure outbound type {outbound.GetType().Name} for matching {plan.MatchingId}.");
            }
        }
    }

    /// <summary>
    ///     폐쇄 잔류 오브 파괴 (#226 E): 폐쇄 완료 순간, 폐쇄 구역에 남아 있는 꼬리 접미를
    ///     끝에서부터 무보상 파괴한다 — 소환석 낙수 없음. 긴 꼬리는 점수·화력이 높지만
    ///     폐쇄 전에 더 일찍 철수해야 한다는 관리 비용이 여기서 성립한다.
    /// </summary>
    private void PrepareDestroySwarmOrbsInClosedAreas(
        long matchingId,
        IReadOnlyCollection<AreaType> closedAreas,
        IReadOnlyList<GameClientSession> sessions,
        ImmutableArray<SwarmClosureOutbound>.Builder outbound)
    {
        var closed = closedAreas.ToHashSet();
        var owners = new List<(long PlayerId, Vector3f Position, int SessionOrdinal)>();
        for (int ordinal = 0; ordinal < sessions.Count; ordinal++)
        {
            GameClientSession session = sessions[ordinal];
            if (session.PlayerId.HasValue && !session.IsEliminated &&
                session.LastValidatedPosition != null)
                owners.Add((session.PlayerId.Value, session.LastValidatedPosition, ordinal));
        }

        foreach (var bot in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
        {
            if (!bot.IsEliminated && !bot.IsSwarmCutDummy)
                owners.Add((bot.PlayerId, bot.Position, -1));
        }

        foreach (var (playerId, ownerPosition, ownerSessionOrdinal) in owners)
        {
            // 본인이 폐쇄 구역 안이면 꼬리는 그대로 둔다: 즉사가 퇴역해 본인은 틱 오염을 받으며
            // 문을 따고 나가는 중이다 — 여기서 꼬리까지 지우면 나가도 빈손이라 살아남을 이유가 없다.
            var ownerCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, ownerPosition);
            if (closed.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, ownerCell)))
                continue;

            int orbCount = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs().Sum(item => item.Count);
            if (orbCount == 0)
                continue;
            var closureTiers = orbTrails.GetSwarmOrbTiersInOrder(matchingId, playerId);

            // 꼬리는 경로를 따르므로 폐쇄 구역 잔류분은 항상 접미다 — 끝에서부터 스캔한다.
            int suffixStart = orbCount;
            Vector3f? suffixPosition = null;
            for (int ordinal = orbCount - 1; ordinal >= 0; ordinal--)
            {
                var position = orbTrails.GetSwarmOrbTrailPosition(
                    matchingId, playerId, ordinal, ownerPosition, closureTiers);
                var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, position);
                if (!closed.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell)))
                    break;
                suffixStart = ordinal;
                suffixPosition = position;
            }

            if (suffixStart >= orbCount || suffixPosition == null)
                continue;

            var destroyed = orbTrails.DestroySwarmOrbsFromOrdinal(matchingId, playerId, suffixStart);
            foreach (var destroyedItem in destroyed)
            {
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.Remove((matchingId, playerId, destroyedItem.ItemUid));
                if (ownerSessionOrdinal >= 0)
                {
                    outbound.Add(new SwarmInventoryUpdateOutbound(
                        SwarmInGameItemSnapshot.Capture(destroyedItem),
                        [ownerSessionOrdinal]));
                }
            }

            // 파열 연출은 절단 링 재사용 — 전리품은 흩뿌리지 않는다 (폐쇄 파괴 무보상).
            var closedArea = GameMapData.GetCurrentArea(
                Config.SWARM_MATCH_MAP,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, suffixPosition));
            ImmutableArray<int> ringRecipients = CaptureSwarmClosureRecipientOrdinals(
                sessions,
                session => session.PlayerId.HasValue && session.CurrentArea == closedArea);
            outbound.Add(new SwarmRingVfxOutbound(
                playerId,
                suffixPosition.X,
                suffixPosition.Y,
                OrbTrailService.CutFlashRadius,
                OrbTrailService.CutVfxKind,
                playerId,
                suffixStart,
                ringRecipients));
            eventLogs.LogSystem(
                matchingId,
                $"closure_orb_destroyed player={playerId} from={suffixStart} count={destroyed.Count}");
            logger.LogInformation(
                "Swarm closure orb destruction: MatchingId={MatchingId}, PlayerId={PlayerId}, FromOrdinal={FromOrdinal}, Count={Count}",
                matchingId, playerId, suffixStart, destroyed.Count);
        }
    }

}
