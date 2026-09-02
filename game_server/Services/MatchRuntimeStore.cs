using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     매치 하나의 직렬화 경계. 같은 matchingId의 권위 상태 변경은 전부 <see cref="Sync"/> 모니터 안에서
///     돌고(타이머 틱·세션 핸들러·종료), 서로 다른 매치는 병렬이다. 수명은 <see cref="MatchRuntimeStore"/>가
///     소유한다 — 생성은 접속/봇 전용 인스턴스, 제거는 터미널 정리가 끝난 최외곽 스코프 탈출 시점.
///     <see cref="IsTerminal"/>은 Sync 안에서만 바뀌고 잠금 밖 읽기는 늦은 패킷을 거르는 게이트로만 쓴다.
/// </summary>
internal sealed class MatchRuntime
{
    private int _terminal;

    internal MatchRuntime(long matchingId)
    {
        MatchingId = matchingId;
    }

    public long MatchingId { get; }
    public object Sync { get; } = new();
    public bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    /// <summary>재진입 깊이 — Sync 안에서만 읽고 쓴다. 0으로 돌아오는 순간이 정리·후처리 시점이다.</summary>
    internal int Depth;
    internal bool CleanupDone;

    /// <summary>
    ///     최외곽 스코프가 잠금을 놓은 뒤 한 번만 실행할 후처리 (요약 파일 쓰기·NATS 발행·Redis 정리).
    ///     Sync 안에서만 추가한다.
    /// </summary>
    internal readonly List<Action> AfterRelease = new();

    /// <summary>터미널 전이 — Sync를 쥔 호출자만 부를 수 있고 첫 호출자만 true를 받는다.</summary>
    public bool TryMarkTerminal()
    {
        if (!Monitor.IsEntered(Sync))
            throw new InvalidOperationException("TryMarkTerminal requires the match monitor to be held.");

        return Interlocked.CompareExchange(ref _terminal, 1, 0) == 0;
    }
}

/// <summary>매치 정리 단계 하나 — 실패해도 다음 단계를 막지 않도록 이름과 함께 격리 실행된다.</summary>
internal sealed record MatchCleanupStep(string Name, Action<long> Cleanup);

/// <summary>
///     <see cref="MatchRuntimeStore.Enter"/>/<see cref="MatchRuntimeStore.TryEnter"/>가 돌려주는 잠금 스코프.
///     Dispose가 깊이를 줄이고, 최외곽(깊이 0)에서 터미널이면 정리를 한 번 돌린 뒤 모니터를 놓고
///     <see cref="MatchRuntime.AfterRelease"/>를 잠금 밖에서 실행한다.
/// </summary>
internal readonly struct MatchScope : IDisposable
{
    private readonly MatchRuntimeStore _store;

    internal MatchScope(MatchRuntimeStore store, MatchRuntime runtime)
    {
        _store = store;
        Runtime = runtime;
    }

    public MatchRuntime Runtime { get; }

    public void Dispose() => _store.Exit(Runtime);
}

/// <summary>
///     프로세스 수명의 매치 런타임 색인. matchingId당 <see cref="MatchRuntime"/> 하나를 만들고(초기화 훅은
///     생성 시 한 번), 잠금 진입/탈출과 터미널 정리 순서를 책임진다. 정리는 터미널 표시 뒤 깊이 0 탈출에서
///     정확히 한 번 돌고, 그 직후 색인에서 빠져 늦은 타이머·패킷이 상태를 되살릴 수 없다.
/// </summary>
internal sealed class MatchRuntimeStore
{
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();
    private readonly Action<long>? _initializeMatch;
    private readonly IReadOnlyList<MatchCleanupStep> _cleanupSteps;
    private readonly Action<long>? _afterCleanup;
    private readonly ILogger _logger;

    /// <param name="initializeMatch">런타임이 처음 만들어질 때 그 모니터 안에서 한 번 실행되는 훅.</param>
    /// <param name="cleanupSteps">터미널 정리 단계 — 순서대로, 각 단계 예외는 격리·로그.</param>
    /// <param name="afterCleanup">정리가 끝난 뒤 잠금 밖에서 실행할 후처리 (Redis 정리 등).</param>
    public MatchRuntimeStore(
        ILogger logger,
        Action<long>? initializeMatch = null,
        IReadOnlyList<MatchCleanupStep>? cleanupSteps = null,
        Action<long>? afterCleanup = null)
    {
        _logger = logger;
        _initializeMatch = initializeMatch;
        _cleanupSteps = cleanupSteps ?? [];
        _afterCleanup = afterCleanup;
    }

