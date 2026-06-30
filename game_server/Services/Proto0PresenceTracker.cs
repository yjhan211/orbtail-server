using network.common;

namespace game_server.services;

/// <summary>
///     프로토 0 기척 트래커 (#159). 매칭별로 최근 25초(5틱)의 조우 강도를 슬라이딩 윈도로 관리한다.
///     - 조우 = 같은 체류 방(복도/None 제외)에 함께 머문 5초 틱.
///     - encounterWeight = 2 / 방 총인원 — 혼잡할수록 희석된다.
///     - presence(0~5) = 최근 5틱 weight 합. 25초가 지나면 자연히 빠진다.
///     서버 권위. 방향(누가 누구를 따라왔는가)·원시 로그는 계산/노출하지 않는다.
/// </summary>
public sealed class Proto0PresenceTracker
{
    private const int WindowTicks = 5;     // 25초 / 5초 틱
    private const float MaxPresence = 5f;

    private readonly Dictionary<long, MatchState> _matches = new();

    /// <summary>5초 틱. playerAreas = 인스턴스의 모든 플레이어(인간 + 생존 봇) 영역.</summary>
    public void Tick(long matchingId, IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var state = GetOrCreate(matchingId);
        int slot = state.Head;

        // 체류 방(비복도)별 인원수
        var roomCount = new Dictionary<AreaType, int>();
        foreach (var area in playerAreas.Values)
        {
            if (area == AreaType.None || area.IsCorridor()) continue;
            roomCount[area] = roomCount.GetValueOrDefault(area) + 1;
        }

        foreach (var (observerId, observerArea) in playerAreas)
        {
            var candidateBuckets = state.GetObserver(observerId);

            // 이번 틱 슬롯은 모든 기존 후보에 대해 0으로 초기화 (이번 틱 미동석 기본값)
            foreach (var buckets in candidateBuckets.Values)
                buckets[slot] = 0f;

            bool inRoom = observerArea != AreaType.None && !observerArea.IsCorridor();
            if (!inRoom) continue;

            float weight = 2f / Math.Max(2, roomCount[observerArea]);
            foreach (var (otherId, otherArea) in playerAreas)
            {
                if (otherId == observerId) continue;
                if (otherArea != observerArea) continue; // 같은 체류 방만 조우로 본다
                state.GetCandidate(candidateBuckets, otherId)[slot] = weight;
            }
        }

        ReconcileNotebookOverlaps(state, playerAreas, DateTime.UtcNow);

        state.Head = (slot + 1) % WindowTicks;
    }

    /// <summary>
    ///     observer 관점의 후보 목록. roster(현재 살아있는 전체 플레이어)에서 자기 자신/타겟만 제외하고
    ///     전원을 반환한다 — 조우가 아직 없어도 presence 0으로 항상 포함(카드가 사라지지 않도록).
    /// </summary>
    public List<(long candidateId, float presence)> GetCandidates(
        long matchingId, long observerId, long targetId, IEnumerable<long> roster)
    {
        var result = new List<(long, float)>();
        _matches.TryGetValue(matchingId, out var state);
        Dictionary<long, float[]>? candidateBuckets = null;
        state?.Observers.TryGetValue(observerId, out candidateBuckets);

        foreach (long candidateId in roster)
        {
            if (candidateId == observerId || candidateId == targetId) continue;

            float score = 0f;
            if (candidateBuckets != null && candidateBuckets.TryGetValue(candidateId, out var buckets))
                foreach (float w in buckets) score += w;

            result.Add((candidateId, Math.Min(MaxPresence, score)));
        }

        return result;
    }

    public List<(long candidateId, float presence)> GetPresenceScores(
        long matchingId, long observerId, IEnumerable<long> roster)
    {
        var result = new List<(long, float)>();
        _matches.TryGetValue(matchingId, out var state);
        Dictionary<long, float[]>? candidateBuckets = null;
        state?.Observers.TryGetValue(observerId, out candidateBuckets);

        foreach (long candidateId in roster)
        {
            if (candidateId == observerId) continue;

            float score = 0f;
            if (candidateBuckets != null && candidateBuckets.TryGetValue(candidateId, out var buckets))
                foreach (float w in buckets) score += w;

            result.Add((candidateId, Math.Min(MaxPresence, score)));
        }

        return result;
    }

