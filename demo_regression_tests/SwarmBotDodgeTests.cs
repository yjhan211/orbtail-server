using game_server;
using game_server.players.bots;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

/// <summary>
///     봇 투사체 회피 반사 (#232 §9, 2026-08-18): 태양 투사체 직선의 띠 안에 서 있는 봇은
///     그 직선의 수직으로 비켜서고, 띠 밖·지나간 투사체·자기 투사체·다른 구역에는 반응하지 않는다.
///     기하는 교차사격 서버 판정과 같은 바닥면(dy×2)이다.
/// </summary>
public class SwarmBotDodgeTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

    // 원점 (0,0)에서 +X 방향으로 사거리 6, 반폭 0.35, 속도 4.5, 0.55초 예고 뒤 발동.
    private static SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat LineAlongX(long ownerId = 7, double armedInSeconds = 0.55) =>
        new(217001, AreaType.S2Ground, ownerId, 0f, 0f, 1f, 0f,
            GroundLength: 6f, HalfWidth: 0.35f, SweepSpeed: 4.5f,
            Now.AddSeconds(armedInSeconds), Now.AddSeconds(armedInSeconds + 1.5));

    [Fact]
    public void BotOnTheLine_StepsPerpendicularToTheAxis()
    {
        var threats = new[] { LineAlongX() };
        // 축에서 살짝 위(+Y)에 서 있다 → 위로 빠진다.
        var direction = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            threats, 217001, -900000001, new Vector3f(2f, 0.05f, 0f), AreaType.S2Ground, Now);

        Assert.NotNull(direction);
        Assert.Equal(0f, direction!.Value.DirectionX, 3);
        Assert.True(direction.Value.DirectionY > 0.99f, $"수직(+Y)으로 비켜서야 한다: {direction.Value.DirectionY}");
    }

    [Fact]
    public void HoldLastsUntilTheFrontHasPassed()
    {
        // 예고 0.55 + (축 2 - 앞머리 -0.35)/4.5 = 1.07초 뒤 착탄, 몸통 두 배(0.5/4.5)와 여유 0.15를 더한 만큼 커밋.
        var advice = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { LineAlongX() }, 217001, -900000001, new Vector3f(2f, 0.05f, 0f), AreaType.S2Ground, Now);

        Assert.NotNull(advice);
        Assert.InRange(advice!.Value.HoldSeconds, 1.3f, 1.4f);
    }

    [Fact]
    public void BotBelowTheLine_StepsAwayDownward()
    {
        var threats = new[] { LineAlongX() };
        var direction = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            threats, 217001, -900000001, new Vector3f(2f, -0.05f, 0f), AreaType.S2Ground, Now);

        Assert.NotNull(direction);
        Assert.True(direction!.Value.DirectionY < -0.99f, $"아래로 비켜서야 한다: {direction.Value.DirectionY}");
    }

    [Fact]
    public void BotOutsideTheBand_DoesNotReact()
    {
        var threats = new[] { LineAlongX() };
        // 반폭 0.35 + 몸 0.25 + 여유 0.2 = 0.8 (바닥면) → 월드 Y로 0.4. 0.6은 밖이다.
        var direction = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            threats, 217001, -900000001, new Vector3f(2f, 0.6f, 0f), AreaType.S2Ground, Now);

        Assert.Null(direction);
    }

    [Fact]
    public void ProjectileAlreadyPassed_DoesNotReact()
    {
        // 발동 1초 뒤: 앞머리 = -0.35 + 4.5 = 4.15. 축 위치 2에 선 봇은 이미 지나갔다.
        var threats = new[] { LineAlongX(armedInSeconds: -1.0) };
        var direction = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            threats, 217001, -900000001, new Vector3f(2f, 0f, 0f), AreaType.S2Ground, Now);

        Assert.Null(direction);
    }

    [Fact]
    public void OwnShapeOtherAreaOrFarFuture_DoesNotReact()
    {
        Assert.Null(SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { LineAlongX(ownerId: -900000001) }, 217001, -900000001,
            new Vector3f(2f, 0f, 0f), AreaType.S2Ground, Now));
        Assert.Null(SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { LineAlongX() }, 217001, -900000001,
            new Vector3f(2f, 0f, 0f), AreaType.S2Gym1, Now));
        // 축 위치 6(사거리 끝) + 예고 3초 뒤 → 착탄까지 3 + 6.35/4.5 ≈ 4.4초 — 아직 안 움직인다.
        Assert.Null(SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { LineAlongX(armedInSeconds: 3.0) }, 217001, -900000001,
            new Vector3f(6f, 0f, 0f), AreaType.S2Ground, Now));
    }

    [Fact]
    public void OtherMatchingId_DoesNotReact()
    {
        Assert.Null(SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { LineAlongX() },
            matchingId: 217002,
            botPlayerId: -900000001,
            new Vector3f(2f, 0f, 0f),
            AreaType.S2Ground,
            Now));
    }

    [Fact]
    public void DiagonalLine_ReturnsWorldSpacePerpendicular()
    {
        // 바닥면 45° 축 (1/√2, 1/√2). 봇은 원점 앞 축 위. 비켜서는 방향은 바닥면 수직 (-1/√2, 1/√2)를
        // 월드로 되돌린 것 — Y가 절반이라 (-0.894, 0.447) 정규화값.
        float s = MathF.Sqrt(0.5f);
        var threat = new SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat(
            217001, AreaType.S2Ground, 7, 0f, 0f, s, s, 6f, 0.35f, 4.5f, Now.AddSeconds(0.55), Now.AddSeconds(2.05));
        // 축 위 점 (바닥면 (1,1)) = 월드 (1, 0.5). 살짝 왼쪽(+perp)으로 벗어난 곳.
        var direction = SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            new[] { threat }, 217001, -900000002, new Vector3f(0.98f, 0.52f, 0f), AreaType.S2Ground, Now);

        Assert.NotNull(direction);
        Assert.Equal(-0.894f, direction!.Value.DirectionX, 2);
        Assert.Equal(0.447f, direction.Value.DirectionY, 2);
    }
}
