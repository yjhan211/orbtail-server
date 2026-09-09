using game_server;
using game_server.matches;
using game_server.matches.combat;
using game_server.matches.entry;
using game_server.matches.field;
using game_server.services;
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
        Action<IEnumerable<long>, IReadOnlyCollection<GameClientSession>> countdown,
        Action<long, List<GameClientSession>> combat,
        Action<MatchRuntime, List<GameClientSession>> environment,
        Action<MatchRuntime> movement,
        Action<long, GameClientSession[]> field,
        TimeProvider? clock = null) =>
        new(runtime, store, logger, pickup,
            new Countdown(countdown), new Combat(combat), new Environment(environment),
            new Movement(movement),
            new BotDecisionService(store, null!, null!, null!, NullLogger<BotDecisionService>.Instance),
            new Field(field), clock);

    private sealed class Countdown(Action<IEnumerable<long>, IReadOnlyCollection<GameClientSession>> run)
        : MatchCountdownService(null!, null!, NullLogger.Instance)
    {
        public override void CheckEntryAndBroadcast(IEnumerable<long> ids, IReadOnlyCollection<GameClientSession> sessions) => run(ids, sessions);
    }

    private sealed class Combat(Action<long, List<GameClientSession>> run)
        : MatchCombatService(null!, GameServerDevOptions.Disabled, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<MatchCombatService>.Instance)
    {
        public override void ProcessTick(long id, List<GameClientSession> sessions) => run(id, sessions);
    }

    private sealed class Environment(Action<MatchRuntime, List<GameClientSession>> run)
        : MatchEnvironmentService(null!, null!, null!, null!, GameServerDevOptions.Disabled, NullLogger<MatchEnvironmentService>.Instance)
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
