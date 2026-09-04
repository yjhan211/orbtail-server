using MessagePack;

namespace network.gamehandoff;

/// <summary>
///     handoff ticket이 증명하는 것: 이 소켓이 누구고(PlayerId), 어느 매치에(MatchingId), 어느 노드로(GameServerNodeId) 왔는가.
///     매치 구성은 <see cref="network.common.data.models.MatchManifest" />가, 스폰·로스터는 Game Server가 정한다.
/// </summary>
[MessagePackObject]
public sealed class GameHandoffContext
{
    [Key("playerId")] public long PlayerId { get; set; }
    [Key("matchingId")] public long MatchingId { get; set; }

    /// <summary>이 매치를 배정받은 Game Server 노드. 다른 노드는 이 ticket을 받아들이지 않는다.</summary>
    [Key("gameServerNodeId")] public string GameServerNodeId { get; set; } = string.Empty;
}