    public int Count => _runtimes.Count;

    /// <summary>
    ///     런타임을 찾거나 만든다. 초기화 훅은 새 런타임의 모니터를 쥔 채 정확히 한 번 실행되고,
    ///     훅이 던지면 그 런타임은 색인에서 빠진다.
    /// </summary>
    public MatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        if (_runtimes.TryGetValue(matchingId, out MatchRuntime? existing))
            return existing;

        var candidate = new MatchRuntime(matchingId);
        lock (candidate.Sync)
        {
            MatchRuntime runtime = _runtimes.GetOrAdd(matchingId, candidate);
            if (!ReferenceEquals(runtime, candidate))
                return runtime;

            try
            {
                _initializeMatch?.Invoke(matchingId);
            }
            catch
            {
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, candidate));
                throw;
            }

            return candidate;
        }
    }

    public MatchRuntime? Get(long matchingId) =>
        matchingId > 0 && _runtimes.TryGetValue(matchingId, out MatchRuntime? runtime) ? runtime : null;

    /// <summary>색인에 남아 있는 매치 id 스냅샷 — 정리가 끝난 매치는 포함되지 않는다.</summary>
    public IReadOnlyList<long> ActiveIds() => _runtimes.Keys.OrderBy(id => id).ToList();

    /// <summary>모니터를 기다려 진입한다 (재진입 가능). 색인에 없는 매치면 false.</summary>
    public bool Enter(long matchingId, out MatchScope scope)
    {
        MatchRuntime? runtime = Get(matchingId);
        if (runtime == null)
        {
            scope = default;
            return false;
        }

        scope = Enter(runtime);
        return true;
    }

    /// <summary>이미 잡아 둔 런타임으로 진입한다 — 종료 경로가 다른 스코프 안에서 재진입할 때 쓴다.</summary>
    public MatchScope Enter(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Monitor.Enter(runtime.Sync);
        runtime.Depth++;
        return new MatchScope(this, runtime);
    }

    /// <summary>
    ///     기다리지 않고 진입을 시도한다. false면 이 펄스는 건너뛴다 — 밀린 틱을 나중에 따라잡지 않는다.
    /// </summary>
    public bool TryEnter(long matchingId, out MatchScope scope)
    {
        scope = default;
        MatchRuntime? runtime = Get(matchingId);
        if (runtime == null)
            return false;

        bool lockTaken = false;
        Monitor.TryEnter(runtime.Sync, ref lockTaken);
        if (!lockTaken)
            return false;

        runtime.Depth++;
        scope = new MatchScope(this, runtime);
        return true;
    }

    public bool Remove(long matchingId) => _runtimes.TryRemove(matchingId, out _);

    internal void Exit(MatchRuntime runtime)
    {
        Action[]? afterRelease = null;
        try
        {
            runtime.Depth--;
            if (runtime.Depth > 0)
                return;

            if (runtime.IsTerminal && !runtime.CleanupDone)
            {
                runtime.CleanupDone = true;
                RunCleanup(runtime.MatchingId);
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(runtime.MatchingId, runtime));
                if (_afterCleanup != null)
                {
                    Action<long> afterCleanup = _afterCleanup;
                    long matchingId = runtime.MatchingId;
                    runtime.AfterRelease.Add(() => afterCleanup(matchingId));
                }
            }

            if (runtime.AfterRelease.Count == 0)
                return;

            afterRelease = runtime.AfterRelease.ToArray();
            runtime.AfterRelease.Clear();
        }
        finally
        {
            Monitor.Exit(runtime.Sync);
        }

        foreach (Action action in afterRelease)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Match post-release action failed: MatchingId={MatchingId}",
                    runtime.MatchingId);
            }
        }
    }

    private void RunCleanup(long matchingId)
    {
        foreach (MatchCleanupStep step in _cleanupSteps)
        {
            try
            {
                step.Cleanup(matchingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Match component cleanup failed: MatchingId={MatchingId}, Component={Component}",
                    matchingId,
                    step.Name);
            }
        }
    }
}
