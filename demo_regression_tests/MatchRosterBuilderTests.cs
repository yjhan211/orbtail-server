using network.common.data.models;
using network.common.data;
using network.common;
using network.gamehandoff;
using user_server.services;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchRosterBuilder: 원형 체인, matchingId 결정 스폰 배정, 봇 로스터·코스튬, 인간 handoff 로스터, 봇 handoff 정보.
/// </summary>
public sealed class MatchRosterBuilderTests
{
    private readonly InMemoryCacheHelper _cache = new();
    private readonly RecordingLogger _logger = new();

    public MatchRosterBuilderTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
    }

    private MatchRosterBuilder CreateBuilder(bool crossfireSandbox = false)
    {
        var overrides = new DevMatchOverrides(false, false, crossfireSandbox, _cache, new FakeRedLockFactory(), _logger);
        return new MatchRosterBuilder(_cache, overrides, _logger);
    }

    private static List<MatchingQueueEntry> MixedGroup(int humans, int bots)
    {
        var entries = new List<MatchingQueueEntry>();
        for (int i = 1; i <= humans; i++)
            entries.Add(UserServerMatchingTestData.HumanEntry(100 + i));
        for (int i = 1; i <= bots; i++)
            entries.Add(MatchingQueueEntry.CreateBot(-i));
        return entries;
    }

    [Fact]
    public void BuildRosterChain_IsCircularAndCoversEveryEntryOnce()
    {
        List<MatchingQueueEntry> group = MixedGroup(3, 5);

        List<RosterChainLink> chain = CreateBuilder().BuildRosterChain(group);

        Assert.Equal(8, chain.Count);
        Assert.Equal(group.Select(e => e.PlayerId).OrderBy(id => id), chain.Select(l => l.PlayerId).OrderBy(id => id));
        for (int i = 0; i < chain.Count; i++)
            Assert.Equal(chain[(i + 1) % chain.Count].PlayerId, chain[i].TargetPlayerId);
        Assert.All(chain, link => Assert.NotEqual(link.PlayerId, link.TargetPlayerId));
    }

    [Fact]
    public void ApplySpawnAssignments_IsDeterministicPerMatchingIdAndUniquePerPlayer()
    {
        MatchRosterBuilder builder = CreateBuilder();
        List<RosterChainLink> first = builder.BuildRosterChain(MixedGroup(2, 6));
        List<RosterChainLink> second = builder.BuildRosterChain(MixedGroup(2, 6));

        builder.ApplySpawnAssignments(4242, first);
        builder.ApplySpawnAssignments(4242, second);

        Dictionary<long, Cell> firstCells = first.ToDictionary(l => l.PlayerId, l => l.SpawnCell);
        foreach (RosterChainLink link in second)
            Assert.Equal(firstCells[link.PlayerId], link.SpawnCell);
        Assert.Equal(8, first.Select(l => (l.SpawnCell.X, l.SpawnCell.Y)).Distinct().Count());
        Assert.All(first, link => Assert.False(link.SpawnCell.X == 0 && link.SpawnCell.Y == 0));
        Assert.All(first, link => Assert.NotEqual(AreaType.None, link.StartArea));
    }

    [Fact]
    public void ApplySpawnAssignments_CrossfireSandboxPutsEveryoneOnTheGround()
    {
        MatchRosterBuilder builder = CreateBuilder(crossfireSandbox: true);
        List<RosterChainLink> chain = builder.BuildRosterChain(MixedGroup(1, 7));
        Cell ground = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA);

        builder.ApplySpawnAssignments(7, chain);

        Assert.All(chain, link => Assert.Equal(ground, link.SpawnCell));
        Assert.All(chain, link => Assert.NotSame(ground, link.SpawnCell));
    }

    [Fact]
    public void ApplySpawnAssignments_EmptyChainIsNoOp()
    {
        CreateBuilder().ApplySpawnAssignments(1, new List<RosterChainLink>());
    }

    [Theory]
    [InlineData(-4, 103000001)]
    [InlineData(-1, 103000004)]
    [InlineData(-2, 103000005)]
    [InlineData(-3, 103000006)]
    [InlineData(-7, 103000006)]
    public void CreateBotRosterInfo_UsesAbsoluteIdNameAndModFourAccessory(long botId, int accessoryId)
    {
        PlayerInfo bot = MatchRosterBuilder.CreateBotRosterInfo(botId);

        Assert.Equal(botId, bot.PlayerId);
        Assert.Equal($"Player{Math.Abs(botId)}", bot.Name);
        Assert.Equal(new[] { 101000003, 102000003, 104000005, 105000005, 106000003, accessoryId }, bot.WearItemIdList);
    }

    [Fact]
    public async Task BuildPlayerRosterAsync_FallsBackToDefaultNameWhenPlayerInfoIsMissing()
    {
        MatchRosterBuilder builder = CreateBuilder();
        List<RosterChainLink> chain = builder.BuildRosterChain(MixedGroup(2, 1));

        List<PlayerInfo> roster = await builder.BuildPlayerRosterAsync(chain);

        Assert.Equal(3, roster.Count);
        Assert.Equal(chain.Select(l => l.PlayerId), roster.Select(p => p.PlayerId));
        PlayerInfo human = roster.Single(p => p.PlayerId == 101);
        Assert.Equal("Player101", human.Name);
        Assert.Empty(human.WearItemIdList);
        PlayerInfo bot = roster.Single(p => p.PlayerId == -1);
        Assert.Equal("Player1", bot.Name);
        Assert.Equal(6, bot.WearItemIdList.Count);
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Matching roster fallback"));
    }

    [Fact]
    public void BuildHumanHandoffRoster_ContainsHumansOnlyWithChainTargets()
    {
        List<RosterChainLink> chain = CreateBuilder().BuildRosterChain(MixedGroup(3, 5));

        List<GameHandoffRosterEntry> roster = MatchRosterBuilder.BuildHumanHandoffRoster(chain);

        Assert.Equal(3, roster.Count);
        Assert.All(roster, entry => Assert.True(entry.PlayerId > 0));
        foreach (GameHandoffRosterEntry entry in roster)
            Assert.Equal(chain.Single(l => l.PlayerId == entry.PlayerId).TargetPlayerId, entry.TargetPlayerId);
    }

    [Fact]
    public void BuildBotHandoffInfos_ContainsBotsOnlyWithClonedSpawnAndEmptyPersona()
    {
        MatchRosterBuilder builder = CreateBuilder();
        List<RosterChainLink> chain = builder.BuildRosterChain(MixedGroup(3, 5));
        builder.ApplySpawnAssignments(99, chain);

        List<BotMatchingInfo> bots = MatchRosterBuilder.BuildBotHandoffInfos(chain);

        Assert.Equal(5, bots.Count);
        foreach (BotMatchingInfo bot in bots)
        {
            RosterChainLink link = chain.Single(l => l.PlayerId == bot.PlayerId);
            Assert.True(bot.PlayerId < 0);
            Assert.Equal(link.TargetPlayerId, bot.TargetPlayerId);
            Assert.Equal(link.StartArea, bot.StartArea);
            Assert.Equal(link.SpawnCell, bot.SpawnCell);
            Assert.NotSame(link.SpawnCell, bot.SpawnCell);
            Assert.Equal(PersonaType.None, bot.Persona);
            Assert.Empty(bot.ActiveBuffIds);
        }
    }
}
