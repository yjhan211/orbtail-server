using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

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
    public void ServicesShareOneRuntimeState_AndDifferentMatchesStayIsolated()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941001);
        var second = store.GetOrCreate(941002);
        var inventory = new InGameInventoryManager(store.Get);
        var anotherInventoryService = new InGameInventoryManager(store.Get);
        var stones = new SummonStoneManager(store.Get);
        var roster = new MatchRosterManager(store.Get, NullLogger.Instance);

        inventory.AddItem(first.MatchingId, 11, 107000010);
        stones.AddStones(first.MatchingId, 11, 9);
        roster.RegisterEntry(first.MatchingId, new RosterEntry { PlayerId = 11 });

        Assert.Same(inventory.GetPlayerInventory(first.MatchingId, 11),
            anotherInventoryService.GetPlayerInventory(first.MatchingId, 11));
        Assert.Empty(inventory.GetAllItems(second.MatchingId, 11));
        Assert.Equal(9, new SummonStoneManager(store.Get).GetSnapshot(first.MatchingId, 11).StoneCount);
        Assert.Equal(0, stones.GetSnapshot(second.MatchingId, 11).StoneCount);
        Assert.Null(roster.GetEntry(second.MatchingId, 11));
        Assert.Equal((false, (long?)null), roster.CheckGameOver(second.MatchingId));
    }

    [Fact]
    public void TerminalRemoval_DetachesAllState_AndLateCallsCannotRecreateMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941003);
        var sibling = store.GetOrCreate(941004);
        var inventory = new InGameInventoryManager(store.Get);
        var ground = new GroundItemManager(store.Get);
        var stones = new SummonStoneManager(store.Get);
        var roster = new MatchRosterManager(store.Get, NullLogger.Instance);
        var closures = new AreaClosureManager(store.Get, NullLogger.Instance);
        var encounters = new EncounterRevealManager(store.Get);
        AreaType area = GameMapData.GetAreas(Config.SWARM_MATCH_MAP).First().AreaType;

        stones.AddStones(runtime.MatchingId, 11, 5);
        stones.AddStones(sibling.MatchingId, 11, 7);
        inventory.AddItem(runtime.MatchingId, 11, 107000010);
        ground.SpawnItems(runtime.MatchingId, area, 0, 0, [107000010]);
        roster.RegisterEntry(runtime.MatchingId, new RosterEntry { PlayerId = 11 });
        closures.InitializeMatching(runtime.MatchingId);

        using (store.Enter(runtime))
            Assert.True(runtime.TryMarkTerminal());

        Assert.Null(store.Get(runtime.MatchingId));
        Assert.Empty(ground.GetSnapshot(runtime.MatchingId, area));
        Assert.Null(roster.GetEntry(runtime.MatchingId, 11));
        Assert.Null(closures.GetMatchingState(runtime.MatchingId));
        Assert.Equal(0, stones.GetSnapshot(runtime.MatchingId, 11).StoneCount);
        Assert.False(encounters.ResolveCorridorEncounter(runtime.MatchingId, 11, new(0, 0, 0),
            [(12L, new(0, 0, 0))]).HasEvent);
        Assert.Throws<InvalidOperationException>(() => inventory.AddItem(runtime.MatchingId, 11, 107000010));
        Assert.Throws<InvalidOperationException>(() => ground.SpawnItems(runtime.MatchingId, area, 0, 0, [107000010]));
        Assert.Throws<InvalidOperationException>(() => stones.AddStones(runtime.MatchingId, 11, 1));
        Assert.Throws<InvalidOperationException>(() => closures.InitializeMatching(runtime.MatchingId));
        Assert.Equal(7, stones.GetSnapshot(sibling.MatchingId, 11).StoneCount);
        Assert.Single(store.ActiveIds());
    }

    [Fact]
    public void ConcurrentClosureInitialization_UsesOneStateAcrossServiceInstances()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941005);
        var states = new MatchingClosureState[16];
        Parallel.For(0, states.Length, i => states[i] =
            new AreaClosureManager(store.Get, NullLogger.Instance).InitializeMatching(runtime.MatchingId));
        Assert.All(states, state => Assert.Same(runtime.Closure, state));
    }

    [Fact]
    public void EncounterCooldownAndPresentationCaches_BelongToOneMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941006);
        var second = store.GetOrCreate(941007);
        var encounters = new EncounterRevealManager(store.Get);
        var position = new network.common.data.models.Vector3f(0, 0, 0);
        Assert.Equal(EncounterRevealManager.CorridorRevealEventType,
            encounters.ResolveCorridorEncounter(first.MatchingId, 11, position, [(12L, position)]).EventType);
        Assert.NotEqual(EncounterRevealManager.CorridorRevealEventType,
            encounters.ResolveCorridorEncounter(first.MatchingId, 11, position, [(12L, position)]).EventType);
        Assert.Equal(EncounterRevealManager.CorridorRevealEventType,
            encounters.ResolveCorridorEncounter(second.MatchingId, 11, position, [(12L, position)]).EventType);
        first.Presentation.OrbRecoveryReadyAtUtc[(11, 22, 0)] = DateTime.UtcNow;
        first.Presentation.NextMonsterPositionBroadcastAtUtc = DateTime.UtcNow;
        Assert.Empty(second.Presentation.OrbRecoveryReadyAtUtc);
        Assert.Equal(default, second.Presentation.NextMonsterPositionBroadcastAtUtc);
    }

    [Fact]
    public void InteractableSnapshots_AreIndependentCopiesOfSharedDefinitions()
    {
        var manager = new InteractableStateManager();
        var definition = GameInteractableData.GetAll().First(item => item.Actions.Any(action => action.State == 0));
        var area = (AreaType)definition.ZoneId;
        var first = manager.GetAreaObjectStates(area);
        var second = manager.GetAreaObjectStates(area);
        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        first[0].Actions[0].IsExplored = true;
        first[0].Actions.Clear();
        Assert.NotEmpty(second[0].Actions);
        Assert.All(second.SelectMany(item => item.Actions), action => Assert.False(action.IsExplored));
    }
}
