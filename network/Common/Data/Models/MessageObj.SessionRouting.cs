using MessagePack;
// ReSharper disable All

namespace network.common.data.models
{
    /// User Server간 매칭 성공 전달 요청. 대상 세션을 가진 서버가 처리 결과를 응답한다.
    [MessagePackObject]
    public sealed class U_TO_U_MATCHING_SUCCESS
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("requestId")] public string RequestId { get; set; } = string.Empty;
        [Key("result")] public U_TO_C_MATCHING_SUCCESS Result { get; set; } = new U_TO_C_MATCHING_SUCCESS();
        [Key("originNodeId")] public string OriginNodeId { get; set; } = string.Empty;
    }

    /// User Server간 매칭 실패 전달 요청. 대상 세션을 가진 서버가 처리 결과를 응답한다.
    [MessagePackObject]
    public sealed class U_TO_U_MATCHING_FAILED
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("requestId")] public string RequestId { get; set; } = string.Empty;
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("originNodeId")] public string OriginNodeId { get; set; } = string.Empty;
    }

    /// User Server간 입장 실패 전달 요청. 대상 세션을 가진 서버가 처리 결과를 응답한다.
    [MessagePackObject]
    public sealed class U_TO_U_ENTRY_FAILED
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("originNodeId")] public string OriginNodeId { get; set; } = string.Empty;
    }

    /// 다른 User Server에 새 로그인 세대를 알린다.
    [MessagePackObject]
    public sealed class U_TO_U_SESSION_LOGIN
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("originNodeId")] public string OriginNodeId { get; set; } = string.Empty;
        [Key("sessionGeneration")] public long SessionGeneration { get; set; }
    }

    /// 다른 User Server에 해당 매칭 배정의 해제를 알린다.
    [MessagePackObject]
    public sealed class U_TO_U_MATCHING_ASSIGNMENT_CLEAR
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("originNodeId")] public string OriginNodeId { get; set; } = string.Empty;
    }
}