    public List<PresenceNominationCandidate> GetNominationCandidates(
        long matchingId, long observerId, IEnumerable<long> roster, long excludedTargetId = 0,
        bool includeZeroEvidence = false)
    {
        var result = new List<PresenceNominationCandidate>();
        var now = DateTime.UtcNow;
        _matches.TryGetValue(matchingId, out var state);

        Dictionary<long, float[]>? candidateBuckets = null;
        Dictionary<long, PresenceNotebookRecord>? observerRecords = null;
        state?.Observers.TryGetValue(observerId, out candidateBuckets);
        state?.NotebookRecords.TryGetValue(observerId, out observerRecords);

        foreach (long candidateId in roster.Distinct())
        {
            if (candidateId == observerId || candidateId == excludedTargetId)
                continue;

            float presence = 0f;
            if (candidateBuckets != null && candidateBuckets.TryGetValue(candidateId, out var buckets))
                foreach (float w in buckets)
                    presence += w;

            presence = Math.Min(MaxPresence, presence);

            PresenceNotebookRecord? record = null;
            if (observerRecords != null && observerRecords.TryGetValue(candidateId, out var notebookRecord))
                record = notebookRecord.Clone(now);

            float notebookScore = CalculateNotebookNominationScore(record);
            float score = presence * 20f + notebookScore;
            if (!includeZeroEvidence && score <= 0f)
                continue;

            result.Add(new PresenceNominationCandidate
            {
                CandidateId = candidateId,
                Presence = presence,
                NotebookScore = notebookScore,
                Score = score,
                TotalOverlapSeconds = record?.TotalOverlapSeconds ?? 0,
                LongestOverlapSeconds = record?.LongestOverlapSeconds ?? 0,
                OverlapStartCount = record?.OverlapStartCount ?? 0,
                EnterAfterObserverCount = record?.EnterAfterObserverCount ?? 0,
                AlreadyThereWhenObserverArrivedCount = record?.AlreadyThereWhenObserverArrivedCount ?? 0,
                UnclassifiedOverlapStartCount = record?.UnclassifiedOverlapStartCount ?? 0,
                IsCurrentlyOverlapping = record?.IsCurrentlyOverlapping ?? false,
                LastSeenArea = record?.LastSeenArea ?? AreaType.None
            });
        }

        return result
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.EnterAfterObserverCount)
            .ThenByDescending(candidate => candidate.TotalOverlapSeconds)
            .ThenByDescending(candidate => candidate.LongestOverlapSeconds)
            .ThenBy(candidate => candidate.CandidateId)
            .ToList();
    }

    public List<PresenceNotebookRecord> GetNotebookRecords(
        long matchingId, long observerId, IEnumerable<long> roster, bool includeEmpty = false)
    {
        var result = new List<PresenceNotebookRecord>();
        var now = DateTime.UtcNow;
        _matches.TryGetValue(matchingId, out var state);
        Dictionary<long, PresenceNotebookRecord>? observerRecords = null;
        state?.NotebookRecords.TryGetValue(observerId, out observerRecords);

        foreach (long candidateId in roster)
        {
            if (candidateId == observerId) continue;

            if (observerRecords != null && observerRecords.TryGetValue(candidateId, out var record))
            {
                result.Add(record.Clone(now));
                continue;
            }

            if (includeEmpty)
                result.Add(new PresenceNotebookRecord { PlayerId = candidateId });
        }

        return result
            .OrderByDescending(record => record.TotalOverlapSeconds)
            .ThenByDescending(record => record.EnterAfterObserverCount)
            .ThenBy(record => record.PlayerId)
            .ToList();
    }

    public void Remove(long matchingId) => _matches.Remove(matchingId);

    public void FreezeNotebookOverlaps(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return;

        var now = DateTime.UtcNow;
        foreach (var records in state.NotebookRecords.Values)
        {
            foreach (var record in records.Values)
                CloseOverlap(record, now);
        }
    }

    public void SetPlayerArea(long matchingId, long playerId, AreaType area, bool countAsEntry)
    {
        var state = GetOrCreate(matchingId);
        var now = DateTime.UtcNow;
        if (state.CurrentAreas.TryGetValue(playerId, out var currentArea) && currentArea == area)
            return;

        if (state.CurrentAreas.TryGetValue(playerId, out var oldArea) &&
            IsNotebookTrackableArea(oldArea))
        {
            CloseOverlapsForAreaExit(state, playerId, oldArea, now);
        }

        state.CurrentAreas[playerId] = area;

        if (countAsEntry && IsNotebookTrackableArea(area))
            RecordAreaEntry(state, playerId, area, now);
    }

    private MatchState GetOrCreate(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
        {
            state = new MatchState();
            _matches[matchingId] = state;
        }

        return state;
    }

    private static void ReconcileNotebookOverlaps(
        MatchState state, IReadOnlyDictionary<long, AreaType> playerAreas, DateTime now)
    {
        var activePairs = new HashSet<(long observerId, long candidateId)>();

        foreach (var (observerId, observerArea) in playerAreas)
        {
            if (!IsNotebookTrackableArea(observerArea))
                continue;

            foreach (var (otherId, otherArea) in playerAreas)
            {
                if (otherId == observerId || otherArea != observerArea)
                    continue;

                activePairs.Add((observerId, otherId));

                var record = state.GetNotebookRecord(observerId, otherId);
                record.PlayerId = otherId;
                record.LastSeenArea = observerArea;

                if (!record.IsCurrentlyOverlapping)
                {
                    StartOverlap(
                        record,
                        observerArea,
                        now,
                        classify: overlap =>
                        {
                            overlap.OverlapStartCount++;
                            overlap.UnclassifiedOverlapStartCount++;
                        });
                }

                RefreshCurrentOverlap(record, now);
            }
        }

        foreach (var (observerId, records) in state.NotebookRecords)
            foreach (var (candidateId, record) in records)
                if (!activePairs.Contains((observerId, candidateId)))
                    CloseOverlap(record, now);

        state.CurrentAreas.Clear();
        foreach (var (playerId, area) in playerAreas)
            state.CurrentAreas[playerId] = area;
    }

    private static void CloseOverlapsForAreaExit(MatchState state, long playerId, AreaType oldArea, DateTime now)
    {
        foreach (var (otherId, otherArea) in state.CurrentAreas)
        {
            if (otherId == playerId || otherArea != oldArea)
                continue;

            if (state.NotebookRecords.TryGetValue(playerId, out var playerRecords) &&
                playerRecords.TryGetValue(otherId, out var playerRecord))
                CloseOverlap(playerRecord, now);

            if (state.NotebookRecords.TryGetValue(otherId, out var otherRecords) &&
                otherRecords.TryGetValue(playerId, out var otherRecord))
                CloseOverlap(otherRecord, now);
        }
    }

    private static bool IsNotebookTrackableArea(AreaType area)
    {
        return area != AreaType.None;
    }

    private static void RecordAreaEntry(MatchState state, long entrantId, AreaType newArea, DateTime now)
    {
        foreach (var (otherId, otherArea) in state.CurrentAreas)
        {
            if (otherId == entrantId || otherArea != newArea)
                continue;

            var observerRecord = state.GetNotebookRecord(otherId, entrantId);
            observerRecord.PlayerId = entrantId;
            StartOverlap(
                observerRecord,
                newArea,
                now,
                classify: record =>
                {
                    record.OverlapStartCount++;
                    record.EnterAfterObserverCount++;
                });

            var entrantRecord = state.GetNotebookRecord(entrantId, otherId);
            entrantRecord.PlayerId = otherId;
            StartOverlap(
                entrantRecord,
                newArea,
                now,
                classify: record =>
                {
                    record.OverlapStartCount++;
                    record.AlreadyThereWhenObserverArrivedCount++;
                });
        }
    }

    private static void StartOverlap(
        PresenceNotebookRecord record,
        AreaType area,
        DateTime now,
        Action<PresenceNotebookRecord> classify)
    {
        if (record.IsCurrentlyOverlapping)
            CloseOverlap(record, now);

        record.LastSeenArea = area;
        record.CurrentOverlapSeconds = 0;
        record.ActiveOverlapStartedAtUtc = now;
        record.IsCurrentlyOverlapping = true;
        classify(record);
    }

    private static void RefreshCurrentOverlap(PresenceNotebookRecord record, DateTime now)
    {
        if (!record.IsCurrentlyOverlapping || !record.ActiveOverlapStartedAtUtc.HasValue)
            return;

        record.CurrentOverlapSeconds = Math.Max(
            0,
            (int)Math.Round((now - record.ActiveOverlapStartedAtUtc.Value).TotalSeconds));
        record.LongestOverlapSeconds = Math.Max(record.LongestOverlapSeconds, record.CurrentOverlapSeconds);
    }

    private static void CloseOverlap(PresenceNotebookRecord record, DateTime now)
    {
        if (!record.IsCurrentlyOverlapping)
            return;

        RefreshCurrentOverlap(record, now);
        record.TotalOverlapSeconds += record.CurrentOverlapSeconds;
        record.CurrentOverlapSeconds = 0;
        record.IsCurrentlyOverlapping = false;
        record.ActiveOverlapStartedAtUtc = null;
    }

    private static float CalculateNotebookNominationScore(PresenceNotebookRecord? record)
    {
        if (record == null)
            return 0f;

        return record.EnterAfterObserverCount * 30f
               + record.TotalOverlapSeconds * 1f
               + record.LongestOverlapSeconds * 1.5f
               + record.OverlapStartCount * 6f
               + record.UnclassifiedOverlapStartCount * 4f
               + record.AlreadyThereWhenObserverArrivedCount * 1.5f
               + (record.IsCurrentlyOverlapping ? 8f : 0f);
    }

    private sealed class MatchState
    {
        public int Head;
        public readonly Dictionary<long, Dictionary<long, float[]>> Observers = new();
        public readonly Dictionary<long, AreaType> CurrentAreas = new();
        public readonly Dictionary<long, Dictionary<long, PresenceNotebookRecord>> NotebookRecords = new();

        public Dictionary<long, float[]> GetObserver(long observerId)
        {
            if (!Observers.TryGetValue(observerId, out var candidates))
            {
                candidates = new Dictionary<long, float[]>();
                Observers[observerId] = candidates;
            }

            return candidates;
        }

        public float[] GetCandidate(Dictionary<long, float[]> candidateBuckets, long candidateId)
        {
            if (!candidateBuckets.TryGetValue(candidateId, out var buckets))
            {
                buckets = new float[WindowTicks];
                candidateBuckets[candidateId] = buckets;
            }

            return buckets;
        }

        public PresenceNotebookRecord GetNotebookRecord(long observerId, long candidateId)
        {
            if (!NotebookRecords.TryGetValue(observerId, out var candidates))
            {
                candidates = new Dictionary<long, PresenceNotebookRecord>();
                NotebookRecords[observerId] = candidates;
            }

            if (!candidates.TryGetValue(candidateId, out var record))
            {
                record = new PresenceNotebookRecord { PlayerId = candidateId };
                candidates[candidateId] = record;
            }

            return record;
        }
    }
}

