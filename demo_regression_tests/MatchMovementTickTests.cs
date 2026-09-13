using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchMovementTickTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickRunsBotMovementOnlyAfterStartAndAlwaysPreparesMonsters(bool started)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987631);
        var bots = new BotProbe();
        var monsters = new MonsterBehaviorService();
        var movement = new MatchMovementService(bots, monsters, new MatchMonsterSpawnService(monsters));
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        // 봇의 Player도 운영과 동일하게 참가자 목록에 등록한다.
        runtime.RegisterParticipant(runtime.Bots.GetBot(-1)!.Player);
        if (started) runtime.StartGameplay();

        movement.ProcessTick(runtime, DateTime.UtcNow);

        Assert.Equal(started ? 1 : 0, bots.Calls);
        Assert.Equal(started ? 1 : 0, bots.Decisions);
        Assert.True(runtime.Monsters.IsInitialized);
    }

    [Fact]
    public void TickRequiresLockAndDoesNotRunAfterEnd()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987632);
        var movement = new MatchMovementService(null!, null!, null!);
        Assert.Throws<InvalidOperationException>(() => movement.ProcessTick(runtime, DateTime.UtcNow));
        using var scope = runtime.Enter();
        runtime.TryMarkEnded();
        movement.ProcessTick(runtime, DateTime.UtcNow);
        Assert.False(runtime.Monsters.IsInitialized);
    }

    private sealed class BotProbe() : BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Calls { get; private set; }
        public int Decisions { get; private set; }
        public override void DecideMovement(MatchRuntime runtime, long botPlayerId) => Decisions++;
        public override void PlanMovement(MatchRuntime runtime, Bot bot, DateTime now, bool canPlanThisTick)
        {
            Assert.True(Monitor.IsEntered(runtime.MatchLock));
            Calls++;
        }
    }
}
