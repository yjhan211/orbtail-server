using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchRosterManagerTests
{
    [Fact]
    public void Elimination_OnlyChangesTheEliminatedPlayer_WhenLegacyChainEffectsAreDisabled()
    {
        const long matchingId = 194001;
        var manager = new MatchRosterManager(NullLogger.Instance);

        manager.RegisterEntry(matchingId, CreateLink(1, 2));
        manager.RegisterEntry(matchingId, CreateLink(2, 3));
        manager.RegisterEntry(matchingId, CreateLink(3, 1));

        var affected = manager.TryEliminatePlayer(matchingId, 2, EliminationReason.MENTAL_ZERO).AffectedPlayers;

        Assert.Equal(new[] { 2L }, affected.Keys);
        Assert.Equal(PlayerMatchStatus.ELIMINATED, affected[2]);
        Assert.Equal(PlayerMatchStatus.ACTIVE, manager.GetEntry(matchingId, 1)!.Status);
        Assert.Equal(PlayerMatchStatus.ACTIVE, manager.GetEntry(matchingId, 3)!.Status);
    }

    [Fact]
    public void Elimination_PreservesAttackerAndClosureContextInGameResult()
    {
        const long matchingId = 194002;
        var manager = new MatchRosterManager(NullLogger.Instance);

        manager.RegisterEntry(matchingId, CreateLink(1, 2));
        manager.RegisterEntry(matchingId, CreateLink(2, 1));

        manager.TryEliminatePlayer(matchingId, 2, EliminationReason.MENTAL_ZERO,
            attackerPlayerId: 1, eliminatedArea: AreaType.S2Library1, isAreaClosureElimination: true);

        var result = Assert.Single(manager.BuildGameResult(matchingId), row => row.playerId == 2);

        Assert.Equal(1, result.attackerPlayerId);
        Assert.Equal(AreaType.S2Library1, result.eliminatedArea);
        Assert.True(result.isAreaClosureElimination);
    }
    [Fact]
    public void Elimination_FixesRankTierAndEnvironmentalCauseAtEliminationTime()
    {
        const long matchingId = 194003;
        var manager = new MatchRosterManager(NullLogger.Instance);

        manager.RegisterEntry(matchingId, CreateLink(1, 2));
        manager.RegisterEntry(matchingId, CreateLink(2, 3));
        manager.RegisterEntry(matchingId, CreateLink(3, 1));

        manager.TryEliminatePlayer(
            matchingId,
            2,
            EliminationReason.MENTAL_ZERO,
            isOvertimeElimination: true,
            forcedRank: 3,
            finalOrbTier: 2);

        var result = Assert.Single(manager.BuildGameResult(matchingId), row => row.playerId == 2);
        Assert.Equal(3, result.eliminationRank);
        Assert.Equal(2, result.finalOrbTier);
        Assert.True(result.isOvertimeElimination);
        Assert.False(result.isAreaClosureElimination);
    }
    [Fact]
    public void Elimination_AppliesOnlyOnceAndPreservesTheFirstResult()
    {
        const long matchingId = 194004;
        var manager = new MatchRosterManager(NullLogger.Instance);

        manager.RegisterEntry(matchingId, CreateLink(1, 2));
        manager.RegisterEntry(matchingId, CreateLink(2, 3));
        manager.RegisterEntry(matchingId, CreateLink(3, 1));

        var first = manager.TryEliminatePlayer(
            matchingId, 2, EliminationReason.MENTAL_ZERO,
            attackerPlayerId: 1, forcedRank: 3, finalOrbTier: 2);
        var duplicate = manager.TryEliminatePlayer(
            matchingId, 2, EliminationReason.DETECTED,
            attackerPlayerId: 3, forcedRank: 2, finalOrbTier: 3);

        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.Empty(duplicate.AffectedPlayers);

        var result = Assert.Single(manager.BuildGameResult(matchingId), row => row.playerId == 2);
        Assert.Equal(EliminationReason.MENTAL_ZERO, result.reason);
        Assert.Equal(1, result.attackerPlayerId);
        Assert.Equal(3, result.eliminationRank);
        Assert.Equal(2, result.finalOrbTier);
    }

    // #227 1단계: 본체 HP 0과 시간 종료·폐쇄가 같은 틱에 겹쳐도 탈락은 한 번만 확정되어야 한다.
    // 중복이 통과하면 생존 수가 여러 번 줄어 뒤 사람의 등수가 밀리고, 절단 보상과 탈락 드롭이
    // 겹쳐 지급된다. 등수는 남은 생존 수에서 나오므로 다음 탈락자의 등수가 그 증거다.
    [Fact]
    public void Elimination_SameTickCollision_DecrementsAliveCountOnce()
    {
        const long matchingId = 227001;
        var manager = new MatchRosterManager(NullLogger.Instance);

        manager.RegisterEntry(matchingId, CreateLink(1, 2));
        manager.RegisterEntry(matchingId, CreateLink(2, 3));
        manager.RegisterEntry(matchingId, CreateLink(3, 1));

        // 본체 HP 0 — 첫 확정.
        var byBodyHp = manager.TryEliminatePlayer(
            matchingId, 2, EliminationReason.MENTAL_ZERO, attackerPlayerId: 1);
        // 같은 틱의 시간 종료·폐쇄 정산이 같은 사람을 다시 밀어 넣는다.
        var byOvertime = manager.TryEliminatePlayer(
            matchingId, 2, EliminationReason.MENTAL_ZERO, isOvertimeElimination: true);
        var byClosure = manager.TryEliminatePlayer(
            matchingId, 2, EliminationReason.MENTAL_ZERO, isAreaClosureElimination: true);

        Assert.True(byBodyHp.Applied);
        Assert.False(byOvertime.Applied);
        Assert.False(byClosure.Applied);

        // 생존 수가 한 번만 줄었다면 다음 탈락자의 등수는 2다 — 세 번 줄었으면 0으로 밀린다.
        Assert.True(manager.TryEliminatePlayer(matchingId, 3, EliminationReason.MENTAL_ZERO).Applied);

        var results = manager.BuildGameResult(matchingId);
        var second = Assert.Single(results, row => row.playerId == 2);
        Assert.Equal(3, second.eliminationRank);
        // 첫 확정의 맥락(공격자)이 뒤 호출에 덮이지 않아야 전리품 정산도 한 번으로 남는다.
        Assert.Equal(1, second.attackerPlayerId);
        Assert.Equal(2, Assert.Single(results, row => row.playerId == 3).eliminationRank);
    }

    private static RosterEntry CreateLink(long playerId, long targetPlayerId) => new()
    {
        PlayerId = playerId,
        TargetPlayerId = targetPlayerId,
    };
}
