using game_server.services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace game_server.matches;

/// <summary>
///     matchingId별 매치의 생성·조회·제거를 담당한다.
///     ID로 잠금 진입을 요청하면 해당 매치를 찾아 위임하며,
///     실제 잠금과 종료 정리는 MatchRuntime이 담당한다.
/// </summary>
internal sealed class MatchRuntimeStore(
    ILogger logger,
    MatchingLifecycleService matchingLifecycle,
    bool monsterSpawnEnabled = true,
    MatchEventArchive? eventArchive = null)
{
    private readonly MatchingLifecycleService _matchingLifecycle = matchingLifecycle ?? throw new ArgumentNullException(nameof(matchingLifecycle));
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();
    private readonly SwarmGrowthOfferIdSequence _growthOfferIds = new();
    private readonly SwarmCrossfireEventIdSequence _crossfireEventIds = new();

    internal event Action<MatchRuntime>? Created;

    public int Count => _runtimes.Count;

    public MatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        if (_runtimes.TryGetValue(matchingId, out var existing))
        {
            return existing;
        }

        var candidate = new MatchRuntime(this, matchingId, logger, _matchingLifecycle, _growthOfferIds, _crossfireEventIds, monsterSpawnEnabled, eventArchive);
        lock (candidate.Sync)
        {
            var runtime = _runtimes.GetOrAdd(matchingId, candidate);
            if (!ReferenceEquals(runtime, candidate))
            {
                return runtime;
            }

            try
            {
                Created?.Invoke(candidate);
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

    public bool Enter(long matchingId, out MatchScope scope)
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

    public static MatchScope Enter(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Enter();
    }

    public bool TryEnter(long matchingId, out MatchScope scope)
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
        lock (runtime.Sync)
        {
            if (!_runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, runtime)))
            {
                return false;
            }
            runtime.TickLoop?.Stop();
            runtime.Sessions.Close();
            return true;
        }
    }

    internal void RemoveCompleted(MatchRuntime runtime) =>
        _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(runtime.MatchingId, runtime));
}