public sealed class PresenceNotebookRecord
{
    public long PlayerId { get; set; }
    public int TotalOverlapSeconds { get; set; }
    public int LongestOverlapSeconds { get; set; }
    public int CurrentOverlapSeconds { get; set; }
    public bool IsCurrentlyOverlapping { get; set; }
    public int OverlapStartCount { get; set; }
    public int EnterAfterObserverCount { get; set; }
    public int AlreadyThereWhenObserverArrivedCount { get; set; }
    public int UnclassifiedOverlapStartCount { get; set; }
    public AreaType LastSeenArea { get; set; }
    internal DateTime? ActiveOverlapStartedAtUtc { get; set; }

    public PresenceNotebookRecord Clone(DateTime now)
    {
        int currentOverlapSeconds = CurrentOverlapSeconds;
        if (IsCurrentlyOverlapping && ActiveOverlapStartedAtUtc.HasValue)
        {
            currentOverlapSeconds = Math.Max(
                0,
                (int)Math.Round((now - ActiveOverlapStartedAtUtc.Value).TotalSeconds));
        }

        return new PresenceNotebookRecord
        {
            PlayerId = PlayerId,
            TotalOverlapSeconds = TotalOverlapSeconds + currentOverlapSeconds,
            LongestOverlapSeconds = Math.Max(LongestOverlapSeconds, currentOverlapSeconds),
            CurrentOverlapSeconds = currentOverlapSeconds,
            IsCurrentlyOverlapping = IsCurrentlyOverlapping,
            OverlapStartCount = OverlapStartCount,
            EnterAfterObserverCount = EnterAfterObserverCount,
            AlreadyThereWhenObserverArrivedCount = AlreadyThereWhenObserverArrivedCount,
            UnclassifiedOverlapStartCount = UnclassifiedOverlapStartCount,
            LastSeenArea = LastSeenArea
        };
    }
}

public sealed class PresenceNominationCandidate
{
    public long CandidateId { get; set; }
    public float Presence { get; set; }
    public float NotebookScore { get; set; }
    public float Score { get; set; }
    public int TotalOverlapSeconds { get; set; }
    public int LongestOverlapSeconds { get; set; }
    public int OverlapStartCount { get; set; }
    public int EnterAfterObserverCount { get; set; }
    public int AlreadyThereWhenObserverArrivedCount { get; set; }
    public int UnclassifiedOverlapStartCount { get; set; }
    public bool IsCurrentlyOverlapping { get; set; }
    public AreaType LastSeenArea { get; set; }
}
