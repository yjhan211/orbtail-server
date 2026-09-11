#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    public enum CombatEntityKind { Player = 0, Monster = 1 }
    public enum HealthRecoveryKind { Orb = 0, Sleep = 1 }
    public enum CombatStatusEffectKind { WaveOrbSlow = 0, SunBurn = 1, WindOrbWound = 2 }

    // 사람과 봇은 Player ID를, 몬스터는 Monster ID를 사용한다. 체력 -1은 정보 없음이다.
    [MessagePackObject]
    public class G_TO_C_COMBAT_HIT : IMessagePackObject
    {
        [Key("attackerId")] public long AttackerId { get; set; }
        [Key("targetId")] public long TargetId { get; set; }
        [Key("attackerKind")] public CombatEntityKind AttackerKind { get; set; }
        [Key("targetKind")] public CombatEntityKind TargetKind { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("weaponItemId")] public int WeaponItemId { get; set; }
        [Key("damage")] public int Damage { get; set; }
        [Key("attackerHealth")] public int AttackerHealth { get; set; } = -1;
        [Key("targetHealth")] public int TargetHealth { get; set; } = -1;
        [Key("isDot")] public bool IsDot { get; set; }
        [Key("isCritical")] public bool IsCritical { get; set; }
        [Key("showDamageOnly")] public bool ShowDamageOnly { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_HEALTH_RECOVERY : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("amount")] public int Amount { get; set; }
        [Key("source")] public HealthRecoveryKind Source { get; set; }
        [Key("orbItemId")] public int OrbItemId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_STATUS_EFFECT : IMessagePackObject
    {
        [Key("sourcePlayerId")] public long SourcePlayerId { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("effect")] public CombatStatusEffectKind Effect { get; set; }
        [Key("durationMs")] public int DurationMs { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utcNow")] public DateTime UtcNow { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_CONNECT : IMessagePackObject
    {
        // 신원·매치는 ticket이 증명한다. 클라이언트가 말하는 값은 받지 않는다.
        [Key("gameHandoffTicket")] public string GameEntryTicket { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CONNECT_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

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
    }

    [MessagePackObject]
    public class G_TO_C_MOVE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("cell")] public Cell Cell { get; set; }
        [Key("serverTime")] public long ServerTimestamp { get; set; }
        // 오브 궤도 위상 (#232): 서버가 검증 이동으로 적산한 권위값 — 클라는 자기 적산을 이 값으로 보정한다.
        [Key("orbPhase")] public float OrbOrbitPhaseDegrees { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_AREA_PLAYER_ENTER : IMessagePackObject
    {
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
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

        // 본체 체력: 같은 구역 상대의 머리 위 게이지를 상시 구동한다 —
        // "때리면 닳는 게 보인다". -1 = 미동기(표시 유지).
        [Key("gauge")] public int BodyHealth { get; set; } = -1;

        // 방어 강화(내구 2+) 오브 순번 비트마스크 (#226): 은백 링 표시의 단일 출처.
        // OrbItemIds 순서 기준 — 64번째 이후 순번은 표시 생략(실전 상한 밖 안전 절단).
        [Key("armorM")] public long ArmorMask { get; set; }
    }












    /// <summary>
    ///     오브 공용 링 연출. 같은 구역에 브로드캐스트 — 링 중심·반경.
    ///     Kind: 0=포위 완성, 1=절단 파열, 2=파도오브 소용돌이 예고 — 클라가 색·효과음을 분기한다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_ORB_RING_EFFECT : IMessagePackObject
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
    ///     태양오브 공격 예고와 폭발 통지. 발사 시 서버가 공격 경로를 확정하고
    ///     같은 구역 전원에게 보낸다. Shape 1 = 직선 공격 예고, Shape 2 = 폭발 통지.
    ///     클라는 TelegraphSeconds 동안 예고색으로 그리다 ActiveSeconds 동안 판정색으로 바꾼다.
    ///     서버 판정은 같은 좌표·같은 시간을 쓴다 — 표시 = 판정.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_SUN_ORB_ATTACK : IMessagePackObject
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
    public class G_TO_C_ORB_UPGRADE_INFO : IMessagePackObject
    {
        [Key("sun")] public int SunLevel { get; set; }
        [Key("wind")] public int WindLevel { get; set; }
        [Key("wave")] public int WaveLevel { get; set; }
        [Key("sunCost")] public int SunCost { get; set; }
        [Key("windCost")] public int WindCost { get; set; }
        [Key("waveCost")] public int WaveCost { get; set; }
    }

    /// <summary>
    ///     오브 강화 요청. Action 1 = 같은 OrbGroupId의 오브 강화, TargetItemId = 계열을 선택하는 오브 아이템 ID —
    ///     그 계열에서 몸체에 가장 가까운 T3 미만 오브 하나가 한 티어 오른다 (2026-08-18, 구 계열 일괄 강화).
    ///     서버 권위 — 강화할 오브 없음·소환석 부족이면 거절.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_UPGRADE_ORB : IMessagePackObject
    {
        [Key("action")] public int Action { get; set; }
        [Key("targetItemId")] public int TargetItemId { get; set; }
    }

    /// <summary>
    ///     오브 강화 결과. ResultItemId: 강화된 오브의 새 아이템, TargetOrdinal: 그 오브의 열 순번
    ///     (0 = 몸체 바로 뒤; 실패·해당 없음 -1) — 클라가 강화 이펙트를 그 오브 위에 띄운다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_UPGRADE_ORB_RESULT : IMessagePackObject
    {
        [Key("action")] public int Action { get; set; }
        [Key("success")] public bool Success { get; set; }
        [Key("resultItemId")] public int ResultItemId { get; set; }
        [Key("targetItemId")] public int TargetItemId { get; set; }
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
    }

    /// <summary>등장할 객체들의 현재 공간 정보. 이름·외형은 매칭 로스터를 사용한다.</summary>
    [MessagePackObject]
    public class G_TO_C_OBJECT_INFO : IMessagePackObject
    {
        [Key("objects")] public List<GameObjectInfo> Objects { get; set; }
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
        [Key("startsAtUnixMs")] public long StartsAtUnixMs { get; set; }
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
