using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

/// <summary>
///     매치별 잠금 (#331) 불변식: 같은 매치 직렬화·다른 매치 병렬, 바쁜 펄스 드롭(따라잡기 없음),
///     터미널 정리는 최외곽 스코프 탈출에서 정확히 한 번, 정리 뒤 재생성 없음, 후처리는 잠금 밖 한 번.
/// </summary>
public sealed class MatchRuntimeStoreTests
{
    [Fact]
    public void Constructor_RejectsMissingLifecycleService()
    {
        Assert.Throws<ArgumentNullException>(() => new MatchRuntimeStore(NullLogger<MatchRuntime>.Instance, null!));
    }
    [Fact]
    public void RuntimeAndStoreScopes_ShareDepthAndCleanupOnlyOnOutermostExit()
    {
        int afterCount = 0;
        MatchRuntime? runtime = null;
        var store = CreateStore(
            onRedisCleanup: _ =>
            {
                Assert.False(Monitor.IsEntered(runtime!.MatchLock));
                afterCount++;
            });
        runtime = store.GetOrCreate(90001);
        using (runtime.Enter())
        {
            using (runtime.Enter())
                Assert.True(runtime.TryMarkEnded());
            Assert.Equal(0, afterCount);
            Assert.Same(runtime, store.GetOrNull(90001));
        }
        Assert.Equal(1, afterCount);
        Assert.Null(store.GetOrNull(90001));
        using (runtime.Enter())
            Assert.True(runtime.IsEnded);
        Assert.Equal(1, afterCount);
    }

