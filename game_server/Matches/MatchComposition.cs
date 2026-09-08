using network.common.data.models;

namespace game_server.matches;

/// <summary>매치 구성: 누가 오는지(사람·봇 ID), 어떤 모드인지, 어디서 시작하는지. 첫 접속 때 확정한다.</summary>
internal sealed record MatchComposition(
    IReadOnlyList<long> HumanPlayerIds,
    IReadOnlyList<long> BotPlayerIds,
    MatchMode Mode,
    IReadOnlyDictionary<long, Cell> SpawnCells,
    IReadOnlyList<PlayerInfo> PlayerRoster);
