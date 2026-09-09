using game_server.matches.bots;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches.orbs;
/// <summary>
///     오브별 회복 시각을 확인하고 같은 틱의 회복량을 플레이어별로 합산해 적용한다.
///     회복 시계는 매치의 Presentation에 보관하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class OrbRecoveryService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    ILogger<OrbRecoveryService> logger)
{
    public void Process(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        DateTime nowUtc)
    {
        if (matchRuntimes.GetOrNull(matchingId)?.Presentation is not { } presentation)
            return;
        var recoveryTimes = presentation.OrbRecoveryReadyAtUtc;
        var activeRecoveryKeys = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();
        var dueRecoveryByPlayer = new Dictionary<long, List<(ProximityCombatActor Actor, int Amount)>>();

        foreach (var actor in actors)
        {
            int requestedRecovery = OrbData.GetRecoveryAmount(actor.WeaponItemId);
            if (requestedRecovery <= 0)
                continue;

            var actorKey = (actor.PlayerId, actor.WeaponItemUid, actor.WeaponStackIndex);
            var stateKey = actorKey;
            activeRecoveryKeys.Add(actorKey);

            if (!recoveryTimes.TryGetValue(stateKey, out var readyAtUtc))
            {
                recoveryTimes[stateKey] =
                    nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);
                continue;
            }

            if (nowUtc < readyAtUtc)
                continue;

            recoveryTimes[stateKey] =
                nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);

            if (!dueRecoveryByPlayer.TryGetValue(actor.PlayerId, out var dueRecoveries))
            {
                dueRecoveries = new List<(ProximityCombatActor Actor, int Amount)>();
                dueRecoveryByPlayer[actor.PlayerId] = dueRecoveries;
            }
            dueRecoveries.Add((actor, requestedRecovery));
        }

        // #227 6단계: 같은 서버 틱에 발동한 회복 오브는 실제 회복·숫자·효과음을 한 번으로
        // 합친다. 개별 오브의 다음 발동 시각은 위에서 그대로 유지한다.
        foreach (var (playerId, dueRecoveries) in dueRecoveryByPlayer)
        {
            int requestedRecovery = dueRecoveries.Sum(entry => entry.Amount);
            var representative = dueRecoveries
                .OrderByDescending(entry => entry.Amount)
                .ThenBy(entry => entry.Actor.WeaponItemUid)
                .First().Actor;

            int effectiveRecovery = 0;
            var session = matchingSessions.FirstOrDefault(candidate =>
                candidate.PlayerId == playerId && !candidate.IsEliminated);
            if (session != null)
            {
                int previousHealth = session.CurrentHealth;
                if (previousHealth < Config.MAX_HEALTH)
                {
                    var change = session.Condition.Recover(requestedRecovery);
                    session.HealthChanges.Handle(change);
                    effectiveRecovery = change.Recovered;
                }
            }
            else
            {
                var bot = matchingBots.FirstOrDefault(candidate =>
                    candidate.PlayerId == playerId && !candidate.IsEliminated);
                if (bot != null && bot.Health < Config.MAX_HEALTH)
                {
                    int previousHealth = bot.Health;
                    bot.Health = Math.Min(Config.MAX_HEALTH, bot.Health + requestedRecovery);
                    effectiveRecovery = bot.Health - previousHealth;
                }
            }

            if (effectiveRecovery <= 0)
                continue;

            if (session != null && session.PlayerId.HasValue && !session.IsEliminated &&
                representative.WeaponItemId > 0)
            {
                using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new()
                {
                    PlayerId = playerId,
                    AreaType = session.CurrentArea,
                    Amount = effectiveRecovery,
                    Source = HealthRecoveryKind.Orb,
                    OrbItemId = representative.WeaponItemId
                });
                session.TrySend(packet);
            }

            // Human sessions already record effective recovery inside PlayerHealthChangeService.Handle.
            // Bots mutate their state directly, so only that path needs explicit telemetry.
            if (session == null)
            {
                eventLogs.RecordRecovery(
                    matchingId, playerId, effectiveRecovery);
            }
            logger.LogDebug(
                "Survivor recovery event tick: MatchingId={MatchingId}, PlayerId={PlayerId}, " +
                "OrbCount={OrbCount}, ItemId={ItemId}, Recovery={Recovery}",
                matchingId,
                playerId,
                dueRecoveries.Count,
                representative.WeaponItemId,
                effectiveRecovery);
        }

        foreach (var key in recoveryTimes.Keys.ToArray())
        {
            if (activeRecoveryKeys.Contains((key.PlayerId, key.ItemUid, key.StackIndex)))
                continue;

            recoveryTimes.TryRemove(key, out _);
        }
    }


}
