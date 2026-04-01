#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 상호작용 요청 (A → 서버)
    [MessagePackObject]
    public class C_TO_G_PLAYER_INTERACT_REQUEST : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; } // 대상 PlayerId
    }

    // 상호작용 요청 결과/알림 (서버 → A,B)
    [MessagePackObject]
    public class G_TO_C_PLAYER_INTERACT_REQUEST : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }       // 상대 PlayerId
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; } // SUCCESS or 에러
    }

    // 상호작용 수락/거절 (B → 서버)
    [MessagePackObject]
    public class C_TO_G_PLAYER_INTERACT_RESPONSE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; } // 요청자 PlayerId
        [Key("accepted")] public bool Accepted { get; set; }
    }

    // 상호작용 최종 결과 (서버 → A,B)
    [MessagePackObject]
    public class G_TO_C_PLAYER_INTERACT_RESULT : IMessagePackObject
    {
        [Key("accepted")] public bool Accepted { get; set; }
        [Key("playerId")] public long PlayerId { get; set; }       // 상대방 PlayerId
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    // 상호작용 중 아이템 사용 요청 (A → 서버)
    [MessagePackObject]
    public class C_TO_G_PLAYER_INTERACT_USE_ITEM : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; } // 대상 PlayerId
        [Key("itemUid")] public long ItemUid { get; set; }   // 사용할 아이템 UID
    }

    // 상호작용 중 아이템 사용 결과 (서버 → A)
    [MessagePackObject]
    public class G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("itemId")] public int ItemId { get; set; } // 사용된 아이템 ID
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; } // 아이템 효과 대상 PlayerId
    }

    // 상호작용 중 수칙 공유 요청 (A → 서버)
    [MessagePackObject]
    public class C_TO_G_PLAYER_INTERACT_SHARE_RULE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }  // 대상 PlayerId
        [Key("ruleId")] public int RuleId { get; set; }       // 공유할 수칙 ID
    }

    // 상호작용 중 수칙 공유 결과 (서버 → A,B)
    [MessagePackObject]
    public class G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("ruleId")] public int RuleId { get; set; }           // 공유된 수칙 ID
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; } // 수칙 받은 플레이어
        [Key("originalDiscovererPlayerId")] public long OriginalDiscovererPlayerId { get; set; } // 최초 발견자 PlayerId
    }

    // 대화 종료 요청 (클라이언트 → 서버)
    [MessagePackObject]
    public class C_TO_G_PLAYER_INTERACT_END : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; } // 상대 PlayerId
    }

    // 대화 종료 알림 (서버 → 클라이언트)
    [MessagePackObject]
    public class G_TO_C_PLAYER_INTERACT_END : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; } // 상대 PlayerId
    }
}
