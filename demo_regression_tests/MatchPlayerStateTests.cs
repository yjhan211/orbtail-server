using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using System.Reflection;

namespace demo_regression_tests;

public sealed class MatchPlayerStateTests
{
    [Fact]
    public void SessionReadsTheRuntimeParticipantWithoutCopiedState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948001);
        var participant = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 10 } };
        match.RegisterParticipant(participant);
        var session = TestGameSessionServices.CreateRecipientSession();
        typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, 10L);
        TestGameSessionServices.BindMatch(session, match.MatchingId, store);

        Assert.Same(match.GetParticipant(10), session._player);
        using (match.Enter())
        {
            participant.Health = 42;
            participant.CurrentArea = AreaType.S2Library1;
            participant.BeginInteraction(123);
            Assert.Equal(42, session._player.Health);
            Assert.Equal(AreaType.S2Library1, session._player.CurrentArea);
            Assert.True(session._player.TryFinishInteraction(123));
            Assert.True(match.TryEliminatePlayer(10, EliminationReason.PRESSURE_FIELD));
            Assert.True(session._player.IsEliminated);
        }

        foreach (string name in new[] { "CurrentHealth", "Health", "CurrentArea", "CurrentMapId", "LastValidatedPosition", "Condition", "Movement", "PlayerMatchStatus", "IsEliminated" })
            Assert.Null(typeof(GameClientSession).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void PreviousConnectionRemoval_DoesNotClearSharedParticipantBuffs()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948002);
        var participant = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 10 } };
        match.RegisterParticipant(participant);
        var previous = TestGameSessionServices.CreateRecipientSession();
        var current = TestGameSessionServices.CreateRecipientSession();
        foreach (var session in new[] { previous, current })
        {
            typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, 10L);
            TestGameSessionServices.BindMatch(session, match.MatchingId, store);
        }
        match.Sessions[10] = current;
        participant.AddPeriodicBuff(default, 1, 1);

        previous.OnRemoved();

        Assert.Same(previous._player, current._player);
        Assert.True(current._player.HasPeriodicBuffs);
    }

    [Fact]
    public void UnauthenticatedSessionRemoval_DoesNotRequireParticipant()
    {
        var session = TestGameSessionServices.CreateRecipientSession();
        Assert.Null(session._player);
        session.OnRemoved();
    }
}
