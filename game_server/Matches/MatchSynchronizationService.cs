using game_server.matches.monsters;
using game_server.players;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     매치 상태를 이전 동기화 상태와 비교하여 관찰자별 전송 정보를 수집한다.
///     객체 입퇴장·상호작용·플레이어·몬스터 상태와 이동을 틱 끝에 모아 전송한다.
/// </summary>
internal sealed class MatchSynchronizationService
{
    internal sealed class SyncBatch(DateTime nowUtc, IReadOnlyList<GameClientSession> sessions)
    {
        private Dictionary<GameClientSession, AreaType>? _observerAreas;

        public IReadOnlyList<GameClientSession> Sessions { get; } = sessions;
        public long ServerTimestamp { get; } = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        public Dictionary<GameClientSession, List<InteractableInfo>> InteractableUpdates { get; init; } = new();
        public Dictionary<GameClientSession, List<MonsterInfo>> RemovedMonsters { get; init; } = new();
        public Dictionary<GameClientSession, List<ObjectIdentity>> Leaves { get; init; } = new();
        public Dictionary<GameClientSession, G_TO_C_OBJECT_ENTER> Entries { get; init; } = new();
        public List<MatchOrbVisual> OrbVisuals { get; set; } = new();
        public Dictionary<GameClientSession, List<GamePlayerInfo>> PlayerUpdates { get; init; } = new();
        public Dictionary<GameClientSession, List<MonsterInfo>> MonsterUpdates { get; init; } = new();
        public Dictionary<GameClientSession, G_TO_C_MOVE> Moves { get; init; } = new();
        public Dictionary<GameClientSession, List<G_TO_C_SUN_ORB_ATTACK>> SunAttacks { get; init; } = new();
        public Dictionary<GameClientSession, List<G_TO_C_WAVE_ORB_ATTACK>> WaveAttacks { get; init; } = new();
        public G_TO_C_ORB_RANKINGS? OrbRankings { get; set; }

        public IReadOnlyDictionary<GameClientSession, AreaType> ObserverAreas
        {
            get
            {
                if (_observerAreas != null)
                {
                    return _observerAreas;
                }
                _observerAreas = new Dictionary<GameClientSession, AreaType>();
                foreach (var session in Sessions)
                {
                    if (session.Player.IsEliminated)
                    {
                        continue;
                    }
                    _observerAreas.Add(session, session.Player.CurrentArea);
                }
                return _observerAreas;
            }
        }

        public G_TO_C_OBJECT_ENTER GetEntries(GameClientSession session)
        {
            if (!Entries.TryGetValue(session, out var entries))
            {
                entries = new G_TO_C_OBJECT_ENTER();
                Entries.Add(session, entries);
            }
            return entries;
        }

        public G_TO_C_MOVE GetMoves(GameClientSession session)
        {
            if (!Moves.TryGetValue(session, out var message))
            {
                message = new G_TO_C_MOVE { ServerTimestamp = ServerTimestamp };
                Moves.Add(session, message);
            }
            return message;
        }

        public static List<T> GetList<T>(Dictionary<GameClientSession, List<T>> lists, GameClientSession session)
        {
            if (!lists.TryGetValue(session, out var list))
            {
                list = [];
                lists.Add(session, list);
            }
            return list;
        }
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

    public void QueueCombatHit(MatchRuntime runtime, GameClientSession? session, G_TO_C_COMBAT_HIT hit)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (session != null)
        {
            runtime.PendingCombatHits.Enqueue((session, hit));
        }
    }

    public void QueueAreaCombatHit(MatchRuntime runtime, AreaType area, G_TO_C_COMBAT_HIT hit, GameClientSession? alwaysInclude)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (alwaysInclude != null)
        {
            runtime.PendingCombatHits.Enqueue((alwaysInclude, hit));
        }

