using MessagePack;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchManifestSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesMatchMode()
    {
        var source = new MatchManifest
        {
            HumanPlayerIds = [1],
            BotPlayerIds = [],
            Mode = MatchMode.SoloMapValidation
        };

        MatchManifest restored = MessagePackSerializer.Deserialize<MatchManifest>(
            MessagePackSerializer.Serialize(source));

        Assert.Equal(MatchMode.SoloMapValidation, restored.Mode);
        Assert.Equal(source.HumanPlayerIds, restored.HumanPlayerIds);
        Assert.Equal(source.BotPlayerIds, restored.BotPlayerIds);
    }

    [Fact]
    public void LegacyTwoFieldPayload_DefaultsToNormalMode()
    {
        var legacy = new LegacyMatchManifest
        {
            HumanPlayerIds = [1],
            BotPlayerIds = [-1]
        };

        MatchManifest restored = MessagePackSerializer.Deserialize<MatchManifest>(
            MessagePackSerializer.Serialize(legacy));

        Assert.Equal(MatchMode.Normal, restored.Mode);
        Assert.Equal(legacy.HumanPlayerIds, restored.HumanPlayerIds);
        Assert.Equal(legacy.BotPlayerIds, restored.BotPlayerIds);
    }

    [MessagePackObject]
    public sealed class LegacyMatchManifest
    {
        [Key(0)] public List<long> HumanPlayerIds { get; set; } = [];
        [Key(1)] public List<long> BotPlayerIds { get; set; } = [];
    }
}
