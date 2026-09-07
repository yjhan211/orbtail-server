namespace game_server.services;

/// <summary>
///     매치 하나의 채집 지점별 다음 회수 가능 시각을 보관한다. 같은 매치의 사람과 봇이 공유한다.
///     확인과 갱신은 이 객체의 잠금 안에서 처리하며, 다른 매치와 잠금을 공유하지 않는다.
/// </summary>
public sealed class RngCollectCooldownStore
{
    public const int DefaultCooldownSeconds = 30;
    private readonly object _cooldownLock = new();
    private readonly Dictionary<int, DateTime> _cooldowns = new();
    private bool _released;

    public bool TryAcquireCooldown(int interactId, int seconds, out int remainingSeconds)
    {
        lock (_cooldownLock)
        {
            remainingSeconds = 0;
            if (_released) return false;

            var now = DateTime.UtcNow;
            if (_cooldowns.TryGetValue(interactId, out var nextAvailable) && now < nextAvailable)
            {
                remainingSeconds = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
                return false;
            }

            _cooldowns[interactId] = now.AddSeconds(seconds);
            return true;
        }
    }

    public List<(int InteractId, int RemainingSeconds)> GetSnapshot()
    {
        lock (_cooldownLock)
        {
            var snapshot = new List<(int InteractId, int RemainingSeconds)>();
            if (_released) return snapshot;

            var now = DateTime.UtcNow;
            var expired = new List<int>();
            foreach (var (interactId, nextAvailable) in _cooldowns)
            {
                int remaining = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
                if (remaining <= 0)
                    expired.Add(interactId);
                else
                    snapshot.Add((interactId, remaining));
            }

            foreach (int interactId in expired) _cooldowns.Remove(interactId);
            return snapshot;
        }
    }

    /// <summary>매치 종료 시 상태를 비우고 이후 쿨타임 등록을 거부한다.</summary>
    internal void Release()
    {
        lock (_cooldownLock)
        {
            _released = true;
            _cooldowns.Clear();
        }
    }

    /// <summary>채집 취소 시 해당 지점을 다른 사람이나 봇이 바로 사용할 수 있게 한다.</summary>
    public void ClearCooldown(int interactId)
    {
        lock (_cooldownLock)
            _cooldowns.Remove(interactId);
    }
}
