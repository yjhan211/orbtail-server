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

    private static ChainLink CreateLink(long playerId, long targetPlayerId) => new()
    {
        PlayerId = playerId,
        TargetPlayerId = targetPlayerId,
        MyJobTitle = JobTitle.BROADCAST_MEMBER,
        TargetJobTitle = JobTitle.BROADCAST_MEMBER
    };
}
