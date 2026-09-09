using Microsoft.Extensions.Logging;
using network.routing;

namespace game_server;

/// <summary>
///     User Server가 매치를 배정할 수 있도록 이 Game Server의 정보를 Redis에 등록한다.
///     시작 시 노드 ID·접속 주소·수용량을 등록하고, 2초마다 활성 매치 수와 하트비트 시각을 갱신한다.
///     종료 시 주기적 갱신을 멈추고 진행 중인 갱신이 끝나면 등록을 삭제한다.
///     비정상 종료 등으로 등록이 남더라도 User Server가 오래된 하트비트를 확인해 배정 대상에서 제외한다.
/// </summary>
internal sealed class GameServerNodeAdvertiser(
    IGameServerRegistry registry,
    GameServerNodeOptions options,
    Func<int> activeMatchCount,
    ILogger logger)
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _heartbeatLock = new(1, 1);
    private Timer? _heartbeatTimer;
    private int _accepting;
    private int _heartbeatFailures;

    public async Task StartAsync()
    {
        Volatile.Write(ref _accepting, 1);
        await registry.PublishAsync(Describe());
        _heartbeatTimer = new Timer(_ => _ = HeartbeatAsync(), null, HeartbeatInterval, HeartbeatInterval);
        logger.LogInformation("Game server node advertised: NodeId={NodeId}, Endpoint={Host}:{Port}, MaxMatches={MaxMatches}", options.NodeId, options.PublicHost, options.PublicPort, options.MaxConcurrentMatches);
    }

    public async Task StopAsync()
    {
        Volatile.Write(ref _accepting, 0);
        var timer = Interlocked.Exchange(ref _heartbeatTimer, null);
        if (timer != null)
        {
            await timer.DisposeAsync();
        }

        await _heartbeatLock.WaitAsync();
        try
        {
            await registry.RemoveAsync(options.NodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game server node could not remove its registry entry: NodeId={NodeId}", options.NodeId);
        }
        finally
        {
            _heartbeatLock.Release();
        }
    }

    private async Task HeartbeatAsync()
    {
        if (!await _heartbeatLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _accepting) == 0)
            {
                return;
            }

            await registry.PublishAsync(Describe());
            if (Interlocked.Exchange(ref _heartbeatFailures, 0) > 0)
            {
                logger.LogInformation("Game server node heartbeat recovered: NodeId={NodeId}", options.NodeId);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _heartbeatFailures) == 1)
            {
                logger.LogWarning(ex, "Game server node heartbeat failed: NodeId={NodeId}", options.NodeId);
            }
        }
        finally
        {
            _heartbeatLock.Release();
        }
    }

    private GameServerNodeDescriptor Describe()
    {
        return new GameServerNodeDescriptor
        {
            NodeId = options.NodeId,
            PublicHost = options.PublicHost,
            PublicPort = options.PublicPort,
            MaxConcurrentMatches = options.MaxConcurrentMatches,
            ActiveMatches = Math.Max(0, activeMatchCount()),
            Accepting = Volatile.Read(ref _accepting) == 1,
            HeartbeatUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}
