using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.redis;

namespace game_server.services;

/// <summary>GameServer가 매치 초기화 시 봇 ID와 사람·봇의 최종 프로필 명단을 만든다.</summary>
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
                logger.LogWarning("Matching roster fallback: PlayerInfo load failed ({PlayerId})", playerId);
            roster.Add(new PlayerInfo
            {
                PlayerId = playerId,
                Name = info?.Name ?? $"Player{playerId}",
                WearItemIdList = info?.WearItemIdList?.ToList() ?? new List<int>()
            });
        }
        roster.AddRange(bots);
        return roster;
    }
}
