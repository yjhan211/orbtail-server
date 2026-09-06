using MessagePack;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchManifestSerializationTests
{
    [Fact]
    public void ManifestUsesStringKeys()
    {
        byte[] bytes = MessagePackSerializer.Serialize(new MatchManifest());
        var reader = new MessagePackReader(bytes);
        Assert.Equal(3, reader.ReadMapHeader());
        var keys = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            keys.Add(reader.ReadString()!);
            reader.Skip();
        }
        Assert.Equal(new[] { "humanPlayerIds", "botCount", "mode" }, keys);
    }

    [Theory]
    [InlineData(0, MatchMode.SoloMapValidation)]
    [InlineData(7, MatchMode.Normal)]
    public void RoundTrip_PreservesHumanIdsBotCountAndMode(int botCount, MatchMode mode)
    {
        var source = new MatchManifest { HumanPlayerIds = [1], BotCount = botCount, Mode = mode };
        var restored = MessagePackSerializer.Deserialize<MatchManifest>(MessagePackSerializer.Serialize(source));
        Assert.Equal(source.HumanPlayerIds, restored.HumanPlayerIds);
        Assert.Equal(botCount, restored.BotCount);
        Assert.Equal(mode, restored.Mode);
    }

    [Fact]
    public void MatchRoster_RoundTripsProfiles()
    {
        var source = new G_TO_C_MATCH_ROSTER
        {
            MatchingId = 42,
            PlayerRoster = [new PlayerInfo { PlayerId = 1, Name = "Human", WearItemIdList = [123] },
                            new PlayerInfo { PlayerId = -1, Name = "Bot", WearItemIdList = [456] }]
        };
        var restored = MessagePackSerializer.Deserialize<G_TO_C_MATCH_ROSTER>(MessagePackSerializer.Serialize(source));
        Assert.Equal(42, restored.MatchingId);
        Assert.Equal(new long[] { 1, -1 }, restored.PlayerRoster.Select(p => p.PlayerId));
        Assert.Equal("Bot", restored.PlayerRoster[1].Name);
        Assert.Equal(new[] {456}, restored.PlayerRoster[1].WearItemIdList);
        Assert.Null(typeof(U_TO_C_MATCHING_SUCCESS).GetProperty("PlayerRoster"));
    }
}
