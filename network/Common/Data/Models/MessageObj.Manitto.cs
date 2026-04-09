#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    // ===== 미션 =====

    [MessagePackObject]
    public class G_TO_C_MISSION_INFO : IMessagePackObject
    {
        [Key("jobTitle")] public JobTitle JobTitle { get; set; }
        [Key("currentStep")] public int CurrentStep { get; set; }
        [Key("totalSteps")] public int TotalSteps { get; set; }
        [Key("targetArea")] public int TargetArea { get; set; }
        [Key("targetInteractId")] public int TargetInteractId { get; set; }
        [Key("targetActionId")] public int TargetActionId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_MISSION_STEP_COMPLETE : IMessagePackObject
    {
        [Key("completedStep")] public int CompletedStep { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
        // 다음 단계 정보 (null이면 전체 완료)
        [Key("nextTargetArea")] public int NextTargetArea { get; set; }
        [Key("nextTargetInteractId")] public int NextTargetInteractId { get; set; }
        [Key("nextTargetActionId")] public int NextTargetActionId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_MISSION_ALL_COMPLETE : IMessagePackObject
    {
        [Key("jobTitle")] public JobTitle JobTitle { get; set; }
    }

    // ===== 구역 폐쇄 =====

    [MessagePackObject]
    public class G_TO_C_AREA_CLOSURE_WARNING : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("secondsRemaining")] public int SecondsRemaining { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_AREA_CLOSED : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
    }

    // ===== 타겟 위치 추적 =====

    [MessagePackObject]
    public class G_TO_C_TARGET_LOCATION : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
    }

    // ===== 흔적 =====

    [MessagePackObject]
    public class TraceInfo : IMessagePackObject
    {
        [Key("traceId")] public long TraceId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
        [Key("description")] public string Description { get; set; }
        [Key("placedByPlayerId")] public long PlacedByPlayerId { get; set; }
        [Key("isMissionTrace")] public bool IsMissionTrace { get; set; } // 미션 흔적 vs 마니또 배치 흔적
    }

    [MessagePackObject]
    public class G_TO_C_TRACE_CREATED : IMessagePackObject
    {
        [Key("trace")] public TraceInfo Trace { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_TRACE_LIST : IMessagePackObject
    {
        [Key("traces")] public List<TraceInfo> Traces { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_PLACE_TRACE : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PLACE_TRACE_RESULT : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("staminaCost")] public int StaminaCost { get; set; }
    }

    // ===== 색출 =====

    [MessagePackObject]
    public class C_TO_G_DETECT_MANITTO : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_DETECT_RESULT : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("isCorrect")] public bool IsCorrect { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    // ===== 탈락 & 체인 =====

    [MessagePackObject]
    public class G_TO_C_PLAYER_ELIMINATED : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("reason")] public EliminationReason Reason { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CHAIN_BREAK : IMessagePackObject
    {
        [Key("eliminatedPlayerId")] public long EliminatedPlayerId { get; set; }
        // 영향받는 플레이어에게만 전송됨
        [Key("newStatus")] public ManittoStatus NewStatus { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PLAYER_STATUS_CHANGE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("status")] public ManittoStatus Status { get; set; }
    }
}
