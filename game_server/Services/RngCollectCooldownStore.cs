using System.Collections.Concurrent;

namespace game_server.services;

/// <summary>
///     RNG 채집 쿨타임 전역 저장소. (MatchingId, InteractId) → 다음 회수 가능 시각.
///     봇/플레이어 모두 동일한 store를 사용하여 인스턴스 단위 쿨타임 공유.
///     #134 — 봇 RNG 채집 통합용으로 GameClientSession에서 추출.
/// </summary>
public static class RngCollectCooldownStore
{
    public const int DefaultCooldownSeconds = 30;
    private static readonly object SyncRoot = new();

    private static readonly ConcurrentDictionary<(long, int), DateTime> _cooldowns = new();

    // 스팟 재개봉 가산의 근거 — (MatchingId, InteractId) → 지금까지 열린 횟수. 사람·봇 공유.
    private static readonly ConcurrentDictionary<(long, int), int> _openCounts = new();

    public static int GetOpenCount(long matchingId, int interactId) =>
        _openCounts.GetValueOrDefault((matchingId, interactId));

    public static void IncrementOpenCount(long matchingId, int interactId) =>
        _openCounts.AddOrUpdate((matchingId, interactId), 1, (_, count) => count + 1);

    public static bool IsInCooldown(long matchingId, int interactId, out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (!_cooldowns.TryGetValue((matchingId, interactId), out var nextAvailable)) return false;
        var now = DateTime.UtcNow;
        if (now >= nextAvailable) return false;
        remainingSeconds = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
        return true;
    }

    public static bool TryAcquireCooldown(long matchingId, int interactId, int seconds, out int remainingSeconds)
    {
        lock (SyncRoot)
        {
            var now = DateTime.UtcNow;
            if (_cooldowns.TryGetValue((matchingId, interactId), out var nextAvailable) && now < nextAvailable)
            {
                remainingSeconds = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
                return false;
            }

            _cooldowns[(matchingId, interactId)] = now.AddSeconds(seconds);
            remainingSeconds = 0;
            return true;
        }
    }

    public static void SetCooldown(long matchingId, int interactId, int seconds)
    {
        lock (SyncRoot)
        {
            if (seconds <= 0)
            {
                _cooldowns.TryRemove((matchingId, interactId), out _);
                return;
            }

            _cooldowns[(matchingId, interactId)] = DateTime.UtcNow.AddSeconds(seconds);
        }
    }

    public static List<(int InteractId, int RemainingSeconds)> GetSnapshot(long matchingId)
    {
        var now = DateTime.UtcNow;
        var snapshot = new List<(int InteractId, int RemainingSeconds)>();

        foreach (var ((storedMatchingId, interactId), nextAvailable) in _cooldowns)
        {
            if (storedMatchingId != matchingId) continue;

            var remaining = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
            if (remaining <= 0)
            {
                _cooldowns.TryRemove((storedMatchingId, interactId), out _);
                continue;
            }

            snapshot.Add((interactId, remaining));
        }

        return snapshot;
    }

    public static void ClearMatching(long matchingId)
    {
        var keys = _cooldowns.Keys.Where(k => k.Item1 == matchingId).ToList();
        foreach (var key in keys) _cooldowns.TryRemove(key, out _);
        var countKeys = _openCounts.Keys.Where(k => k.Item1 == matchingId).ToList();
        foreach (var key in countKeys) _openCounts.TryRemove(key, out _);
    }

    /// <summary>
    ///     특정 InteractObject의 cooldown 즉시 해제 — progress 폐기 시 다른 봇/플레이어가 곧바로 시도 가능.
    /// </summary>
    public static void ClearCooldown(long matchingId, int interactId)
    {
        lock (SyncRoot)
            _cooldowns.TryRemove((matchingId, interactId), out _);
    }
}
