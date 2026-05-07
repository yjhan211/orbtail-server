using System.Collections.Concurrent;

namespace game_server.services;

/// <summary>
///     RNG 채집 쿨타임 전역 저장소. (MatchingId, InteractId) → 다음 회수 가능 시각.
///     봇/플레이어 모두 동일한 store를 사용하여 인스턴스 단위 쿨타임 공유.
///     #134 — 봇 RNG 채집 통합용으로 GameClientSession에서 추출.
/// </summary>
public static class RngCollectCooldownStore
{
    private static readonly ConcurrentDictionary<(long, int), DateTime> _cooldowns = new();

    public static bool IsInCooldown(long matchingId, int interactId, out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (!_cooldowns.TryGetValue((matchingId, interactId), out var nextAvailable)) return false;
        var now = DateTime.UtcNow;
        if (now >= nextAvailable) return false;
        remainingSeconds = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
        return true;
    }

    public static void SetCooldown(long matchingId, int interactId, int seconds)
    {
        _cooldowns[(matchingId, interactId)] = DateTime.UtcNow.AddSeconds(seconds);
    }

    public static void ClearMatching(long matchingId)
    {
        var keys = _cooldowns.Keys.Where(k => k.Item1 == matchingId).ToList();
        foreach (var key in keys) _cooldowns.TryRemove(key, out _);
    }
}
