using game_server.matches.lifecycle;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace game_server.matches;

/// <summary>
///     matchingId별 매치의 생성·조회·제거를 담당한다.
///     ID로 잠금 진입을 요청하면 해당 매치를 찾아 위임하며,
///     실제 잠금과 종료 정리는 MatchRuntime이 담당한다.
/// </summary>
internal sealed class MatchRuntimeStore(ILogger<MatchRuntime> runtimeLogger, MatchingLifecycleService matchingLifecycle)
{
    private readonly MatchingLifecycleService _matchingLifecycle = matchingLifecycle ?? throw new ArgumentNullException(nameof(matchingLifecycle));
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();
    internal event Action<MatchRuntime>? MatchCreated;

    public int Count => _runtimes.Count;

    public MatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        if (_runtimes.TryGetValue(matchingId, out var existing))
        {
            return existing;
        }

        var candidate = new MatchRuntime(this, matchingId, runtimeLogger, _matchingLifecycle);
        lock (candidate.MatchLock)
        {
            var runtime = _runtimes.GetOrAdd(matchingId, candidate);
            if (!ReferenceEquals(runtime, candidate))
            {
                return runtime;
            }

            try
            {
                MatchCreated?.Invoke(candidate);
            }
            catch
            {
                candidate.TickLoop?.Stop();
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, candidate));
                throw;
            }

            return candidate;
        }
    }

    public MatchRuntime GetOrThrow(long matchingId) => GetOrNull(matchingId) ?? throw new InvalidOperationException($"Match is not available: {matchingId}");

    public MatchRuntime? GetOrNull(long matchingId) => matchingId > 0 && _runtimes.TryGetValue(matchingId, out var runtime) ? runtime : null;

    public IReadOnlyList<long> ActiveIds() => _runtimes.Keys.OrderBy(id => id).ToList();

    public bool Enter(long matchingId, out MatchLockScope scope)
    {
        var runtime = GetOrNull(matchingId);
        if (runtime == null)
        {
            scope = default;
            return false;
        }

        scope = runtime.Enter();
        return true;
    }

    public static MatchLockScope Enter(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Enter();
    }

    public bool TryEnter(long matchingId, out MatchLockScope scope)
    {
        scope = default;
        var runtime = GetOrNull(matchingId);
        return runtime != null && runtime.TryEnter(out scope);
    }

    public bool Remove(long matchingId)
    {
        var runtime = GetOrNull(matchingId);
        if (runtime == null)
        {
            return false;
        }
        using (runtime.Enter())
        {
            if (!_runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, runtime)))
            {
                return false;
            }
            runtime.TryMarkEnded();
            return true;
        }
    }

    internal void RemoveCompleted(MatchRuntime runtime) =>
        _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(runtime.MatchingId, runtime));
}
