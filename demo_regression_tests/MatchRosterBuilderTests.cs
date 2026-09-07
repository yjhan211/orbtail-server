using game_server.services;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchRosterBuilderTests
{
    [Theory]
    [InlineData(1, 7)]
    [InlineData(3, 5)]
    [InlineData(8, 0)]
    public void CreateBotIds_AllocatesNegativeIdsOnGameServer(int humans, int bots)
    {
        var manifest = new MatchManifest { HumanPlayerIds = Enumerable.Range(1, humans).Select(x => (long)x).ToList(), BotCount = bots };
        var ids = MatchRosterBuilder.CreateBotIds(manifest);
        var otherIds = MatchRosterBuilder.CreateBotIds(manifest);
        Assert.Equal(bots, ids.Count);
        Assert.All(ids, id => Assert.True(id < 0));
        Assert.Equal(bots, ids.Distinct().Count());
        Assert.Empty(ids.Intersect(otherIds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void CreateBotIds_RejectsInvalidCount(int count)
    {
        Assert.Throws<InvalidOperationException>(() => MatchRosterBuilder.CreateBotIds(
            new MatchManifest { HumanPlayerIds = [1], BotCount = count }));
    }

    [Fact]
    public void CreateBotIds_ValidatesHumanRosterAndSoloMode()
    {
        Assert.Throws<InvalidOperationException>(() => MatchRosterBuilder.CreateBotIds(new MatchManifest { BotCount = 8 }));
        Assert.Throws<InvalidOperationException>(() => MatchRosterBuilder.CreateBotIds(new MatchManifest { HumanPlayerIds = [1, 1] }));
        Assert.Throws<InvalidOperationException>(() => MatchRosterBuilder.CreateBotIds(new MatchManifest { HumanPlayerIds = [-1] }));
        Assert.Throws<InvalidOperationException>(() => MatchRosterBuilder.CreateBotIds(
            new MatchManifest { HumanPlayerIds = [1], BotCount = 1, Mode = MatchMode.SoloMapValidation }));
        Assert.Empty(MatchRosterBuilder.CreateBotIds(new MatchManifest { HumanPlayerIds = [1], Mode = MatchMode.SoloMapValidation }));
    }

    [Fact]
    public async Task BuildAsync_CombinesHumanProfilesAndActualBotProfiles()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var logger = new RecordingLogger();
        var redis = new InMemoryRedisOperations();
        await new PlayerInfo(101, false) { Name = "Human", WearItemIdList = [123] }.Save(redis);
        var builder = new MatchRosterBuilder(redis, logger);
        var bot = new PlayerInfo { PlayerId = -1, Name = "Bot", WearItemIdList = [103000004] };
        var roster = await builder.BuildAsync([101], [bot]);
        Assert.Equal(2, roster.Count);
        Assert.Equal("Human", roster[0].Name);
        Assert.Equal(new[] { 123 }, roster[0].WearItemIdList);
        Assert.Same(bot, roster[1]);
    }

    [Fact]
    public async Task BuildAsync_RejectsMissingHumanProfile()
    {
        var builder = new MatchRosterBuilder(new InMemoryRedisOperations(), new RecordingLogger());
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync([101], []));
    }
}
