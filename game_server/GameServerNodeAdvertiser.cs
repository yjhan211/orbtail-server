using Microsoft.Extensions.Logging;
using network.routing;

namespace game_server;

/// <summary>
///     레지스트리에 광고할 이 노드의 식별자·공개 주소·용량. 설정 <c>gameServerId</c>와 환경 변수
///     <c>GAME_SERVER_PUBLIC_HOST</c>·
///     <c>GAME_SERVER_PUBLIC_PORT</c>·<c>GAME_SERVER_MAX_MATCHES</c>에서 온다.
/// </summary>
public sealed class GameServerNodeOptions
{
    public const int DefaultPublicPort = 9001;
    public const int DefaultMaxConcurrentMatches = 64;

    public string NodeId { get; init; } = "";
    public string PublicHost { get; init; } = "";
    public int PublicPort { get; init; } = DefaultPublicPort;
    public int MaxConcurrentMatches { get; init; } = DefaultMaxConcurrentMatches;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NodeId))
            throw new InvalidOperationException("gameServerId must name this Game Server node.");
        if (string.IsNullOrWhiteSpace(PublicHost))
            throw new InvalidOperationException("GAME_SERVER_PUBLIC_HOST must be the address clients connect to.");
        if (PublicPort is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException($"GAME_SERVER_PUBLIC_PORT is out of range: {PublicPort}");
        if (MaxConcurrentMatches <= 0)
            throw new InvalidOperationException($"GAME_SERVER_MAX_MATCHES must be positive: {MaxConcurrentMatches}");
    }
}

/// <summary>
///     이 Game Server를 레지스트리에 광고한다. 기동 시 accepting=true로 쓰고 2초마다 활성 매치 수를 실어 갱신하며,
///     종료 시작 시 하트비트를 멈추고 진행 중인 갱신이 끝나면 레지스트리에서 삭제한다.
///     <see cref="GameServer" />가 소유하고 그 Start/Stop 순서 안에서만 부른다. 하트비트 실패는 로그만 남긴다 —
///     User Server는 낡은 하트비트를 죽은 노드로 보므로 Redis가 돌아오면 다음 하트비트로 자연히 복귀한다.
/// </summary>
internal sealed class GameServerNodeAdvertiser(
    IGameServerRegistry registry,
    GameServerNodeOptions options,
    Func<int> activeMatchCount,
    ILogger logger)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly long _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly SemaphoreSlim _heartbeatLock = new(1, 1);
    private Timer? _heartbeatTimer;
    private int _accepting;
    private int _heartbeatFailures;

    public string NodeId => options.NodeId;

    /// <summary>accepting=true로 첫 광고를 쓴다. 실패는 예외로 올려 기동을 막는다 — 광고 없는 노드는 매치를 받지 못한다.</summary>
    public async Task StartAsync()
    {
        Volatile.Write(ref _accepting, 1);
        await registry.PublishAsync(Describe());
        _heartbeatTimer = new Timer(_ => _ = HeartbeatAsync(), null, HeartbeatInterval, HeartbeatInterval);
        logger.LogInformation(
            "Game server node advertised: NodeId={NodeId}, Endpoint={Host}:{Port}, MaxMatches={MaxMatches}",
            NodeId, options.PublicHost, options.PublicPort, options.MaxConcurrentMatches);
    }

    /// <summary>하트비트를 멈추고 마지막 갱신이 끝난 뒤 노드 등록을 삭제한다.</summary>
    public async Task StopAsync()
    {
        Volatile.Write(ref _accepting, 0);
        Timer? timer = Interlocked.Exchange(ref _heartbeatTimer, null);
        if (timer != null)
            await timer.DisposeAsync();

        // Timer는 비동기 Redis 작업까지 기다리지 않으므로, 진행 중인 갱신을 별도로 기다린다.
        await _heartbeatLock.WaitAsync();
        try
        {
            await registry.RemoveAsync(NodeId);
        }
        catch (Exception ex)
        {
            // 삭제에 실패해도 더는 갱신하지 않는다. User Server의 하트비트 신선도 검사에서 제외된다.
            logger.LogWarning(ex, "Game server node could not remove its registry entry: NodeId={NodeId}", NodeId);
        }
        finally
        {
            _heartbeatLock.Release();
        }
    }

    private async Task HeartbeatAsync()
    {
        // 이전 갱신이 아직 진행 중이면 이번 틱은 건너뛴다.
        if (!await _heartbeatLock.WaitAsync(0))
            return;

        try
        {
            if (Volatile.Read(ref _accepting) == 0)
                return;

            await registry.PublishAsync(Describe());
            if (Interlocked.Exchange(ref _heartbeatFailures, 0) > 0)
                logger.LogInformation("Game server node heartbeat recovered: NodeId={NodeId}", NodeId);
        }
        catch (Exception ex)
        {
            // 연속 실패는 첫 번째만 경고 — Redis 단절 동안 2초마다 같은 로그를 쌓지 않는다.
            if (Interlocked.Increment(ref _heartbeatFailures) == 1)
                logger.LogWarning(ex, "Game server node heartbeat failed: NodeId={NodeId}", NodeId);
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
            NodeId = NodeId,
            PublicHost = options.PublicHost,
            PublicPort = options.PublicPort,
            MaxConcurrentMatches = options.MaxConcurrentMatches,
            ActiveMatches = Math.Max(0, activeMatchCount()),
            Accepting = Volatile.Read(ref _accepting) == 1,
            HeartbeatUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            StartedUnixMs = _startedUnixMs
        };
    }
}
