using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchTickRunnerTests
{
    public MatchTickRunnerTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void Tick_RunsEveryStageUnderTheSameMatchLock_InOrder()
    {
        using var fixture = new Fixture(945101);
        var steps = new List<string>();
        var locksHeld = new List<bool>();
        void Record(string step)
        {
            steps.Add(step);
            locksHeld.Add(Monitor.IsEntered(fixture.Match.Sync));
        }
        // 실제 시간을 기다리지 않고 환경 정산 시각이 지난 상태를 준비한다.
        typeof(MatchRuntime).GetProperty(nameof(MatchRuntime.NextEnvironmentalTickAtUtc))!
            .SetValue(fixture.Match, DateTime.UtcNow.AddSeconds(-1));
        var runner = new MatchTickRunner(fixture.Store, new GameSessionRegistry(), NullLogger.Instance,
            (_, _) => Record("countdown"),
            (_, _) => Record("combat"),
            (_, _) => Record("environment"),
            _ => Record("movement"));

        runner.Run();
        Assert.Equal(new[] { "countdown", "combat", "environment", "movement" }, steps);
        Assert.All(locksHeld, Assert.True);

        steps.Clear();
        runner.Run();
        Assert.Equal(new[] { "countdown", "combat", "movement" }, steps);
    }

    [Fact]
    public void CombatFailure_DoesNotPreventBotMovementOrOtherMatches()
    {
        using var first = new Fixture(945102);
        var second = first.Store.GetOrCreate(945103);
        var combatIds = new List<long>();
        int movements = 0;
        var runner = new MatchTickRunner(first.Store, new GameSessionRegistry(), NullLogger.Instance,
            (_, _) => { },
            (id, _) =>
            {
                combatIds.Add(id);
                if (id == first.Match.MatchingId)
                    throw new InvalidOperationException("test combat failure");
            },
            (_, _) => throw new InvalidOperationException("environment should not be due"),
            _ => movements++);

        runner.Run();
        Assert.Contains(first.Match.MatchingId, combatIds);
        Assert.Contains(second.MatchingId, combatIds);
        Assert.Equal(1, movements);
    }

    [Fact]
    public void TerminalDuringCombat_SkipsMovementAndRemovesRuntime()
    {
        using var fixture = new Fixture(945104);
        int movements = 0;
        int combats = 0;
        var runner = new MatchTickRunner(fixture.Store, new GameSessionRegistry(), NullLogger.Instance,
            (_, _) => { },
            (_, _) =>
            {
                combats++;
                fixture.Match.TryMarkTerminal();
            },
            (_, _) => { },
            _ => movements++);

        runner.Run();
        runner.Run();
        Assert.Equal(1, combats);
        Assert.Equal(0, movements);
        Assert.Null(fixture.Store.Get(fixture.Match.MatchingId));
    }

    [Fact]
    public async Task BusyMatch_IsSkipped_WhileOtherMatchStillRuns()
    {
        using var fixture = new Fixture(945105);
        var other = fixture.Store.GetOrCreate(945106);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var combatIds = new List<long>();
        var runner = new MatchTickRunner(fixture.Store, new GameSessionRegistry(), NullLogger.Instance,
            (_, _) => { }, (id, _) => combatIds.Add(id), (_, _) => { }, _ => { });
        Task holder = Task.Run(() =>
        {
            using (fixture.Store.Enter(fixture.Match))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(runner.Run).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(fixture.Match.MatchingId, combatIds);
            Assert.Contains(other.MatchingId, combatIds);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        combatIds.Clear();
        runner.Run();
        Assert.Equal(1, combatIds.Count(id => id == fixture.Match.MatchingId));
    }

    [Fact]
    public void CountdownMatch_RunsCombatWarmup_ButNotBotMovement()
    {
        using var fixture = new Fixture(945107);
        MatchStartGate.RemoveMatching(fixture.Match.MatchingId);
        MatchStartGate.RegisterHumanPlayer(fixture.Match.MatchingId, 11, botCount: 7);
        var steps = new List<string>();
        var runner = new MatchTickRunner(fixture.Store, new GameSessionRegistry(), NullLogger.Instance,
            (_, _) => steps.Add("countdown"), (_, _) => steps.Add("combat"),
            (_, _) => steps.Add("environment"), _ => steps.Add("movement"));

        runner.Run();
        Assert.Equal(new[] { "countdown", "combat" }, steps);
    }

    private sealed class Fixture : IDisposable
    {
        public MatchRuntimeStore Store { get; } = new(NullLogger.Instance);
        public MatchRuntime Match { get; }

        public Fixture(long id)
        {
            Match = Store.GetOrCreate(id);
            Match.Bots.RegisterBots(id, Config.SWARM_MATCH_MAP, [-42],
                new Dictionary<long, Cell> { [-42] = new(0, 0) });
            MatchStartGate.RegisterBotOnlyMatch(id);
        }

        public void Dispose() => MatchStartGate.RemoveMatching(Match.MatchingId);
    }
}
