using MessagePack;
using game_server.sessions;
using network.packets;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>틱의 최종 봇·몬스터 상태를 비교해 상태·구역 알림을 먼저 보내고 이동을 일괄 전송한다. 비교 상태는 매치가 소유한다.</summary>
internal sealed class MatchSynchronizationService
{
    // 첫 이동에서도 이전 구역을 알 수 있도록 새 개체의 기준 상태만 기록한다. 전송하지 않는다.
    public void TrackNewObjects(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        foreach (var bot in runtime.Bots.GetBots())
        {
            runtime.SynchronizedObjects.TryAdd((ObjectType.PLAYER, bot.PlayerId), bot.Player.GameInfo.ObjectInfo.Clone());
            runtime.SynchronizedBotStates.TryAdd(bot.PlayerId, bot.Player.State);
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

        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation)
        {
            return;
        }

        var snapshots = runtime.Monsters.GetVisualStatesByArea();
        foreach (var session in runtime.GetSessions())
        {
            session.SendMonsterSnapshot(snapshots);
        }

        if (!runtime.IsGameplayActive(nowUtc))
        {
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

        var messages = new Dictionary<GameClientSession, G_TO_C_MOVE>();
        foreach (var (key, info) in currentObjects)
        {
            if (!runtime.SynchronizedObjects.TryGetValue(key, out var previous))
            {
                QueueObjectMovement(runtime, info, info.Area, messages);
                continue;
            }
            bool changed = false;
            if (key.Item1 == ObjectType.PLAYER)
            {
                if (runtime.SynchronizedBotStates.TryGetValue(key.Item2, out var previousState))
                {
                    var bot = runtime.Bots.GetBot(key.Item2)!;
                    if (previousState != bot.Player.State)
                    {
                        changed = true;
                    }
                }
            }
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
            if (!changed)
            {
                continue;
            }
            QueueObjectMovement(runtime, info, previous.Area, messages);
        }

        SendMovements(runtime, messages);
        runtime.SynchronizedObjects.Clear();
        runtime.SynchronizedBotStates.Clear();
        foreach (var (key, info) in currentObjects)
        {
            runtime.SynchronizedObjects[key] = info.Clone();
        }

        foreach (var bot in runtime.Bots.GetBots())
        {
            if (!bot.Player.IsEliminated)
            {
                runtime.SynchronizedBotStates[bot.PlayerId] = bot.Player.State;
            }
        }
    }

    internal void QueueObjectMovement(MatchRuntime runtime, GameObjectInfo info, AreaType fromArea, Dictionary<GameClientSession, G_TO_C_MOVE> messages)
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
            var player = runtime.Bots.GetBot(info.ObjectId)?.Player;
            if (player != null && (!runtime.SynchronizedBotStates.TryGetValue(player.PlayerId, out var previousState) || previousState != player.State))
            {
                using var statePacket = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == fromArea || session.Player.CurrentArea == info.Area)
                    {
                        session.TrySend(statePacket);
                    }
                }
            }

            if (fromArea != info.Area)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(info.ObjectId);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == fromArea)
                    {
                        session.TrySend(leavePacket);
                    }
                }

                var enteringBot = runtime.Bots.GetPlayerObjectInfo(info.ObjectId);
                if (enteringBot != null)
                {
                    using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(enteringBot);
                    foreach (var session in sessions)
                    {
                        if (session.Player.CurrentArea == info.Area)
                        {
                            session.TrySend(enterPacket);
                        }
                    }
                }
            }
        }

        foreach (var session in sessions)
        {
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
                var player = runtime.Bots.GetBot(info.ObjectId)?.Player;
                message.OrbPhases[info.ObjectId] = player?.OrbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(info.ObjectId);
            }
        }
    }

    internal void SendMovements(MatchRuntime runtime, IReadOnlyDictionary<GameClientSession, G_TO_C_MOVE> messages)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Object movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish object movement after the match has ended.");
        }
        foreach (var (session, message) in messages)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MOVE);
            packet.SetBody(MessagePackSerializer.Serialize(message));
            session.TrySend(packet);
        }
    }
}
