using user_server.matching.queue;

namespace user_server.sessions;

/// <summary>
///     PlayerSession 하나의 매칭 진행 상태를 관리하는 클래스
/// </summary>
internal sealed class MatchingAssignment
{
    private readonly object _matchingStateLock = new();
    private string? _activeRequestId;
    private long _assignedMatchingId;

    public string? ActiveRequestId
    {
        get
        {
            lock (_matchingStateLock)
            {
                return _activeRequestId;
            }
        }
    }

    public bool TryBegin(out string? requestId, out long blockingMatchingId)
    {
        lock (_matchingStateLock)
        {
            requestId = null;
            blockingMatchingId = _assignedMatchingId;
            if (_assignedMatchingId != 0 || _activeRequestId != null)
            {
                return false;
            }

            _activeRequestId = NewRequestId();
            requestId = _activeRequestId;
            return true;
        }
    }

    public string? TryRestart(long staleMatchingId)
    {
        lock (_matchingStateLock)
        {
            if (_assignedMatchingId != 0 && _assignedMatchingId != staleMatchingId)
            {
                return null;
            }

            if (_assignedMatchingId == staleMatchingId)
            {
                _assignedMatchingId = 0;
                _activeRequestId = null;
            }

            if (_assignedMatchingId != 0 || _activeRequestId != null)
            {
                return null;
            }

            _activeRequestId = NewRequestId();
            return _activeRequestId;
        }
    }

    public bool TryAssign(long matchingId, string requestId)
    {
        if (matchingId <= 0 || !MatchingRequestTokens.IsSafeTokenComponent(requestId))
        {
            return false;
        }

        lock (_matchingStateLock)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
            {
                return false;
            }

            _assignedMatchingId = matchingId;
            return true;
        }
    }

    public void Clear(long matchingId)
    {
        if (matchingId <= 0)
        {
            return;
        }

        lock (_matchingStateLock)
        {
            if (_assignedMatchingId != matchingId)
            {
                return;
            }
            _assignedMatchingId = 0;
            _activeRequestId = null;
        }
    }

    public long TakeAndClear()
    {
        lock (_matchingStateLock)
        {
            long matchingId = _assignedMatchingId;
            _assignedMatchingId = 0;
            _activeRequestId = null;
            return matchingId;
        }
    }

    public void ClearRequest(string requestId)
    {
        lock (_matchingStateLock)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }
            _activeRequestId = null;
        }
    }

    public bool FailRequest(string requestId, long matchingId)
    {
        lock (_matchingStateLock)
        {
            if (!string.Equals(_activeRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
            {
                return false;
            }

            _activeRequestId = null;
            if (_assignedMatchingId == matchingId)
            {
                _assignedMatchingId = 0;
            }
            return true;
        }
    }

    public bool FailEntry(long matchingId)
    {
        lock (_matchingStateLock)
        {
            if (_assignedMatchingId != matchingId)
            {
                return false;
            }

            _assignedMatchingId = 0;
            _activeRequestId = null;
            return true;
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}
