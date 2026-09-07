namespace game_server.services;

/// <summary>진행 중인 매치 하나의 이벤트·전투 통계·계측 상태. 종료 시 기록만 보관소로 넘긴다.</summary>
internal sealed class MatchEventLogState
{
    internal GameEventLogManager.MatchingEventLog? Log;
    internal GameEventLogManager.MatchCombatState? Combat;
    internal GameEventLogManager.MatchTelemetryState? Telemetry;
    internal int Archived;
}
