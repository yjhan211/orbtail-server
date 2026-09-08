using game_server.services;

namespace game_server.matches.states;

/// <summary>진행 중인 매치 하나의 이벤트·전투 통계·계측 상태. 파일 저장용 복사본을 만든 뒤 종료 시 해제한다.</summary>
internal sealed class EventLogState
{
    internal GameEventLogManager.MatchingEventLog? Log;
    internal GameEventLogManager.MatchCombatState? Combat;
    internal GameEventLogManager.MatchTelemetryState? Telemetry;
    internal bool IsReleased { get; private set; }

    internal void Release()
    {
        IsReleased = true;
        Log = null;
        Combat = null;
        Telemetry = null;
    }
}
