using System.Text.Json;
using System.Text.Json.Serialization;

namespace game_server.matches.logging;

/// <summary>매치 기록에 남기는 이벤트 종류. JSON에는 기존 대문자 밑줄 형식으로 저장한다.</summary>
[JsonConverter(typeof(GameEventTypeJsonConverter))]
public enum GameEventType
{
    Unknown,
    AfterimageHit,
    AfterimageKilled,
    AreaEnter,
    Closure,
    ClosureWarningExit,
    ClosureWarningReentry,
    ClosureWarningSnapshot,
    CutAttempt,
    CutRetaliationWindow,
    Eliminate,
    EliminationDrop,
    ExploreCancelled,
    ExploreStarted,
    GroundItemPickedUp,
    GroundItemSpawned,
    MatchAbandoned,
    MatchEnded,
    MatchStarted,
    Mission,
    Move,
    OrbGrowthCardSelected,
    OrbGrowthCardsOffered,
    OrbSuffixCut,
    OrbSummonBlocked,
    OrbSummonSucceeded,
    OvertimeStageChanged,
    PelletPickupOutcome,
    RecoveryUsed,
    Resource,
    SpawnAssignment,
    SummonStoneAwarded,
    SurvivorBotMovementTickPerformance,
    SurvivorCombatElimination,
    SurvivorFirstElimination,
    SurvivorFirstT2,
    SurvivorFirstT3,
    SurvivorHit,
    SurvivorOrbAttackTargets,
    SurvivorOrbBoardFull,
    SurvivorOrbBoardState,
    SurvivorOrbColorSummary,
    SurvivorOrbFirstPickup,
    SurvivorOrbPickupBlockedFull,
    SurvivorOrbResonanceApplied,
    SurvivorOrbResonanceRemoved,
    SurvivorOrbSummary,
    System,
}

internal sealed class GameEventTypeJsonConverter() : JsonStringEnumConverter<GameEventType>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false);
