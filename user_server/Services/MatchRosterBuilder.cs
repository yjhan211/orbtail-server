using Microsoft.Extensions.Logging;
using network.common.data.models;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     한 매치의 로스터를 조립한다: 성공 패킷용 PlayerInfo 로스터(이름·착용 아이템)와 Game Server가 읽는 매치 manifest.
///     스폰·타깃 같은 매치 안의 사실은 여기서 정하지 않는다 — Game Server 권위다. Redis는 PlayerInfo 읽기에만 쓴다.
/// </summary>
internal sealed class MatchRosterBuilder(IRedisOperations redisOperations, ILogger logger)
{
    /// <summary>
    ///     Game Server가 매치당 한 번 읽는 구성: 사람 ID, 봇 ID, 매치 모드.
    /// </summary>
    public static MatchManifest BuildManifest(
        IReadOnlyList<MatchingQueueEntry> entries,
        MatchMode mode = MatchMode.Normal)
    {
        return new MatchManifest
        {
            HumanPlayerIds = entries.Where(entry => entry.IsHuman).Select(entry => entry.PlayerId).Distinct().ToList(),
            BotPlayerIds = entries.Where(entry => entry.IsBot).Select(entry => entry.PlayerId).Distinct().ToList(),
            Mode = mode
        };
    }

    /// <summary>
    ///     성공 패킷에 실을 전원 로스터(이름·착용 아이템). 로드 실패한 사람은 기본 이름으로 대체한다.
    /// </summary>
    public async Task<List<PlayerInfo>> BuildPlayerRosterAsync(IReadOnlyList<MatchingQueueEntry> entries)
    {
        var roster = new List<PlayerInfo>(entries.Count);

        foreach (var entry in entries)
        {
            if (entry.IsBot)
            {
                roster.Add(CreateBotRosterInfo(entry.PlayerId));
                continue;
            }

            var playerInfo = await PlayerInfo.Load(redisOperations, entry.PlayerId);
            if (playerInfo == null)
            {
                logger.LogWarning("Matching roster fallback: PlayerInfo load failed ({PlayerId})", entry.PlayerId);
                roster.Add(new PlayerInfo
                {
                    PlayerId = entry.PlayerId,
                    Name = $"Player{entry.PlayerId}",
                    WearItemIdList = new List<int>()
                });
                continue;
            }

            roster.Add(new PlayerInfo
            {
                PlayerId = playerInfo.PlayerId,
                Name = playerInfo.Name,
                WearItemIdList = playerInfo.WearItemIdList != null
                    ? new List<int>(playerInfo.WearItemIdList)
                    : new List<int>()
            });
        }

        return roster;
    }

    internal static PlayerInfo CreateBotRosterInfo(long playerId)
    {
        return new PlayerInfo
        {
            PlayerId = playerId,
            Name = $"Player{Math.Abs(playerId)}",
            WearItemIdList = BuildBotRosterWearItems(playerId)
        };
    }

    /// <summary>
    ///     봇 코스튬: 공통 5종 + PlayerId mod 4로 고르는 안경 1종.
    /// </summary>
    internal static List<int> BuildBotRosterWearItems(long playerId)
    {
        var list = new List<int>
        {
            101000003,
            102000003,
            104000005,
            105000005,
            106000003
        };

        int accessoryId = (Math.Abs((int)playerId) % 4) switch
        {
            0 => 103000001,
            1 => 103000004,
            2 => 103000005,
            _ => 103000006
        };

        list.Add(accessoryId);
        return list;
    }
}
