#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 탈출 절차 단계 정보 응답
    [MessagePackObject]
    public class G_TO_C_EXIT_STEP_INFO : IMessagePackObject
    {
        [Key("groupId")] public int GroupId { get; set; } // 탈출 절차 그룹 ID
        [Key("currentStepOrder")] public int CurrentStepOrder { get; set; } // 현재 단계 (이보다 작은 order는 완료)
        [Key("totalStepCount")] public int TotalStepCount { get; set; } // 총 단계 수
        [Key("isCompleted")] public bool IsCompleted { get; set; } // 탈출 완료 여부
        [Key("lastAdvancedBy")] public long LastAdvancedBy { get; set; } // 마지막으로 진행한 플레이어 UID (0이면 아직 진행 안함)
    }

    // 탈출 절차 다음 단계 진행 요청
    [MessagePackObject]
    public class C_TO_G_EXIT_ADVANCE : IMessagePackObject
    {
        [Key("currentStepOrder")] public int CurrentStepOrder { get; set; } // 검증용 (클라이언트가 생각하는 현재 단계)
    }

    // 탈출 절차 진행 결과
    [MessagePackObject]
    public class G_TO_C_EXIT_ADVANCE_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("escaped")] public bool Escaped { get; set; } // 탈출 완료 여부
        [Key("newStepOrder")] public int NewStepOrder { get; set; }
    }

    // 탈출 절차 단계 변경 브로드캐스트 (다른 플레이어가 진행시켜도 모두에게 알림)
    [MessagePackObject]
    public class G_TO_C_EXIT_STEP_UPDATE : IMessagePackObject
    {
        [Key("advancedByPlayerId")] public long AdvancedByPlayerId { get; set; }
        [Key("newStepOrder")] public int NewStepOrder { get; set; }
        [Key("escaped")] public bool Escaped { get; set; }
    }

    // 로비 복귀 요청
    [MessagePackObject]
    public class C_TO_G_RETURN_TO_LOBBY : IMessagePackObject
    {
    }

    // 로비 복귀 결과
    [MessagePackObject]
    public class G_TO_C_RETURN_TO_LOBBY_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }
}
