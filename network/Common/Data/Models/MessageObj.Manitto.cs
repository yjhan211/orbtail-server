#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    // ===== 미션 =====

    [MessagePackObject]
    public class ChecklistTaskInfo : IMessagePackObject
    {
        [Key("taskId")] public int TaskId { get; set; }
        [Key("category")] public ChecklistTaskCategory Category { get; set; }
        [Key("taskKey")] public string TaskKey { get; set; } = "";
        [Key("titleKr")] public string TitleKr { get; set; } = "";
        [Key("descriptionKr")] public string DescriptionKr { get; set; } = "";
        [Key("score")] public float Score { get; set; }
        [Key("areaType")] public int AreaType { get; set; }
        [Key("areaNameKr")] public string AreaNameKr { get; set; } = "";
        [Key("objectType")] public int ObjectType { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
        [Key("staminaCost")] public int StaminaCost { get; set; }
        [Key("durationSeconds")] public int DurationSeconds { get; set; }
        [Key("requiredItemId")] public int RequiredItemId { get; set; }
        [Key("requiredItemNameKr")] public string RequiredItemNameKr { get; set; } = "";
        [Key("requiredItemPolicy")] public ChecklistRequiredItemPolicy RequiredItemPolicy { get; set; }
        [Key("successLogKr")] public string SuccessLogKr { get; set; } = "";
        [Key("progress")] public float Progress { get; set; }
    }

    [MessagePackObject]
    public class ChecklistTaskProgressInfo : IMessagePackObject
    {
        [Key("taskId")] public int TaskId { get; set; }
        [Key("progress")] public float Progress { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CHECKLIST_INFO : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("roundNumber")] public int RoundNumber { get; set; }
        [Key("activeTaskIds")] public List<int> ActiveTaskIds { get; set; } = new();
        [Key("completedTaskIds")] public List<int> CompletedTaskIds { get; set; } = new();
        [Key("activeTaskProgresses")] public List<ChecklistTaskProgressInfo> ActiveTaskProgresses { get; set; } = new();
        [Key("generalJobScore")] public float GeneralJobScore { get; set; }
        [Key("manittoRoleScore")] public float ManittoRoleScore { get; set; }
        [Key("bonusScore")] public float BonusScore { get; set; }
        [Key("contribution")] public int Contribution { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_CHECKLIST_ACTIVITY_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CHECKLIST_ACTIVITY_ACK : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("cooldownRemainSeconds")] public int CooldownRemainSeconds { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_CHECKLIST_ACTIVITY_FINISH : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CHECKLIST_ACTIVITY_RESULT : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("awardedScore")] public float AwardedScore { get; set; }
        [Key("awardedContribution")] public int AwardedContribution { get; set; }
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
        [Key("narrativeKr")] public string NarrativeKr { get; set; }
        [Key("narrativeEn")] public string NarrativeEn { get; set; }
        [Key("narrativeJp")] public string NarrativeJp { get; set; }
    }

    [MessagePackObject]
    public class MissionGraphNodeProgressInfo : IMessagePackObject
    {
        [Key("nodeId")] public int NodeId { get; set; }
        [Key("nodeKey")] public string NodeKey { get; set; } = "";
        [Key("nodeKind")] public int NodeKind { get; set; }
        [Key("storyletId")] public string StoryletId { get; set; } = "";
        [Key("storyletType")] public int StoryletType { get; set; }
        [Key("targetAreaType")] public int TargetAreaType { get; set; }
        [Key("targetObjectType")] public int TargetObjectType { get; set; }
        [Key("claimPolicy")] public int ClaimPolicy { get; set; }
        [Key("rewardKind")] public int RewardKind { get; set; }
        [Key("caseGroup")] public string CaseGroup { get; set; } = "";
        [Key("isVictoryStorylet")] public bool IsVictoryStorylet { get; set; }
        [Key("isCompleted")] public bool IsCompleted { get; set; }
        [Key("isUnlocked")] public bool IsUnlocked { get; set; }
        [Key("isAvailable")] public bool IsAvailable { get; set; }
        [Key("isDiscovered")] public bool IsDiscovered { get; set; }
        [Key("isTracked")] public bool IsTracked { get; set; }
        [Key("isClaimed")] public bool IsClaimed { get; set; }
        [Key("isLost")] public bool IsLost { get; set; }
    }

    // ===== 부품 결합 시스템 (v0.2.0 — 이슈 #38) =====

    /// <summary>
    ///     부품 결합 결과. 중간재(IsRaceComplete=false) 또는 최종(IsRaceComplete=true).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_ITEMS_COMBINED : IMessagePackObject
    {
        [Key("recipeId")] public int RecipeId { get; set; }
        [Key("inputPartA")] public int InputItemA { get; set; }
        [Key("inputPartB")] public int InputItemB { get; set; }
        [Key("outputPartId")] public int OutputItemId { get; set; }
        [Key("outputPartNameKr")] public string OutputItemName { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
        [Key("isRaceComplete")] public bool IsRaceComplete { get; set; }
    }

    /// <summary>
    ///     부품 결합 요청.
    ///     #87 N12: 동시 race 완주 시 결합 시작 시각이 빠른 쪽이 우선. 0이면 서버는 패킷 도착 시각으로 폴백.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_COMBINE_ITEMS : IMessagePackObject
    {
        [Key("partA")] public int ItemA { get; set; }
        [Key("partB")] public int ItemB { get; set; }
        /// <summary>클라이언트 결합 액션 시작 시각 (UTC Unix ms). #87 동시성 가드용. 미지원 클라는 0.</summary>
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    [MessagePackObject]
    public class MissionShortRewardInfo : IMessagePackObject
    {
        [Key("rewardType")] public int RewardType { get; set; }
        [Key("remainingUses")] public int RemainingUses { get; set; }
        [Key("valuePercent")] public int ValuePercent { get; set; }
        [Key("durationSeconds")] public int DurationSeconds { get; set; }
        [Key("expiresAtUnixMs")] public long ExpiresAtUnixMs { get; set; }
    }

    /// <summary>
    ///     v0.2.1 (#79) — RNG 채집 결과 통합 패킷. 5종 결과(부품/선행/디코이/빈손/지역 아이템) 단일 응답.
    ///     - 부품/선행 회수 시 추가로 G_TO_C_PART_COLLECTED / G_TO_C_PREREQUISITE_COLLECTED 송신 (인벤토리 갱신용)
    ///     - 본 패킷은 ItemAlert / 시각 이펙트 / 쿨타임 갱신 트리거 전용 (회수자 한정)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_RESULT : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        /// <summary>0=빈손, 1=디코이, 2=지역 아이템, 3=부품, 4=선행</summary>
        [Key("resultType")] public int ResultType { get; set; }
        /// <summary>부품/선행/지역 아이템의 식별자 (resultType 0/1은 0). 클라가 csv로 텍스트 조회.</summary>
        [Key("itemId")] public int ItemId { get; set; }
        [Key("staminaReward")] public int StaminaReward { get; set; }
        /// <summary>다음 채집 가능까지 쿨타임 (초). 30초 표준, 0이면 클라 기본값 사용</summary>
        [Key("cooldownSeconds")] public int CooldownSeconds { get; set; }
    }

    /// <summary>
    ///     v0.2.1 (#134) — RNG 채집 인스턴스 쿨타임 broadcast. 매칭 내 모든 클라가 받아 해당 InteractId 마커를
    ///     cooldownSeconds 동안 숨김. 결과(itemId/이름/사유) 정보는 포함 X — 직책 노출 방지 (회수자만 RESULT 받음).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("cooldownSeconds")] public int CooldownSeconds { get; set; }
    }

    [MessagePackObject]
    public class InteractCooldownSnapshotEntry : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("remainSeconds")] public int RemainSeconds { get; set; }
    }

    /// <summary>
    ///     #137 — 합류/리커넥트 클라이언트용 현재 InteractObject cooldown snapshot.
    ///     실시간 갱신은 G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST가 계속 담당한다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_INTERACT_COOLDOWN_SNAPSHOT : IMessagePackObject
    {
        [Key("entries")] public List<InteractCooldownSnapshotEntry> Entries { get; set; } = new();
    }

    /// <summary>
    ///     #134 — RNG 채집 시작 요청. RippleMarker 클릭 즉시 송신. 서버가 stamina 차감 + 쿨타임 등록 + ACK 응답.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_RNG_COLLECT_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    /// <summary>
    ///     #134 — RNG 채집 시작 승인/거부 응답.
    ///     ErrorCode=SUCCESS면 progress 진행 후 FINISH 송신. 거부면 클라가 InteractionPanel 닫음.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_ACK : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        /// <summary>거부 시 cooldown 남은 초 (이미 회수됨 케이스). 성공 시 0.</summary>
        [Key("cooldownRemainSeconds")] public int CooldownRemainSeconds { get; set; }
    }

    /// <summary>
    ///     #134 — RNG 채집 progress 완료 → 결과 산출 요청. 서버가 RNG 분포로 결과 결정 + RESULT 응답.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_RNG_COLLECT_FINISH : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("encounterCheckOnly")] public bool EncounterCheckOnly { get; set; }
    }

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
    ///     수축 길이(매치 길이 - 유예)·보행 거리 필드(SwarmPressureField)는 Common이 단일 출처라
    ///     클라가 서버와 같은 값으로 안전 거리를 보간해 경계를 그린다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_FIELD_STATE : IMessagePackObject
    {
        [Key("startedAtUnixMs")] public long StartedAtUnixMs { get; set; }
    }

    // ===== 타겟 위치 추적 =====

    [MessagePackObject]
    public class G_TO_C_TARGET_LOCATION : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
    }

    // ===== 기척 (프로토 0, #159) =====

    [MessagePackObject]
    public class PresenceCandidate : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }

        // 최근 25초 조우 강도 0~5 (정수부=완료 슬롯, 소수부=진행 슬롯). 표시 양자화는 클라가 담당.
        [Key("presence")] public float Presence { get; set; }

        // 정체성 — 후보가 현재 같은 구역에 없어도 카드를 채울 수 있도록 서버가 함께 전송.
        // 봇은 서버 메모리값, 인간은 비어 올 수 있고 그땐 클라가 FindPlayerByPlayerId로 폴백.
        [Key("name")] public string Name { get; set; }
        [Key("wearItemIds")] public List<int> WearItemIds { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PRESENCE_UPDATE : IMessagePackObject
    {
        [Key("candidates")] public List<PresenceCandidate> Candidates { get; set; }
    }

    [MessagePackObject]
    public class PresenceNotebookEntry : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("lastSeenArea")] public AreaType LastSeenArea { get; set; }
        [Key("totalOverlapSeconds")] public int TotalOverlapSeconds { get; set; }
        [Key("longestOverlapSeconds")] public int LongestOverlapSeconds { get; set; }
        [Key("currentOverlapSeconds")] public int CurrentOverlapSeconds { get; set; }
        [Key("overlapStartCount")] public int OverlapStartCount { get; set; }
        [Key("enterAfterObserverCount")] public int EnterAfterObserverCount { get; set; }
        [Key("alreadyThereWhenObserverArrivedCount")] public int AlreadyThereWhenObserverArrivedCount { get; set; }
        [Key("unclassifiedOverlapStartCount")] public int UnclassifiedOverlapStartCount { get; set; }
        [Key("isCurrentlyOverlapping")] public bool IsCurrentlyOverlapping { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PRESENCE_NOTEBOOK_UPDATE : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("roundNumber")] public int RoundNumber { get; set; }
        [Key("entries")] public List<PresenceNotebookEntry> Entries { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_BOOKMARK_PRESENCE : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_BOOKMARK_PRESENCE_RESULT : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SHARP_GAZE_MARK_UPDATE : IMessagePackObject
    {
        [Key("isActive")] public bool IsActive { get; set; }
    }

    // ===== 흔적 =====

    // ===== 색출 =====

    [MessagePackObject]
    public class SettlementNominationEntry : IMessagePackObject
    {
        [Key("nominatorPlayerId")] public long NominatorPlayerId { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    }

    // ===== 탈락 & 체인 =====

    [MessagePackObject]
    public class G_TO_C_PLAYER_ELIMINATED : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("attackerPlayerId")] public long AttackerPlayerId { get; set; }
        [Key("reason")] public EliminationReason Reason { get; set; }
        [Key("resultPlayers")] public List<GameResultPlayerInfo> ResultPlayers { get; set; } = new();
        [Key("resultChunkIndex")] public int ResultChunkIndex { get; set; }
        [Key("isResultEnd")] public bool IsResultEnd { get; set; } = true;
    }

    // ===== 1:1 상호작용 선택지 =====

    /// <summary>
    ///     질문 선택지 카테고리
    /// </summary>
    public enum InteractionQuestionType : short
    {
        ASK_JOB = 1,          // 직책 추궁: "너 무슨 직책이야?"
        ASK_LOCATION = 2,     // 동선 추궁: "[X구역]에서 방금 나왔지?"
        CROSS_CHECK = 3,      // 교차 검증: "[Y]도 같은 직책이라던데?"
        ASK_TRACE = 4,         // 흔적 추궁: "여기 누가 온 것 같던데?"
        ASK_NEARBY_REASON = 5,
        ENCOUNTER_ACTION = 6
    }

    /// <summary>
    ///     다국어 텍스트 args의 타입. 클라가 enum 값을 다국어 라벨로 변환할 수 있게 한다.
    /// </summary>
    public enum TextArgType : byte
    {
        RAW_STRING = 0,
        INT_NUMBER = 1,
        AREA_TYPE = 2,    // IntValue를 AreaType으로 캐스팅 → GameAreaNameData 룩업
        JOB_TITLE = 3,
        ITEM_NAME = 4
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

    // ===== 게임 결과 =====

    [MessagePackObject]
    public class GameResultPlayerInfo : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("name")] public string Name { get; set; } = "";
        [Key("jobTitle")] public JobTitle JobTitle { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("manittoPlayerId")] public long WatcherPlayerId { get; set; }
        [Key("eliminationReason")] public EliminationReason EliminationReason { get; set; }
        [Key("survivalTimeSeconds")] public int SurvivalTimeSeconds { get; set; }
        [Key("finalStatus")] public PlayerMatchStatus FinalStatus { get; set; }
        [Key("corruption")] public int Corruption { get; set; }
        [Key("maxCorruption")] public int MaxCorruption { get; set; }
        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; } = new();
        [Key("killCount")] public int KillCount { get; set; }
        [Key("totalDamageDealt")] public int TotalDamageDealt { get; set; }
        [Key("totalRecovery")] public int TotalRecovery { get; set; }
        [Key("attackerPlayerId")] public long AttackerPlayerId { get; set; }
        [Key("eliminatedArea")] public AreaType EliminatedArea { get; set; }
        [Key("isAreaClosureElimination")] public bool IsAreaClosureElimination { get; set; }
        [Key("isOvertimeElimination")] public bool IsOvertimeElimination { get; set; }
        [Key("rank")] public int Rank { get; set; }
        [Key("finalOrbTier")] public int FinalOrbTier { get; set; }

        // 결과 화면 승점 (#229): 오브 수. 인게임 순위가 오브로 매겨지는데 결과표만 잼 지갑을
        // 읽고 있었다 — 잼은 #226에서 퇴역해 스포너가 없어 10명 전원 0으로 떴다.
        [Key("orbCount")] public int OrbCount { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GAME_RESULT : IMessagePackObject
    {
        [Key("winnerId")] public long WinnerId { get; set; }
        [Key("isTimeout")] public bool IsTimeout { get; set; }
        [Key("players")] public List<GameResultPlayerInfo> Players { get; set; }
        [Key("resultChunkIndex")] public int ResultChunkIndex { get; set; }
        [Key("isResultEnd")] public bool IsResultEnd { get; set; } = true;
    }
}
