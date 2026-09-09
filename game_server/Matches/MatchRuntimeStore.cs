using game_server.combat;
using game_server.logging;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace game_server.matches;

/// <summary>
///     matchingId별 매치의 생성·조회·제거를 담당한다.
///     ID로 잠금 진입을 요청하면 해당 매치를 찾아 위임하며,
///     실제 잠금과 종료 정리는 MatchRuntime이 담당한다.
/// </summary>
internal sealed class MatchRuntimeStore
{
    private readonly MatchSessionCleanupService _matchSessionCleanup;
    private readonly ILogger<MatchRuntime> _runtimeLogger;
    private readonly ILogger<MatchCombatDamageService> _damageLogger;
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();

    internal event Action<MatchRuntime>? MatchCreated;
    internal GameEventLogManager EventLogs { get; }

    internal MatchRuntimeStore(ILogger<MatchRuntime> runtimeLogger, MatchSessionCleanupService matchSessionCleanup,
        ILogger<MatchCombatDamageService> damageLogger)
    {
        _runtimeLogger = runtimeLogger;
        _matchSessionCleanup = matchSessionCleanup ?? throw new ArgumentNullException(nameof(matchSessionCleanup));
        _damageLogger = damageLogger;
        EventLogs = new GameEventLogManager(id => GetOrNull(id)?.EventLog);
    }

    public int Count => _runtimes.Count;

    public MatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        if (_runtimes.TryGetValue(matchingId, out var existing))
        {
            return existing;
        }

        var newRuntime = new MatchRuntime(this, matchingId, _runtimeLogger, _matchSessionCleanup, EventLogs, _damageLogger);
        lock (newRuntime.MatchLock)
        {
            var runtime = _runtimes.GetOrAdd(matchingId, newRuntime);
            if (!ReferenceEquals(runtime, newRuntime))
            {
                return runtime;
            }

            try
            {
                MatchCreated?.Invoke(newRuntime);
            }
            catch
            {
                newRuntime.TickLoop?.Stop();
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, newRuntime));
                throw;
            }

            return newRuntime;
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
