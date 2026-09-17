using game_server.players;
using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;

namespace demo_regression_tests;

// 운영과 동일하게 행동 요청 생성과 공통 준비를 연결한다.
internal static class MovementPreparationTestSteps
{
    // 테스트가 요구한 시간 간격을 이전·현재 시각으로 구성한다.
    internal static MovementResult Advance(MatchRuntime runtime,
        network.common.data.models.GameObjectInfo info, MovementState state, MovementRequest request,
        float elapsedSeconds, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        state.LastProcessedAtUtc = now.AddSeconds(-elapsedSeconds);
        return MatchMoveService.Move(runtime, info, state, request, now);
    }
    internal static MovementRequest Bot(BotBehaviorService behavior, MatchRuntime runtime, Bot bot, DateTime now)
    {
        var request = behavior.CreateMovementRequest(runtime, bot, now);
        MatchMoveService.PrepareMovement(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, request, now);
        return request;
    }

    internal static MovementRequest Monster(MonsterBehaviorService behavior, MatchRuntime runtime, Monster monster,
        IReadOnlyList<Player> participants, DateTime now)
    {
        var request = behavior.CreateMovementRequest(runtime, monster, participants, now);
        MatchMoveService.PrepareMovement(runtime, monster.Info.ObjectInfo, monster.Movement, request, now, true);
        return request;
    }
}
