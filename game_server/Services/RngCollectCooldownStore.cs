namespace game_server.services;

/// <summary>
///     RNG 채집 쿨타임 전역 저장소. (MatchingId, InteractId) → 다음 회수 가능 시각.
///     봇/플레이어 모두 동일한 store를 사용하여 인스턴스 단위 쿨타임 공유.
///     #134 — 봇 RNG 채집 통합용으로 GameClientSession에서 추출.
///     조회-갱신이 한 단위여야 하므로 모든 접근은 SyncRoot 아래에서 이루어진다.
/// </summary>
public static class RngCollectCooldownStore
{
    public const int DefaultCooldownSeconds = 30;
    private static readonly object SyncRoot = new();

    private static readonly Dictionary<(long MatchingId, int InteractId), DateTime> _cooldowns = new();

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

    public static List<(int InteractId, int RemainingSeconds)> GetSnapshot(long matchingId)
    {
        var now = DateTime.UtcNow;
        var snapshot = new List<(int InteractId, int RemainingSeconds)>();

        lock (SyncRoot)
        {
            var expired = new List<(long MatchingId, int InteractId)>();
            foreach (var ((storedMatchingId, interactId), nextAvailable) in _cooldowns)
            {
                if (storedMatchingId != matchingId) continue;

                var remaining = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
                if (remaining <= 0)
                {
                    expired.Add((storedMatchingId, interactId));
                    continue;
                }

                snapshot.Add((interactId, remaining));
            }

            foreach (var key in expired) _cooldowns.Remove(key);
        }

        return snapshot;
    }

    public static void ClearMatching(long matchingId)
    {
        lock (SyncRoot)
        {
            var keys = _cooldowns.Keys.Where(k => k.MatchingId == matchingId).ToList();
            foreach (var key in keys) _cooldowns.Remove(key);
        }
    }

    /// <summary>
    ///     특정 InteractObject의 cooldown 즉시 해제 — progress 폐기 시 다른 봇/플레이어가 곧바로 시도 가능.
    /// </summary>
    public static void ClearCooldown(long matchingId, int interactId)
    {
        lock (SyncRoot)
            _cooldowns.Remove((matchingId, interactId));
    }
}
