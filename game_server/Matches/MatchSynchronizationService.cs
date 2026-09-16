using MessagePack;
using game_server.sessions;
using game_server.players;
using game_server.matches.monsters;
using network.packets;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치 상태를 이전 동기화 상태와 비교하여 관찰자별 전송 정보를 수집한다.
///     객체 입퇴장·상호작용·플레이어·몬스터 상태와 이동을 틱 끝에 모아 전송한다.
/// </summary>
internal sealed class MatchSynchronizationService
{
    internal sealed class SyncBatch(DateTime nowUtc, IReadOnlyList<GameClientSession> sessions)
    {
        public long ServerTimestamp { get; } = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        public IReadOnlyList<GameClientSession> Sessions { get; } = sessions;
        public Dictionary<GameClientSession, G_TO_C_MOVE> Moves { get; init; } = new();
        public Dictionary<GameClientSession, List<MonsterInfo>> MonsterUpdates { get; init; } = new();
        public Dictionary<GameClientSession, List<GamePlayerInfo>> PlayerUpdates { get; init; } = new();
        public Dictionary<GameClientSession, G_TO_C_OBJECT_ENTER> Entries { get; init; } = new();
        public Dictionary<GameClientSession, List<ObjectIdentity>> Leaves { get; init; } = new();
        public Dictionary<GameClientSession, List<InteractableInfo>> InteractableUpdates { get; init; } = new();
    }

