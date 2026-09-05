using network.common.data.models;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     MatchRosterBuilder: 매치 manifest(사람·봇 ID), 봇 로스터·코스튬, 성공 패킷용 PlayerInfo 로스터.
///     스폰·타깃은 여기서 정하지 않는다 — Game Server 권위.
/// </summary>
public sealed class MatchRosterBuilderTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly RecordingLogger _logger = new();

    public MatchRosterBuilderTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
    }

    private MatchRosterBuilder CreateBuilder()
    {
        return new MatchRosterBuilder(_cache, _logger);
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
    public void BuildManifest_SplitsHumansAndBotsAndDropsDuplicates()
    {
        List<MatchingQueueEntry> group = MixedGroup(3, 5);
        group.Add(UserServerMatchingTestData.HumanEntry(101));

        MatchManifest manifest = MatchRosterBuilder.BuildManifest(group);

        Assert.Equal(MatchMode.Normal, manifest.Mode);
        Assert.Equal(new long[] { 101, 102, 103 }, manifest.HumanPlayerIds);
        Assert.Equal(new long[] { -1, -2, -3, -4, -5 }, manifest.BotPlayerIds);
    }

    [Fact]
    public void BuildManifest_EmptyGroupIsEmpty()
    {
        MatchManifest manifest = MatchRosterBuilder.BuildManifest(new List<MatchingQueueEntry>());

        Assert.Empty(manifest.HumanPlayerIds);
        Assert.Empty(manifest.BotPlayerIds);
    }

    [Fact]
    public void BuildManifest_PreservesRequestedMatchMode()
    {
        List<MatchingQueueEntry> group = MixedGroup(1, 0);

        MatchManifest manifest = MatchRosterBuilder.BuildManifest(group, MatchMode.SoloMapValidation);

        Assert.Equal(MatchMode.SoloMapValidation, manifest.Mode);
        Assert.Single(manifest.HumanPlayerIds);
        Assert.Empty(manifest.BotPlayerIds);
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
        List<MatchingQueueEntry> group = MixedGroup(2, 1);

        List<PlayerInfo> roster = await CreateBuilder().BuildPlayerRosterAsync(group);

        Assert.Equal(3, roster.Count);
        Assert.Equal(group.Select(e => e.PlayerId), roster.Select(p => p.PlayerId));
        PlayerInfo human = roster.Single(p => p.PlayerId == 101);
        Assert.Equal("Player101", human.Name);
        Assert.Empty(human.WearItemIdList);
        PlayerInfo bot = roster.Single(p => p.PlayerId == -1);
        Assert.Equal("Player1", bot.Name);
        Assert.Equal(6, bot.WearItemIdList.Count);
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Matching roster fallback"));
    }
}
