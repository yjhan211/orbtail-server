using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

public partial class GameServer
{
    // 유리 떼 원킬 기준(#214 "무섭지만 녹는다"). 8이면 2타라 처치율이 스폰율을 못 따라가
    // 스웜이 계속 누적된다 — 실측 18초 판에서 처치 7 / 스폰 18.
    private const int SwarmArenaBasicDamage = 12;
    private const float SwarmArenaBasicRange = 7f;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;
    private const int SwarmBotRespawnSeconds = 5;
    private const float SwarmBotSpawnOffset = 4f;

    // P0-b A/B: A안 = 1.0 (이동 무관), B안 = 0.4 (이동 중 공격 감쇠, Archero 문법).
    private const float SwarmMovingAttackMultiplier = 1f;
    private const float SwarmMovingSpeedThreshold = 1.5f;

    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotRespawnAtUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), (Vector3f Position, DateTime At, bool Moving)>
        _swarmMovementSamples = new();

    private void ProcessSwarmArenaForMatching(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue &&
                              session.CurrentMapSubId == matchingId &&
                              !session.IsGameEnded)
            .ToList();
        if (sessions.Count == 0)
            return;
        var player = sessions[0];
        var bots = _botPlayerManager.GetBots(matchingId).ToList();

        if (!_swarmArenaManager.HasMatching(matchingId))
        {
            Cell startCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground);
            if (!_swarmArenaManager.InitializeMatching(
                    matchingId, player.PlayerId!.Value, AreaType.Ground, startCell, DateTime.UtcNow))
                return;

            player.PlaceAtSpotArenaStart(AreaType.Ground, startCell);
            player.GrantSwarmArenaOrb(SwarmArenaWeaponItemId);
            for (int index = 0; index < bots.Count; index++)
                PlaceSwarmBot(bots[index], startCell, index);
            logger.LogInformation(
                "Swarm arena initialized: MatchingId={MatchingId}, PlayerId={PlayerId}, Bots={BotCount}",
                matchingId, player.PlayerId, bots.Count);
        }

        DateTime nowUtc = DateTime.UtcNow;
        ProcessSwarmBotRespawns(matchingId, bots, nowUtc);

        var participants = new List<SpotArenaPlayerSpatial>();
        if (player.LastValidatedPosition != null && !player.IsEliminated)
        {
            participants.Add(new SpotArenaPlayerSpatial(
                player.PlayerId!.Value, player.CurrentArea, player.LastValidatedPosition));
        }

        participants.AddRange(bots
            .Where(bot => !bot.IsEliminated)
            .Select(bot => new SpotArenaPlayerSpatial(bot.PlayerId, bot.CurrentArea, bot.Position)));

        var tick = _swarmArenaManager.Tick(matchingId, participants, nowUtc);

        foreach (var damage in tick.PlayerDamage)
            ApplySwarmParticipantDamage(matchingId, damage.MonsterId, damage.TargetPlayerId, damage.Damage,
                player, bots, nowUtc);

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterMinimapSnapshot(sessions, _swarmArenaManager.GetVisualStates(matchingId));

        UpdateSwarmMovementSamples(matchingId, participants, nowUtc);
        var actors = BuildSwarmArenaCombatActors(matchingId, player, bots);
        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            (attacker, target) => !attacker.IsMonsterTarget &&
                                  (target.IsMonsterTarget
                                      ? attacker.Area == target.Area
                                      : ProximityCombatLineOfSight.CanTarget(attacker, target)));
        foreach (var attack in attacks)
        {
            if (_swarmArenaManager.TryGetEndState(matchingId, out _))
                break;

            var damageResult = _swarmArenaManager.ApplyMonsterDamage(
                matchingId, attack.TargetPlayerId, attack.AttackerPlayerId, attack.Damage);
            if (damageResult.Applied)
            {
                sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                    ?.SendEmotionAfterimageMonsterAttackFeedback(
                        damageResult.MonsterId, attack.Area, attack.WeaponItemId, attack.Damage);
                if (damageResult.Killed && damageResult.MonsterState != null)
                    SpawnSpotArenaSummonStone(matchingId, damageResult.MonsterState, sessions);
                continue;
            }

            // PvP는 저데미지 보조다. 킬의 주 경로는 스웜이어야 한다 (#217 P0-b 결합 원칙).
            ApplySwarmPvpAttack(matchingId, attack, player, bots, sessions, nowUtc);
        }

        if (!_swarmArenaManager.TryGetEndState(matchingId, out bool survived))
            return;

        var summary = _swarmArenaManager.GetSummary(matchingId);
        logger.LogInformation(
            "Swarm arena ended: MatchingId={MatchingId}, Survived={Survived}, " +
            "SurvivalSeconds={SurvivalSeconds:F1}, HitsTaken={HitsTaken}, Kills={Kills}, PatternHits={PatternHits}",
            matchingId,
            summary.Survived,
            summary.SurvivalSeconds,
            summary.HitsTaken,
            summary.Kills,
            string.Join(",", summary.PatternHits.Select(pair => $"{pair.Key}:{pair.Value}")));

        player.SendSpotArenaGameResult(
            sessions,
            survived ? player.PlayerId!.Value : 0,
            survived ? "swarm_survived" : "swarm_dead");
        _swarmArenaManager.RemoveMatching(matchingId);
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
        CleanupSwarmArenaState(matchingId);
    }

    private void PlaceSwarmBot(BotPlayerState bot, Cell centerCell, int index)
    {
        var center = BotPlayerManager.CellToWorldPosition(MapId.School, centerCell);
        float angle = index * 2.1f + 0.8f;
        var position = new Vector3f(
            center.X + MathF.Cos(angle) * SwarmBotSpawnOffset,
            center.Y + MathF.Sin(angle) * SwarmBotSpawnOffset,
            0f);
        Cell cell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, position);
        if (!GameMapData.IsMoveablePosition(MapId.School, cell))
        {
            cell = Cell.Clone(centerCell);
            position = center;
        }

        bot.IsEliminated = false;
        bot.ManittoStatus = ManittoStatus.ACTIVE;
        bot.Corruption = 0;
        bot.Stamina = 100;
        bot.CurrentArea = AreaType.Ground;
        bot.Cell = cell;
        bot.Position = position;
        bot.Path.Clear();
        bot.PathIndex = 0;
    }

    private void ProcessSwarmBotRespawns(long matchingId, List<BotPlayerState> bots, DateTime nowUtc)
    {
        foreach (var bot in bots)
        {
            if (!_swarmBotRespawnAtUtc.TryGetValue((matchingId, bot.PlayerId), out var respawnAt) ||
                nowUtc < respawnAt)
                continue;

            _swarmBotRespawnAtUtc.Remove((matchingId, bot.PlayerId));
            PlaceSwarmBot(bot, GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground), bot.GetHashCode() & 3);
            logger.LogInformation(
                "Swarm bot respawned: MatchingId={MatchingId}, BotId={BotId}", matchingId, bot.PlayerId);
        }
    }

    private void ApplySwarmParticipantDamage(
        long matchingId,
        int sourceMonsterId,
        long targetPlayerId,
        int damage,
        GameClientSession player,
        List<BotPlayerState> bots,
        DateTime nowUtc)
    {
        if (player.PlayerId == targetPlayerId)
        {
            if (player.ApplySpotArenaMonsterHit(sourceMonsterId, damage))
                _swarmArenaManager.EndForDeath(matchingId, nowUtc);
            return;
        }

        var bot = bots.FirstOrDefault(candidate =>
            candidate.PlayerId == targetPlayerId && !candidate.IsEliminated);
        if (bot == null)
            return;

        bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + damage);
        if (bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION)
            DownSwarmBot(matchingId, bot, nowUtc);
    }

    private void ApplySwarmPvpAttack(
        long matchingId,
        ProximityCombatAttack attack,
        GameClientSession player,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions,
        DateTime nowUtc)
    {
        int damage = Math.Min(attack.Damage, SwarmArenaManager.PvpDamage);
        if (player.PlayerId == attack.TargetPlayerId)
        {
            if (player.ApplySpotArenaCombatHit(
                    attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, damage))
                _swarmArenaManager.EndForDeath(matchingId, nowUtc);
        }
        else
        {
            var bot = bots.FirstOrDefault(candidate =>
                candidate.PlayerId == attack.TargetPlayerId && !candidate.IsEliminated);
            if (bot == null)
                return;

            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + damage);
            if (bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION)
                DownSwarmBot(matchingId, bot, nowUtc);
        }

        sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
            ?.SendProximityAutoCombatAttackFeedback(
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, damage);
        BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, sessions);
    }

    private void DownSwarmBot(long matchingId, BotPlayerState bot, DateTime nowUtc)
    {
        bot.IsEliminated = true;
        bot.ManittoStatus = ManittoStatus.SPECTATING;
        bot.Path.Clear();
        bot.PathIndex = 0;
        _swarmBotRespawnAtUtc[(matchingId, bot.PlayerId)] = nowUtc.AddSeconds(SwarmBotRespawnSeconds);
        logger.LogInformation(
            "Swarm bot downed: MatchingId={MatchingId}, BotId={BotId}", matchingId, bot.PlayerId);
    }

    private void UpdateSwarmMovementSamples(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerSpatial> participants,
        DateTime nowUtc)
    {
        foreach (var participant in participants)
        {
            var key = (matchingId, participant.PlayerId);
            if (!_swarmMovementSamples.TryGetValue(key, out var sample))
            {
                _swarmMovementSamples[key] = (participant.Position, nowUtc, false);
                continue;
            }

            double elapsed = (nowUtc - sample.At).TotalSeconds;
            if (elapsed < 0.1d)
                continue;

            float dx = participant.Position.X - sample.Position.X;
            float dy = participant.Position.Y - sample.Position.Y;
            float speed = MathF.Sqrt(dx * dx + dy * dy) / (float)elapsed;
            _swarmMovementSamples[key] =
                (participant.Position, nowUtc, speed >= SwarmMovingSpeedThreshold);
        }
    }

    private bool IsSwarmParticipantMoving(long matchingId, long playerId) =>
        _swarmMovementSamples.TryGetValue((matchingId, playerId), out var sample) && sample.Moving;

    private void CleanupSwarmArenaState(long matchingId)
    {
        foreach (var key in _swarmBotRespawnAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotRespawnAtUtc.Remove(key);
        foreach (var key in _swarmMovementSamples.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmMovementSamples.Remove(key);
    }

    private List<ProximityCombatActor> BuildSwarmArenaCombatActors(
        long matchingId,
        GameClientSession player,
        List<BotPlayerState> bots)
    {
        var actors = new List<ProximityCombatActor>();
        if (player.PlayerId.HasValue &&
            player.LastValidatedPosition != null &&
            !player.IsEliminated &&
            TryCreateSpatialActor(
                player.PlayerId.Value,
                player.CurrentMapId,
                player.CurrentArea,
                player.LastValidatedPosition,
                out var playerSpatial))
        {
            actors.Add(CreateSwarmParticipantActor(matchingId, playerSpatial));
        }

        MapId botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in bots.Where(candidate => !candidate.IsEliminated))
        {
            if (TryCreateSpatialActor(bot.PlayerId, botMapId, bot.CurrentArea, bot.Position, out var botSpatial))
                actors.Add(CreateSwarmParticipantActor(matchingId, botSpatial));
        }

        foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
        {
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
                TargetPriority: 1));
        }

        return actors;
    }

    private ProximityCombatActor CreateSwarmParticipantActor(long matchingId, ProximityCombatActor spatial)
    {
        int damage = SwarmArenaBasicDamage;
        if (IsSwarmParticipantMoving(matchingId, spatial.PlayerId))
            damage = Math.Max(1, (int)(damage * SwarmMovingAttackMultiplier));

        return spatial with
        {
            WeaponItemId = SwarmArenaWeaponItemId,
            AttackRange = SwarmArenaBasicRange,
            Damage = damage,
            AttackIntervalSeconds = SwarmArenaBasicAttackIntervalSeconds,
            WeaponItemUid = spatial.PlayerId,
            TargetPriority = 0
        };
    }
}
