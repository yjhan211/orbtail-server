namespace game_server;

/// <summary>
///     이 Game Server의 노드 ID, 클라이언트 접속 주소·포트, 최대 동시 매치 수를 설정한다.
///     접속 주소·포트는 클라이언트가 실제로 이 노드에 연결할 수 있는 값이어야 한다.
///     GameServerNodeAdvertiser가 이 설정을 바탕으로 노드 정보를 Redis에 등록한다.
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
        {
            throw new InvalidOperationException("gameServerId must name this Game Server node.");
        }

        if (string.IsNullOrWhiteSpace(PublicHost))
        {
            throw new InvalidOperationException("GAME_SERVER_PUBLIC_HOST must be the address clients connect to.");
        }

        if (PublicPort is <= 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"GAME_SERVER_PUBLIC_PORT is out of range: {PublicPort}");
        }

        if (MaxConcurrentMatches <= 0)
        {
            throw new InvalidOperationException($"GAME_SERVER_MAX_MATCHES must be positive: {MaxConcurrentMatches}");
        }
    }
}
