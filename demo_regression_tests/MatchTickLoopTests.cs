using game_server.matches.bots;
using game_server.matches.items;
using game_server.matches.logging;
using game_server.matches.entry;
using game_server.matches;
using game_server.network;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchTickLoopTests
{
    public MatchTickLoopTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData("countdown")]
    [InlineData("combat")]
    [InlineData("environment")]
    [InlineData("movement")]
    public async Task StageFailure_PropagatesWithoutRunningLaterStagesAndReleasesLock(string failingStage)
    {
        using var fixture = new Fixture(945111);
        string[] stages = ["countdown", "combat", "environment", "movement"];
        var called = new List<string>();
        var failure = new InvalidOperationException("stage failure");
        void Process(string stage)
        {
            Assert.True(Monitor.IsEntered(fixture.Match.MatchLock));
            called.Add(stage);
            if (stage == failingStage) throw failure;
        }
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => Process("countdown"), (_, _) => Process("combat"),
            (_, _) => Process("environment"), _ => Process("movement"), (_, _) => { });
        fixture.Loops.Add(loop);
        TestMatchTickServices.ForceNextEnvironmentalTick(loop);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(loop.ProcessTick));
        Assert.Equal(stages.Take(Array.IndexOf(stages, failingStage) + 1), called);
        Assert.False(Monitor.IsEntered(fixture.Match.MatchLock));
        await Task.Run(() =>
        {
            using var scope = fixture.Match.Enter();
            Assert.False(fixture.Match.IsEnded);
        }).WaitAsync(TimeSpan.FromSeconds(5));
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
            locksHeld.Add(Monitor.IsEntered(fixture.Match.MatchLock));
        }
        // 실제 시간을 기다리지 않고 다음 환경 정산이 실행되도록 준비한다.
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => Record("countdown"),
            (_, _) => Record("combat"),
            (_, _) => Record("environment"),
            runtime =>
            {
                Assert.Same(fixture.Match, runtime);
                Record("movement");
            }, (_, _) => { });

        fixture.Loops.Add(loop);
        TestMatchTickServices.ForceNextEnvironmentalTick(loop);
        loop.ProcessTick();
        Assert.Equal(new[] { "countdown", "combat", "environment", "movement" }, steps);
        Assert.All(locksHeld, Assert.True);

        steps.Clear();
        loop.ProcessTick();
        Assert.Equal(new[] { "countdown", "combat", "movement" }, steps);
    }

    [Fact]
    public void CombatFailure_StopsRemainingStagesButDoesNotAffectOtherMatches()
    {
        using var first = new Fixture(945102);
        var second = first.Store.GetOrCreate(945103);
        var combatIds = new List<long>();
        int movements = 0;
        var loop = TestMatchTickServices.CreateLoop(first.Match, first.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => { },
            (id, _) =>
            {
                combatIds.Add(id);
                if (id == first.Match.MatchingId)
                    throw new InvalidOperationException("test combat failure");
            },
            (_, _) => throw new InvalidOperationException("environment should not be due"),
            _ => movements++, (_, _) => { });

        first.Loops.Add(loop);
        Assert.Throws<InvalidOperationException>(loop.ProcessTick);
        Assert.False(Monitor.IsEntered(first.Match.MatchLock));
        var secondLoop = TestMatchTickServices.CreateLoop(second, first.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => { }, (id, _) => combatIds.Add(id), (_, _) => { }, _ => { }, (_, _) => { });
        secondLoop.ProcessTick();
        secondLoop.Stop();
        Assert.Contains(first.Match.MatchingId, combatIds);
        Assert.Contains(second.MatchingId, combatIds);
        Assert.Equal(0, movements);
    }

    [Fact]
    public void TerminalDuringCombat_SkipsMovementAndRemovesRuntime()
    {
        using var fixture = new Fixture(945104);
        int movements = 0;
        int combats = 0;
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => { },
            (_, _) =>
            {
                combats++;
                fixture.Match.TryMarkEnded();
            },
            (_, _) => { },
            _ => movements++, (_, _) => { });

        fixture.Loops.Add(loop);
        loop.ProcessTick();
        loop.ProcessTick();
        Assert.Equal(1, combats);
        Assert.Equal(0, movements);
        Assert.Null(fixture.Store.GetOrNull(fixture.Match.MatchingId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BusyMatch_WaitsForLock_AndRechecksStopBeforeProcessing(int termination)
    {
        using var fixture = new Fixture(945105);
        var other = fixture.Store.GetOrCreate(945106);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        int calls = 0;
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => { }, (_, _) => Interlocked.Increment(ref calls), (_, _) => { }, _ => { }, (_, _) => { });
        Task holder = Task.Run(() =>
        {
            using (fixture.Match.Enter())
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                if (termination == 2) fixture.Match.TryMarkEnded();
            }
        });
        Task? tick = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            tick = Task.Run(() => { attempted.Set(); loop.ProcessTick(); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(tick.IsCompleted);
            int otherCalls = 0;
            var otherLoop = TestGameSessionServices.CreateTickLoopFactory(fixture.Store, _ => otherCalls++)(other, TimeProvider.System);
            try
            {
                await Task.Run(otherLoop.ProcessTick).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, otherCalls);
                Assert.Equal(0, Volatile.Read(ref calls));
                if (termination == 1) loop.Stop();
            }
            finally { otherLoop.Stop(); }
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if (tick != null) await tick.WaitAsync(TimeSpan.FromSeconds(5));
            loop.Stop();
        }
        Assert.Equal(termination == 0 ? 1 : 0, calls);
    }

    [Fact]
    public void CountdownMatch_RunsCombatWarmup_ButNotBotMovement()
    {
        using var fixture = new Fixture(945107);
        MatchStartGate.RemoveMatching(fixture.Match.MatchingId);
        MatchStartGate.RegisterHumanPlayer(fixture.Match.MatchingId, 11, botCount: 7);
        var steps = new List<string>();
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => steps.Add("countdown"), (_, _) => steps.Add("combat"),
            (_, _) => steps.Add("environment"), _ => steps.Add("movement"), (_, _) => { });

        fixture.Loops.Add(loop);
        TestMatchTickServices.ForceNextEnvironmentalTick(loop);
        loop.ProcessTick();
        Assert.Equal(new[] { "countdown", "combat" }, steps);
    }

    [Fact]
    public void BotMovementService_RecordsOnlyTheSuppliedMatchAndPublishesItsMetrics()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(945108);
        var second = store.GetOrCreate(945109);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new BotMovementService( logs,
            NullLogger<BotMovementService>.Instance);
        using (MatchRuntimeStore.Enter(first))
        {
            service.ProcessTick(first, (_, _) => throw new InvalidOperationException("No bots should request a directive."));
            Assert.Equal(1, first.BotTickMetrics.SampleCount);
            Assert.Equal(0, second.BotTickMetrics.SampleCount);
            for (int i = 1; i < SwarmBotTickMetrics.WindowSize; i++)
                service.ProcessTick(first, (_, _) => throw new InvalidOperationException("No bots."));
            Assert.Equal(0, first.BotTickMetrics.SampleCount);
            var entry = Assert.Single(logs.GetRecent(first.MatchingId),
                e => e.Type == "SURVIVOR_BOT_MOVEMENT_TICK_PERFORMANCE");
            Assert.Equal(SwarmBotTickMetrics.WindowSize, entry.BotMovementTickSampleCount);
            Assert.Empty(logs.GetRecent(second.MatchingId));
            first.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(second)) second.TryMarkEnded();
    }

    [Fact]
    public void TerminalDuringCountdownDoesNotRunCombatOrOtherStages()
    {
        using var fixture = new Fixture(945110);
        int laterStages = 0;
        var loop = TestMatchTickServices.CreateLoop(fixture.Match, fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
            (_, _) => fixture.Match.TryMarkEnded(),
            (_, _) => laterStages++,
            (_, _) => laterStages++,
            _ => laterStages++,
            (_, _) => laterStages++);
        fixture.Loops.Add(loop);
        loop.ProcessTick();
        Assert.Equal(0, laterStages);
        Assert.Null(fixture.Store.GetOrNull(fixture.Match.MatchingId));
    }

    private sealed class Fixture : IDisposable
    {
        public MatchRuntimeStore Store { get; } = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        public MatchRuntime Match { get; }
        public HashSet<MatchTickLoop> Loops { get; } = [];

        public Fixture(long id)
        {
            Match = Store.GetOrCreate(id);
            Match.Bots.RegisterBots(id, Config.SWARM_MATCH_MAP, [-42],
                new Dictionary<long, Cell> { [-42] = new(0, 0) });
            MatchStartGate.RegisterBotOnlyMatch(id);
        }

        public void Dispose()
        {
            foreach (var loop in Loops) loop.Stop();
            MatchStartGate.RemoveMatching(Match.MatchingId);
        }
    }
}
