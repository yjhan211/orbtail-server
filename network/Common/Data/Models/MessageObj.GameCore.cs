#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class G_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utcNow")] public DateTime UtcNow { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_CONNECT : IMessagePackObject
    {
        // 신원·매치는 ticket이 증명한다. 클라이언트가 말하는 값은 받지 않는다.
        [Key("gameHandoffTicket")] public string GameHandoffTicket { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CONNECT_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }

        // 성공 시에만 채운다. 스폰은 Game Server가 매치 첫 접속 때 결정하므로 클라이언트는 여기서 처음 안다.
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("spawnCell")] public Cell SpawnCell { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_MOVE : IMessagePackObject
    {
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("inputSeq")] public uint InputSequence { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_MOVE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("cell")] public Cell Cell { get; set; }
        [Key("lastProcessedInput")] public uint LastProcessedInput { get; set; }
        [Key("serverTime")] public long ServerTimestamp { get; set; }
        // 오브 궤도 위상 (#232): 서버가 검증 이동으로 적산한 권위값 — 클라는 자기 적산을 이 값으로 보정한다.
        [Key("orbPhase")] public float OrbOrbitPhaseDegrees { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_AREA_PLAYER_ENTER : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
        [Key("cell")] public Cell Cell { get; set; } // 최신 Cell 위치
    }

    [MessagePackObject]
    public class G_TO_C_AREA_PLAYER_LEAVE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_ENCOUNTER_REVEAL : IMessagePackObject
    {
        /// <summary>수신자 기준으로 감지/드러낼 상대 PlayerId.</summary>
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        /// <summary>1=복도 인기척, 2=복도 조우, 3=방 탐색 조우.</summary>
        [Key("eventType")] public int EventType { get; set; }
        [Key("cooldownSeconds")] public int CooldownSeconds { get; set; }
        [Key("revealDelayMs")] public int RevealDelayMs { get; set; }
        [Key("damageValue")] public int DamageValue { get; set; }
        /// <summary>자동전투로 공개된 대상의 현재 오염도. -1이면 공개 정보가 없다.</summary>
        [Key("targetCorruption")] public int TargetCorruption { get; set; } = -1;
    }

    /// <summary>
    /// 자동 전투 당사자가 아닌 같은 구역 관전자에게만 보내는 월드 이펙트 이벤트다.
    /// HUD 피해, 카메라 흔들기, 탈락 사유 표시는 이 메시지로 처리하지 않는다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_PROXIMITY_ATTACK_VFX : IMessagePackObject
    {
        [Key("attackerPlayerId")] public long AttackerPlayerId { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("weaponItemId")] public int WeaponItemId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_ORB_EFFECT_STATE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("weaponItemId")] public int WeaponItemId { get; set; }
        [Key("isActive")] public bool IsActive { get; set; }
        [Key("orbItemIds")] public List<int> OrbItemIds { get; set; } = new();

        // 앞줄(최저 티어·선입) 오브의 현재 HP — 오브별 체력바 표시용 (#219). -1 = 만충 취급.
        [Key("frontOrbHp")] public int FrontOrbHp { get; set; } = -1;

        // 잼 보유량 (#222 M3) — SB처럼 머리 위에 공개되는 점수. 같은 구역 관전자에게 동기화.
        [Key("jamCount")] public int JamCount { get; set; }

        // 본체 오염 (#226 단계 B 가시화): 같은 구역 상대의 머리 위 게이지를 상시 구동한다 —
        // "때리면 닳는 게 보인다". -1 = 미동기(표시 유지).
        [Key("gauge")] public int BodyCorruption { get; set; } = -1;

        // 방어 강화(내구 2+) 오브 순번 비트마스크 (#226): 은백 링 표시의 단일 출처.
        // OrbItemIds 순서 기준 — 64번째 이후 순번은 표시 생략(실전 상한 밖 안전 절단).
        [Key("armorM")] public long ArmorMask { get; set; }
    }

    /// <summary>잼(승점 재화) 지갑 상태 (#222 M3). 픽업·변동 시 소유자에게 전송.</summary>
    [MessagePackObject]
    public class G_TO_C_JAM_STATE : IMessagePackObject
    {
        [Key("jamCount")] public int JamCount { get; set; }
    }

    /// <summary>열쇠 무료 소환 충전 상태 (#222 M4). 획득·소비 시 소유자에게 전송.</summary>
    [MessagePackObject]
    public class G_TO_C_FREE_SUMMON_STATE : IMessagePackObject
    {
        [Key("charges")] public int Charges { get; set; }
    }

    /// <summary>절단 실험 더미 조종 (#226 실험장, 개발용). WASD 방향 — 서버가 더미를 스텝 이동.</summary>
    [MessagePackObject]
    public class C_TO_G_DEV_DUMMY_MOVE : IMessagePackObject
    {
        [Key("dirX")] public float DirX { get; set; }
        [Key("dirY")] public float DirY { get; set; }
    }

    /// <summary>
    ///     성장 카드 오퍼 (#226 단계 C). 소환석이 비용에 도달하면 서버가 내린다.
    ///     SpawnItemId: 오브 생성 카드가 지급할 오브(색·티어 명시) — 픽 시 그대로 지급.
    ///     EnhanceTargetTier: 0=공격 강화 무효(대상 없음), 1=T1→T2(등급 I), 2=T2→T3(등급 II).
    ///     ArmorCount: 방어 강화가 부여할 외피 장수(0=무효) — 등급 = 장수.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_GROWTH_OFFER : IMessagePackObject
    {
        [Key("offerId")] public int OfferId { get; set; }
        /// <summary>대표 비용(가장 싼 카드). 실제 차감·표시는 Costs가 한다 — 구 클라 호환용.</summary>
        [Key("cost")] public int Cost { get; set; }
        /// <summary>
        ///     카드별 비용 (#229): [0]=소환 · [1]=공격 강화 · [2]=방어 강화.
        ///     셋이 한 곡선을 공유하면 오브를 늘릴수록 강화가 비싸지고 그 반대도 된다 —
        ///     한 축에 투자하면 다른 축이 벌을 받는 구조라 빌드 선택의 의미가 사라진다.
        /// </summary>
        [Key("costs")] public List<int> Costs { get; set; } = new();
        [Key("spawnId")] public int SpawnItemId { get; set; }
        [Key("enhTier")] public int EnhanceTargetTier { get; set; }
        [Key("armorN")] public int ArmorCount { get; set; }
    }

    /// <summary>성장 카드 선택 (#226 단계 C). CardIndex: 0=증식, 1=강화, 2=철갑.</summary>
    [MessagePackObject]
    public class C_TO_G_SWARM_GROWTH_PICK : IMessagePackObject
    {
        [Key("offerId")] public int OfferId { get; set; }
        [Key("cardIndex")] public int CardIndex { get; set; }
    }

    /// <summary>성장 카드 선택 결과 (#226 단계 C). 실패 시 오퍼는 유지된다.</summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_GROWTH_RESULT : IMessagePackObject
    {
        [Key("offerId")] public int OfferId { get; set; }
        [Key("cardIndex")] public int CardIndex { get; set; }
        [Key("success")] public bool Success { get; set; }
        [Key("stones")] public int StoneCount { get; set; }
    }

    /// <summary>
    ///     스웜 링 연출 (#226). 같은 구역에 브로드캐스트 — 링 중심·반경.
    ///     Kind: 0=포위 완성, 1=절단 파열, 2=파도 물폭탄 — 클라가 색·효과음을 분기한다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_ENCIRCLE_VFX : IMessagePackObject
    {
        [Key("ownerId")] public long OwnerPlayerId { get; set; }
        [Key("centerX")] public float CenterX { get; set; }
        [Key("centerY")] public float CenterY { get; set; }
        [Key("radius")] public float Radius { get; set; }
        [Key("kind")] public int Kind { get; set; }

        // 절단(kind 1) 전용: 잘린 열의 주인과 절단 시작 순번 — 클라가 꼬리 섬광을 그린다.
        [Key("victimId")] public long VictimPlayerId { get; set; }
        [Key("ord")] public int FromOrdinal { get; set; }
    }

    /// <summary>
    ///     교차사격 예고 (#232 2단계). 오브가 몬스터를 향해 쏘는 순간 서버가 모양을 잠그고
    ///     같은 구역 전원에게 보낸다. Shape 1 = 직선(태양): Origin→End 선분에 폭 Width의 캡슐.
    ///     클라는 TelegraphSeconds 동안 예고색으로 그리다 ActiveSeconds 동안 판정색으로 바꾼다.
    ///     서버 판정은 같은 좌표·같은 시간을 쓴다 — 표시 = 판정.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_CROSSFIRE_TELEGRAPH : IMessagePackObject
    {
        [Key("eventId")] public long EventId { get; set; }
        [Key("ownerId")] public long OwnerPlayerId { get; set; }
        [Key("weaponItemId")] public int WeaponItemId { get; set; }
        [Key("shape")] public int Shape { get; set; }
        [Key("originX")] public float OriginX { get; set; }
        [Key("originY")] public float OriginY { get; set; }
        [Key("endX")] public float EndX { get; set; }
        [Key("endY")] public float EndY { get; set; }
        [Key("width")] public float Width { get; set; }
        [Key("telegraphSeconds")] public float TelegraphSeconds { get; set; }
        [Key("activeSeconds")] public float ActiveSeconds { get; set; }
        [Key("anchorMonsterId")] public int AnchorMonsterId { get; set; }

        // 발사한 오브의 열 순번 — 클라는 서버 원점 대신 자기가 그리는 그 슬롯 위치에서 선을 시작한다
        // (서버 꼬리 좌표와 클라 슬롯이 어긋나 "오브가 아닌 곳에서 나가는" 것처럼 보이던 문제).
        [Key("ownerOrbOrdinal")] public int OwnerOrbOrdinal { get; set; }
    }

    /// <summary>
    ///     계열 공유 레벨 (#232 4단계). 플레이어별 태양·바람·파도 T1~T3와 다음 강화 비용.
    ///     보유하지 않은 계열의 비용은 0(강화 불가)이다. 시작·강화·오브 증감 때 보낸다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_FAMILY_LEVELS : IMessagePackObject
    {
        [Key("sun")] public int SunLevel { get; set; }
        [Key("wind")] public int WindLevel { get; set; }
        [Key("wave")] public int WaveLevel { get; set; }
        [Key("sunCost")] public int SunCost { get; set; }
        [Key("windCost")] public int WindCost { get; set; }
        [Key("waveCost")] public int WaveCost { get; set; }
    }

    /// <summary>
    ///     6칸 빌드 결정 (#232 4단계). Action 1 = 오브 강화, TargetItemUid = OrbColor 값 —
    ///     그 계열에서 몸체에 가장 가까운 T3 미만 오브 하나가 한 티어 오른다 (2026-08-18, 구 계열 일괄 강화).
    ///     서버 권위 — 강화할 오브 없음·소환석 부족이면 거절.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_SWARM_ORB_DECISION : IMessagePackObject
    {
        [Key("action")] public int Action { get; set; }
        [Key("targetUid")] public long TargetItemUid { get; set; }
        [Key("secondUid")] public long SecondItemUid { get; set; }
    }

    /// <summary>
    ///     결정 결과 (#232 4단계). ResultItemId: 강화된 오브의 새 아이템, TargetOrdinal: 그 오브의 열 순번
    ///     (0 = 몸체 바로 뒤; 실패·해당 없음 -1) — 클라가 강화 이펙트를 그 오브 위에 띄운다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_ORB_DECISION_RESULT : IMessagePackObject
    {
        [Key("action")] public int Action { get; set; }
        [Key("success")] public bool Success { get; set; }
        [Key("resultItemId")] public int ResultItemId { get; set; }
        [Key("targetUid")] public long TargetItemUid { get; set; }
        [Key("stones")] public int StoneCount { get; set; }
        [Key("ordinal")] public int TargetOrdinal { get; set; } = -1;
    }

    /// <summary>
    ///     잼 리더보드 (#222 M3). 전 참가자를 잼 내림차순으로 정렬한 병렬 리스트다.
    ///     구역 게이트 없이 매치 전역으로 브로드캐스트 — 순위표(RankDisplay)의 단일 출처.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_JAM_RANKINGS : IMessagePackObject
    {
        [Key("playerIds")] public List<long> PlayerIds { get; set; } = new();
        [Key("jamCounts")] public List<int> JamCounts { get; set; } = new();
    }

    /// <summary>
    /// 공개 미니맵용 지역 자연 재고 상태다. 정확한 잔여 수량은 전송하지 않는다.
    /// </summary>
    [MessagePackObject]
    public class AreaNaturalStockState : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("isDepleted")] public bool IsDepleted { get; set; }
        // 공개 정보는 색상별 소진 여부까지만이다. 정확한 남은 개수는 서버에만 둔다.
        [Key("availableOrbColors")] public List<OrbColor> AvailableOrbColors { get; set; } = new();
    }

    [MessagePackObject]
    public class G_TO_C_AREA_STOCK_STATE : IMessagePackObject
    {
        [Key("areas")] public List<AreaNaturalStockState> Areas { get; set; } = new();
    }

    [MessagePackObject]
    public class G_TO_C_AREA_EXIT_BLOCKED : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; } // 나가려던 Area
        [Key("correctedCell")] public Cell CorrectedCell { get; set; } // 되돌아갈 셀 위치
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_LIST : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("objects")] public List<InteractableObjectState> Objects { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_UPDATE : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("order")] public int Order { get; set; }
        [Key("isExplored")] public bool IsExplored { get; set; }
        [Key("exploredBy")] public long ExploredBy { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfoList")] public List<PlayerInfo> PlayerInfoList { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GAME_TIME_WARNING : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("remainingSeconds")] public int RemainingSeconds { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_MATCH_START_COUNTDOWN : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("remainingSeconds")] public int RemainingSeconds { get; set; }
        [Key("serverUnixMs")] public long ServerUnixMs { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GAME_END : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("isEscaped")] public bool IsEscaped { get; set; } // true: 탈출 성공, false: 탈출 실패 (시간 초과)
    }

    [MessagePackObject]
    public class G_TO_C_ERROR : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }
}
