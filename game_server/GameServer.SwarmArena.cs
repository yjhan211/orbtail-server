using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

public partial class GameServer
{
    private const int SwarmArenaBasicDamage = 8;
    private const float SwarmArenaBasicRange = 7f;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;

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

        if (!_swarmArenaManager.HasMatching(matchingId))
        {
            Cell startCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground);
            if (!_swarmArenaManager.InitializeMatching(
                    matchingId, player.PlayerId!.Value, AreaType.Ground, startCell, DateTime.UtcNow))
                return;

            player.PlaceAtSpotArenaStart(AreaType.Ground, startCell);
            player.GrantSwarmArenaOrb(SwarmArenaWeaponItemId);
            logger.LogInformation(
                "Swarm arena initialized: MatchingId={MatchingId}, PlayerId={PlayerId}",
                matchingId, player.PlayerId);
        }

        DateTime nowUtc = DateTime.UtcNow;
        var tick = _swarmArenaManager.Tick(matchingId, player.LastValidatedPosition, nowUtc);

        foreach (var damage in tick.PlayerDamage)
        {
            if (player.ApplySpotArenaMonsterHit(damage.MonsterId, damage.Damage))
                _swarmArenaManager.EndForDeath(matchingId, nowUtc);
        }

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterMinimapSnapshot(sessions, _swarmArenaManager.GetVisualStates(matchingId));

        var actors = BuildSwarmArenaCombatActors(matchingId, player);
        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            (attacker, target) => !attacker.IsMonsterTarget &&
                                  target.IsMonsterTarget &&
                                  attacker.Area == target.Area);
        foreach (var attack in attacks)
        {
            var damageResult = _swarmArenaManager.ApplyMonsterDamage(
                matchingId, attack.TargetPlayerId, attack.Damage);
            if (!damageResult.Applied)
                continue;

            player.SendEmotionAfterimageMonsterAttackFeedback(
                damageResult.MonsterId, attack.Area, attack.WeaponItemId, attack.Damage);
            if (damageResult.Killed && damageResult.MonsterState != null)
                SpawnSpotArenaSummonStone(matchingId, damageResult.MonsterState, sessions);
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
    }

    private List<ProximityCombatActor> BuildSwarmArenaCombatActors(long matchingId, GameClientSession player)
    {
        var actors = new List<ProximityCombatActor>();
        if (player.PlayerId.HasValue &&
            player.LastValidatedPosition != null &&
            TryCreateSpatialActor(
                player.PlayerId.Value,
                player.CurrentMapId,
                player.CurrentArea,
                player.LastValidatedPosition,
                out var spatial))
        {
            actors.Add(spatial with
            {
                WeaponItemId = SwarmArenaWeaponItemId,
                AttackRange = SwarmArenaBasicRange,
                Damage = SwarmArenaBasicDamage,
                AttackIntervalSeconds = SwarmArenaBasicAttackIntervalSeconds,
                WeaponItemUid = player.PlayerId.Value,
                TargetPriority = 0
            });
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
}
