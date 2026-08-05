using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

public partial class GameServer
{

    private const int SpotArenaBasicDamage = 8;
    private const float SpotArenaBasicRange = 7f;
    private const float SpotArenaBasicAttackIntervalSeconds = 1f;

    private void ProcessSpotArenaForMatching(long matchingId, List<GameClientSession> activeSessions)
    {
        var sessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue &&
                              session.CurrentMapSubId == matchingId &&
                              !session.IsGameEnded)
            .ToList();
        var bots = _botPlayerManager.GetBots(matchingId).ToList();

        if (!_spotArenaManager.HasMatching(matchingId))
        {
            if (!TryInitializeSpotArena(matchingId, sessions, bots))
                return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        ProcessBotOrbSummons(matchingId);
        var spatialPlayers = sessions
            .Where(session => session.PlayerId.HasValue && session.LastValidatedPosition != null)
            .Select(session => new SpotArenaPlayerSpatial(
                session.PlayerId!.Value,
                session.CurrentArea,
                session.LastValidatedPosition!))
            .Concat(bots.Select(bot => new SpotArenaPlayerSpatial(
                bot.PlayerId,
                bot.CurrentArea,
                bot.Position)))
            .ToArray();

        var tick = _spotArenaManager.Tick(matchingId, spatialPlayers, nowUtc);
        ApplySpotArenaWaveDamage(matchingId, tick.PlayerDamage, sessions, bots);
        ApplySpotArenaRespawns(tick.RespawnedPlayers, sessions, bots);
        ApplySpotArenaReconnections(tick.Reconnections, sessions, bots);
        ApplySpotArenaDestroyedSpots(tick.DestroyedSpots, sessions, bots);

        var resonanceStates = UpdateSurvivorOrbResonanceStates(matchingId, sessions, bots, nowUtc);
        var actors = BuildSpotArenaCombatActors(matchingId, sessions, bots, resonanceStates);
        ProcessSurvivorOrbRecovery(matchingId, actors, sessions, bots, nowUtc);
        BroadcastSurvivorOrbVisualStates(matchingId, actors, sessions);
        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            (attacker, target) => CanResolveSpotArenaTarget(matchingId, attacker, target));

        foreach (var attack in attacks)
            ApplySpotArenaAttack(matchingId, attack, sessions, bots, nowUtc);

        SynchronizeSpotArenaParticipants(matchingId, sessions, bots);

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
        {
            // Spot Arena monsters cross room boundaries. Every participant must receive
            // every area chunk so a monster is removed from its previous room and appears
            // in its destination room on the same snapshot tick.
            BroadcastMonsterMinimapSnapshot(sessions, _spotArenaManager.GetVisualStates(matchingId));
        }

        BroadcastSpotArenaState(matchingId, sessions);

        var snapshot = _spotArenaManager.GetSnapshot(matchingId);
        if (!snapshot.Ended)
            return;

        var resultSender = sessions.FirstOrDefault();
        if (resultSender != null)
        {
            string endReason = snapshot.RemainingSeconds <= 0 ? "spot_timeout" : "spot_last_alive";
            resultSender.SendSpotArenaGameResult(sessions, snapshot.WinnerPlayerId, endReason);
        }

