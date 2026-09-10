using game_server.combat;
using game_server.players;
using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.orbs;
/// <summary>
///     사람·봇 참가자의 오브별 회복 시각을 확인하고 같은 틱의 회복량을 합산해 적용한다.
///     회복 시각은 매치별 OrbRecoveryState에 보관하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class OrbRecoveryService(
    MatchRuntimeStore matchRuntimes,
    PlayerHealthService healthService,
    ILogger<OrbRecoveryService> logger)
{
    public void Process(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<Player> players,
        DateTime nowUtc)
    {
        if (matchRuntimes.GetOrNull(matchingId)?.OrbRecovery is not { } recovery)
            return;
        var recoveryTimes = recovery.ReadyAtUtc;
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

            var player = players.FirstOrDefault(candidate => candidate.PlayerId == playerId && !candidate.IsEliminated);
            if (player == null) continue;
            var change = healthService.Recover(matchRuntimes.GetOrThrow(matchingId), player, requestedRecovery);
            int effectiveRecovery = change.Recovered;
            if (effectiveRecovery <= 0) continue;

            if (representative.WeaponItemId > 0)
            {
                using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new()
                {
                    PlayerId = playerId,
                    AreaType = player.CurrentArea,
                    Amount = effectiveRecovery,
                    Source = HealthRecoveryKind.Orb,
                    OrbItemId = representative.WeaponItemId
                });
                player.Session?.TrySend(packet);
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
