#pragma warning disable CS8618
// ReSharper disable All
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    /// <summary>
    ///     구역 이동 요청 (문/계단 마커 클릭 후 오토무브 도착 시)
    ///     GDD v0.0.8 §2.1.3: 문/계단 마커 방식
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_AREA_MOVE : IMessagePackObject
    {
        [Key("targetArea")] public AreaType TargetArea { get; set; }
        [Key("connectionType")] public ConnectionType ConnectionType { get; set; }
        [Key("stairSide")] public StairSide StairSide { get; set; }
    }

    /// <summary>
    ///     구역 이동 결과 (서버 응답)
    ///     성공: area + spawnCell 반영, staminaCost 차감
    ///     실패: errorCode (INVALID_AREA, INSUFFICIENT_STAMINA 등)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_AREA_MOVE_RESULT : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("area")] public AreaType Area { get; set; }
        [Key("spawnCellX")] public int SpawnCellX { get; set; }
        [Key("spawnCellY")] public int SpawnCellY { get; set; }
        [Key("staminaCost")] public int StaminaCost { get; set; }
        [Key("remainingStamina")] public int RemainingStamina { get; set; }
    }

    /// <summary>
    ///     방 입장 사건 알림. 서버가 구역 이동 성공 후 판정해서 내려준다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_ROOM_ENTRY_EVENT : IMessagePackObject
    {
        [Key("eventId")] public int EventId { get; set; }
    }

    /// <summary>
    ///     방 입장 초기 사건 선택 응답. 서버는 eventId와 choiceId를 CSV 기준으로 검증한다.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_ROOM_ENTRY_EVENT_CHOICE : IMessagePackObject
    {
        [Key("eventId")] public int EventId { get; set; }
        [Key("choiceId")] public int ChoiceId { get; set; }
    }
}
