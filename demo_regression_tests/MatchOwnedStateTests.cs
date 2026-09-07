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
    public void RuntimeOwnsServiceInstances_AndDifferentMatchesStayIsolated()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941001);
        var second = store.GetOrCreate(941002);
        Assert.NotSame(first.Inventory, second.Inventory);
        Assert.NotSame(first.GroundItems, second.GroundItems);
        Assert.NotSame(first.SummonStones, second.SummonStones);
        Assert.NotSame(first.Roster, second.Roster);
        Assert.NotSame(first.Closures, second.Closures);
        Assert.NotSame(first.Encounters, second.Encounters);
        var inventory = first.Inventory;
        var stones = first.SummonStones;
        var roster = first.Roster;

        inventory.AddItem(11, 107000010);
        stones.AddStones(11, 9);
        roster.RegisterEntry(new RosterEntry { PlayerId = 11 });

        Assert.Same(inventory.GetPlayerInventory(11),
            store.GetRequired(first.MatchingId).Inventory.GetPlayerInventory(11));
        Assert.Empty(second.Inventory.GetAllItems(11));
        Assert.Equal(9, store.GetRequired(first.MatchingId).SummonStones.GetSnapshot(11).StoneCount);
        Assert.Equal(0, second.SummonStones.GetSnapshot(11).StoneCount);
        Assert.Null(second.Roster.GetEntry(11));
        Assert.Equal((false, (long?)null), second.Roster.CheckGameOver());
    }

    [Fact]
    public void TerminalRemoval_DetachesAllState_AndLateCallsCannotRecreateMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941003);
        var sibling = store.GetOrCreate(941004);
        var inventory = runtime.Inventory;
        var ground = runtime.GroundItems;
        var stones = runtime.SummonStones;
        var roster = runtime.Roster;
        var closures = runtime.Closures;
        var encounters = runtime.Encounters;
        AreaType area = GameMapData.GetAreas(Config.SWARM_MATCH_MAP).First().AreaType;

        stones.AddStones(11, 5);
        sibling.SummonStones.AddStones(11, 7);
        inventory.AddItem(11, 107000010);
        ground.SpawnItems(area, 0, 0, [107000010]);
        roster.RegisterEntry(new RosterEntry { PlayerId = 11 });
        closures.InitializeMatching();

        using (store.Enter(runtime))
            Assert.True(runtime.TryMarkTerminal());

        Assert.Null(store.Get(runtime.MatchingId));
        Assert.Empty(ground.GetSnapshot(area));
        Assert.Null(roster.GetEntry(11));
        Assert.Null(closures.GetMatchingState());
        Assert.Equal(0, stones.GetSnapshot(11).StoneCount);
        Assert.False(encounters.ResolveCorridorEncounter(11, new(0, 0, 0),
            [(12L, new(0, 0, 0))]).HasEvent);
        Assert.Throws<InvalidOperationException>(() => inventory.AddItem(11, 107000010));
        Assert.Throws<InvalidOperationException>(() => ground.SpawnItems(area, 0, 0, [107000010]));
        Assert.Throws<InvalidOperationException>(() => stones.AddStones(11, 1));
        Assert.Throws<InvalidOperationException>(() => closures.InitializeMatching());
        Assert.Equal(7, sibling.SummonStones.GetSnapshot(11).StoneCount);
        Assert.Single(store.ActiveIds());
    }

    [Fact]
    public void ConcurrentClosureInitialization_UsesOneStateInRuntime()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941005);
        var states = new MatchingClosureState[16];
        Parallel.For(0, states.Length, i => states[i] =
            runtime.Closures.InitializeMatching());
        Assert.All(states, state => Assert.Same(runtime.Closures.GetMatchingState(), state));
    }

    [Fact]
    public void EncounterCooldownAndPresentationCaches_BelongToOneMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
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
        first.Presentation.OrbRecoveryReadyAtUtc[(11, 22, 0)] = DateTime.UtcNow;
        first.Presentation.NextMonsterPositionBroadcastAtUtc = DateTime.UtcNow;
        Assert.Empty(second.Presentation.OrbRecoveryReadyAtUtc);
        Assert.Equal(default, second.Presentation.NextMonsterPositionBroadcastAtUtc);
    }

    [Fact]
    public void ServerAndSession_DoNotRetainMatchComponentFieldsOrConstructorArguments()
    {
        Type[] components = [typeof(InGameInventoryManager), typeof(GroundItemManager),
            typeof(SummonStoneManager), typeof(MatchRosterManager), typeof(AreaClosureManager),
            typeof(EncounterRevealManager)];
        foreach (Type owner in new[] { typeof(game_server.GameServer), typeof(game_server.network.GameClientSession) })
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
