using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class LegacyManittoChainEffectsTests
{
    [Fact]
    public void Elimination_OnlyChangesTheEliminatedPlayer_WhenLegacyChainEffectsAreDisabled()
    {
        const long matchingId = 194001;
        var manager = new ManittoChainManager(NullLogger.Instance);

        manager.RegisterLink(matchingId, CreateLink(1, 2));
        manager.RegisterLink(matchingId, CreateLink(2, 3));
        manager.RegisterLink(matchingId, CreateLink(3, 1));

        var affected = manager.EliminatePlayer(matchingId, 2, EliminationReason.MENTAL_ZERO);

        Assert.Equal(new[] { 2L }, affected.Keys);
        Assert.Equal(ManittoStatus.ELIMINATED, affected[2]);
        Assert.Equal(ManittoStatus.ACTIVE, manager.GetLink(matchingId, 1)!.Status);
        Assert.Equal(ManittoStatus.ACTIVE, manager.GetLink(matchingId, 3)!.Status);
        Assert.Empty(manager.GetTerminalPlayers(matchingId));
    }

    [Fact]
    public void Elimination_PreservesAttackerAndClosureContextInGameResult()
    {
        const long matchingId = 194002;
        var manager = new ManittoChainManager(NullLogger.Instance);

        manager.RegisterLink(matchingId, CreateLink(1, 2));
        manager.RegisterLink(matchingId, CreateLink(2, 1));

        manager.EliminatePlayer(matchingId, 2, EliminationReason.MENTAL_ZERO,
            attackerPlayerId: 1, eliminatedArea: AreaType.Library, isAreaClosureElimination: true);

        var result = Assert.Single(manager.BuildGameResult(matchingId), row => row.playerId == 2);

        Assert.Equal(1, result.attackerPlayerId);
        Assert.Equal(AreaType.Library, result.eliminatedArea);
        Assert.True(result.isAreaClosureElimination);
    }
    [Fact]
    public void Elimination_FixesRankTierAndEnvironmentalCauseAtEliminationTime()
    {
        const long matchingId = 194003;
        var manager = new ManittoChainManager(NullLogger.Instance);

        manager.RegisterLink(matchingId, CreateLink(1, 2));
        manager.RegisterLink(matchingId, CreateLink(2, 3));
        manager.RegisterLink(matchingId, CreateLink(3, 1));

        manager.EliminatePlayer(
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
    private static ChainLink CreateLink(long playerId, long targetPlayerId) => new()
    {
        PlayerId = playerId,
        TargetPlayerId = targetPlayerId,
        MyJobTitle = JobTitle.BROADCAST_MEMBER,
        TargetJobTitle = JobTitle.BROADCAST_MEMBER
    };
}
