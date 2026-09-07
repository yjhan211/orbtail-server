using MessagePack;

namespace network.gameentry;

/// <summary>
///     User Server가 발급하는 일회용 Game Server 입장권에 저장되는 정보.
///     Game Server는 이 값으로 접속한 플레이어와 참가할 매치, 배정받은 서버 노드가 맞는지 확인한다.
///     전체 참가자 목록은 network.common.data.models.MatchManifest에서 읽고,
///     플레이어의 스폰 위치와 매치 런타임 상태는 Game Server가 정한다.
/// </summary>
[MessagePackObject]
public sealed class GameEntryContext
{
    [Key("playerId")] public long PlayerId { get; set; }
    [Key("matchingId")] public long MatchingId { get; set; }
    [Key("gameServerNodeId")] public string GameServerNodeId { get; set; } = string.Empty;
}