    private static MatchRuntimeStore CreateStore(
        Action<long>? onRedisCleanup = null) =>
        TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance, onRedisCleanup);

    [Fact]
    public void SameMatch_Serializes()
    {
        MatchRuntimeStore store = CreateStore();
        store.GetOrCreate(1);
        int concurrent = 0;
        int maxConcurrent = 0;
        int completed = 0;

        Parallel.For(0, 64, _ =>
        {
            Assert.True(store.Enter(1, out MatchLockScope scope));
            using (scope)
            {
                int now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                Thread.SpinWait(2000);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            }
        });

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(64, completed);
    }

    [Fact]
    public async Task DifferentMatches_Overlap()
    {
        MatchRuntimeStore store = CreateStore();
        store.GetOrCreate(1);
        store.GetOrCreate(2);
        using var firstEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task holder = Task.Run(() =>
        {
            Assert.True(store.Enter(1, out MatchLockScope scope));
            using (scope)
            {
                firstEntered.Set();
                release.Wait();
            }
        });

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(store.TryEnter(2, out MatchLockScope other));
        other.Dispose();
        release.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TryEnter_Busy_SkipsNoCatchUp()
    {
        MatchRuntimeStore store = CreateStore();
        store.GetOrCreate(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int pulsesRun = 0;

        Task holder = Task.Run(() =>
        {
            Assert.True(store.Enter(1, out MatchLockScope scope));
            using (scope)
            {
                pulsesRun++;
                entered.Set();
                release.Wait();
            }
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(store.TryEnter(1, out _));
        Assert.False(store.TryEnter(1, out _));
        release.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));

        // 놓친 두 펄스는 밀리지 않는다 — 다음 진입은 그냥 한 번이다.
        Assert.True(store.TryEnter(1, out MatchLockScope next));
        using (next)
        {
            pulsesRun++;
        }

        Assert.Equal(2, pulsesRun);
        Assert.False(store.TryEnter(0, out _));
        Assert.False(store.TryEnter(999, out _));
    }

    [Fact]
    public void TryMarkTerminal_ConcurrentWinnerOnce()
    {
        MatchRuntimeStore store = CreateStore();
        MatchRuntime runtime = store.GetOrCreate(1);
        int winners = 0;

        Parallel.For(0, 32, _ =>
        {
            using MatchLockScope scope = runtime.Enter();
            if (runtime.TryMarkEnded())
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.True(runtime.IsEnded);
        Assert.Throws<InvalidOperationException>(() => runtime.TryMarkEnded());
    }

    [Fact]
    public void Terminal_CleanupOnceAtDepthZero_ThenRemoved()
    {
        var cleanupCalls = new List<long>();
        MatchRuntimeStore store = CreateStore(
            onRedisCleanup: cleanupCalls.Add);
        MatchRuntime runtime = store.GetOrCreate(7);

        using (MatchLockScope scope = runtime.Enter())
        {
            Assert.True(runtime.TryMarkEnded());
            Assert.Empty(cleanupCalls);
            Assert.NotNull(store.GetOrNull(7));
        }

        Assert.Equal([7L], cleanupCalls);
        Assert.Null(store.GetOrNull(7));
        Assert.DoesNotContain(7L, store.ActiveIds());

        // 잡아 둔 런타임으로 다시 들어가도 정리는 반복되지 않는다.
        using (runtime.Enter())
        {
        }

        Assert.Single(cleanupCalls);
    }

    [Fact]
    public void Terminal_NestedMark_DefersCleanupToOuterScope()
    {
        var cleanupCalls = new List<long>();
        MatchRuntimeStore store = CreateStore(
            onRedisCleanup: cleanupCalls.Add);
        MatchRuntime runtime = store.GetOrCreate(3);

        using (MatchLockScope outer = runtime.Enter())
        {
            using (MatchLockScope inner = runtime.Enter())
            {
                Assert.True(runtime.TryMarkEnded());
            }

            // 안쪽 스코프가 닫혀도 바깥이 아직 상태를 만지고 있으므로 정리는 미뤄진다.
            Assert.Empty(cleanupCalls);
            Assert.True(runtime.IsEnded);
            Assert.NotNull(store.GetOrNull(3));
        }

        Assert.Equal([3L], cleanupCalls);
        Assert.Null(store.GetOrNull(3));
    }

    [Fact]
    public void AfterRelease_RunsOutsideLockOnce()
    {
        MatchRuntimeStore store = CreateStore(onRedisCleanup: null);
        MatchRuntime runtime = store.GetOrCreate(5);
        int runs = 0;
        bool heldDuringRun = true;

        using (MatchLockScope outer = runtime.Enter())
        {
            using (MatchLockScope inner = runtime.Enter())
            {
                runtime.AfterRelease.Add(() =>
                {
                    runs++;
                    heldDuringRun = Monitor.IsEntered(runtime.MatchLock);
                });
            }

            Assert.Equal(0, runs);
        }

        Assert.Equal(1, runs);
        Assert.False(heldDuringRun);

        using (runtime.Enter())
        {
        }

        Assert.Equal(1, runs);
    }

    [Fact]
    public void AfterCleanup_RunsOutsideLockAfterRemoval()
    {
        var order = new List<string>();
        bool heldDuringAfterCleanup = true;
        MatchRuntime? runtime = null;
        MatchRuntimeStore store = CreateStore(
            onRedisCleanup: _ =>
            {
                Assert.False(MatchStartGate.IsGameplayActive(11));
                order.Add("after");
                heldDuringAfterCleanup = Monitor.IsEntered(runtime!.MatchLock);
            });
        runtime = store.GetOrCreate(11);

        using (runtime.Enter())
        {
            runtime.TryMarkEnded();
            runtime.AfterRelease.Add(() => order.Add("release"));
        }

        Assert.Equal(["release", "after"], order);
        Assert.False(heldDuringAfterCleanup);
    }

    [Fact]
    public void TerminalCleanup_RemovesStartStateOnlyAfterOutermostScope()
    {
        const long matchingId = 90009;
        var store = CreateStore();
        var runtime = store.GetOrCreate(matchingId);
        MatchStartGate.RegisterBotOnlyMatch(matchingId);
        try
        {
            using (runtime.Enter())
            {
                using (runtime.Enter())
                    runtime.TryMarkEnded();
                Assert.True(MatchStartGate.IsGameplayActive(matchingId));
                Assert.Same(runtime, store.GetOrNull(matchingId));
            }
            Assert.False(MatchStartGate.IsGameplayActive(matchingId));
            Assert.Null(store.GetOrNull(matchingId));
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    [Fact]
    public void GetAfterRemove_IsNull_NoRecreate()
    {
        int createdCount = 0;
        MatchRuntimeStore store = CreateStore();
        store.MatchCreated += _ => createdCount++;
        MatchRuntime runtime = store.GetOrCreate(4);

        using (runtime.Enter())
        {
            runtime.TryMarkEnded();
        }

        Assert.Null(store.GetOrNull(4));
        Assert.False(store.Enter(4, out _));
        Assert.False(store.TryEnter(4, out _));
        Assert.Equal(1, createdCount);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void GetOrCreate_ConcurrentCallers_RaisesCreatedOnce()
    {
        int createdCount = 0;
        MatchRuntimeStore store = CreateStore();
        store.MatchCreated += runtime =>
        {
            Assert.True(Monitor.IsEntered(runtime.MatchLock));
            Interlocked.Increment(ref createdCount);
            Thread.SpinWait(5000);
        };

        MatchRuntime[] runtimes = new MatchRuntime[32];
        Parallel.For(0, runtimes.Length, index => runtimes[index] = store.GetOrCreate(8));

        Assert.Equal(1, createdCount);
        Assert.All(runtimes, runtime => Assert.Same(runtimes[0], runtime));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void GetOrCreate_CreatedHandlerFailure_LeavesNoRuntime()
    {
        MatchRuntimeStore store = CreateStore();
        store.MatchCreated += _ => throw new InvalidOperationException("register failed");

        Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(6));
        Assert.Null(store.GetOrNull(6));
        Assert.Equal(0, store.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;
            current = previous;
        }
    }
}
