using MessagePack;
using game_server.sessions;
using network.packets;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>틱의 최종 플레이어·몬스터 상태를 비교해 상태 알림과 이동을 전송한다. 비교 상태는 매치가 소유한다.</summary>
internal sealed class MatchSynchronizationService
{
    // 첫 이동에서도 이전 구역을 알 수 있도록 새 개체의 기준 상태만 기록한다. 전송하지 않는다.
    public void TrackNewObjects(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.PLAYER, player.PlayerId), player.GameInfo.ObjectInfo.Clone());
            runtime.SynchronizedPlayerStates.TryAdd(player.PlayerId, player.State);
        }
        foreach (var bot in runtime.Bots.GetBots())
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.PLAYER, bot.PlayerId), bot.Player.GameInfo.ObjectInfo.Clone());
            runtime.SynchronizedPlayerStates.TryAdd(bot.PlayerId, bot.Player.State);
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.MONSTER, monster.MonsterId), monster.Info.ObjectInfo.Clone());
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

        var areaSnapshots = new HashSet<GameClientSession>();
        var messages = new Dictionary<GameClientSession, G_TO_C_MOVE>();
        var monsterUpdates = new Dictionary<GameClientSession, List<MonsterInfo>>();
        var playerUpdates = new Dictionary<GameClientSession, List<(Protocol Protocol, byte[] Body)>>();
        if (!runtime.IsGameplayActive(nowUtc))
        {
            foreach (var monster in runtime.Monsters.Entities.Values)
            {
                var info = monster.Info.ObjectInfo;
                SynchronizeObject(runtime, info, info.Area, messages, monsterUpdates, playerUpdates, areaSnapshots, includeMovement: false);
            }
            SendUpdates(runtime, messages, monsterUpdates, playerUpdates, areaSnapshots);
            return;
        }
        var currentObjects = new Dictionary<(ObjectType, long), GameObjectInfo>();
        foreach (var bot in runtime.Bots.GetBots())
        {
            if (!bot.Player.IsEliminated)
            {
                currentObjects[(ObjectType.PLAYER, bot.PlayerId)] = bot.Player.GameInfo.ObjectInfo;
            }
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            currentObjects[(ObjectType.MONSTER, monster.MonsterId)] = monster.Info.ObjectInfo;
        }

        foreach (var player in runtime.GetAlivePlayers())
        {
            currentObjects[(ObjectType.PLAYER, player.PlayerId)] = player.GameInfo.ObjectInfo;
        }

        foreach (var (key, info) in currentObjects)
        {
            var fromArea = info.Area;
            bool changed = false;
            if (!runtime.SynchronizedObjects.TryGetValue(key, out var previous))
            {
                changed = true;
            }
            else
            {
                fromArea = previous.Area;
                if (previous.Area != info.Area)
                {
                    changed = true;
                }
                if (!previous.Position.Equals(info.Position))
                {
                    changed = true;
                }
                if (!previous.Velocity.Equals(info.Velocity))
                {
                    changed = true;
                }
                if (!previous.Rotation.Equals(info.Rotation))
                {
                    changed = true;
                }
            }
            if (key.Item1 == ObjectType.PLAYER)
            {
                if (runtime.SynchronizedPlayerStates.TryGetValue(key.Item2, out var previousState))
                {
                    var player = runtime.GetParticipant(key.Item2) ?? runtime.Bots.GetBot(key.Item2)!.Player;
                    if (previousState != player.State)
                    {
                        changed = true;
                    }
                }
            }
            SynchronizeObject(runtime, info, fromArea, messages, monsterUpdates, playerUpdates, areaSnapshots, includeMovement: changed);
        }
        SendUpdates(runtime, messages, monsterUpdates, playerUpdates, areaSnapshots);
        runtime.SynchronizedObjects.Clear();
        runtime.SynchronizedPlayerStates.Clear();
        foreach (var (key, info) in currentObjects)
        {
            runtime.SynchronizedObjects[key] = info.Clone();
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            runtime.SynchronizedPlayerStates[player.PlayerId] = player.State;
        }
    }

    internal void SynchronizeObject(MatchRuntime runtime, GameObjectInfo info, AreaType fromArea, Dictionary<GameClientSession, G_TO_C_MOVE> messages, Dictionary<GameClientSession, List<MonsterInfo>> monsterUpdates, Dictionary<GameClientSession, List<(Protocol Protocol, byte[] Body)>> playerUpdates, HashSet<GameClientSession> areaSnapshots, bool includeMovement = true)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Object movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish object movement after the match has ended.");
        }

        var sessions = runtime.GetSessions();
        if (info.ObjectType == ObjectType.PLAYER)
        {
            var player = runtime.GetParticipant(info.ObjectId) ?? runtime.Bots.GetBot(info.ObjectId)?.Player;
            if (player != null && (!runtime.SynchronizedPlayerStates.TryGetValue(player.PlayerId, out var previousState) || previousState != player.State))
            {
                var stateBody = MessagePackSerializer.Serialize(new G_TO_C_PLAYER_STATE { PlayerId = player.PlayerId, State = player.State });
                foreach (var session in sessions)
                {
                    if (!session.Player.IsEliminated && (session.Player.CurrentArea == fromArea || session.Player.CurrentArea == info.Area))
                    {
                        if (!playerUpdates.TryGetValue(session, out var notifications))
                        {
                            notifications = new List<(Protocol Protocol, byte[] Body)>();
                            playerUpdates.Add(session, notifications);
                        }
                        notifications.Add((Protocol.G_TO_C_PLAYER_STATE, stateBody));
                    }
                }
            }

            if (player != null)
            {
                if (fromArea != info.Area && player.Session != null)
                {
                    areaSnapshots.Add(player.Session);
                }
                foreach (var session in sessions)
                {
                    if (session.Player.IsEliminated || session.Player.PlayerId == info.ObjectId)
                    {
                        continue;
                    }
                    var previousObserverArea = session.Player.CurrentArea;
                    if (runtime.SynchronizedObjects.TryGetValue((ObjectType.PLAYER, session.Player.PlayerId), out var observer))
                    {
                        previousObserverArea = observer.Area;
                    }
                    bool wasVisible = previousObserverArea == fromArea;
                    bool isVisible = session.Player.CurrentArea == info.Area;
                    if (wasVisible == isVisible)
                    {
                        continue;
                    }
                    if (!playerUpdates.TryGetValue(session, out var notifications))
                    {
                        notifications = new List<(Protocol Protocol, byte[] Body)>();
                        playerUpdates.Add(session, notifications);
                    }
                    if (isVisible)
                    {
                        var presence = player.CreatePlayerObjectInfo();
                        var body = MessagePackSerializer.Serialize(new G_TO_C_AREA_PLAYER_ENTER { Player = presence.Player, GamePlayer = presence.GamePlayer });
                        notifications.Add((Protocol.G_TO_C_AREA_PLAYER_ENTER, body));
                    }
                    else
                    {
                        var body = MessagePackSerializer.Serialize(new G_TO_C_AREA_PLAYER_LEAVE { PlayerId = info.ObjectId });
                        notifications.Add((Protocol.G_TO_C_AREA_PLAYER_LEAVE, body));
                    }
                }
            }
        }
        if (info.ObjectType == ObjectType.MONSTER)
        {
            var monster = runtime.Monsters.Entities[(int)info.ObjectId].ToMonsterInfo();
            foreach (var session in sessions)
            {
                if (!session.HasMonsterStateChanged(monster))
                {
                    continue;
                }
                if (!monsterUpdates.TryGetValue(session, out var changedMonsters))
                {
                    changedMonsters = new List<MonsterInfo>();
                    monsterUpdates.Add(session, changedMonsters);
                }
                changedMonsters.Add(monster);
            }
        }
        if (!includeMovement)
        {
            return;
        }

        foreach (var session in sessions)
        {
            if (session.Player.IsEliminated || (info.ObjectType == ObjectType.PLAYER && session.Player.PlayerId == info.ObjectId))
            {
                continue;
            }
            bool inArea = session.Player.CurrentArea == info.Area;
            bool departingMonster = info.ObjectType == ObjectType.MONSTER && session.Player.CurrentArea == fromArea;
            if (!inArea && !departingMonster)
            {
                continue;
            }
            if (!messages.TryGetValue(session, out var message))
            {
                message = new G_TO_C_MOVE
                {
                    ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                messages.Add(session, message);
            }
            message.Objects.Add(info.Clone());
            if (info.ObjectType == ObjectType.PLAYER)
            {
                var player = runtime.GetParticipant(info.ObjectId) ?? runtime.Bots.GetBot(info.ObjectId)?.Player;
                message.OrbPhases[info.ObjectId] = player?.OrbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(info.ObjectId);
            }
        }
    }

    internal static void SendCombatHits(MatchRuntime runtime)
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

    internal void SendUpdates(MatchRuntime runtime, IReadOnlyDictionary<GameClientSession, G_TO_C_MOVE> messages, IReadOnlyDictionary<GameClientSession, List<MonsterInfo>> monsterUpdates, IReadOnlyDictionary<GameClientSession, List<(Protocol Protocol, byte[] Body)>> playerUpdates, IReadOnlyCollection<GameClientSession> areaSnapshots)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Object movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish object movement after the match has ended.");
        }
        SendCombatHits(runtime);
        foreach (var session in areaSnapshots)
        {
            session.SendAreaSnapshot();
        }
        foreach (var (session, notifications) in playerUpdates)
        {
            foreach (var (protocol, body) in notifications)
            {
                using var packet = Packet.Create((int)protocol);
                packet.SetBody(body);
                session.TrySend(packet);
            }
        }
        foreach (var (session, changedMonsters) in monsterUpdates)
        {
            session.SendChangedMonsterStates(changedMonsters);
        }
        foreach (var (session, message) in messages)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MOVE);
            packet.SetBody(MessagePackSerializer.Serialize(message));
            session.TrySend(packet);
        }
    }
}
