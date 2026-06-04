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

        state.Head = (slot + 1) % WindowTicks;
    }

    /// <summary>observer 관점의 후보 presence(0~5). 자기 자신/타겟은 제외한다.</summary>
    public List<(long candidateId, float presence)> GetCandidates(long matchingId, long observerId, long targetId)
    {
        var result = new List<(long, float)>();
        if (!_matches.TryGetValue(matchingId, out var state)) return result;
        if (!state.Observers.TryGetValue(observerId, out var candidateBuckets)) return result;

        foreach (var (candidateId, buckets) in candidateBuckets)
        {
            if (candidateId == observerId || candidateId == targetId) continue;

            float score = 0f;
            foreach (float w in buckets) score += w;
            result.Add((candidateId, Math.Min(MaxPresence, score)));
        }

        return result;
    }

    public void Remove(long matchingId) => _matches.Remove(matchingId);

    private MatchState GetOrCreate(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
        {
            state = new MatchState();
            _matches[matchingId] = state;
        }

        return state;
    }

    private sealed class MatchState
    {
        public int Head;
        public readonly Dictionary<long, Dictionary<long, float[]>> Observers = new();

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
    }
}
