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

    // ===== 1:1 상호작용 선택지 =====

    /// <summary>
    ///     질문 선택지 카테고리
    /// </summary>
    public enum InteractionQuestionType : short
    {
        ASK_JOB = 1,          // 직책 추궁: "너 무슨 직책이야?"
        ASK_LOCATION = 2,     // 동선 추궁: "[X구역]에서 방금 나왔지?"
        CROSS_CHECK = 3,      // 교차 검증: "[Y]도 도서위원이라던데?"
        ASK_TRACE = 4         // 흔적 추궁: "여기 누가 온 것 같던데?"
    }

    /// <summary>
    ///     질문 선택지 항목
    /// </summary>
    [MessagePackObject]
    public class InteractionQuestion : IMessagePackObject
    {
        [Key("questionType")] public InteractionQuestionType QuestionType { get; set; }
        [Key("text")] public string Text { get; set; }
        /// <summary>교차검증 시 참조 플레이어 ID</summary>
        [Key("referencePlayerId")] public long ReferencePlayerId { get; set; }
        /// <summary>동선추궁 시 참조 구역</summary>
        [Key("referenceArea")] public AreaType ReferenceArea { get; set; }
    }

    /// <summary>
    ///     답변 선택지 항목
    /// </summary>
    [MessagePackObject]
    public class InteractionAnswer : IMessagePackObject
    {
        [Key("isTrue")] public bool IsTrue { get; set; }  // 진실인지 거짓인지
        [Key("claimedJob")] public JobTitle ClaimedJob { get; set; }
        [Key("text")] public string Text { get; set; }
    }

    /// <summary>
    ///     대화 수락 시 질문 선택지 전송
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_INTERACTION_CHOICES : IMessagePackObject
    {
        [Key("partnerPlayerId")] public long PartnerPlayerId { get; set; }
        [Key("isAsker")] public bool IsAsker { get; set; }  // true=질문자, false=답변자(대기)
        [Key("questions")] public List<InteractionQuestion> Questions { get; set; }
    }

    /// <summary>
    ///     질문자가 질문 선택
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_INTERACTION_ASK : IMessagePackObject
    {
        [Key("questionType")] public InteractionQuestionType QuestionType { get; set; }
    }

    /// <summary>
    ///     답변자에게 답변 선택지 전송
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_INTERACTION_ANSWER_CHOICES : IMessagePackObject
    {
        [Key("questionType")] public InteractionQuestionType QuestionType { get; set; }
        [Key("questionText")] public string QuestionText { get; set; }
        [Key("answers")] public List<InteractionAnswer> Answers { get; set; }
    }

    /// <summary>
    ///     답변자가 답변 선택
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_INTERACTION_ANSWER : IMessagePackObject
    {
        [Key("answerIndex")] public int AnswerIndex { get; set; }  // 선택한 답변 인덱스
    }

    /// <summary>
    ///     상호작용 결과 (양쪽에 전송)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_INTERACTION_RESULT : IMessagePackObject
    {
        [Key("partnerPlayerId")] public long PartnerPlayerId { get; set; }
        [Key("questionType")] public InteractionQuestionType QuestionType { get; set; }
        [Key("claimedJob")] public JobTitle ClaimedJob { get; set; }       // 상대가 주장한 직책
        [Key("claimedArea")] public AreaType ClaimedArea { get; set; }     // 상대가 주장한 알리바이(구역)
        [Key("isFakeDetected")] public bool IsFakeDetected { get; set; }   // 사칭 발각 여부
        [Key("conflictInfo")] public string ConflictInfo { get; set; }     // 충돌 정보 (교차검증 결과)
    }

    // ===== 시한부 사보타주 =====

    /// <summary>
    ///     시한부 전용: 사보타주 요청 (패키지 Y 4B 개선, #24)
    ///     대상 1명 지정 → 다음 미션 단계 무효화 + ▓▓ 위치 5초 공개
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_SABOTAGE_MISSION : IMessagePackObject
    {
        /// <summary>사보타주 대상 플레이어 ID (생존자 중 1명)</summary>
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        /// <summary>레거시 호환 (interactId 기반 재설정, 미사용)</summary>
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SABOTAGE_RESULT : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("staminaCost")] public int StaminaCost { get; set; }
    }

    /// <summary>
    ///     훼손으로 미션 목적지가 재설정된 플레이어에게 전송
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_MISSION_REDIRECTED : IMessagePackObject
    {
        [Key("currentStep")] public int CurrentStep { get; set; }
        [Key("newTargetArea")] public int NewTargetArea { get; set; }
        [Key("newTargetInteractId")] public int NewTargetInteractId { get; set; }
        [Key("newTargetActionId")] public int NewTargetActionId { get; set; }
    }

    /// <summary>
    ///     사보타주 4B: ▓▓(타겟) 위치를 모든 생존자에게 5초간 공개 (패키지 Y, #24)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SABOTAGE_TARGET_EXPOSED : IMessagePackObject
    {
        /// <summary>사보타주를 발동한 시한부 플레이어 ID</summary>
        [Key("terminalPlayerId")] public long TerminalPlayerId { get; set; }
        /// <summary>시한부의 타겟(▓▓) 플레이어 ID</summary>
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        /// <summary>타겟이 현재 위치한 구역</summary>
        [Key("targetAreaType")] public AreaType TargetAreaType { get; set; }
        /// <summary>위치 공개 지속 시간 (초)</summary>
        [Key("exposeDurationSeconds")] public int ExposeDurationSeconds { get; set; }
    }

    // ===== 게임 결과 =====

    [MessagePackObject]
    public class GameResultPlayerInfo : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("jobTitle")] public JobTitle JobTitle { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("manittoPlayerId")] public long ManittoPlayerId { get; set; }
        [Key("eliminationReason")] public EliminationReason EliminationReason { get; set; }
        [Key("survivalTimeSeconds")] public int SurvivalTimeSeconds { get; set; }
        [Key("finalStatus")] public ManittoStatus FinalStatus { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GAME_RESULT : IMessagePackObject
    {
        [Key("winnerId")] public long WinnerId { get; set; }
        [Key("isTimeout")] public bool IsTimeout { get; set; }
        [Key("players")] public List<GameResultPlayerInfo> Players { get; set; }
    }
}
