using game_server;
using game_server.field;
using game_server.items;
using game_server.logging;
using game_server.matches;
using game_server.matches.results;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOwnedStateTests
{
    public MatchOwnedStateTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void RuntimeOwnsServiceInstances_AndDifferentMatchesStayIsolated()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941001);
        var second = store.GetOrCreate(941002);
        Assert.NotSame(first.GroundItems, second.GroundItems);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Closures, second.Closures);
        Assert.NotSame(first.Encounters, second.Encounters);
        var roster = first;

        roster.RegisterParticipant(new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 11 } });
        TestGameSessionServices.AddSummonStones(first, 11, 9);
        TestGameSessionServices.Orbs(first, 11).AddItem(107000010);

        Assert.Same(first.GetParticipant(11)!.Orbs,
            TestGameSessionServices.Orbs(store.GetOrThrow(first.MatchingId), 11));
        Assert.Null(second.GetParticipant(11));
        Assert.Empty(second.GetOrbs(11).GetAllItems());
        Assert.Equal(9, TestGameSessionServices.SummonStones(store.GetOrThrow(first.MatchingId), 11).StoneCount);
        Assert.Equal(0, TestGameSessionServices.SummonStones(second, 11).StoneCount);
        Assert.DoesNotContain(second.BuildGameResult(), row => row.playerId == 11);
        Assert.Equal((false, (long?)null), second.CheckGameOver());
    }

    [Fact]
    public void TerminalRemoval_DetachesAllState_AndLateCallsCannotRecreateMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941003);
        var sibling = store.GetOrCreate(941004);
        var ground = runtime.GroundItems;
        var roster = runtime;
        var closures = runtime.Closures;
        var encounters = runtime.Encounters;
        AreaType area = GameMapData.GetAreas(Config.SWARM_MATCH_MAP).First().AreaType;

        TestGameSessionServices.AddSummonStones(sibling, 11, 7);
        ground.SpawnItems(area, 0, 0, [107000010]);
        roster.RegisterParticipant(new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 11 } });
        TestGameSessionServices.AddSummonStones(runtime, 11, 5);
        TestGameSessionServices.Orbs(runtime, 11).AddItem(107000010);
        closures.InitializeMatching();

        using (MatchRuntimeStore.Enter(runtime))
            Assert.True(runtime.TryMarkEnded());

        Assert.Null(store.GetOrNull(runtime.MatchingId));
        Assert.Empty(ground.GetSnapshot(area));
        Assert.DoesNotContain(roster.BuildGameResult(), row => row.playerId == 11);
        Assert.Null(closures.GetMatchingState());
        Assert.Equal(Player.SummonStoneState.Empty, TestGameSessionServices.SummonStones(runtime, 11));
        Assert.False(encounters.ResolveCorridorEncounter(11, new(0, 0, 0),
            [(12L, new(0, 0, 0))]).HasEvent);
        Assert.Throws<InvalidOperationException>(() => TestGameSessionServices.Orbs(runtime, 11));
        Assert.Throws<InvalidOperationException>(() => ground.SpawnItems(area, 0, 0, [107000010]));
        Assert.Throws<InvalidOperationException>(() => TestGameSessionServices.AddSummonStones(runtime, 11, 1));
        Assert.Throws<InvalidOperationException>(() => closures.InitializeMatching());
        Assert.Equal(7, TestGameSessionServices.SummonStones(sibling, 11).StoneCount);
        Assert.Single(store.ActiveIds());
    }

    [Fact]
    public void ConcurrentClosureInitialization_UsesOneStateInRuntime()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941005);
        var states = new MatchingClosureState[16];
        Parallel.For(0, states.Length, i => states[i] =
            runtime.Closures.InitializeMatching());
        Assert.All(states, state => Assert.Same(runtime.Closures.GetMatchingState(), state));
    }

    [Fact]
    public void EncounterCooldownAndOrbAndMonsterState_BelongToOneMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941006);
        var second = store.GetOrCreate(941007);
        var encounters = first.Encounters;
        var position = new network.common.data.models.Vector3f(0, 0, 0);
        Assert.Equal(EncounterRevealManager.CorridorRevealEventType,
            encounters.ResolveCorridorEncounter(11, position, [(12L, position)]).EventType);
        Assert.NotEqual(EncounterRevealManager.CorridorRevealEventType,
            encounters.ResolveCorridorEncounter(11, position, [(12L, position)]).EventType);
        Assert.Equal(EncounterRevealManager.CorridorRevealEventType,
            second.Encounters.ResolveCorridorEncounter(11, position, [(12L, position)]).EventType);
        first.OrbRecovery.ReadyAtUtc[(11, 22, 0)] = DateTime.UtcNow;
        first.Monsters.NextMonsterPositionBroadcastAtUtc = DateTime.UtcNow;
        Assert.Empty(second.OrbRecovery.ReadyAtUtc);
        Assert.NotSame(first.OrbVisualCache, second.OrbVisualCache);
        Assert.NotSame(first.OrbVisualCache.States, second.OrbVisualCache.States);
        Assert.Equal(default, second.Monsters.NextMonsterPositionBroadcastAtUtc);
    }

    [Fact]
    public void ServerAndSession_DoNotRetainMatchComponentFieldsOrConstructorArguments()
    {
        Type[] components = [typeof(GroundItemManager),
            typeof(AreaClosureManager),
            typeof(EncounterRevealManager)];
        foreach (Type owner in new[] { typeof(game_server.GameServer), typeof(game_server.sessions.GameClientSession) })
        {
            var fields = owner.GetFields(System.Reflection.BindingFlags.Instance |
                                         System.Reflection.BindingFlags.Public |
                                         System.Reflection.BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => components.Contains(field.FieldType));
            var parameters = owner.GetConstructors(System.Reflection.BindingFlags.Instance |
                                                  System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters());
            Assert.DoesNotContain(parameters, parameter => components.Contains(parameter.ParameterType));
        }
    }

    [Fact]
    public void BotElimination_RemovesInventoryOnce_AndKeepsOtherMatchesUntouched()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941101);
        var sibling = store.GetOrCreate(941102);
        const long botId = -42;
        foreach (var runtime in new[] { match, sibling })
        {
            runtime.Bots.RegisterBots(runtime.MatchingId, Config.SWARM_MATCH_MAP,
                [botId], new Dictionary<long, Cell> { [botId] = new(0, 0) });
            runtime.RegisterParticipant(runtime.Bots.GetBot(runtime.MatchingId, botId)!.Player);
            runtime.RegisterParticipant(new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 11 } });
            TestGameSessionServices.Orbs(runtime, botId).AddItem(107000010);
        }
        var bot = match.Bots.GetBot(match.MatchingId, botId)!;
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = TestGameSessionServices.CreateEliminationService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance);
        bot.PathIndex = 3;
        bot.LoopWaitUntil = DateTime.UtcNow.AddMinutes(1);
        using (MatchRuntimeStore.Enter(match))
        {
            service.EliminatePlayer(match, bot.Player, EliminationReason.HEALTH_ZERO, attackerPlayerId: 11);
            var entry = match.BuildGameResult().Single(row => row.playerId == botId);
            Assert.Equal(PlayerMatchStatus.ELIMINATED, entry.finalStatus);
            Assert.True(bot.Player.IsEliminated);
            Assert.Same(bot.Player, match.GetParticipant(botId));
            Assert.Equal(0, bot.PathIndex);
            Assert.Equal(DateTime.MinValue, bot.LoopWaitUntil);
            Assert.Empty(TestGameSessionServices.Orbs(match, botId).GetAllItems());
            int drops = match.GroundItems.GetSnapshot(bot.Player.CurrentArea).Count;
            var eliminatedAt = entry.eliminatedAt;

            service.EliminatePlayer(match, bot.Player, EliminationReason.HEALTH_ZERO, attackerPlayerId: 99);
            Assert.Equal(drops, match.GroundItems.GetSnapshot(bot.Player.CurrentArea).Count);
            Assert.Equal(eliminatedAt, match.BuildGameResult().Single(row => row.playerId == botId).eliminatedAt);
            Assert.Equal(11, match.BuildGameResult().Single(row => row.playerId == botId).attackerPlayerId);
        }
        Assert.False(sibling.Bots.GetBot(sibling.MatchingId, botId)!.Player.IsEliminated);
        Assert.NotEmpty(TestGameSessionServices.Orbs(sibling, botId).GetAllItems());
        Assert.NotEqual(PlayerMatchStatus.ELIMINATED, sibling.BuildGameResult().Single(row => row.playerId == botId).finalStatus);
    }
    [Theory]
    [InlineData(42)]
    [InlineData(-42)]
    public void Elimination_UsesSharedStateAndRecordsRankOnceWithoutSession(long playerId)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941103);
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
        if (playerId < 0)
        {
            match.Bots.RegisterBots(match.MatchingId, Config.SWARM_MATCH_MAP,
                [playerId], new Dictionary<long, Cell> { [playerId] = new(0, 0) });
            player = match.Bots.GetBot(match.MatchingId, playerId)!.Player;
        }
        match.RegisterParticipant(player);
        match.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = 11 } });
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = TestGameSessionServices.CreateEliminationService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance);
        using (match.Enter())
        {
            service.EliminatePlayer(match, player, EliminationReason.PRESSURE_FIELD,
                deferGameOver: true, attackerPlayerId: 11, forcedRank: 5);
            var eliminatedAt = player.EliminatedAt;
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO,
                deferGameOver: true, attackerPlayerId: 99, forcedRank: 9);
            Assert.Null(player.Session);
            Assert.True(player.IsEliminated);
            Assert.Equal(PlayerMatchStatus.ELIMINATED, player.Status);
            Assert.Equal(EliminationReason.PRESSURE_FIELD, player.EliminationReason);
            Assert.Equal(5, player.EliminationRank);
            Assert.Equal(11, player.AttackerPlayerId);
            Assert.Equal(eliminatedAt, player.EliminatedAt);
            Assert.Single(logs.GetRecent(match.MatchingId), entry => entry.Type == GameEventType.Eliminate);
            Assert.Equal((true, (long?)11), match.CheckGameOver());
            Assert.False(match.IsEnded);
        }
    }
    [Fact]
    public void HumanEliminationWithoutSessionDropsInventoryAtStoredPosition()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941104);
        var player = new Player
        {
            Profile = new PlayerInfo { PlayerId = 42 },
            CurrentArea = Config.SWARM_MATCH_GROUND_AREA,
            Position = new Vector3f(0, 0, 0)
        };
        match.RegisterParticipant(player);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = TestGameSessionServices.CreateEliminationService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance);
        using (match.Enter())
        {
            TestGameSessionServices.Orbs(match, player.PlayerId).AddItem(107000010);
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO, deferGameOver: true);
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO, deferGameOver: true);
            Assert.Null(player.Session);
            Assert.Equal(player.CurrentArea, player.EliminatedArea);
            Assert.Empty(TestGameSessionServices.Orbs(match, player.PlayerId).GetAllItems());
            Assert.Single(match.GroundItems.GetSnapshot(player.CurrentArea));
            Assert.Single(logs.GetRecent(match.MatchingId), entry => entry.Type == GameEventType.EliminationDrop);
        }
    }
    [Fact]
    public void InteractableSnapshots_AreIndependentCopiesOfSharedDefinitions()
    {

        var definition = GameInteractableData.GetAll().First(item => item.Actions.Any(action => action.State == 0));
        var area = (AreaType)definition.ZoneId;
        var first = InteractableStateManager.GetAreaObjectStates(area);
        var second = InteractableStateManager.GetAreaObjectStates(area);
        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        first[0].Actions[0].IsExplored = true;
        first[0].Actions.Clear();
        Assert.NotEmpty(second[0].Actions);
        Assert.All(second.SelectMany(item => item.Actions), action => Assert.False(action.IsExplored));
    }
}
