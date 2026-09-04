using network.common;
using user_server.services;

namespace demo_regression_tests;

/// <summary>
///     #323 DevMatchOverrides: 환경 변수 1회 읽기, 그룹/정원 규칙, 봇 채움 허용, 두 명 테스트 결정적 체인, 코스튬 no-op.
/// </summary>
public sealed class DevMatchOverridesTests
{
    private static readonly object _environmentLock = new();
    private readonly InMemoryCacheHelper _cache = new();
    private readonly RecordingLogger _logger = new();

    private DevMatchOverrides Create(bool twoPlayer = false, bool solo = false)
    {
        return new DevMatchOverrides(twoPlayer, solo, _cache, new FakeRedLockFactory(), _logger);
    }

    [Fact]
    public void FromEnvironment_ReadsVariablesOnceAtConstruction()
    {
        lock (_environmentLock)
        {
            string? previous = Environment.GetEnvironmentVariable(DevMatchOverrides.TwoPlayerTestMatchVariable);
            try
            {
                Environment.SetEnvironmentVariable(DevMatchOverrides.TwoPlayerTestMatchVariable, "1");
                DevMatchOverrides overrides = DevMatchOverrides.FromEnvironment(_cache, new FakeRedLockFactory(), _logger);
                Environment.SetEnvironmentVariable(DevMatchOverrides.TwoPlayerTestMatchVariable, null);

                Assert.True(overrides.IsTwoPlayerTestMatch);
                Assert.Equal(2, overrides.PlayersPerMatch);
                Assert.False(DevMatchOverrides.FromEnvironment(_cache, new FakeRedLockFactory(), _logger).IsTwoPlayerTestMatch);
            }
            finally
            {
                Environment.SetEnvironmentVariable(DevMatchOverrides.TwoPlayerTestMatchVariable, previous);
            }
        }
    }

    [Fact]
    public void FromEnvironment_OnlyLiteralOneEnables()
    {
        lock (_environmentLock)
        {
            string? previous = Environment.GetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable);
            try
            {
                Environment.SetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable, "true");
                Assert.False(DevMatchOverrides.FromEnvironment(_cache, new FakeRedLockFactory(), _logger).IsSoloMapValidation);
            }
            finally
            {
                Environment.SetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable, previous);
            }
        }
    }

    [Fact]
    public void DefaultRules_OneHumanPerGroupAndConfigCapacity()
    {
        DevMatchOverrides overrides = Create();

        Assert.Equal(1, overrides.PlayersPerMatch);
        Assert.Equal(Config.SWARM_PLAYERS_PER_MATCH, overrides.GamePlayersPerMatch);
        Assert.True(overrides.AllowsBotFill);
    }

    [Fact]
    public void SoloMapValidation_MakesSelfContainedSingleMatchWithoutBotFill()
    {
        DevMatchOverrides overrides = Create(solo: true);

        Assert.Equal(1, overrides.PlayersPerMatch);
        Assert.Equal(1, overrides.GamePlayersPerMatch);
        Assert.False(overrides.AllowsBotFill);
    }

    [Fact]
    public void TwoPlayerTestMatch_GroupsTwoHumansWithoutBotFill()
    {
        DevMatchOverrides overrides = Create(twoPlayer: true);

        Assert.Equal(2, overrides.PlayersPerMatch);
        Assert.Equal(Config.SWARM_PLAYERS_PER_MATCH, overrides.GamePlayersPerMatch);
        Assert.False(overrides.AllowsBotFill);
    }

    [Fact]
    public void SoloWinsOverTwoPlayerWhenBothSet()
    {
        DevMatchOverrides overrides = Create(twoPlayer: true, solo: true);

        Assert.Equal(1, overrides.PlayersPerMatch);
        Assert.Equal(1, overrides.GamePlayersPerMatch);
    }

    [Fact]
    public async Task ApplyTwoPlayerTestOutfitAsync_IsNoOpWhenDisabled()
    {
        var entries = new List<MatchingQueueEntry>
        {
            UserServerMatchingTestData.HumanEntry(1),
            UserServerMatchingTestData.HumanEntry(2)
        };
        var redLock = new FakeRedLockFactory();
        var overrides = new DevMatchOverrides(false, false, _cache, redLock, _logger);

        await overrides.ApplyTwoPlayerTestOutfitAsync(entries);

        Assert.Empty(redLock.AcquiredResources);
        Assert.Empty(_cache.StringKeys);
    }

    [Fact]
    public async Task ApplyTwoPlayerTestOutfitAsync_MissingSecondPlayerLogsAndReturns()
    {
        var baseTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = new List<MatchingQueueEntry>
        {
            UserServerMatchingTestData.HumanEntry(1, baseTime),
            UserServerMatchingTestData.HumanEntry(2, baseTime.AddSeconds(1))
        };
        var redLock = new FakeRedLockFactory();
        var overrides = new DevMatchOverrides(true, false, _cache, redLock, _logger);

        await overrides.ApplyTwoPlayerTestOutfitAsync(entries);

        Assert.Single(redLock.AcquiredResources);
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Two-player outfit setup failed"));
    }

    [Fact]
    public void TwoPlayerTestOutfit_CoversSixCostumeSlots()
    {
        Assert.Equal(6, DevMatchOverrides.TwoPlayerTestOutfitItemIds.Length);
        Assert.Equal(DevMatchOverrides.TwoPlayerTestOutfitItemIds.Length,
            DevMatchOverrides.TwoPlayerTestOutfitItemIds.Select(id => id / 1_000_000).Distinct().Count());
    }
}
