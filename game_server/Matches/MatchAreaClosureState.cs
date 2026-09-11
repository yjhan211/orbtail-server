using network.common;
using network.common.data;

namespace game_server.matches;

/// <summary>
///     매치별 자기장 폐쇄 시간표와 진행 상태를 관리한다.
///     폐쇄·경고할 구역과 현재 안전 거리를 계산하며, 실제 처리와 전송은 MatchFieldService가 담당한다.
///     MatchRuntime이 소유하고, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
public sealed class MatchAreaClosureState(Func<DateTime>? utcNow = null)
{
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly HashSet<AreaType> _closedAreas = [];
    private List<(AreaType Area, int ClosureAtSeconds)> _schedule = [];
    private int _nextClosureIndex;
    public DateTime? GameStartTime { get; internal set; }

    public bool InitializeMatching(IReadOnlyList<(AreaType Area, int ClosureAtSeconds)> schedule)
    {
        if (GameStartTime.HasValue)
        {
            return false;
        }

        _schedule = schedule.ToList();
        GameStartTime = _utcNow();
        return true;
    }

    internal void Release()
    {
        GameStartTime = null;
        _schedule = [];
        _closedAreas.Clear();
        _nextClosureIndex = 0;
    }

    public IReadOnlyList<AreaType> CloseDueAreas()
    {
        if (GameStartTime is not { } gameStartTime)
        {
            return [];
        }

        double elapsedSeconds = (_utcNow() - gameStartTime).TotalSeconds;
        var closedAreas = new List<AreaType>();
        while (_nextClosureIndex < _schedule.Count && elapsedSeconds >= _schedule[_nextClosureIndex].ClosureAtSeconds)
        {
            if (_closedAreas.Add(_schedule[_nextClosureIndex].Area))
            {
                closedAreas.Add(_schedule[_nextClosureIndex].Area);
            }
            _nextClosureIndex++;
        }
        return closedAreas;
    }

    public bool IsAreaClosed(AreaType area) => _closedAreas.Contains(area);

    public double GetSafeDistance(DateTime nowUtc)
    {
        if (GameStartTime is not { } gameStartTime)
        {
            return double.MaxValue;
        }
        return SwarmPressureField.GetSafeDistanceAtElapsed((nowUtc - gameStartTime).TotalSeconds);
    }
}
