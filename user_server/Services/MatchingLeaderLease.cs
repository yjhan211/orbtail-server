using Microsoft.Extensions.Logging;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     매칭 pass를 도는 User Server를 한 프로세스로 제한하는 Redis lease. 1초 tick마다 얻거나 갱신하고,
///     TTL 안에 갱신하지 못하면 다른 프로세스가 가져간다. 리더가 아닌 프로세스는 큐를 읽지 않는다 —
///     claim이 중복 매치를 막아 주더라도 성공 전달·watchdog 소유자가 둘이면 롤백 판단이 갈리기 때문이다.
///     매칭 timer 스레드에서만 부른다.
/// </summary>
internal sealed class MatchingLeaderLease(ICacheHelper cacheHelper, string nodeId, ILogger logger)
{
    public const string Key = "user_server:matching_leader";

    /// <summary>tick(1초)을 몇 번 놓치면 리더를 잃는지 — 5회. 리더 교체 공백도 이 길이다.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private bool _isLeader;

    public string NodeId { get; } = nodeId;
    public bool IsLeader => _isLeader;

    /// <summary>리더면 갱신하고, 아니면 빈 자리를 노린다. Redis 실패는 리더 아님으로 본다(안전 쪽).</summary>
    public async Task<bool> TryAcquireOrRenewAsync()
    {
        bool held;
        try
        {
            held = await cacheHelper.StringSetIfEqualsAsync(Key, NodeId, NodeId, Lifetime) ||
                   await cacheHelper.StringSetIfNotExistsAsync(Key, NodeId, Lifetime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching leader lease check failed; standing down: NodeId={NodeId}", NodeId);
            held = false;
        }

        if (held != _isLeader)
        {
            if (held)
                logger.LogInformation("Matching leader acquired: NodeId={NodeId}", NodeId);
            else
                logger.LogWarning("Matching leader lost: NodeId={NodeId}", NodeId);
        }

        _isLeader = held;
        return held;
    }

    /// <summary>정상 종료 시 lease를 바로 비워 다음 리더가 TTL을 기다리지 않게 한다.</summary>
    public async Task ReleaseAsync()
    {
        if (!_isLeader)
            return;

        _isLeader = false;
        try
        {
            await cacheHelper.StringDeleteIfEqualsAsync(Key, NodeId);
            logger.LogInformation("Matching leader released: NodeId={NodeId}", NodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching leader lease release failed; it expires on its own: NodeId={NodeId}", NodeId);
        }
    }
}
