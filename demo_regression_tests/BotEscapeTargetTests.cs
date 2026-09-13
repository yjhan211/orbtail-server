using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotEscapeTargetTests
{
    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void EscapeTargetNeverMovesCloserToThreat(float threatOffset)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981230);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            runtime.RegisterParticipant(bot.Player);
            var position = bot.Player.Position!;
            var threatPosition = new Vector3f(position.X + threatOffset, position.Y, 0f);
            var rival = new Player
            {
                Profile = new PlayerInfo { PlayerId = 1 },
                Position = threatPosition,
                CurrentArea = bot.Player.CurrentArea,
                Health = Config.MAX_HEALTH
            };
            rival.Orbs.TryAddItemWithCapacity(107000020, 8, out _);
            runtime.RegisterParticipant(rival);
            var service = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance);

            service.DecideMovement(runtime, bot.PlayerId);

            Assert.True(bot.FleeDirective);
            var target = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, bot.Movement.DestinationCell);
            float dx = target.X - threatPosition.X;
            float dy = target.Y - threatPosition.Y;
            Assert.True(dx * dx + dy * dy >= threatOffset * threatOffset);
            Assert.NotEqual(AreaType.None, bot.Movement.DestinationArea);
            Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, bot.Movement.DestinationCell));
        }
    }
}
