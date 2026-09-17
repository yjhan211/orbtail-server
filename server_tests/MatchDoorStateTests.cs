using System.Collections;
using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace server_tests;

public sealed class MatchDoorStateTests
{
    private readonly string _networkBasePath;

    public MatchDoorStateTests()
    {
        _networkBasePath = FindNetworkBasePath();
        GameDataHelper.SetBasePath(_networkBasePath);
        GameDataHelper.Initialize();
    }

    [Fact]
    public async Task SlowInitializationForOneMatch_DoesNotBlockAnotherMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var slow = store.GetOrCreate(231001);
        var sibling = store.GetOrCreate(231002);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        Task initialization = Task.Run(() =>
        {
            using (slow.Enter())
                slow.Doors.Initialize(new BlockingAreaSequence(entered, release));
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<bool> other = Task.Run(() =>
            {
                using (sibling.Enter())
                {
                    sibling.Doors.Initialize();
                    return sibling.Doors.OpenDoor(990002);
                }
            });
            Assert.True(await other.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(initialization.IsCompleted);
        }
        finally
        {
            release.Set();
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConcurrentOpenDoor_HasOneWinnerAndReturnsSnapshotCopy()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(231003);
        var doors = runtime.Doors;
        using (runtime.Enter())
            doors.Initialize();
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                using (runtime.Enter())
                    return doors.OpenDoor(990003);
            })));
        Assert.Equal(1, results.Count(result => result));
        using (runtime.Enter())
        {
            List<int> snapshot = doors.GetOpenDoors();
            Assert.Contains(990003, snapshot);
            snapshot.Clear();
            Assert.True(doors.IsDoorOpen(990003));
        }
    }

    [Fact]
    public void RuntimeFinalization_ClearsStateAndLateReferenceCannotReinitializeIt()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(231004);
        var doors = runtime.Doors;
        using (MatchRuntimeStore.Enter(runtime))
        {
            doors.Initialize();
            Assert.True(doors.OpenDoor(990004));
            Assert.True(runtime.TryMarkEnded());
        }
        doors.Initialize();
        Assert.False(doors.OpenDoor(990004));
        Assert.False(doors.IsDoorOpen(990004));
        Assert.Empty(doors.GetOpenDoors());
        Assert.Null(store.GetOrNull(231004));
    }

    [Fact]
    public void RuntimeOwnsIndependentDoorStateWithoutRegistration()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        Assert.Null(store.GetOrNull(231005));
        var first = store.GetOrCreate(231005).Doors;
        var second = store.GetOrCreate(231006).Doors;
        Assert.NotSame(first, second);
        Assert.True(first.OpenDoor(990005));
        Assert.False(second.IsDoorOpen(990005));
        Assert.Same(first, store.GetOrCreate(231005).Doors);
    }

    [Fact]
    public void NestedFinalization_ClearsOnlyEndedMatchAtOutermostExit()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var ended = store.GetOrCreate(231009);
        var sibling = store.GetOrCreate(231010);
        ended.Doors.OpenDoor(990009);
        sibling.Doors.OpenDoor(990009);
        using (MatchRuntimeStore.Enter(ended))
        {
            using (MatchRuntimeStore.Enter(ended))
                ended.TryMarkEnded();
            Assert.True(ended.Doors.IsDoorOpen(990009));
        }
        Assert.False(ended.Doors.IsDoorOpen(990009));
        Assert.True(sibling.Doors.IsDoorOpen(990009));
        Assert.Same(sibling, store.GetOrNull(231010));
    }

    [Fact]
    public void Initialize_SeedsOnlyUnlockedInitiallyOpenDoorsPerMatch()
    {
        const int openId = 991001;
        const int closedId = 991002;
        const AreaType area = AreaType.S2Classroom1;
        GameDoorData.Initialize([CreateDoorRow(openId, area, true), CreateDoorRow(closedId, area, false)]);
        try
        {
            var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
            var unlocked = store.GetOrCreate(231007).Doors;
            var locked = store.GetOrCreate(231008).Doors;
            unlocked.Initialize();
            locked.Initialize([area]);
            Assert.True(unlocked.IsDoorOpen(openId));
            Assert.False(unlocked.IsDoorOpen(closedId));
            Assert.False(locked.IsDoorOpen(openId));
            Assert.False(locked.IsDoorOpen(closedId));
            Assert.Equal([openId], unlocked.CloseDoorsForAreas([area]));
            Assert.False(unlocked.IsDoorOpen(openId));
            Assert.True(locked.OpenDoor(closedId));
            Assert.False(unlocked.IsDoorOpen(closedId));
        }
        finally
        {
            GameDoorData.Initialize(CsvHelper.LoadCsv(Path.Combine(_networkBasePath, "Common", "csv", "door_info.csv")));
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
