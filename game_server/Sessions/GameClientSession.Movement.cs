using System.Diagnostics;
using game_server.matches;
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

                match.SynchronizedObjects.TryAdd((ObjectType.PLAYER, Player.PlayerId), new MatchObjectSnapshot(Player.GameInfo.ObjectInfo));
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
                // 본인 보정·주기 응답은 틱 동기화를 기다리지 않고 요청 처리에서 직접 보낸다.
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

    internal void SendGroundItemEntries(AreaType area)
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
        SendObjectEntries(new G_TO_C_OBJECT_ENTER { Items = items });
    }

    // 성공적으로 등장시킨 객체만 기억한다. 상태/이동 패킷은 생성을 대신하지 않는다.
    internal HashSet<(ObjectType Type, long Id)> PublishedObjects { get; } = new();

    internal void SendObjectEntries(G_TO_C_OBJECT_ENTER entries)
    {
        if (entries.Players.Count == 0 && entries.Monsters.Count == 0 && entries.Items.Count == 0) return;
        using var packet = Packet.Create((int)Protocol.G_TO_C_OBJECT_ENTER);
        packet.SetBody(MessagePackSerializer.Serialize(entries));
        if (!TrySend(packet)) return;
        foreach (var player in entries.Players)
            PublishedObjects.Add((ObjectType.PLAYER, player.ObjectInfo.ObjectId));
        foreach (var item in entries.Items)
            PublishedObjects.Add((ObjectType.ITEM, item.GroundItemUid));
        foreach (var monster in entries.Monsters)
        {
            PublishedObjects.Add((ObjectType.MONSTER, monster.MonsterId));
            _publishedMonsterStates[monster.MonsterId] = monster;
        }
    }

    internal void SendObjectLeaves(List<ObjectIdentity> objects)
    {
        if (objects.Count == 0) return;
        using var packet = Packet.Create((int)Protocol.G_TO_C_OBJECT_LEAVE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_OBJECT_LEAVE { Objects = objects }));
        if (!TrySend(packet)) return;
        foreach (var identity in objects)
        {
            PublishedObjects.Remove((identity.Type, identity.Id));
            if (identity.Type == ObjectType.MONSTER)
                _publishedMonsterStates.Remove((int)identity.Id);
        }
    }

    private readonly Dictionary<int, MonsterInfo> _publishedMonsterStates = new();

    internal void SendMonsterSnapshot(IReadOnlyDictionary<AreaType, List<MonsterInfo>> snapshotsByArea, bool fullSnapshot = true)
    {
        var entries = new G_TO_C_OBJECT_ENTER();
        var changed = new List<MonsterInfo>();
        var visibleIds = new HashSet<long>();
        foreach (var (area, monsters) in snapshotsByArea)
        {
            if (Player.CurrentArea != area) continue;
            foreach (var monster in monsters)
            {
                visibleIds.Add(monster.MonsterId);
                if (!PublishedObjects.Contains((ObjectType.MONSTER, monster.MonsterId)))
                {
                    if (monster.IsAlive) entries.Monsters.Add(monster);
                }
                else if (HasMonsterStateChanged(monster))
                {
                    changed.Add(monster);
                }
            }
        }
        SendObjectEntries(entries);
        SendChangedMonsterStates(changed);
        if (fullSnapshot)
        {
            var leaves = new List<ObjectIdentity>();
            foreach (var identity in PublishedObjects)
            {
                if (identity.Type == ObjectType.MONSTER && !visibleIds.Contains(identity.Id))
                    leaves.Add(new ObjectIdentity { Type = identity.Type, Id = identity.Id });
            }
            SendObjectLeaves(leaves);
        }
    }

    internal bool HasMonsterStateChanged(MonsterInfo monster)
    {
        if (monster.AreaType != Player.CurrentArea)
        {
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
        using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_INFO);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_INFO
        {
            Monsters = changed
        }));
        if (!TrySend(packet)) return;
        foreach (var monster in changed)
        {
            _publishedMonsterStates[monster.MonsterId] = monster;
        }
    }


}
