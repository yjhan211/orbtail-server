using game_server.bots;
using game_server.items;
using game_server;
using game_server.matches;
using game_server.combat;
using game_server.matches.entry;
using game_server.field;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

// 순서·잠금·종료 테스트용 서비스 대역. 운영 Loop에는 콜백을 주입하지 않는다.
internal static class TestMatchTickServices
{
    public static MatchTickLoop CreateLoop(
        MatchRuntime runtime, MatchRuntimeStore store, ILogger logger,
        GroundItemAutoPickupService pickup,
        Action<long, List<GameClientSession>> combat,
        Action<MatchRuntime, List<GameClientSession>> environment,
        Action<MatchRuntime> movement,
        Action<long, GameClientSession[]> field,
        TimeProvider? clock = null) =>
        new(runtime, store, logger, pickup,
            new MatchEntryFailureHandler(store, new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance), new game_server.matches.MatchSessionCleanupService(new InMemoryRedisOperations(), new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger), logger), new Combat(combat), new Environment(environment),
            new Movement(movement),
            new BotDecisionService(store, null!, null!, null!, NullLogger<BotDecisionService>.Instance),
            new Field(field), clock);

    // 실제 ProcessTick을 실행해 시간 조건과 단계별 호출 여부를 검증한다.
    internal sealed class ScheduleProbe : IDisposable
    {
        private readonly MatchTickLoop _loop;
        private readonly MatchRuntime _runtime;
        private readonly ScheduleClock _clock = new();
        private int _environmentCalls;
        private int _closureCalls;

        public ScheduleProbe(MatchRuntime runtime, MatchRuntimeStore store)
        {
            _runtime = runtime;

            _loop = CreateLoop(runtime, store, NullLogger.Instance,
                new GroundItemAutoPickupService(TestGameEventLogs.Create(), NullLogger<GroundItemAutoPickupService>.Instance),
                (_, _) => { },
                (match, _) =>
                {
                    Assert.True(Monitor.IsEntered(match.MatchLock));
                    _environmentCalls++;
                }, _ => { }, (_, _) =>
                {
                    Assert.True(Monitor.IsEntered(runtime.MatchLock));
                    _closureCalls++;
                }, _clock);
        }

        public long LastAreaClosureSecond => ReadInterval("_lastAreaClosureSecond");
        public long LastEnvironmentInterval => ReadInterval("_lastEnvironmentInterval");
        public bool TryBeginAreaClosureTick(DateTime now, DateTime? startedAt)
        {
            int before = _closureCalls;
            RunTick(now, startedAt);
            return _closureCalls != before;
        }
        public bool TryBeginEnvironmentalTick(DateTime now, DateTime? startedAt)
        {
            int before = _environmentCalls;
            RunTick(now, startedAt);
            return _environmentCalls != before;
        }
        public void Dispose()
        {
            _loop.Stop();

        }

        private long ReadInterval(string field) => (long)typeof(MatchTickLoop)
            .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_loop)!;

        private void RunTick(DateTime now, DateTime? startedAt)
        {
            _clock.UtcNow = new DateTimeOffset(now);
            if (startedAt.HasValue)
            {
                typeof(MatchRuntime).GetField("_startsAtUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(_runtime, startedAt);
            }
            else
            {
                typeof(MatchRuntime).GetField("_startsAtUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(_runtime, null);
            }
            _loop.ProcessTick();
        }

        private sealed class ScheduleClock : TimeProvider
        {
            public DateTimeOffset UtcNow { get; set; }
            public override DateTimeOffset GetUtcNow() => UtcNow;
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        }
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public static void ForceNextEnvironmentalTick(MatchTickLoop loop) =>
        typeof(MatchTickLoop)
            .GetField("_lastEnvironmentInterval", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(loop, -1L);



    private sealed class Combat(Action<long, List<GameClientSession>> run)
        : MatchCombatService(null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<MatchCombatService>.Instance)
    {
        public override void ProcessTick(long id, List<GameClientSession> sessions) => run(id, sessions);
    }

    private sealed class Environment(Action<MatchRuntime, List<GameClientSession>> run)
        : MatchEnvironmentService(null!, null!, null!, null!, null!, NullLogger<MatchEnvironmentService>.Instance)
    {
        public override void ProcessTick(MatchRuntime runtime, List<GameClientSession> sessions) => run(runtime, sessions);
    }

    private sealed class Movement(Action<MatchRuntime> run)
        : BotMovementService(null!, NullLogger<BotMovementService>.Instance)
    {
        public override void ProcessTick(MatchRuntime runtime, Func<long, long, SwarmBotDirective> resolveDirective) => run(runtime);
    }

    private sealed class Field(Action<long, GameClientSession[]> run)
        : MatchZoneService(null!, null!, null!, NullLogger<MatchZoneService>.Instance)
    {
        public override void ProcessTick(long id, GameClientSession[] sessions) => run(id, sessions);
    }
}
