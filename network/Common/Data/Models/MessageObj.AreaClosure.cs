#pragma warning disable CS8618
// ReSharper disable All
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    // ===== 구역 폐쇄 =====

    [MessagePackObject]
    public class G_TO_C_AREA_CLOSURE_WARNING : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("secondsRemaining")] public int SecondsRemaining { get; set; }

        /// <summary>서버 측 폐쇄 예정 시각 (UTC Unix ms). 클라이언트는 이 값으로 카운트다운하여 네트워크 지연 보정.</summary>
        [Key("closureAtUnixMs")] public long ClosureAtUnixMs { get; set; }
        [Key("isGlobalClosure")] public bool IsGlobalClosure { get; set; }
        [Key("isGlobalClosureActive")] public bool IsGlobalClosureActive { get; set; }
    }
    [MessagePackObject]
    public class G_TO_C_AREA_CLOSED : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("isClosed")] public bool IsClosed { get; set; } = true;
        [Key("suppressAlert")] public bool SuppressAlert { get; set; }
    }

    /// <summary>
    ///     자기장 시계 (#272): 수축 시작 시각(UTC Unix ms)만 나른다 — 유예(SWARM_FIELD_HOLD_SECONDS)·
    ///     수축 길이(매치 길이 - 유예)·거리 필드(SwarmPressureField)는 Common이 단일 출처라
    ///     클라가 서버와 같은 값으로 안전 거리를 보간해 경계를 그린다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_FIELD_STATE : IMessagePackObject
    {
        [Key("startedAtUnixMs")] public long StartedAtUnixMs { get; set; }
    }
}
