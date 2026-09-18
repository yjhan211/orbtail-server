#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    // ===== 탈락 =====

    [MessagePackObject]
    public sealed class PlayerEliminationInfo
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("attackerPlayerId")] public long AttackerPlayerId { get; set; }
        [Key("reason")] public EliminationReason Reason { get; set; }
    }

    [MessagePackObject]
    public sealed class G_TO_C_PLAYER_ELIMINATED : IMessagePackObject
    {
        [Key("eliminations")] public List<PlayerEliminationInfo> Eliminations { get; set; } = new();
    }

    [MessagePackObject]
    public sealed class G_TO_C_ELIMINATION_RESULT : IMessagePackObject
    {
        [Key("players")] public List<GameResultPlayerInfo> Players { get; set; } = new();
    }

    // ===== 게임 결과 =====

    [MessagePackObject]
    public class GameResultPlayerInfo : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("name")] public string Name { get; set; } = "";
        [Key("eliminationReason")] public EliminationReason EliminationReason { get; set; }
        [Key("finalStatus")] public PlayerMatchStatus FinalStatus { get; set; }
        [Key("health")] public int Health { get; set; }
        [Key("maxHealth")] public int MaxHealth { get; set; }
        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; } = new();
        [Key("attackerPlayerId")] public long AttackerPlayerId { get; set; }
        [Key("eliminatedArea")] public AreaType EliminatedArea { get; set; }
        [Key("rank")] public int Rank { get; set; }

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
    }
}
