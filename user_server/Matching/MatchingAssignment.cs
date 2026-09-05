namespace user_server.matching;

/// <summary>
///     세션 하나의 매칭 상태 기계: 요청 중(requestId) → 배정됨(matchingId) → 해제.
///     잠금 하나로 두 필드를 함께 전이하며, 연결 수명·Redis는 모르고 호출 측(PlayerSession)이 그 조건을 앞에 둔다.
///     requestId는 늦게 도착한 성공·실패 패킷이 취소된 요청에 붙지 않게 하는 fence다.
/// </summary>
internal sealed class MatchingAssignment
{
    private readonly object _gate = new();
    private string? _activeRequestId;
    private long _assignedMatchingId;

    public string? ActiveRequestId
    {
        get
        {
            lock (_gate) return _activeRequestId;
        }
    }

    /// <summary>
    ///     새 요청을 시작한다. 배정이 남아 있으면 그 matchingId를 돌려주고 시작하지 않는다(0이면 요청 중이라 거부).
    /// </summary>
    public bool TryBegin(out string? requestId, out long blockingMatchingId)
    {
        lock (_gate)
        {
            requestId = null;
            blockingMatchingId = _assignedMatchingId;
            if (_assignedMatchingId != 0 || _activeRequestId != null)
                return false;

            _activeRequestId = NewRequestId();
            requestId = _activeRequestId;
            return true;
        }
    }

    /// <summary>
    ///     정본(Redis claim)에 없는 낡은 배정을 버리고 새 요청을 시작한다. 그사이 배정이나 요청이 바뀌었으면 null.
    /// </summary>
    public string? TryReplaceStale(long staleMatchingId)
    {
        lock (_gate)
        {
            if (_assignedMatchingId != 0 && _assignedMatchingId != staleMatchingId)
                return null;

            if (_assignedMatchingId == staleMatchingId)
            {
                _assignedMatchingId = 0;
                _activeRequestId = null;
            }

            if (_assignedMatchingId != 0 || _activeRequestId != null)
                return null;

            _activeRequestId = NewRequestId();
            return _activeRequestId;
        }
    }

    public bool TryAssign(long matchingId, string requestId)
    {
        if (matchingId <= 0 || !MatchingRequestTokens.IsSafeTokenComponent(requestId))
            return false;

        lock (_gate)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
                return false;

            _assignedMatchingId = matchingId;
            return true;
        }
    }

    public void Clear(long matchingId)
    {
        if (matchingId <= 0)
            return;

        lock (_gate)
        {
            if (_assignedMatchingId != matchingId) return;
            _assignedMatchingId = 0;
            _activeRequestId = null;
        }
    }

    /// <summary>배정을 꺼내며 상태를 비운다. 세션 정리 때 claim 해제 대상을 얻는다.</summary>
    public long Take()
    {
        lock (_gate)
        {
            long matchingId = _assignedMatchingId;
            _assignedMatchingId = 0;
            _activeRequestId = null;
            return matchingId;
        }
    }

    public void ClearRequest(string requestId)
    {
        lock (_gate)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal)) return;
            _activeRequestId = null;
        }
    }

    public bool FailRequest(string requestId, long matchingId)
    {
        lock (_gate)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
                return false;

            _activeRequestId = null;
            if (_assignedMatchingId == matchingId)
                _assignedMatchingId = 0;
            return true;
        }
    }

    public bool FailAdmission(long matchingId)
    {
        lock (_gate)
        {
            if (_assignedMatchingId != matchingId)
                return false;

            _assignedMatchingId = 0;
            _activeRequestId = null;
            return true;
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}
