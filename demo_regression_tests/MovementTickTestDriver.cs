using network.common.data;
using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data.models;

namespace demo_regression_tests;

// 행동 선택만 대체하고 운영 ProcessTick의 계획·이동·정지 처리를 검증한다.
internal static class MovementTickTestDriver
{
    internal static (List<BotMovementResult> Movements, IReadOnlyList<long> RequestedBotIds) RunBotTick(
        MatchRuntime runtime, Action<long> decide)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Test tick requires the match lock.");
        var before = runtime.Bots.GetBots().ToDictionary(bot => bot.PlayerId,
            bot => (bot.Player.Position, bot.Player.Velocity, Area: GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell)));
        var behavior = new BehaviorProbe(decide);
        var monsters = new MonsterBehaviorService();
        var service = new MatchMoveService(behavior, monsters);
        if (!runtime.IsEnded) runtime.StartGameplay();
        service.ProcessTick(runtime, DateTime.UtcNow);
        var movements = new List<BotMovementResult>();
        foreach (var bot in runtime.Bots.GetBots())
        {
            var previous = before[bot.PlayerId];
            var player = bot.Player;
            if (Equals(previous.Position, player.Position) && Equals(previous.Velocity, player.Velocity))
                continue;
            movements.Add(new BotMovementResult
            {
                BotPlayerId = bot.PlayerId, FromArea = previous.Area, ToArea = GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell),
                Position = player.Position!, ToCell = player.Cell!, Velocity = player.Velocity,
                Rotation = player.Rotation, IsAreaTransition = previous.Area != GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell)
            });
        }
        return (movements, behavior.RequestedBotIds);
    }

    private sealed class BehaviorProbe(Action<long> decide)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public List<long> RequestedBotIds { get; } = [];
        public override Cell? SelectMovementTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
        {
            RequestedBotIds.Add(bot.PlayerId);
            decide(bot.PlayerId);
            return null;
        }
    }

}