    public void InitializeComparisonSnapshots(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.PLAYER, player.PlayerId), new MatchObjectSnapshot(player.GameInfo.ObjectInfo));
            runtime.SynchronizedPlayerStates.TryAdd(player.PlayerId, player.State);
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.MONSTER, monster.MonsterId), new MatchObjectSnapshot(monster.Info.ObjectInfo));
        }
    }

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        var batch = new SyncBatch(nowUtc, runtime.GetSessions());
        CollectInteractableUpdates(runtime, batch);
        CollectGroundItemEntries(runtime, batch);

        bool isGameplayActive = runtime.IsGameplayActive(nowUtc);
        var players = runtime.GetAlivePlayers();
        if (isGameplayActive)
        {
            foreach (var player in players)
            {
                CollectPlayerUpdates(runtime, player, batch);
            }
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            CollectMonsterUpdates(runtime, monster, batch);
            if (isGameplayActive)
            {
                CollectMovementUpdates(runtime, monster.Info.ObjectInfo, batch);
            }
        }
        CollectObjectLeaves(runtime, batch);
        SendBatch(runtime, batch);

        if (!isGameplayActive)
        {
            return;
        }

        runtime.SynchronizedObjects.Clear();
        runtime.SynchronizedPlayerStates.Clear();
        foreach (var player in players)
        {
            runtime.SynchronizedObjects[(ObjectType.PLAYER, player.PlayerId)] = new MatchObjectSnapshot(player.GameInfo.ObjectInfo);
            runtime.SynchronizedPlayerStates[player.PlayerId] = player.State;
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            runtime.SynchronizedObjects[(ObjectType.MONSTER, monster.MonsterId)] = new MatchObjectSnapshot(monster.Info.ObjectInfo);
        }
    }

    internal void CollectInteractableUpdates(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Area state collection requires the match lock.");
        }
        var openDoors = runtime.Doors.GetOpenDoors().ToHashSet();
        foreach (var session in batch.Sessions)
        {
            if (session.Player.IsEliminated || session.Player.GameInfo.ObjectInfo.Area == AreaType.None)
            {
                continue;
            }

            if (session.PublishedInteractionArea != session.Player.GameInfo.ObjectInfo.Area || !session.PublishedOpenDoors.SetEquals(openDoors))
            {
                batch.InteractableUpdates[session] = session.GetInteractableInfos();
            }
        }
    }

    internal void CollectGroundItemEntries(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Ground item collection requires the match lock.");
        }
        var areaItems = new Dictionary<AreaType, List<GroundItemInfo>>();
        foreach (var session in batch.Sessions)
        {
            var area = session.Player.GameInfo.ObjectInfo.Area;
            if (session.Player.IsEliminated || area == AreaType.None) continue;
            if (!areaItems.TryGetValue(area, out var items))
            {
                items = runtime.GroundItems.GetItemsInArea(area);
                areaItems.Add(area, items);
            }
            foreach (var item in items)
            {
                if (session.PublishedObjects.Contains((ObjectType.ITEM, item.GroundItemUid))) continue;
                if (!batch.Entries.TryGetValue(session, out var entries))
                {
                    entries = new G_TO_C_OBJECT_ENTER();
                    batch.Entries.Add(session, entries);
                }
                entries.Items.Add(item);
            }
        }
    }

    internal void CollectPlayerUpdates(MatchRuntime runtime, Player player, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization collection requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot collect synchronization after the match has ended.");
        }
        var info = player.GameInfo.ObjectInfo;
        bool stateChanged = !runtime.SynchronizedPlayerStates.TryGetValue(player.PlayerId, out var previousState) || previousState != player.State;
        GamePlayerInfo? snapshot = null;
        foreach (var session in batch.Sessions)
        {
            if (session.Player.IsEliminated)
            {
                continue;
            }
            bool isSelf = session.Player.PlayerId == player.PlayerId;
            if (session.Player.GameInfo.ObjectInfo.Area != info.Area) continue;
            bool known = isSelf || session.PublishedObjects.Contains((ObjectType.PLAYER, player.PlayerId));
            if (!known)
            {
                if (!batch.Entries.TryGetValue(session, out var entries))
                {
                    entries = new G_TO_C_OBJECT_ENTER();
                    batch.Entries.Add(session, entries);
                }
                snapshot ??= player.CreatePlayerObjectInfo();
                entries.Players.Add(snapshot);
                continue;
            }
            if (!stateChanged) continue;
            if (!batch.PlayerUpdates.TryGetValue(session, out var updates))
            {
                updates = [];
                batch.PlayerUpdates.Add(session, updates);
            }
            snapshot ??= player.CreatePlayerObjectInfo();
            updates.Add(snapshot);
        }
        CollectMovementUpdates(runtime, info, batch, player.OrbOrbitPhaseDegrees, stateChanged);
    }

    internal void CollectMonsterUpdates(MatchRuntime runtime, Monster monster, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization collection requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot collect synchronization after the match has ended.");
        }
        var snapshot = monster.ToMonsterInfo();
        foreach (var session in batch.Sessions)
        {
            if (session.Player.IsEliminated || session.Player.GameInfo.ObjectInfo.Area != snapshot.AreaType) continue;
            if (!session.PublishedObjects.Contains((ObjectType.MONSTER, monster.MonsterId)))
            {
                if (!snapshot.IsAlive) continue;
                if (!batch.Entries.TryGetValue(session, out var entries))
                {
                    entries = new G_TO_C_OBJECT_ENTER();
                    batch.Entries.Add(session, entries);
                }
                entries.Monsters.Add(snapshot);
                continue;
            }
            if (!session.HasMonsterStateChanged(snapshot))
            {
                continue;
            }
            if (!batch.MonsterUpdates.TryGetValue(session, out var changedMonsters))
            {
                changedMonsters = [];
                batch.MonsterUpdates.Add(session, changedMonsters);
            }
            changedMonsters.Add(snapshot);
        }
    }

    internal void CollectMovementUpdates(MatchRuntime runtime, GameObjectInfo info, SyncBatch batch, float? orbPhase = null, bool stateChanged = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization collection requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot collect synchronization after the match has ended.");
        }
        if (runtime.SynchronizedObjects.TryGetValue((info.ObjectType, info.ObjectId), out var previous))
        {
            if (!stateChanged && previous.Matches(info))
            {
                return;
            }
        }
        GameObjectInfo? snapshot = null;
        foreach (var session in batch.Sessions)
        {
            if (session.Player.IsEliminated)
            {
                continue;
            }
            bool inArea = session.Player.GameInfo.ObjectInfo.Area == info.Area;
            if (!inArea)
            {
                continue;
            }
            if (!batch.Moves.TryGetValue(session, out var message))
            {
                message = new G_TO_C_MOVE { ServerTimestamp = batch.ServerTimestamp };
                batch.Moves.Add(session, message);
            }
            snapshot ??= info.Clone();
            message.Objects.Add(snapshot);
            if (orbPhase.HasValue)
            {
                message.OrbPhases[info.ObjectId] = orbPhase.Value;
            }
        }
    }

    internal void CollectObjectLeaves(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Visibility collection requires the match lock.");
        }
        var areas = new Dictionary<(ObjectType Type, long Id), AreaType>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            areas[(ObjectType.PLAYER, player.PlayerId)] = player.GameInfo.ObjectInfo.Area;
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            areas[(ObjectType.MONSTER, monster.MonsterId)] = monster.Area;
        }
        foreach (var session in batch.Sessions)
        {
            foreach (var identity in session.PublishedObjects)
            {
                var area = identity.Type == ObjectType.ITEM
                    ? runtime.GroundItems.GetItemArea(identity.Id)
                    : areas.GetValueOrDefault(identity, AreaType.None);
                if (!session.Player.IsEliminated && area != AreaType.None && area == session.Player.GameInfo.ObjectInfo.Area)
                {
                    continue;
                }
                if (!batch.Leaves.TryGetValue(session, out var leaves))
                {
                    leaves = [];
                    batch.Leaves.Add(session, leaves);
                }
                leaves.Add(new ObjectIdentity { Type = identity.Type, Id = identity.Id });
            }
        }
    }



    internal static void SendPendingCombatHits(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat hit publication requires the match lock.");
        }
        while (runtime.PendingCombatHits.TryDequeue(out var notification))
        {
            using var packet = PacketMaker.G_TO_C_COMBAT_HIT(notification.Hit);
            notification.Session.TrySend(packet);
        }
    }

    internal void SendBatch(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Object movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish object movement after the match has ended.");
        }
        SendPendingCombatHits(runtime);
        foreach (var (session, snapshot) in batch.InteractableUpdates)
        {
            session.SendInteractableInfos(snapshot);
        }
        foreach (var (session, objects) in batch.Leaves)
        {
            session.SendObjectLeaves(objects);
        }
        foreach (var (session, entries) in batch.Entries)
        {
            session.SendObjectEntries(entries);
        }
        foreach (var (session, players) in batch.PlayerUpdates)
        {
            using var packet = PacketMaker.G_TO_C_PLAYER_INFO(players);
            session.TrySend(packet);
        }
        foreach (var (session, changedMonsters) in batch.MonsterUpdates)
        {
            session.SendChangedMonsterStates(changedMonsters);
        }
        foreach (var (session, message) in batch.Moves)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MOVE);
            packet.SetBody(MessagePackSerializer.Serialize(message));
            session.TrySend(packet);
        }
    }
}
