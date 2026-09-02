#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    // ===== 탈락 =====

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

    // ===== 게임 결과 =====

    [MessagePackObject]
    public class GameResultPlayerInfo : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("name")] public string Name { get; set; } = "";
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("watcherPlayerId")] public long WatcherPlayerId { get; set; }
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
