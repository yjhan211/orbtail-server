using System.Diagnostics;
using game_server.players;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     클라이언트의 이동 요청을 처리한다.
///     매치 잠금 안에서 이동 값과 플레이 가능 상태를 확인하고,
///     이동 검증과 상태 반영은 PlayerMovementService에 맡긴다.
///     주변 동기화는 틱 끝에서 처리하고, 본인에게는 보정이 필요하거나 응답 간격이 지났을 때 전송한다.
/// </summary>
public partial class GameClientSession
{
    private Task HandleMove(C_TO_G_MOVE msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded || PlayerId == null || IsGameplayActionBlocked(out _))
            {
                return Task.CompletedTask;
            }

            try
            {
                if (!PlayerMovementService.IsFinite(msg.Position) || !PlayerMovementService.IsFinite(msg.Velocity) || !float.IsFinite(msg.Rotation))
                {
                    Logger.LogWarning("Player {PlayerId} sent invalid movement values", PlayerId);
                    return Task.CompletedTask;
                }

                long timestamp = Stopwatch.GetTimestamp();
                float deltaTime = Player.CalculateMoveDeltaTime(timestamp);

                match.SynchronizedObjects.TryAdd((ObjectType.PLAYER, Player.PlayerId), Player.GameInfo.ObjectInfo.Clone());
                var result = _movement.ProcessMovement(match, Player, msg, deltaTime);
                if (result.BlockedCell is { } blockedCell)
                {
                    using var rejected = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(result.NewArea, blockedCell);
                    TrySend(rejected);
                    Logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})", PlayerId, result.NewArea, blockedCell.X, blockedCell.Y);
                    return Task.CompletedTask;
                }


                var validation = result.Movement;
                long serverTimestamp = result.ServerTimestamp;
                bool requiresClientCorrection = validation.RequiresCorrection;

                using var packet = PacketMaker.G_TO_C_MOVE(Player.GameInfo.ObjectInfo, serverTimestamp, Player.OrbOrbitPhaseDegrees);

                if (requiresClientCorrection || Player.ShouldSendMoveResponse(timestamp))
                {
                    TrySend(packet);
                    Player.RecordMoveResponse(timestamp);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
                using var errorPacket = PacketMaker.G_TO_C_ERROR(ErrorCode.SERVER_INTERNAL_ERROR);
                TrySend(errorPacket);
            }
        }

        return Task.CompletedTask;
    }

    internal void SendGroundItemSnapshot(AreaType area)
    {
        if (MatchingId <= 0 || area == AreaType.None)
        {
            return;
        }

        var match = Match;
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Ground item snapshots require the match lock.");
        }

        var items = match.GroundItems.GetItemsInArea(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, items);
        TrySend(packet);
    }

    private readonly Dictionary<int, MonsterInfo> _publishedMonsterStates = new();

    internal void SendMonsterSnapshot(IReadOnlyDictionary<AreaType, List<MonsterInfo>> snapshotsByArea, bool fullSnapshot = true)
    {
        if (fullSnapshot)
        {
            var visibleIds = new HashSet<int>();
            foreach (var pair in snapshotsByArea)
            {
                if (pair.Key != Player.CurrentArea) continue;
                foreach (var monster in pair.Value) visibleIds.Add(monster.MonsterId);
            }
            foreach (int id in _publishedMonsterStates.Keys.ToArray())
            {
                if (!visibleIds.Contains(id)) _publishedMonsterStates.Remove(id);
            }
        }
        foreach (var (area, monsters) in snapshotsByArea)
        {
            if (Player.CurrentArea != area)
            {
                continue;
            }

            var changed = new List<MonsterInfo>();
            foreach (var monster in monsters)
            {
                if (!HasMonsterStateChanged(monster))
                {
                    continue;
                }
                changed.Add(monster);
            }
            SendChangedMonsterStates(changed);
        }
    }

    internal bool HasMonsterStateChanged(MonsterInfo monster)
    {
        if (monster.AreaType != Player.CurrentArea)
        {
            _publishedMonsterStates.Remove(monster.MonsterId);
            return false;
        }
        if (!_publishedMonsterStates.TryGetValue(monster.MonsterId, out var previous))
        {
            return true;
        }
        return previous.AreaType != monster.AreaType || previous.CurrentHealth != monster.CurrentHealth ||
            previous.MaxHealth != monster.MaxHealth || previous.IsAlive != monster.IsAlive ||
            previous.ChaseTargetPlayerId != monster.ChaseTargetPlayerId ||
            previous.RewardItemId != monster.RewardItemId || previous.IsCore != monster.IsCore ||
            previous.SummonStoneReward != monster.SummonStoneReward ||
            previous.Kind != monster.Kind || previous.Phase != monster.Phase;
    }

    internal void SendChangedMonsterStates(List<MonsterInfo> changed)
    {
        if (changed.Count == 0) return;
        using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
        {
            Monsters = changed
        }));
        if (!TrySend(packet)) return;
        foreach (var monster in changed)
        {
            if (monster.IsAlive) _publishedMonsterStates[monster.MonsterId] = monster;
            else _publishedMonsterStates.Remove(monster.MonsterId);
        }
    }

    internal void SendAreaSnapshot()
    {
        SendInteractableList();
        SendGroundItemSnapshot(Player.CurrentArea);
    }
}
