using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     한 매치의 로스터를 조립한다: 원형 타겟 체인, 권위 스폰 배정, 성공 패킷용 PlayerInfo 로스터,
///     인간 handoff 로스터, 봇 handoff 정보. Redis는 PlayerInfo 읽기에만 쓰고 프로세스 상태를 갖지 않는다.
/// </summary>
internal sealed class MatchRosterBuilder(ICacheHelper cacheHelper, DevMatchOverrides overrides, ILogger logger)
{
    /// <summary>
    ///     원형 타겟 체인을 만든다: i번째가 (i+1) mod N을 노린다. entry 순서는 무작위로 섞는다.
    ///     두 명 테스트 매치(8인 구성)는 결정적 체인을 대신 쓴다.
    /// </summary>
    public List<RosterChainLink> BuildRosterChain(IReadOnlyList<MatchingQueueEntry> groupEntries)
    {
        if (overrides.IsTwoPlayerTestMatch && groupEntries.Count == DevMatchOverrides.DefaultGamePlayersPerMatch)
            return overrides.BuildTwoPlayerTestRosterChain(groupEntries);

        var entries = groupEntries.ToList();
        var rng = Random.Shared;
        for (int i = entries.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        var chain = new List<RosterChainLink>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            int targetIndex = (i + 1) % entries.Count;
            chain.Add(new RosterChainLink(entries[i], entries[targetIndex].PlayerId));
        }

        logger.LogInformation("Target chain created: {Chain}", DescribeChain(entries));

        return chain;
    }

    internal static string DescribeChain(IReadOnlyList<MatchingQueueEntry> orderedEntries)
    {
        if (orderedEntries.Count == 0) return string.Empty;
        return string.Join(" -> ", orderedEntries.Select(entry => entry.PlayerId.ToString()))
               + " -> " + orderedEntries[0].PlayerId;
    }

    /// <summary>
    ///     matchingId로 결정되는 시작방 분산 스폰을 체인에 기록한다. 교차사격 샌드박스는 전원 운동장 스폰.
    /// </summary>
    public void ApplySpawnAssignments(long matchingId, List<RosterChainLink> chain)
    {
        if (chain.Count == 0) return;

        var playerIds = chain.Select(link => link.PlayerId).ToList();
        IReadOnlyDictionary<long, Cell> assignments =
            MatchSpawnData.CreatePhaseRoomAssignments(matchingId, playerIds);
        Cell? sandboxCell = overrides.GetSandboxSpawnCell();

        foreach (var link in chain)
        {
            link.SpawnCell = Cell.Clone(sandboxCell ?? assignments[link.PlayerId]);
            link.StartArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, link.SpawnCell);

            logger.LogInformation(
                "Swarm spawn assigned: MatchingId={MatchingId}, PlayerId={PlayerId}, Area={Area}, Cell=({X},{Y})",
                matchingId, link.PlayerId, link.StartArea, link.SpawnCell.X, link.SpawnCell.Y);
        }
    }

    /// <summary>
    ///     성공 패킷에 실을 전원 로스터(이름·착용 아이템). 로드 실패한 사람은 기본 이름으로 대체한다.
    /// </summary>
    public async Task<List<PlayerInfo>> BuildPlayerRosterAsync(List<RosterChainLink> chain)
    {
        var roster = new List<PlayerInfo>(chain.Count);

        foreach (var link in chain)
        {
            if (link.Entry.IsBot)
            {
                roster.Add(CreateBotRosterInfo(link.PlayerId));
                continue;
            }

            var playerInfo = await PlayerInfo.Load(cacheHelper, link.PlayerId);
            if (playerInfo == null)
            {
                logger.LogWarning("Matching roster fallback: PlayerInfo load failed ({PlayerId})", link.PlayerId);
                roster.Add(new PlayerInfo
                {
                    PlayerId = link.PlayerId,
                    Name = $"Player{link.PlayerId}",
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

    /// <summary>
    ///     handoff ticket에 싣는 전체 인간 로스터(PlayerId → TargetPlayerId).
    /// </summary>
    public static List<GameHandoffRosterEntry> BuildHumanHandoffRoster(IEnumerable<RosterChainLink> chain)
    {
        return chain
            .Where(link => link.Entry.IsHuman)
            .Select(link => new GameHandoffRosterEntry
            {
                PlayerId = link.PlayerId,
                TargetPlayerId = link.TargetPlayerId
            })
            .ToList();
    }

    /// <summary>
    ///     Game Server가 매치당 한 번 읽는 봇 handoff 정보. Persona·ActiveBuffIds는 현행 wire 계약상 항상 비어 있다.
    /// </summary>
    public static List<BotMatchingInfo> BuildBotHandoffInfos(IEnumerable<RosterChainLink> chain)
    {
        return chain
            .Where(link => link.Entry.IsBot)
            .Select(link => new BotMatchingInfo
            {
                PlayerId = link.PlayerId,
                TargetPlayerId = link.TargetPlayerId,
                Persona = PersonaType.None,
                StartArea = link.StartArea,
                SpawnCell = Cell.Clone(link.SpawnCell),
                ActiveBuffIds = new List<int>()
            })
            .ToList();
    }
}

/// <summary>
///     원형 타겟 체인의 링크 하나. 스폰 배정 뒤 StartArea·SpawnCell이 채워진다.
/// </summary>
internal sealed class RosterChainLink(MatchingQueueEntry entry, long targetPlayerId)
{
    public MatchingQueueEntry Entry { get; } = entry;
    public long PlayerId => Entry.PlayerId;
    public long TargetPlayerId { get; } = targetPlayerId;
    public AreaType StartArea { get; set; } = AreaType.None;
    public Cell SpawnCell { get; set; } = new(0, 0);
}
