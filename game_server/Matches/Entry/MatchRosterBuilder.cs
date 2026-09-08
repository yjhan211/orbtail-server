using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.redis;

namespace game_server.matches.entry;

/// <summary>매치 최초 구성 시 사람 프로필을 검증하고 사람·봇의 이름과 외형을 확정한다. 사람 프로필이 없으면 입장을 실패시킨다.</summary>
internal sealed class MatchRosterBuilder(IRedisOperations redisOperations, ILogger logger)
{
    private static long _botIdCounter;

    internal static List<long> CreateBotIds(MatchManifest manifest)
    {
        if (!Enum.IsDefined(manifest.Mode) || manifest.HumanPlayerIds.Count == 0 ||
            manifest.HumanPlayerIds.Any(id => id <= 0) ||
            manifest.HumanPlayerIds.Distinct().Count() != manifest.HumanPlayerIds.Count ||
            manifest.BotCount < 0 || manifest.BotCount > Config.SWARM_PLAYERS_PER_MATCH ||
            manifest.HumanPlayerIds.Count + manifest.BotCount > Config.SWARM_PLAYERS_PER_MATCH ||
            (manifest.Mode == MatchMode.SoloMapValidation &&
             (manifest.HumanPlayerIds.Count != 1 || manifest.BotCount != 0)))
            throw new InvalidOperationException("Invalid match participant configuration.");

        return Enumerable.Range(0, manifest.BotCount)
            .Select(_ => Interlocked.Decrement(ref _botIdCounter)).ToList();
    }

    internal async Task<List<PlayerInfo>> BuildAsync(IReadOnlyList<long> humanIds, IEnumerable<PlayerInfo> bots)
    {
        var roster = new List<PlayerInfo>();
        foreach (long playerId in humanIds)
        {
            var info = await PlayerInfo.Load(redisOperations, playerId);
            if (info == null)
            {
                logger.LogWarning("Match entry rejected: PlayerInfo missing ({PlayerId})", playerId);
                throw new InvalidOperationException($"PlayerInfo not found for match participant {playerId}.");
            }
            roster.Add(new PlayerInfo
            {
                PlayerId = playerId,
                Name = info.Name,
                WearItemIdList = info.WearItemIdList?.ToList() ?? new List<int>()
            });
        }
        roster.AddRange(bots);
        return roster;
    }
}
