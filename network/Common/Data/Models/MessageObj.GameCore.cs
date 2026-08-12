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
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }

        // 마니또 체인 정보 (UserServer → Client → GameServer 전달)
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("targetJobTitle")] public JobTitle TargetJobTitle { get; set; }
        [Key("myJobTitle")] public JobTitle MyJobTitle { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CONNECT_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_MOVE : IMessagePackObject
    {
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("inputSeq")] public uint InputSequence { get; set; }
        [Key("clientTime")] public long ClientTimestamp { get; set; }
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
    public class G_TO_C_SURVIVOR_ORB_EFFECT_STATE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("weaponItemId")] public int WeaponItemId { get; set; }
        [Key("isActive")] public bool IsActive { get; set; }
        [Key("orbItemIds")] public List<int> OrbItemIds { get; set; } = new();

        // 앞줄(최저 티어·선입) 오브의 현재 HP — 오브별 체력바 표시용 (#219). -1 = 만충 취급.
        [Key("frontOrbHp")] public int FrontOrbHp { get; set; } = -1;

        // 잼 보유량 (#222 M3) — SB처럼 머리 위에 공개되는 점수. 같은 구역 관전자에게 동기화.
        [Key("jamCount")] public int JamCount { get; set; }
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
    ///     EnhanceTargetTier: 0=강화 무효(대상 없음), 1=T1→T2, 2=T2→T3.
    ///     ArmorValid: 철갑 카드 유효(외피 없는 오브 존재) 여부.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SWARM_GROWTH_OFFER : IMessagePackObject
    {
        [Key("offerId")] public int OfferId { get; set; }
        [Key("cost")] public int Cost { get; set; }
        [Key("enhTier")] public int EnhanceTargetTier { get; set; }
        [Key("armorOk")] public bool ArmorValid { get; set; }
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
    public class SurvivorAreaNaturalStockState : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("isDepleted")] public bool IsDepleted { get; set; }
        // 공개 정보는 색상별 소진 여부까지만이다. 정확한 남은 개수는 서버에만 둔다.
        [Key("availableOrbColors")] public List<SurvivorOrbColor> AvailableOrbColors { get; set; } = new();
    }

    [MessagePackObject]
    public class G_TO_C_SURVIVOR_AREA_STOCK_STATE : IMessagePackObject
    {
        [Key("areas")] public List<SurvivorAreaNaturalStockState> Areas { get; set; } = new();
    }

    [MessagePackObject]
    public class C_TO_G_ROOM_ENCOUNTER_AVOID : IMessagePackObject
    {
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("actionType")] public int ActionType { get; set; }
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
    public class G_TO_U_SPAWN : IMessagePackObject
    {
        [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
        [Key("cellsToRemove")] public List<Cell> CellsToRemove { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_DESTROY : IMessagePackObject
    {
        [Key("objectKey")] public string ObjectKey { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SPAWN : IMessagePackObject
    {
        [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
        [Key("cellsToRemove")] public List<Cell> CellsToRemove { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_DESTROY : IMessagePackObject
    {
        [Key("objectKey")] public string ObjectKey { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfoList")] public List<PlayerInfo> PlayerInfoList { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_ATTACK : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class C_TO_G_INTERACT : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class G_TO_C_GAME_TIME_WARNING : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("remainingSeconds")] public int RemainingSeconds { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_ROUND_STATE : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("roundNumber")] public int RoundNumber { get; set; }
        [Key("totalRounds")] public int TotalRounds { get; set; }
        [Key("phase")] public RoundPhase Phase { get; set; }
        [Key("remainingSeconds")] public int RemainingSeconds { get; set; }
        [Key("phaseDurationSeconds")] public int PhaseDurationSeconds { get; set; }
        [Key("serverUnixMs")] public long ServerUnixMs { get; set; }
        [Key("isSessionEnded")] public bool IsSessionEnded { get; set; }
        [Key("survivorNextRoomAreaTypes")] public int[] SurvivorNextRoomAreaTypes { get; set; } = Array.Empty<int>();
        [Key("survivorNextRoomOccupancies")] public int[] SurvivorNextRoomOccupancies { get; set; } = Array.Empty<int>();
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
