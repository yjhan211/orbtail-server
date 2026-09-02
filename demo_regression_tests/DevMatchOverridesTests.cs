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

    private DevMatchOverrides Create(bool twoPlayer = false, bool solo = false, bool sandbox = false)
    {
        return new DevMatchOverrides(twoPlayer, solo, sandbox, _cache, new FakeRedLockFactory(), _logger);
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
        Assert.Null(overrides.GetSandboxSpawnCell());
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
    public void BuildTwoPlayerTestRosterChain_OrdersHumansByRequestTimeThenBots()
    {
        var baseTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = new List<MatchingQueueEntry>
        {
            UserServerMatchingTestData.HumanEntry(200, baseTime.AddSeconds(5)),
            MatchingQueueEntry.CreateBot(-1),
            UserServerMatchingTestData.HumanEntry(100, baseTime.AddSeconds(1)),
            MatchingQueueEntry.CreateBot(-2),
            MatchingQueueEntry.CreateBot(-3),
            MatchingQueueEntry.CreateBot(-4),
            MatchingQueueEntry.CreateBot(-5),
            MatchingQueueEntry.CreateBot(-6)
        };

        List<RosterChainLink> chain = Create(twoPlayer: true).BuildTwoPlayerTestRosterChain(entries);

        Assert.Equal(new long[] { 100, 200, -1, -2, -3, -4, -5, -6 }, chain.Select(l => l.PlayerId));
        Assert.Equal(200, chain[0].TargetPlayerId);
        Assert.Equal(-1, chain[1].TargetPlayerId);
        Assert.Equal(100, chain[^1].TargetPlayerId);
    }

    [Fact]
    public void BuildTwoPlayerTestRosterChain_RejectsWrongComposition()
    {
        var entries = new List<MatchingQueueEntry>
        {
            UserServerMatchingTestData.HumanEntry(1),
            UserServerMatchingTestData.HumanEntry(2),
            UserServerMatchingTestData.HumanEntry(3),
            MatchingQueueEntry.CreateBot(-1),
            MatchingQueueEntry.CreateBot(-2),
            MatchingQueueEntry.CreateBot(-3),
            MatchingQueueEntry.CreateBot(-4),
            MatchingQueueEntry.CreateBot(-5)
        };

        Assert.Throws<InvalidOperationException>(() => Create(twoPlayer: true).BuildTwoPlayerTestRosterChain(entries));
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "composition invalid"));
    }

    [Fact]
    public async Task ApplyTwoPlayerTestTargetOutfitAsync_IsNoOpWhenDisabled()
    {
        var chain = new List<RosterChainLink>
        {
            new(UserServerMatchingTestData.HumanEntry(1), 2),
            new(UserServerMatchingTestData.HumanEntry(2), 1)
        };
        var redLock = new FakeRedLockFactory();
        var overrides = new DevMatchOverrides(false, false, false, _cache, redLock, _logger);

        await overrides.ApplyTwoPlayerTestTargetOutfitAsync(chain);

        Assert.Empty(redLock.AcquiredResources);
        Assert.Empty(_cache.StringKeys);
    }

    [Fact]
    public async Task ApplyTwoPlayerTestTargetOutfitAsync_MissingTargetPlayerLogsAndReturns()
    {
        var baseTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var chain = new List<RosterChainLink>
        {
            new(UserServerMatchingTestData.HumanEntry(1, baseTime), 2),
            new(UserServerMatchingTestData.HumanEntry(2, baseTime.AddSeconds(1)), 1)
        };
        var redLock = new FakeRedLockFactory();
        var overrides = new DevMatchOverrides(true, false, false, _cache, redLock, _logger);

        await overrides.ApplyTwoPlayerTestTargetOutfitAsync(chain);

        Assert.Single(redLock.AcquiredResources);
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Two-player outfit setup failed"));
    }

    [Fact]
    public void TwoPlayerTargetOutfit_CoversSixCostumeSlots()
    {
        Assert.Equal(6, DevMatchOverrides.TwoPlayerTargetOutfitItemIds.Length);
        Assert.Equal(DevMatchOverrides.TwoPlayerTargetOutfitItemIds.Length,
            DevMatchOverrides.TwoPlayerTargetOutfitItemIds.Select(id => id / 1_000_000).Distinct().Count());
    }
}