        foreach (var session in runtime.GetSessions())
        {
            if (ReferenceEquals(session, alwaysInclude) || session.IsGameEnded || !session.PlayerId.HasValue)
            {
                continue;
            }
            if (session.Player.CurrentArea == area)
            {
                runtime.PendingCombatHits.Enqueue((session, hit));
            }
        }
    }

    public void QueuePlayerHitForAttacker(MatchRuntime runtime, Player? attacker, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (attacker == null || attacker.PlayerId == 0 || targetPlayerId == 0)
        {
            return;
        }

        QueueCombatHit(runtime, attacker.Session, new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        });
    }

    public void QueueMonsterHitForAttacker(MatchRuntime runtime, Player? attacker, int monsterId, AreaType area, int weaponItemId, int damage, bool critical = false, bool showDamageOnly = false)
    {
        if (attacker == null || attacker.PlayerId == 0 || attacker.IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0)
        {
            return;
        }

        QueueCombatHit(runtime, attacker.Session, new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = monsterId,
            TargetKind = CombatEntityKind.Monster,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            IsCritical = critical,
            ShowDamageOnly = showDamageOnly
        });
    }

    public void QueueAreaPacket<T>(MatchRuntime runtime, AreaType area, Protocol protocol, T body) where T : IMessagePackObject
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }

        byte[]? serialized = null;
        foreach (var session in runtime.GetSessions())
        {
            if (session.IsGameEnded || !session.PlayerId.HasValue)
            {
                continue;
            }
            if (session.Player.CurrentArea != area)
            {
                continue;
            }

            serialized ??= MessagePackSerializer.Serialize(body);
            runtime.PendingCombatEffects.Enqueue((session, protocol, serialized));
        }
    }

    public void QueueBroadcastPacket<T>(MatchRuntime runtime, Protocol protocol, T body) where T : IMessagePackObject
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }

        byte[]? serialized = null;
        foreach (var session in runtime.GetSessions())
        {
            serialized ??= MessagePackSerializer.Serialize(body);
            runtime.PendingCombatEffects.Enqueue((session, protocol, serialized));
        }
    }

    public void QueueStatusEffect(MatchRuntime runtime, Player target, long sourcePlayerId, AreaType area, CombatStatusEffectKind effect, float seconds)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (target.Session is not { PlayerId: not null } session)
        {
            return;
        }

        byte[] serialized = MessagePackSerializer.Serialize(new G_TO_C_STATUS_EFFECT
        {
            SourcePlayerId = sourcePlayerId,
            TargetPlayerId = target.PlayerId,
            AreaType = area,
            Effect = effect,
            DurationMs = (int)(seconds * 1000f)
        });
        runtime.PendingCombatEffects.Enqueue((session, Protocol.G_TO_C_STATUS_EFFECT, serialized));
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
        CollectSunAttacks(runtime, batch, nowUtc);
        CollectWaveAttacks(runtime, batch, nowUtc);
        if (isGameplayActive && runtime.Mode != MatchMode.SoloMapValidation)
        {
            batch.OrbVisuals = MatchOrbVisual.Build(runtime, players);
            CollectOrbRankings(runtime, batch);
        }
        CollectRemovedMonsters(runtime, batch);
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
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        var openDoors = new HashSet<int>(runtime.Doors.GetOpenDoors());
        foreach (var (session, sessionArea) in batch.ObserverAreas)
        {
            if (sessionArea == AreaType.None)
            {
                continue;
            }
            if (session.PublishedInteractionArea != sessionArea || !session.PublishedOpenDoors.SetEquals(openDoors))
            {
                batch.InteractableUpdates[session] = session.GetInteractableInfos();
            }
        }
    }

    internal void CollectGroundItemEntries(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        var areaItems = new Dictionary<AreaType, List<GroundItemInfo>>();
        foreach (var (session, sessionArea) in batch.ObserverAreas)
        {
            if (sessionArea == AreaType.None)
            {
                continue;
            }
            if (!areaItems.TryGetValue(sessionArea, out var items))
            {
                items = runtime.GroundItems.GetItemsInArea(sessionArea);
                areaItems.Add(sessionArea, items);
            }
            foreach (var item in items)
            {
                if (!session.PublishedObjects.Contains((ObjectType.ITEM, item.GroundItemUid)))
                {
                    batch.GetEntries(session).Items.Add(item);
                }
            }
        }
    }

    internal void CollectPlayerUpdates(MatchRuntime runtime, Player player, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        var info = player.GameInfo.ObjectInfo;
        var playerArea = GameMapData.GetCurrentArea(info.MapId, info.Cell);
        bool stateChanged = !runtime.SynchronizedPlayerStates.TryGetValue(player.PlayerId, out var previousState) || previousState != player.State;
        GamePlayerInfo? snapshot = null;
        foreach (var (session, sessionArea) in batch.ObserverAreas)
        {
            if (sessionArea != playerArea)
            {
                continue;
            }

            bool known = session.Player.PlayerId == player.PlayerId || session.PublishedObjects.Contains((ObjectType.PLAYER, player.PlayerId));
            if (!known)
            {
                snapshot ??= player.CreatePlayerObjectInfo();
                batch.GetEntries(session).Players.Add(snapshot);
                continue;
            }
            if (stateChanged)
            {
                snapshot ??= player.CreatePlayerObjectInfo();
                SyncBatch.GetList(batch.PlayerUpdates, session).Add(snapshot);
            }
        }
        CollectMovementUpdates(runtime, info, batch, stateChanged);
    }

    internal void CollectMonsterUpdates(MatchRuntime runtime, Monster monster, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        var snapshot = monster.ToMonsterInfo();
        var monsterArea = GameMapData.GetCurrentArea(snapshot.ObjectInfo.MapId, snapshot.ObjectInfo.Cell);
        foreach (var (session, sessionArea) in batch.ObserverAreas)
        {
            if (sessionArea != monsterArea)
            {
                continue;
            }
            if (!session.PublishedObjects.Contains((ObjectType.MONSTER, monster.MonsterId)))
            {
                if (snapshot.IsAlive)
                {
                    batch.GetEntries(session).Monsters.Add(snapshot);
                }
                continue;
            }
            if (session.HasMonsterStateChanged(snapshot))
            {
                SyncBatch.GetList(batch.MonsterUpdates, session).Add(snapshot);
            }
        }
    }

    internal void CollectMovementUpdates(MatchRuntime runtime, GameObjectInfo info, SyncBatch batch, bool stateChanged = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (!stateChanged && runtime.SynchronizedObjects.TryGetValue((info.ObjectType, info.ObjectId), out var previous) && previous.Matches(info))
        {
            return;
        }

        var objectArea = GameMapData.GetCurrentArea(info.MapId, info.Cell);
        GameObjectInfo? snapshot = null;
        foreach (var (session, sessionArea) in batch.ObserverAreas)
        {
            if (sessionArea != objectArea)
            {
                continue;
            }
            snapshot ??= info.Clone();
            batch.GetMoves(session).Objects.Add(snapshot);
        }
    }

    private static void CollectSunAttacks(MatchRuntime runtime, SyncBatch batch, DateTime nowUtc)
    {
        foreach (var shape in runtime.PendingSunAttacks)
        {
            if (shape.IsPublished)
            {
                continue;
            }
            shape.IsPublished = true;
            var origin = shape.Origin;
            var end = shape.End;
            var message = new G_TO_C_SUN_ORB_ATTACK
            {
                EventId = shape.EventId,
                OwnerPlayerId = shape.OwnerId,
                WeaponItemId = shape.WeaponItemId,
                OriginX = origin.X,
                OriginY = origin.Y,
                EndX = end.X,
                EndY = end.Y,
                Width = OrbData.GetSunWidth(shape.WeaponItemId),
                TelegraphSeconds = MathF.Max(0f, (float)(shape.ArmedAtUtc - nowUtc).TotalSeconds),
                ActiveSeconds = (float)(shape.ExpiresAtUtc - shape.ArmedAtUtc).TotalSeconds,
                DetonateAtEnd = shape.DetonateAtEnd,
                OwnerOrbOrdinal = shape.OwnerOrbOrdinal
            };
            foreach (var (session, sessionArea) in batch.ObserverAreas)
            {
                if (sessionArea == shape.Area)
                {
                    SyncBatch.GetList(batch.SunAttacks, session).Add(message);
                }
            }
        }
    }

    private static void CollectWaveAttacks(MatchRuntime runtime, SyncBatch batch, DateTime nowUtc)
    {
        for (int index = 0; index < runtime.PendingWaveAttacks.Count; index++)
        {
            var vortex = runtime.PendingWaveAttacks[index];
            if (vortex.IsPublished)
            {
                continue;
            }
            runtime.PendingWaveAttacks[index] = vortex with { IsPublished = true };
            var message = new G_TO_C_WAVE_ORB_ATTACK
            {
                OwnerPlayerId = vortex.OwnerId,
                CenterX = vortex.Position.X,
                CenterY = vortex.Position.Y,
                Radius = vortex.Radius,
                FuseSeconds = MathF.Max(0f, (float)(vortex.ExplodeAtUtc - nowUtc).TotalSeconds)
            };
            foreach (var (session, sessionArea) in batch.ObserverAreas)
            {
                if (sessionArea == vortex.Area)
                {
                    SyncBatch.GetList(batch.WaveAttacks, session).Add(message);
                }
            }
        }
    }

    internal void CollectOrbRankings(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (batch.Sessions.Count == 0)
        {
            return;
        }

        var entries = new List<(long PlayerId, int OrbCount, int TierSum, int Health)>();
        foreach (var player in runtime.GetPlayers())
        {
            if (player.IsEliminated)
            {
                entries.Add((player.PlayerId, 0, 0, 0));
                continue;
            }
            var (orbCount, tierSum) = runtime.GetOrbs(player.PlayerId).GetOrbScore();
            entries.Add((player.PlayerId, orbCount, tierSum, player.Health));
        }
        if (entries.Count == 0)
        {
            return;
        }

        entries.Sort(MatchResultService.CompareOrbScore);

        var playerIds = new List<long>(entries.Count);
        var orbCounts = new List<int>(entries.Count);
        var signatureParts = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            playerIds.Add(entry.PlayerId);
            orbCounts.Add(entry.OrbCount);
            signatureParts.Add($"{entry.PlayerId}:{entry.OrbCount}");
        }

        string signature = string.Join("|", signatureParts);
        if (runtime.OrbRankingsSignature == signature)
        {
            return;
        }

        runtime.OrbRankingsSignature = signature;
        batch.OrbRankings = new G_TO_C_ORB_RANKINGS
        {
            PlayerIds = playerIds,
            OrbCounts = orbCounts
        };
    }

    private static void CollectRemovedMonsters(MatchRuntime runtime, SyncBatch batch)
    {
        foreach (var removed in runtime.PendingRemovedMonsters)
        {
            foreach (var session in batch.Sessions)
            {
                if (session.PublishedObjects.Contains((ObjectType.MONSTER, removed.MonsterId)))
                {
                    SyncBatch.GetList(batch.RemovedMonsters, session).Add(removed);
                }
            }
        }
        runtime.PendingRemovedMonsters.Clear();
    }

    internal void CollectObjectLeaves(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        var areas = new Dictionary<(ObjectType Type, long Id), AreaType>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            areas[(ObjectType.PLAYER, player.PlayerId)] = player.CurrentArea;
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            areas[(ObjectType.MONSTER, monster.MonsterId)] = monster.CurrentArea;
        }
        foreach (var session in batch.Sessions)
        {
            var sessionArea = batch.ObserverAreas.GetValueOrDefault(session, AreaType.None);
            foreach (var identity in session.PublishedObjects)
            {
                var area = identity.Type == ObjectType.ITEM ? runtime.GroundItems.GetItemArea(identity.Id) : areas.GetValueOrDefault(identity, AreaType.None);
                if (area != AreaType.None && area == sessionArea)
                {
                    continue;
                }
                SyncBatch.GetList(batch.Leaves, session).Add(new ObjectIdentity { Type = identity.Type, Id = identity.Id });
            }
        }
    }

    internal void SendBatch(MatchRuntime runtime, SyncBatch batch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot synchronize after the match has ended.");
        }
        SendPendingCombatHits(runtime);
        foreach (var (session, snapshot) in batch.InteractableUpdates)
        {
            session.SendInteractableInfos(snapshot);
        }
        // 죽은 상태가 퇴장보다 먼저 가야 클라이언트가 사망 연출을 낸다.
        foreach (var (session, removedMonsters) in batch.RemovedMonsters)
        {
            session.SendChangedMonsterStates(removedMonsters);
        }
        foreach (var (session, objects) in batch.Leaves)
        {
            session.SendObjectLeaves(objects);
        }
        foreach (var (session, entries) in batch.Entries)
        {
            session.SendObjectEntries(entries);
        }
        if (batch.OrbVisuals.Count > 0)
        {
            foreach (var session in batch.Sessions)
            {
                if (!session.IsGameEnded)
                {
                    session.SendOrbVisualStates(batch.OrbVisuals);
                }
            }
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
        foreach (var (session, attacks) in batch.SunAttacks)
        {
            foreach (var attack in attacks)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
                packet.SetBody(MessagePackSerializer.Serialize(attack));
                session.TrySend(packet);
            }
        }
        foreach (var (session, attacks) in batch.WaveAttacks)
        {
            foreach (var attack in attacks)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_WAVE_ORB_ATTACK);
                packet.SetBody(MessagePackSerializer.Serialize(attack));
                session.TrySend(packet);
            }
        }
        if (batch.OrbRankings != null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RANKINGS);
            packet.SetBody(MessagePackSerializer.Serialize(batch.OrbRankings));
            foreach (var session in batch.Sessions)
            {
                if (!session.IsGameEnded)
                {
                    session.TrySend(packet);
                }
            }
        }
        SendPendingCombatEffects(runtime);
    }

    internal static void SendPendingCombatHits(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        while (runtime.PendingCombatHits.TryDequeue(out var notification))
        {
            using var packet = PacketMaker.G_TO_C_COMBAT_HIT(notification.Hit);
            notification.Session.TrySend(packet);
        }
    }

    internal static void SendPendingCombatEffects(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Synchronization requires the match lock.");
        }
        while (runtime.PendingCombatEffects.TryDequeue(out var effect))
        {
            using var packet = Packet.Create((int)effect.Protocol);
            packet.SetBody(effect.Body);
            effect.Session.TrySend(packet);
        }
    }
}
