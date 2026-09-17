using network.common.data;
using game_server.matches;
using game_server.players;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchPlayerStateTests
{
    [Fact]
    public void SessionReadsTheRuntimeParticipantWithoutCopiedState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948001);
        var participant = new Player(new PlayerInfo { PlayerId = 10 });
        match.RegisterPlayer(participant);
        var session = TestGameSessionServices.CreateRecipientSession();
        typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, 10L);
        TestGameSessionServices.BindMatch(session, match.MatchingId, store);

        Assert.Same(match.GetPlayer(10), session.Player);
        using (match.Enter())
        {
            participant.Health = 42;
            participant.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
            participant.Interactions.Begin(123, TestTime.Ms(0));
            Assert.Equal(42, session.Player.Health);
            Assert.Equal(AreaType.S2Library1, GameMapData.GetCurrentArea(session.Player.GameInfo.ObjectInfo.MapId, session.Player.GameInfo.ObjectInfo.Cell));
            Assert.True(session.Player.Interactions.Cancel(123));
            Assert.True(match.TryEliminatePlayer(10, EliminationReason.PRESSURE_FIELD));
            Assert.True(session.Player.IsEliminated);
        }
    }

    [Fact]
    public void PreviousConnectionRemoval_PreservesSharedParticipantAndCurrentSession()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948002);
        var participant = new Player(new PlayerInfo { PlayerId = 10 });
        match.RegisterPlayer(participant);
        var previous = TestGameSessionServices.CreateRecipientSession();
        var current = TestGameSessionServices.CreateRecipientSession();
        foreach (var session in new[] { previous, current })
        {
            typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, 10L);
            TestGameSessionServices.BindMatch(session, match.MatchingId, store);
        }
        TestGameSessionServices.AttachSession(current);

        previous.OnRemoved();

        Assert.Same(previous.Player, current.Player);
        Assert.Same(current, participant.Session);
    }

    [Fact]
    public void UnauthenticatedSessionRemoval_DoesNotRequireParticipant()
    {
        var session = TestGameSessionServices.CreateRecipientSession();
        Assert.Null(session.Player);
        session.OnRemoved();
    }
    [Fact]
    public void NullableSession_PreservesPlayersAfterDisconnectAndClearsOnMatchCleanup()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948003);
        var human = new Player(new PlayerInfo { PlayerId = 10 });
        var bot = new Player(new PlayerInfo { PlayerId = -1 });
        match.RegisterPlayer(human);
        match.RegisterPlayer(bot);
        Assert.Null(human.Session);
        Assert.Null(bot.Session);
        Assert.Empty(match.GetSessions());

        var previous = TestGameSessionServices.CreateRecipientSession();
        var current = TestGameSessionServices.CreateRecipientSession();
        foreach (var session in new[] { previous, current })
        {
            typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, 10L);
            typeof(GameClientSession).GetProperty(nameof(GameClientSession.MatchingId))!.SetValue(session, match.MatchingId);
            TestGameSessionServices.BindMatch(session, match.MatchingId, store);
        }
        var registry = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        registry.Register(10, previous);
        var snapshot = match.GetSessions();
        Assert.Same(previous, registry.Register(10, current));
        Assert.False(human.DetachSession(previous));
        Assert.False(registry.Remove(previous));
        Assert.Same(current, human.Session);
        Assert.Same(previous, Assert.Single(snapshot));
        Assert.Same(current, Assert.Single(match.GetSessions()));

        Assert.True(registry.Remove(current));
        Assert.Null(human.Session);
        Assert.Empty(match.GetSessions());
        Assert.Same(human, match.GetPlayer(10));
        Assert.Same(bot, match.GetPlayer(-1));
        Assert.Equal(2, match.BuildGameResult().Count);

        registry.Register(10, current);
        using (match.Enter())
            match.TryMarkEnded();
        Assert.Null(human.Session);
        Assert.Empty(match.GetSessions());
        Assert.True(registry.Remove(current));
    }
}
