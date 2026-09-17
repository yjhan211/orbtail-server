using game_server;
using game_server.matches;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

// 순서·잠금·종료 테스트용 서비스 대역. 운영 Loop에는 콜백을 주입하지 않는다.
internal static class TestMatchTickServices
{
    public static MatchTickLoop CreateLoop(
        MatchRuntime runtime, MatchRuntimeStore store, ILogger logger,
        PlayerPickupService pickup,
        Action<long, List<GameClientSession>> combat,
        Action<MatchRuntime, List<GameClientSession>> damage,
        Action<MatchRuntime> movement,
        Action<MatchRuntime> closure,
        TimeProvider? clock = null) =>
        new(runtime, store, logger, pickup,
            new MatchEntryFailureHandler(store, new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance), new game_server.matches.MatchSessionCleanupService(new InMemoryRedisOperations(), new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger), logger), new Combat(combat), new Field(damage, closure),
            new Movement(movement), new game_server.matches.monsters.MatchMonsterSpawnService(),
            new MatchSynchronizationService(), clock);

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
                new PlayerPickupService(TestGameSessionServices.CreateHealthService(store), NullLogger<PlayerPickupService>.Instance),
                (_, _) => { },
                (match, _) =>
                {
                    Assert.True(Monitor.IsEntered(match.MatchLock));
                    _environmentCalls++;
                }, _ => { }, runtime =>
                {
                    Assert.True(Monitor.IsEntered(runtime.MatchLock));
                    _closureCalls++;
                }, _clock);
        }

        public long LastAreaClosureSecond => _runtime.LastAreaClosureSecond;
        public long LastEnvironmentInterval => _runtime.LastFieldDamageInterval;
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

    public static void ForceNextEnvironmentalTick(MatchRuntime runtime) => runtime.LastFieldDamageInterval = -1;



    private sealed class Combat(Action<long, List<GameClientSession>> run)
        : MatchCombatService(null!, null!, null!,
            null!, null!, null!, null!)
    {
        public override void ProcessTick(MatchRuntime runtime, DateTime nowUtc) => run(runtime.MatchingId, runtime.GetSessions());
    }

    private sealed class Movement(Action<MatchRuntime> run)
        : MatchMoveService(null!, null!)
    {
        public override void ProcessTick(MatchRuntime runtime, DateTime nowUtc) => run(runtime);
    }

    private sealed class Field(Action<MatchRuntime, List<GameClientSession>> damage, Action<MatchRuntime> closure)
        : MatchFieldService(null!, null!, null!, null!, null!, null!, null!)
    {
        public override void ProcessDamageTick(MatchRuntime runtime, DateTime nowUtc) => damage(runtime, runtime.GetSessions());
        public override void ProcessClosureTick(MatchRuntime runtime, DateTime nowUtc) => closure(runtime);
    }
}
