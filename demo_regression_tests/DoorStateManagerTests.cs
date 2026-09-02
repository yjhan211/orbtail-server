using System.Collections;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class DoorStateManagerTests
{
    private readonly string _networkBasePath;

    public DoorStateManagerTests()
    {
        _networkBasePath = FindNetworkBasePath();
        GameDataHelper.SetBasePath(_networkBasePath);
        GameDataHelper.Initialize();
    }

    [Fact]
    public async Task SlowInitializationForOneMatch_DoesNotBlockAnotherMatch()
    {
        const long slowMatchingId = 231_001;
        const long siblingMatchingId = 231_002;
        var manager = new DoorStateManager();
        Assert.True(manager.RegisterMatching(slowMatchingId));
        Assert.True(manager.RegisterMatching(siblingMatchingId));
        var slowEnumerationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSlowEnumeration = new ManualResetEventSlim(false);

        Task slowInitialization = Task.Run(() => manager.InitializeMatching(
            slowMatchingId,
            new BlockingAreaSequence(slowEnumerationEntered, releaseSlowEnumeration)));
        await slowEnumerationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            Task<bool> sibling = Task.Run(() =>
            {
                manager.InitializeMatching(siblingMatchingId);
                return manager.OpenDoor(siblingMatchingId, 990_002);
            });

            Assert.True(await sibling.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(slowInitialization.IsCompleted);
        }
        finally
        {
            releaseSlowEnumeration.Set();
            await slowInitialization.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ConcurrentOpenDoorForSameMatch_HasOneWinnerAndReturnsSnapshotCopy()
    {
        const long matchingId = 231_003;
        const int doorId = 990_003;
        var manager = new DoorStateManager();
        Assert.True(manager.RegisterMatching(matchingId));
        manager.InitializeMatching(matchingId);

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => manager.OpenDoor(matchingId, doorId))));

        Assert.Equal(1, results.Count(result => result));
        List<int> snapshot = manager.GetOpenDoors(matchingId);
        Assert.Contains(doorId, snapshot);
        snapshot.Clear();
        Assert.True(manager.IsDoorOpen(matchingId, doorId));
    }

    [Fact]
    public void RuntimeFinalization_ClearsStateAndLateGameplayCannotRecreateIt()
    {
        const long matchingId = 231_004;
        const int doorId = 990_004;
        var manager = new DoorStateManager();
        var store = new MatchRuntimeStore(
            NullLogger.Instance,
            id => Assert.True(manager.RegisterMatching(id)),
            [new MatchCleanupStep("doors", manager.ClearMatching)]);

        MatchRuntime runtime = store.GetOrCreate(matchingId);
        using (store.Enter(runtime))
        {
            manager.InitializeMatching(matchingId);
            Assert.True(manager.OpenDoor(matchingId, doorId));
            Assert.True(runtime.TryMarkTerminal());
        }

        Assert.False(manager.OpenDoor(matchingId, doorId));
        Assert.False(manager.IsDoorOpen(matchingId, doorId));
        Assert.Empty(manager.GetOpenDoors(matchingId));
        Assert.Null(store.Get(matchingId));
    }

    [Fact]
    public void MissingGameplayState_FailsClosedUntilExplicitInitialization()
    {
        const long matchingId = 231_005;
        const int doorId = 990_005;
        var manager = new DoorStateManager();

        Assert.False(manager.OpenDoor(matchingId, doorId));
        Assert.False(manager.IsDoorOpen(matchingId, doorId));
        Assert.Empty(manager.GetOpenDoors(matchingId));

        manager.InitializeMatching(matchingId);
        Assert.True(manager.OpenDoor(matchingId, doorId));
    }

    [Fact]
    public void InitializeMatching_SeedsOnlyUnlockedInitiallyOpenDoorsPerMatch()
    {
        const long unlockedMatchingId = 231_006;
        const long lockedMatchingId = 231_007;
        const int initiallyOpenDoorId = 991_001;
        const int initiallyClosedDoorId = 991_002;
        const AreaType area = AreaType.S2Classroom1;
        GameDoorData.Initialize(
        [
            CreateDoorRow(initiallyOpenDoorId, area, initiallyOpen: true),
            CreateDoorRow(initiallyClosedDoorId, area, initiallyOpen: false)
        ]);

        try
        {
            var manager = new DoorStateManager();
            Assert.True(manager.RegisterMatching(unlockedMatchingId));
            Assert.True(manager.RegisterMatching(lockedMatchingId));

            manager.InitializeMatching(unlockedMatchingId);
            manager.InitializeMatching(lockedMatchingId, [area]);

            Assert.True(manager.IsDoorOpen(unlockedMatchingId, initiallyOpenDoorId));
            Assert.False(manager.IsDoorOpen(unlockedMatchingId, initiallyClosedDoorId));
            Assert.False(manager.IsDoorOpen(lockedMatchingId, initiallyOpenDoorId));
            Assert.False(manager.IsDoorOpen(lockedMatchingId, initiallyClosedDoorId));
            Assert.Equal(
                [initiallyOpenDoorId],
                manager.CloseDoorsForAreas(unlockedMatchingId, [area]));
            Assert.False(manager.IsDoorOpen(unlockedMatchingId, initiallyOpenDoorId));
            Assert.True(manager.OpenDoor(lockedMatchingId, initiallyClosedDoorId));
            Assert.False(manager.IsDoorOpen(unlockedMatchingId, initiallyClosedDoorId));
        }
        finally
        {
            GameDoorData.Initialize(CsvHelper.LoadCsv(
                Path.Combine(_networkBasePath, "Common", "csv", "door_info.csv")));
        }
    }

    private static CsvRow CreateDoorRow(int doorId, AreaType area, bool initiallyOpen) =>
        new(
            [
                "door_id",
                "required_item_id",
                "position_x",
                "position_y",
                "fallback_cell_x",
                "fallback_cell_y",
                "interact_distance",
                "area_type",
                "is_initially_open",
                "area_type_b",
                "gauge_seconds"
            ],
            [
                doorId.ToString(),
                "0",
                "0",
                "0",
                "0",
                "0",
                "2",
                ((int)area).ToString(),
                initiallyOpen ? "1" : "0",
                ((int)AreaType.S2Corridor1).ToString(),
                "0"
            ]);

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }

    private sealed class BlockingAreaSequence(
        TaskCompletionSource entered,
        ManualResetEventSlim release) : IEnumerable<AreaType>
    {
        public IEnumerator<AreaType> GetEnumerator()
        {
            entered.TrySetResult();
            release.Wait();
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