        _spotArenaManager.RemoveMatching(matchingId);
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
    }

    private bool TryInitializeSpotArena(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        var participants = sessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => new SpotArenaParticipant(
                session.PlayerId!.Value,
                session.CurrentArea,
                session.LastValidatedPosition == null
                    ? GameMapData.GetAreaSpawnCell(MapId.School, session.CurrentArea)
                    : ProximityCombatLineOfSight.WorldPositionToCell(
                        MapId.School, session.LastValidatedPosition)))
            .Concat(bots.Select(bot => new SpotArenaParticipant(
                bot.PlayerId,
                bot.CurrentArea,
                Cell.Clone(bot.Cell))))
            .Where(participant => participant.Area != AreaType.None)
            .ToList();

        if (participants.Count != 4)
            return false;

        // 사람을 항상 첫 좌석(SR3, 공격 왕복 ~13.8초)에 고정한다. 좌석마다 체인 경로 길이가
        // 2.1~6.9초로 비대칭이라, 자리를 판마다 섞으면 "동일 조건 5판" 검증이 오염된다.
        var botPlayerIds = bots.Select(bot => bot.PlayerId).ToHashSet();
        participants = participants
            .OrderBy(participant => botPlayerIds.Contains(participant.PlayerId) ? 1 : 0)
            .ThenBy(participant => participant.PlayerId)
            .ToList();

        var registrations = new List<SpotArenaPlayerRegistration>(participants.Count);
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            long targetPlayerId = participants[(index + 1) % participants.Count].PlayerId;
            registrations.Add(new SpotArenaPlayerRegistration(
                participant.PlayerId,
                targetPlayerId,
                participant.Area,
                participant.Cell));

            sessions.FirstOrDefault(session => session.PlayerId == participant.PlayerId)
                ?.SetSpotArenaTarget(targetPlayerId);
            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == participant.PlayerId);
            if (bot != null)
                bot.TargetPlayerId = targetPlayerId;
        }

        DateTime startsAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId) ?? DateTime.UtcNow;
        bool initialized = _spotArenaManager.InitializeMatching(matchingId, registrations, startsAtUtc);
        if (initialized)
        {
            PlaceSpotArenaParticipantsAtOwnSpots(matchingId, sessions, bots);
            logger.LogInformation(
                "Spot arena initialized: MatchingId={MatchingId}, Players={Players}",
                matchingId,
                string.Join(",", registrations.Select(item =>
                    $"{item.PlayerId}->{item.TargetPlayerId}@{item.Area}")));
        }

        return initialized;
    }

    private void PlaceSpotArenaParticipantsAtOwnSpots(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        var snapshot = _spotArenaManager.GetSnapshot(matchingId);
        foreach (var spot in snapshot.Spots)
        {
            int anchorNumber = SurvivorRoyaleSpawnData.GetAnchorIndex(spot.Cell);
            Cell spawnCell = anchorNumber > 0
                ? SurvivorRoyaleSpawnData.GetCorridorSpawnCell(anchorNumber)
                : Cell.Clone(spot.Cell);

            sessions.FirstOrDefault(session => session.PlayerId == spot.OwnerPlayerId)
                ?.PlaceAtSpotArenaStart(spot.Area, spawnCell);

            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == spot.OwnerPlayerId);
            if (bot == null)
                continue;

            bot.CurrentArea = spot.Area;
            bot.Cell = Cell.Clone(spawnCell);
            bot.Position = BotPlayerManager.CellToWorldPosition(MapId.School, spawnCell);
            bot.Path.Clear();
            bot.PathIndex = 0;
        }
    }

    private List<ProximityCombatActor> BuildSpotArenaCombatActors(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots,
        IReadOnlyDictionary<long, SurvivorOrbResonanceSnapshot> resonanceStates)
    {
        var actors = new List<ProximityCombatActor>();

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue ||
                session.IsEliminated ||
                session.LastValidatedPosition == null ||
                _spotArenaManager.IsRespawning(matchingId, session.PlayerId.Value) ||
                _spotArenaManager.IsInvulnerable(matchingId, session.PlayerId.Value) ||
                !TryCreateSpatialActor(
                    session.PlayerId.Value,
                    session.CurrentMapId,
                    session.CurrentArea,
                    session.LastValidatedPosition,
                    out var spatial))
            {
                continue;
            }

            AddSpotArenaPlayerCombatActors(
                actors,
                matchingId,
                spatial,
                session.PlayerId.Value,
                resonanceStates.GetValueOrDefault(session.PlayerId.Value));
        }

        MapId botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in bots)
        {
            if (bot.IsEliminated ||
                _spotArenaManager.IsRespawning(matchingId, bot.PlayerId) ||
                _spotArenaManager.IsInvulnerable(matchingId, bot.PlayerId) ||
                !TryCreateSpatialActor(
                    bot.PlayerId,
                    botMapId,
                    bot.CurrentArea,
                    bot.Position,
                    out var spatial))
            {
                continue;
            }

            AddSpotArenaPlayerCombatActors(
                actors,
                matchingId,
                spatial,
                bot.PlayerId,
                resonanceStates.GetValueOrDefault(bot.PlayerId));
        }

        foreach (var target in _spotArenaManager.GetCombatTargets(matchingId))
        {
            int priority = target.Kind == SpotArenaCombatTargetKind.Wave ? 1 : 2;
            actors.Add(new ProximityCombatActor(
                target.CombatTargetId,
                target.Area,
                target.Position,
                0,
                0f,
                0,
                0f,
                MapId: MapId.School,
                Cell: ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, target.Position),
                IsMonsterTarget: true,
                IsCoreMonsterTarget: target.Kind == SpotArenaCombatTargetKind.Spot,
                TargetPriority: priority));
        }

        return actors;
    }

    private void AddSpotArenaPlayerCombatActors(
        ICollection<ProximityCombatActor> actors,
        long matchingId,
        ProximityCombatActor spatial,
        long playerId,
        SurvivorOrbResonanceSnapshot resonanceState)
    {
        var fallback = CreateSpotArenaPlayerActor(matchingId, spatial);
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        if (!inventory.GetAllItems().Any(item => item.Count > 0))
        {
            actors.Add(fallback);
            return;
        }

        AddInventoryCombatActors(
            actors,
            fallback with
            {
                AttackRange = 0f,
                Damage = 0,
                AttackIntervalSeconds = 0f
            },
            inventory,
            resonanceState);
    }

    private ProximityCombatActor CreateSpotArenaPlayerActor(long matchingId, ProximityCombatActor spatial) =>
        spatial with
        {
            WeaponItemId = _spotArenaManager.TryGetPlayerOrbItemId(
                matchingId, spatial.PlayerId, out int itemId) ? itemId : 107000010,
            AttackRange = SpotArenaBasicRange,
            Damage = SpotArenaBasicDamage,
            AttackIntervalSeconds = SpotArenaBasicAttackIntervalSeconds,
            WeaponItemUid = spatial.PlayerId,
            TargetPriority = 0
        };

    private bool CanResolveSpotArenaTarget(
        long matchingId,
        ProximityCombatActor attacker,
        ProximityCombatActor target)
    {
        if (attacker.IsMonsterTarget || attacker.Damage <= 0 ||
            _spotArenaManager.IsRespawning(matchingId, attacker.PlayerId) ||
            _spotArenaManager.IsInvulnerable(matchingId, attacker.PlayerId))
        {
            return false;
        }

        if (_spotArenaManager.TryGetWaveByCombatTarget(matchingId, target.PlayerId, out var wave))
        {
            return attacker.Area == wave.Area &&
                   _spotArenaManager.CanPlayerAttackWave(
                       matchingId,
                       attacker.PlayerId,
                       wave.MonsterId,
                       wave.OwnerPlayerId,
                       wave.TargetOwnerPlayerId);
        }

        // 스팟 피해는 전선을 밀어붙인 웨이브만 만든다. 플레이어는 스팟을 직접 치지 못한다.
        if (_spotArenaManager.TryGetSpotTarget(matchingId, target.PlayerId, out _))
            return false;

        return !_spotArenaManager.IsRespawning(matchingId, target.PlayerId) &&
               !_spotArenaManager.IsInvulnerable(matchingId, target.PlayerId) &&
               _spotArenaManager.CanPlayersFight(matchingId, attacker.PlayerId, target.PlayerId) &&
               ProximityCombatLineOfSight.CanTarget(attacker, target);
    }

    private void ApplySpotArenaAttack(
        long matchingId,
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots,
        DateTime nowUtc)
    {
        if (_spotArenaManager.TryGetWaveByCombatTarget(matchingId, attack.TargetPlayerId, out var wave))
        {
            var damageResult = _spotArenaManager.ApplyWaveDamage(
                matchingId, wave.MonsterId, attack.AttackerPlayerId, attack.Damage, nowUtc);
            if (damageResult.DestroyedOrKilled && damageResult.WaveState != null)
                SpawnSpotArenaSummonStone(matchingId, damageResult.WaveState, sessions);
            sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                ?.SendEmotionAfterimageMonsterAttackFeedback(
                    wave.MonsterId, attack.Area, attack.WeaponItemId, attack.Damage);
            return;
        }

        if (_spotArenaManager.TryGetSpotTarget(matchingId, attack.TargetPlayerId, out var spot))
        {
            _spotArenaManager.ApplySpotDamage(
                matchingId, spot.CombatTargetId, attack.AttackerPlayerId, attack.Damage);
            sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                ?.SendEmotionAfterimageMonsterAttackFeedback(
                    spot.MonsterId, attack.Area, attack.WeaponItemId, attack.Damage);
            return;
        }

        var targetSession = sessions.FirstOrDefault(session =>
            session.PlayerId == attack.TargetPlayerId && !session.IsEliminated);
        if (targetSession != null)
        {
            if (targetSession.ApplySpotArenaCombatHit(
                    attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, attack.Damage))
            {
                _spotArenaManager.BeginRespawn(matchingId, attack.TargetPlayerId, nowUtc);
            }
        }
        else
        {
            var targetBot = bots.FirstOrDefault(bot =>
                bot.PlayerId == attack.TargetPlayerId && !bot.IsEliminated);
            if (targetBot != null && !_spotArenaManager.IsInvulnerable(matchingId, targetBot.PlayerId))
            {
                targetBot.Corruption = Math.Min(
                    Config.SURVIVOR_MAX_CORRUPTION,
                    targetBot.Corruption + attack.Damage);
                if (targetBot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION)
                    _spotArenaManager.BeginRespawn(matchingId, targetBot.PlayerId, nowUtc);
            }
        }

        sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
            ?.SendProximityAutoCombatAttackFeedback(
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, attack.Damage);
        BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, sessions);
    }

    private void SpawnSpotArenaSummonStone(
        long matchingId,
        MonsterRuntimeInfo defeatedWave,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        if (defeatedWave.SummonStoneReward <= 0)
            return;

        var itemIds = Enumerable.Repeat(
            Config.SUMMON_STONE_GROUND_ITEM_ID,
            defeatedWave.SummonStoneReward).ToArray();
        var spawned = _groundItemManager.SpawnItems(
            matchingId,
            defeatedWave.AreaType,
            defeatedWave.PositionX,
            defeatedWave.PositionY,
            itemIds,
            mapId: MapId.School,
            layout: GroundItemSpawnLayout.EliminationScatter);

        foreach (var item in spawned)
        {
            _gameEventLogManager.LogGroundItemSpawned(
                matchingId,
                0,
                item.GroundItemUid,
                item.ItemId,
                defeatedWave.AreaType.ToString(),
                0,
                isBot: false);
        }

        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)defeatedWave.AreaType);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
            (int)defeatedWave.AreaType,
            remaining,
            spawned);
        foreach (var session in sessions.Where(session => session.CurrentArea == defeatedWave.AreaType))
            session.Send(packet);
    }

    private static void BroadcastSpotArenaAttackVfxToTargetAndObservers(
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        foreach (var observer in sessions)
        {
            if (!observer.PlayerId.HasValue || observer.IsEliminated ||
                observer.PlayerId.Value == attack.AttackerPlayerId ||
                observer.CurrentArea != attack.Area)
            {
                continue;
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.Send(packet);
        }
    }

    private void ApplySpotArenaWaveDamage(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerDamage> damageEvents,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        foreach (var damageEvent in damageEvents)
        {
            var session = sessions.FirstOrDefault(candidate =>
                candidate.PlayerId == damageEvent.TargetPlayerId && !candidate.IsEliminated);
            if (session != null)
            {
                if (session.ApplySpotArenaMonsterHit(damageEvent.MonsterId, damageEvent.Damage))
                    _spotArenaManager.BeginRespawn(matchingId, damageEvent.TargetPlayerId);
                continue;
            }

            var bot = bots.FirstOrDefault(candidate =>
                candidate.PlayerId == damageEvent.TargetPlayerId && !candidate.IsEliminated);
            if (bot == null)
                continue;
            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + damageEvent.Damage);
            if (bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION)
                _spotArenaManager.BeginRespawn(matchingId, bot.PlayerId);
        }
    }

    private static void ApplySpotArenaRespawns(
        IReadOnlyCollection<SpotArenaRespawnEvent> respawns,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        foreach (var respawn in respawns)
        {
            sessions.FirstOrDefault(session => session.PlayerId == respawn.PlayerId)
                ?.CompleteSpotArenaRespawn(respawn.Area, respawn.Cell);

            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == respawn.PlayerId);
            if (bot == null)
                continue;
            bot.IsEliminated = false;
            bot.ManittoStatus = ManittoStatus.ACTIVE;
            bot.Corruption = 0;
            bot.Stamina = 100;
            bot.CurrentArea = respawn.Area;
            bot.Cell = Cell.Clone(respawn.Cell);
            bot.Position = BotPlayerManager.CellToWorldPosition(MapId.School, respawn.Cell);
            bot.Path.Clear();
            bot.PathIndex = 0;
        }
    }

    private static void ApplySpotArenaReconnections(
        IReadOnlyCollection<SpotArenaReconnectEvent> reconnections,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        foreach (var reconnect in reconnections)
        {
            sessions.FirstOrDefault(session => session.PlayerId == reconnect.PredatorPlayerId)
                ?.SetSpotArenaTarget(reconnect.NewTargetPlayerId);
            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == reconnect.PredatorPlayerId);
            if (bot != null)
                bot.TargetPlayerId = reconnect.NewTargetPlayerId;
        }
    }

    private void SynchronizeSpotArenaParticipants(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        var snapshot = _spotArenaManager.GetSnapshot(matchingId);
        foreach (var spot in snapshot.Spots)
        {
            var session = sessions.FirstOrDefault(candidate => candidate.PlayerId == spot.OwnerPlayerId);
            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == spot.OwnerPlayerId);
            if (spot.Destroyed)
            {
                session?.EnterSpotArenaSpectatorMode();
                if (bot != null && !bot.IsEliminated)
                {
                    bot.IsEliminated = true;
                    bot.ManittoStatus = ManittoStatus.SPECTATING;
                    bot.Path.Clear();
                    bot.PathIndex = 0;
                }
                continue;
            }

            session?.SetSpotArenaTarget(spot.TargetPlayerId);
            if (bot != null)
                bot.TargetPlayerId = spot.TargetPlayerId;
        }
    }

    private static void ApplySpotArenaDestroyedSpots(
        IReadOnlyCollection<SpotArenaSpotDestroyedEvent> destroyed,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots)
    {
        foreach (var destroyedSpot in destroyed)
        {
            sessions.FirstOrDefault(session => session.PlayerId == destroyedSpot.OwnerPlayerId)
                ?.EnterSpotArenaSpectatorMode();
            var bot = bots.FirstOrDefault(candidate => candidate.PlayerId == destroyedSpot.OwnerPlayerId);
            if (bot != null)
            {
                bot.IsEliminated = true;
                bot.ManittoStatus = ManittoStatus.SPECTATING;
                bot.Path.Clear();
                bot.PathIndex = 0;
            }
        }
    }

    private void BroadcastSpotArenaState(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        var snapshot = _spotArenaManager.GetSnapshot(matchingId);
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue)
                continue;

            long playerId = session.PlayerId.Value;
            var own = snapshot.Spots.FirstOrDefault(spot => spot.OwnerPlayerId == playerId);
            var prey = snapshot.Spots.FirstOrDefault(spot => spot.OwnerPlayerId == own.TargetPlayerId);
            var predator = snapshot.Spots.FirstOrDefault(spot =>
                !spot.Destroyed && spot.TargetPlayerId == playerId);
            int incoming = snapshot.Waves.Count(wave =>
                wave.Alive && wave.TargetOwnerPlayerId == playerId);
            snapshot.RespawnSecondsByPlayer.TryGetValue(playerId, out int respawnSeconds);

            using var packet = Packet.Create((int)Protocol.G_TO_C_ROUND_STATE);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ROUND_STATE
            {
                MatchingId = matchingId,
                RoundNumber = own.Health,
                TotalRounds = own.MaxHealth,
                Phase = RoundPhase.SpotArena,
                RemainingSeconds = snapshot.RemainingSeconds,
                PhaseDurationSeconds = prey.Health,
                ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsSessionEnded = snapshot.Ended,
                SurvivorNextRoomAreaTypes =
                [
                    (int)own.Area,
                    (int)prey.Area,
                    (int)predator.Area
                ],
                SurvivorNextRoomOccupancies =
                [
                    prey.MaxHealth,
                    incoming,
                    respawnSeconds,
                    snapshot.Spots.Count(spot => !spot.Destroyed),
                    EncodeSpotArenaPlayerId(own.TargetPlayerId),
                    EncodeSpotArenaPlayerId(predator.OwnerPlayerId)
                ]
            }));
            session.Send(packet);
        }
    }

    private static int EncodeSpotArenaPlayerId(long playerId) =>
        playerId is >= int.MinValue and <= int.MaxValue ? (int)playerId : 0;
    private readonly record struct SpotArenaParticipant(
        long PlayerId,
        AreaType Area,
        Cell Cell);
}
