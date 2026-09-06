using Microsoft.Extensions.Logging;
using network.infrastructure.redis;

namespace user_server.matching;

/// <summary>
///     여러 UserServer 중 매칭을 실행할 리더를 정한다.
///     매칭 회차마다 자신이 리더이면 만료 시간을 갱신하고, 등록이 비어 있으면 획득을 시도한다.
///     다른 서버가 리더이거나 Redis 확인에 실패하면 해당 회차의 매칭을 실행하지 않는다.
///     정상 종료 시 자신의 등록을 해제하며, 갱신이 끊기면 만료 후 다른 서버가 획득할 수 있다.
/// </summary>
internal sealed class MatchingLeaderLease(IRedisOperations redisOperations, string nodeId, ILogger logger)
{
    public const string Key = "user_server:matching_leader";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private string NodeId { get; } = nodeId;
    public bool IsLeader { get; private set; }

    public async Task<bool> TryAcquireOrRenewAsync()
    {
        bool hasLeaderLease;
        try
        {
            hasLeaderLease = await redisOperations.StringSetIfEqualsAsync(Key, NodeId, NodeId, Lifetime) ||
                             await redisOperations.StringSetIfNotExistsAsync(Key, NodeId, Lifetime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching leader lease check failed; standing down: NodeId={NodeId}", NodeId);
            hasLeaderLease = false;
        }

        if (hasLeaderLease != IsLeader)
        {
            if (hasLeaderLease)
            {
                logger.LogInformation("Matching leader acquired: NodeId={NodeId}", NodeId);
            }
            else
            {
                logger.LogWarning("Matching leader lost: NodeId={NodeId}", NodeId);
            }
        }

        IsLeader = hasLeaderLease;
        return hasLeaderLease;
    }

    public async Task ReleaseAsync()
    {
        if (!IsLeader)
        {
            return;
        }

        IsLeader = false;
        try
        {
            await redisOperations.StringDeleteIfEqualsAsync(Key, NodeId);
            logger.LogInformation("Matching leader released: NodeId={NodeId}", NodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching leader lease release failed; it expires on its own: NodeId={NodeId}", NodeId);
        }
    }
}
