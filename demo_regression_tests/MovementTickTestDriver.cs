using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

// 행동 선택만 대체하고 운영 ProcessTick의 계획·이동·정지 처리를 검증한다.
internal static class MovementTickTestDriver
{
    internal static (List<BotMovementResult> Movements, long PlanningBotId) RunBotTick(
        MatchRuntime runtime, Action<long> decide)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Test tick requires the match lock.");
        var before = runtime.Bots.GetBots().ToDictionary(bot => bot.PlayerId,
            bot => (bot.Player.Position, bot.Player.Velocity, bot.Player.CurrentArea));
        var behavior = new BehaviorProbe(decide);
        var monsters = new MonsterBehaviorService();
        var service = new MatchMovementService(behavior, monsters, new MatchMonsterSpawnService(monsters));
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
                BotPlayerId = bot.PlayerId, FromArea = previous.CurrentArea, ToArea = player.CurrentArea,
                Position = player.Position!, ToCell = player.Cell!, Velocity = player.Velocity,
                Rotation = player.Rotation, IsAreaTransition = previous.CurrentArea != player.CurrentArea
            });
        }
        return (movements, behavior.PlanningBotId);
    }

    private sealed class BehaviorProbe(Action<long> decide)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public long PlanningBotId { get; private set; }
        private Action? _restoreTarget;
        public override void SelectMovementTarget(MatchRuntime runtime, Bot bot)
        {
            _restoreTarget?.Invoke();
            decide(bot.PlayerId);
        }
        public override void PrepareMovement(MatchRuntime runtime, Bot bot, DateTime now, bool canPlanThisTick)
        {
            // 실행 검증용으로 미리 지정한 목표를 행동 선택 단계에서 다시 제공한다.
            var destination = bot.Movement.Destination;
            var area = bot.Movement.DestinationArea;
            _restoreTarget = () =>
            {
                bot.Movement.Destination = destination;
                bot.Movement.DestinationArea = area;
            };
            if (canPlanThisTick) PlanningBotId = bot.PlayerId;
            base.PrepareMovement(runtime, bot, now, canPlanThisTick);
        }
    }
}
