#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    // ===== 미션 =====

    /// <summary>
    ///     v0.2.0 — 게임 시작 시 직책 + 부품 진행 정보 전달.
    ///     CurrentStep / TotalSteps는 회수+결합 결과 부품 수 기반(호환).
    ///     TargetArea / TargetInteractId / TargetActionId는 v0.2.0에서 의미 없음(0 송신).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_MISSION_INFO : IMessagePackObject
    {
        [Key("jobTitle")] public JobTitle JobTitle { get; set; }
        [Key("currentStep")] public int CurrentStep { get; set; }
        [Key("totalSteps")] public int TotalSteps { get; set; }
        [Key("targetArea")] public int TargetArea { get; set; }
        [Key("targetInteractId")] public int TargetInteractId { get; set; }
        [Key("targetActionId")] public int TargetActionId { get; set; }
        /// <summary>v0.2.0 — 직책별 모든 부품(소재 4 + 중간재 2 + 최종 1) 메타데이터</summary>
        [Key("parts")] public List<MissionPartInfo> Parts { get; set; }
    }

    /// <summary>
    ///     v0.2.0 — 부품 메타 정보 (클라 UI 표시용)
    /// </summary>
    [MessagePackObject]
    public class MissionPartInfo : IMessagePackObject
    {
        [Key("partId")] public int PartId { get; set; }
        [Key("partNameKr")] public string PartNameKr { get; set; }
        [Key("partTier")] public int PartTier { get; set; }
        [Key("targetArea")] public int TargetArea { get; set; }
        [Key("targetObjectType")] public int TargetObjectType { get; set; }
        [Key("prerequisiteShareGroup")] public int PrerequisiteShareGroup { get; set; }
        [Key("isCollected")] public bool IsCollected { get; set; }
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

    // ===== 부품 결합 시스템 (v0.2.0 — 이슈 #38) =====

    /// <summary>
    ///     부품 회수 알림 (소재 회수). object_action result_type=1 액션 선택 → 직책 발견 풀 매칭 시 송신.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PART_COLLECTED : IMessagePackObject
    {
        [Key("partId")] public int PartId { get; set; }
        [Key("partNameKr")] public string PartNameKr { get; set; }
        [Key("partTier")] public int PartTier { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
    }

    /// <summary>
    ///     부품 결합 결과. 중간재(IsRaceComplete=false) 또는 최종(IsRaceComplete=true).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PART_COMBINED : IMessagePackObject
    {
        [Key("recipeId")] public int RecipeId { get; set; }
        [Key("inputPartA")] public int InputPartA { get; set; }
        [Key("inputPartB")] public int InputPartB { get; set; }
        [Key("outputPartId")] public int OutputPartId { get; set; }
        [Key("outputPartNameKr")] public string OutputPartNameKr { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
        [Key("isRaceComplete")] public bool IsRaceComplete { get; set; }
    }

    /// <summary>
    ///     부품 결합 요청.
    ///     #87 N12: 동시 race 완주 시 결합 시작 시각이 빠른 쪽이 우선. 0이면 서버는 패킷 도착 시각으로 폴백.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_COMBINE_PARTS : IMessagePackObject
    {
        [Key("partA")] public int PartA { get; set; }
        [Key("partB")] public int PartB { get; set; }
        /// <summary>클라이언트 결합 액션 시작 시각 (UTC Unix ms). #87 동시성 가드용. 미지원 클라는 0.</summary>
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    /// <summary>
    ///     선행 아이템 회수 알림. (장갑/드라이버/로프/걸레/결재 잉크 등 share_group 1~5)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PREREQUISITE_COLLECTED : IMessagePackObject
    {
        [Key("shareGroup")] public int ShareGroup { get; set; }
        [Key("itemNameKr")] public string ItemNameKr { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
    }

    /// <summary>
    ///     색출 적중 시 부품 전이 알림. 마니또(피탈자) → 색출자(획득자)로 가장 가치 높은 부품 1개 이동.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PART_STOLEN : IMessagePackObject
    {
        [Key("partId")] public int PartId { get; set; }
        [Key("partNameKr")] public string PartNameKr { get; set; }
        [Key("partTier")] public int PartTier { get; set; }
        /// <summary>피탈자(마니또) 플레이어 ID</summary>
        [Key("fromPlayerId")] public long FromPlayerId { get; set; }
        /// <summary>획득자(색출자) 플레이어 ID</summary>
        [Key("toPlayerId")] public long ToPlayerId { get; set; }
    }

    /// <summary>
    ///     사보타주 부품 무효화 알림. 대상의 가장 가치 높은 부품 1개 인벤토리에서 제거(재회수 불가).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PART_INVALIDATED : IMessagePackObject
    {
        [Key("partId")] public int PartId { get; set; }
        [Key("partNameKr")] public string PartNameKr { get; set; }
        [Key("partTier")] public int PartTier { get; set; }
        /// <summary>무효화 대상 플레이어 ID</summary>
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    // ===== 구역 폐쇄 =====

    [MessagePackObject]
    public class G_TO_C_AREA_CLOSURE_WARNING : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("secondsRemaining")] public int SecondsRemaining { get; set; }

        /// <summary>서버 측 폐쇄 예정 시각 (UTC Unix ms). 클라이언트는 이 값으로 카운트다운하여 네트워크 지연 보정.</summary>
        [Key("closureAtUnixMs")] public long ClosureAtUnixMs { get; set; }
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
    ///     다국어 텍스트 args의 타입. 클라가 enum 값을 다국어 라벨로 변환할 수 있게 한다.
    /// </summary>
    public enum TextArgType : byte
    {
        RAW_STRING = 0,
        INT_NUMBER = 1,
        AREA_TYPE = 2,    // IntValue를 AreaType으로 캐스팅 → GameAreaNameData 룩업
        JOB_TITLE = 3     // IntValue를 JobTitle로 캐스팅 → 직책 textId(11000~)로 룩업
    }

    /// <summary>
    ///     system_text.csv textId 포맷 인자. 서버는 enum 값을 그대로 보내고 클라가 현재 언어로 변환.
    /// </summary>
    [MessagePackObject]
    public class TextArg : IMessagePackObject
    {
        [Key("type")] public TextArgType Type { get; set; }
        [Key("intValue")] public int IntValue { get; set; }
        [Key("stringValue")] public string StringValue { get; set; }
    }

    /// <summary>
    ///     질문 선택지 항목
    /// </summary>
    [MessagePackObject]
    public class InteractionQuestion : IMessagePackObject
    {
        [Key("questionType")] public InteractionQuestionType QuestionType { get; set; }
        [Key("textId")] public int TextId { get; set; }
        [Key("args")] public List<TextArg> Args { get; set; }
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
        [Key("textId")] public int TextId { get; set; }
        [Key("args")] public List<TextArg> Args { get; set; }
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
        [Key("questionTextId")] public int QuestionTextId { get; set; }
        [Key("questionArgs")] public List<TextArg> QuestionArgs { get; set; }
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
        [Key("conflictTextId")] public int ConflictTextId { get; set; }    // 사칭 발각 시 충돌 정보 textId (0이면 미표시)
        [Key("conflictArgs")] public List<TextArg> ConflictArgs { get; set; }
        [Key("answerTextId")] public int AnswerTextId { get; set; }        // 답변자가 고른 답변 textId (양쪽 동일 표시용)
        [Key("answerArgs")] public List<TextArg> AnswerArgs { get; set; }
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
