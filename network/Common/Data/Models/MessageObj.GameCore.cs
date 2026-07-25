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
