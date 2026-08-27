using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Owns emotion-afterimage state per matching.  Node placement and regional
/// capacity are fixed; occupancy, combat, closure removal and wave refill are server-owned.
/// </summary>
public sealed class EmotionAfterimageMonsterManager
{
    public const int FirstMonsterId = 202001;
    private static readonly TimeSpan ResetDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AmbientCorridorSpawnInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AmbientCorridorDespawnDelay = TimeSpan.FromSeconds(4);
    private const float AmbientCorridorSafeSpawnDistance = 3f;
    private const int AmbientCorridorAliveLimit = 6;
    private static readonly TimeSpan DensitySampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     페이즈마다 개체 하나씩 줄인다. 한 마리가 강해지고 보상도 커지므로 수까지 유지하면
    ///     복도 총량이 폭증한다. 다만 1까지 내리면 후반 복도가 위험한 통로가 아니라 빈 통로가
    ///     되므로 3에서 멈춘다.
    /// </summary>
    private static int GetAmbientCorridorAliveLimit(int closurePhase) =>
        Math.Max(MinAmbientCorridorAliveLimit, AmbientCorridorAliveLimit - Math.Max(0, closurePhase));

    private const int MinAmbientCorridorAliveLimit = 3;
    private readonly ConcurrentDictionary<long, MatchingMonsterState> _matchingStates = new();
    private readonly bool _ambientCorridorEnabled;
    private Action<long>? _matchingStateRemoved;

    public EmotionAfterimageMonsterManager(bool ambientCorridorEnabled = true)
    {
        _ambientCorridorEnabled = ambientCorridorEnabled;
    }

    public void InitializeMatching(long matchingId)
    {
        if (matchingId <= 0 || IsSoloMapValidationEnabled) return;
        _matchingStates.GetOrAdd(matchingId,
            id => new MatchingMonsterState(
                EmotionAfterimageMonsterSpawnData.CreateDefinitionsForMatching(id),
                _ambientCorridorEnabled));
    }

    private static bool IsSoloMapValidationEnabled =>
        Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";

    public void SetMatchingStateRemovedCallback(Action<long> callback) =>
        _matchingStateRemoved = callback ?? throw new ArgumentNullException(nameof(callback));

    public void RemoveMatchingState(long matchingId)
    {
        _matchingStates.TryRemove(matchingId, out _);
        _matchingStateRemoved?.Invoke(matchingId);
    }

    public IReadOnlyList<MonsterRuntimeInfo> GetSnapshot(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state) ? state.GetSnapshot() : [];

    public IReadOnlyList<MonsterRuntimeInfo> GetSnapshot(long matchingId, AreaType area) =>
        area != AreaType.None && _matchingStates.TryGetValue(matchingId, out var state)
            ? state.GetSnapshot(area)
            : [];

    public MonsterRewardAreaSnapshot GetRewardAreaSnapshot(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state)
            ? state.GetRewardAreaSnapshot()
            : MonsterRewardAreaSnapshot.Empty;

    public IReadOnlyList<MonsterCombatTarget> GetAliveTargets(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state) ? state.GetAliveTargets() : [];

    public bool HasAliveMonsterInArea(long matchingId, AreaType area) =>
        _matchingStates.TryGetValue(matchingId, out var state) && state.HasAliveMonsterInArea(area);

    public bool IsAreaWaveCleared(long matchingId, AreaType area) =>
        _matchingStates.TryGetValue(matchingId, out var state) && state.IsAreaWaveCleared(area);

    public MonsterDamageResult ApplyDamage(long matchingId, int monsterId, long attackerPlayerId, int damage,
        DateTime nowUtc)
    {
        if (damage <= 0 || attackerPlayerId == 0 ||
            !_matchingStates.TryGetValue(matchingId, out var state))
            return MonsterDamageResult.None;

        return state.ApplyDamage(monsterId, attackerPlayerId, damage, nowUtc);
    }

    public MonsterTickResult Tick(long matchingId, IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc) =>
        _matchingStates.TryGetValue(matchingId, out var state)
            ? state.Tick(possibleTargets, nowUtc)
            : MonsterTickResult.None;

    public IReadOnlyList<MonsterReinforcementState> GetReinforcementSnapshot(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state) ? state.GetReinforcementSnapshot() : [];

    public IReadOnlyList<MonsterDensitySample> SampleDensity(
        long matchingId,
        IReadOnlySet<AreaType> occupiedAreas,
        IReadOnlySet<AreaType> attackableMonsterAreas,
        DateTime nowUtc) => _matchingStates.TryGetValue(matchingId, out var state)
        ? state.SampleDensity(occupiedAreas, attackableMonsterAreas, nowUtc) : [];

    /// <summary>
    /// A wave is consumed only when a new area actually reaches the closed state.
    /// Closing an area despawns its live monsters without rewards; only empty nodes
    /// in still-open areas may be filled.
    /// </summary>
    public bool ApplyAreaClosureAndSpawnWave(long matchingId, IReadOnlyCollection<AreaType> closedAreas,
        DateTime nowUtc) =>
        closedAreas.Count > 0 && _matchingStates.TryGetValue(matchingId, out var state)
        && state.ApplyAreaClosureAndQueueWave(closedAreas, nowUtc);

    public IReadOnlyList<MonsterRuntimeInfo> ApplyPhaseSnapshot(
        long matchingId,
        IReadOnlyCollection<AreaType> openAreas,
        bool beginRoomWave,
        int stageIndex,
        DateTime nowUtc) =>
        _matchingStates.TryGetValue(matchingId, out var state)
            ? state.ApplyPhaseSnapshot(openAreas, beginRoomWave, stageIndex, nowUtc)
            : [];
    private sealed class MatchingMonsterState
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, MonsterState> _monsters;
        private readonly HashSet<AreaType> _closedAreas = [];
        private readonly HashSet<AreaType> _activeHotspotAreas = [];
        private readonly Dictionary<AreaType, ReinforcementAreaState> _reinforcements = [];
        private readonly List<PendingWavePack> _pendingWavePacks = [];
        private int _waveIndex;
        private readonly bool _ambientCorridorEnabled;
        private DateTime _nextAmbientCorridorSpawnAtUtc = DateTime.MinValue;
        private DateTime _nextDensitySampleAtUtc = DateTime.MinValue;

        public MatchingMonsterState(IEnumerable<MonsterDefinition> definitions, bool ambientCorridorEnabled)
        {
            _ambientCorridorEnabled = ambientCorridorEnabled;
            var materialized = definitions.ToList();
            _monsters = materialized.ToDictionary(definition => definition.MonsterId,
                definition => new MonsterState(definition));
            // #272 경계 토출 워크인: 개전 활성 팩도 구역 바깥 띠에서 홈으로 걸어 들어온다.
            foreach (var state in _monsters.Values)
            {
                if (state.IsAlive && !state.Definition.IsAmbientCorridor)
                    state.PlaceAtFieldEntry();
            }

            foreach (AreaType area in materialized
                         .Where(definition => definition.StartsActive && definition.IsCore)
                         .Select(definition => definition.Area)
                         .Distinct())
                ActivateHotspot(area, 0);

        }

        public IReadOnlyList<MonsterReinforcementState> GetReinforcementSnapshot()
        {
            lock (_sync)
                return _reinforcements.Values
                    .OrderBy(state => state.Area)
                    .Select(state => state.ToSnapshot())
                    .ToList();
        }


        public IReadOnlyList<MonsterRuntimeInfo> GetSnapshot()
        {
            lock (_sync)
                return _monsters.Values.OrderBy(state => state.Definition.MonsterId).Select(ToRuntimeInfo).ToList();
        }

        public IReadOnlyList<MonsterRuntimeInfo> GetSnapshot(AreaType area)
        {
            lock (_sync)
                return _monsters.Values
                    .Where(state => state.Definition.Area == area)
                    .OrderBy(state => state.Definition.MonsterId)
                    .Select(ToRuntimeInfo)
                    .ToList();
        }

        public MonsterRewardAreaSnapshot GetRewardAreaSnapshot()
        {
            lock (_sync)
            {
                var hotspotAreas = _monsters.Values
                    .Where(state => state.IsAlive && state.Definition.IsCore &&
                                    !state.Definition.IsAmbientCorridor)
                    .Select(state => state.Definition.Area)
                    .ToHashSet();
                var areas = _monsters.Values
                    .Where(state => state.IsAlive && !state.Definition.IsAmbientCorridor &&
                                    hotspotAreas.Contains(state.Definition.Area))
                    .GroupBy(state => state.Definition.Area)
                    .OrderBy(group => group.Key)
                    .Select(group => new MonsterRewardAreaState(
                        group.Key,
                        group.Count(),
                        group.Count(state => state.Definition.IsCore),
                        group.Sum(state => state.SummonStoneReward)))
                    .ToList();
                return new MonsterRewardAreaSnapshot(_waveIndex, areas);
            }
        }

        public IReadOnlyList<MonsterCombatTarget> GetAliveTargets()
        {
            lock (_sync)
                return _monsters.Values.Where(state => state.IsAlive).OrderBy(state => state.Definition.MonsterId)
                    .Select(state => new MonsterCombatTarget(state.Definition.MonsterId, state.Definition.MapId,
                        state.Definition.Area, state.Position, state.Definition.RewardItemId,
                        state.Definition.ClusterId, state.Definition.ClusterMemberIndex,
                        state.Definition.ClusterSize, state.Definition.IsCore)).ToList();
        }

        public bool HasAliveMonsterInArea(AreaType area)
        {
            lock (_sync)
                return _monsters.Values.Any(state => state.IsAlive && state.Definition.Area == area);
        }

        public bool IsAreaWaveCleared(AreaType area)
        {
            lock (_sync)
            {
                if (!_activeHotspotAreas.Contains(area) || _closedAreas.Contains(area))
                    return false;

                // 무한 리필에서 "전부 정리"는 성립하지 않는다. 방의 목표는 유한한 핵이며,
                // 이 방의 핵이 모두 쓰러진 순간을 클리어로 판정한다. 클리어 후에도 필러는
                // 계속 나오지만 문 개방과는 무관하다. 대기 중인 핵 팩이 있으면 아직이다.
                bool corePending = _pendingWavePacks.Any(pending => _monsters.Values.Any(state =>
                    state.Definition.ClusterId == pending.ClusterId &&
                    state.Definition.IsCore &&
                    state.Definition.Area == area));
                if (corePending)
                    return false;

                return !_monsters.Values.Any(state =>
                    state.IsAlive &&
                    state.Definition.IsCore &&
                    !state.Definition.IsAmbientCorridor &&
                    state.Definition.Area == area);
            }
        }

        public MonsterDamageResult ApplyDamage(int monsterId, long attackerPlayerId, int damage, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (!_monsters.TryGetValue(monsterId, out var state) || !state.IsAlive)
                    return MonsterDamageResult.None;

                state.LastDamagedAtUtc = nowUtc;
                if (state.FirstAttackerPlayerId == 0)
                    state.FirstAttackerPlayerId = attackerPlayerId;
                state.LastAttackerPlayerId = attackerPlayerId;
                state.DamageByPlayer.TryGetValue(attackerPlayerId, out int accumulatedDamage);
                state.DamageByPlayer[attackerPlayerId] = accumulatedDamage + damage;
                state.CurrentHealth = Math.Max(0, state.CurrentHealth - damage);
                var contributions = state.DamageByPlayer.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (state.CurrentHealth > 0)
                    return new MonsterDamageResult(
                        ToRuntimeInfo(state),
                        false,
                        0,
                        true,
                        state.FirstAttackerPlayerId,
                        state.LastAttackerPlayerId,
                        contributions);

                state.IsAlive = false;
                state.NextAttackAtUtc = DateTime.MaxValue;

                // 보상 예산: 핵은 방의 목표라 예산 외로 지급하고, 일반·증원은 방 예산에서 차감한다.
                // 예산이 마르면 몸은 계속 나오되 0석이 되어, 위험만 남은 방을 떠날 이유가 생긴다.
                int grantedReward = state.SummonStoneReward;
                if (!state.Definition.IsCore && !state.Definition.IsAmbientCorridor &&
                    _reinforcements.TryGetValue(state.Definition.Area, out var areaBudget))
                {
                    grantedReward = Math.Clamp(areaBudget.RewardBudgetRemaining, 0, grantedReward);
                    areaBudget.RewardBudgetRemaining -= grantedReward;
                }

                return new MonsterDamageResult(
                    ToRuntimeInfo(state),
                    true,
                    grantedReward,
                    true,
                    state.FirstAttackerPlayerId,
                    state.LastAttackerPlayerId,
                    contributions,
                    state.Definition.IsReinforcement);
            }
        }

        public IReadOnlyList<MonsterRuntimeInfo> ApplyPhaseSnapshot(
            IReadOnlyCollection<AreaType> openAreas,
            bool beginRoomWave,
            int stageIndex,
            DateTime nowUtc)
        {
            lock (_sync)
            {
                var open = openAreas.ToHashSet();
                var changed = new Dictionary<int, MonsterRuntimeInfo>();
                _closedAreas.Clear();
                foreach (AreaType area in _monsters.Values.Select(monster => monster.Definition.Area).Distinct())
                {
                    if (!open.Contains(area))
                        _closedAreas.Add(area);
                }

                _pendingWavePacks.RemoveAll(pack =>
                {
                    var member = _monsters.Values.FirstOrDefault(monster =>
                        monster.Definition.ClusterId == pack.ClusterId);
                    return member == null || _closedAreas.Contains(member.Definition.Area);
                });
                foreach (AreaType area in _reinforcements.Keys.Where(_closedAreas.Contains).ToList())
                    _reinforcements.Remove(area);
                _activeHotspotAreas.RemoveWhere(_closedAreas.Contains);

                foreach (var monster in _monsters.Values.Where(monster =>
                             monster.IsAlive && _closedAreas.Contains(monster.Definition.Area)))
                {
                    monster.Deactivate();
                    changed[monster.Definition.MonsterId] = ToRuntimeInfo(monster);
                }

                if (!beginRoomWave)
                    return changed.Values.OrderBy(monster => monster.MonsterId).ToList();

                _waveIndex = Math.Max(0, stageIndex);
                _pendingWavePacks.Clear();
                foreach (var monster in _monsters.Values.Where(monster =>
                             !monster.Definition.IsAmbientCorridor && open.Contains(monster.Definition.Area)))
                {
                    if (monster.IsAlive)
                    {
                        monster.Deactivate();
                        changed[monster.Definition.MonsterId] = ToRuntimeInfo(monster);
                    }
                }

                foreach (AreaType area in open.Where(area => !area.IsCorridor()))
                {
                    var pack = _monsters.Values
                        .Where(monster => !monster.Definition.IsAmbientCorridor &&
                                          monster.Definition.Area == area)
                        .GroupBy(monster => monster.Definition.ClusterId)
                        .Where(group => group.Any(monster => monster.Definition.IsCore))
                        .OrderBy(group => group.Min(monster => monster.Definition.SpawnPriority))
                        .FirstOrDefault();
                    if (pack == null)
                        continue;

                    foreach (var monster in pack)
                    {
                        monster.ActivateAtHome(stageIndex);
                        changed[monster.Definition.MonsterId] = ToRuntimeInfo(monster);
                    }
                    ActivateHotspot(area, stageIndex);
                }

                _nextAmbientCorridorSpawnAtUtc = nowUtc;
                return changed.Values.OrderBy(monster => monster.MonsterId).ToList();
            }
        }
        public bool ApplyAreaClosureAndQueueWave(IReadOnlyCollection<AreaType> closedAreas, DateTime nowUtc)
        {
            lock (_sync)
            {
                bool addedClosure = false;
                foreach (var area in closedAreas) addedClosure |= _closedAreas.Add(area);
                if (!addedClosure) return false;
                foreach (AreaType closedArea in closedAreas)
                {
                    _activeHotspotAreas.Remove(closedArea);
                    _reinforcements.Remove(closedArea);
                }


                bool changed = false;
                foreach (var state in _monsters.Values.Where(state => state.IsAlive &&
                             !state.Definition.IsAmbientCorridor && _closedAreas.Contains(state.Definition.Area)))
                {
                    state.IsAlive = false;
                    state.NextAttackAtUtc = DateTime.MaxValue;
                    changed = true;
                }

                int waveIndex = _waveIndex++;
                RefreshOpenHotspotBudgets(_waveIndex);
                foreach (var ambientState in _monsters.Values
                             .Where(state => state.IsAlive && state.Definition.IsAmbientCorridor))
                    changed |= ambientState.ApplyAmbientCorridorPhase(_waveIndex);

                var activeCoreAreas = _monsters.Values
                    .Where(state => state.IsAlive && state.Definition.IsCore &&
                                    !state.Definition.IsAmbientCorridor)
                    .Select(state => state.Definition.Area)
                    .ToHashSet();
                var pendingClusterIds = _pendingWavePacks.Select(pack => pack.ClusterId).ToHashSet();
                foreach (var pendingArea in _pendingWavePacks
                             .Select(pending => _monsters.Values.FirstOrDefault(state =>
                                 state.Definition.ClusterId == pending.ClusterId && state.Definition.IsCore))
                             .Where(state => state != null)
                             .Select(state => state!.Definition.Area))
                    activeCoreAreas.Add(pendingArea);

                int activeAreaTarget = EmotionAfterimageMonsterSpawnData.GetWaveActiveAreaTarget(waveIndex);
                int spawnBudget = Math.Max(0, activeAreaTarget - activeCoreAreas.Count);
                int requestedSpawnBudget = spawnBudget;
                if (spawnBudget == 0) return changed;

                TimeSpan releaseDuration = EmotionAfterimageMonsterSpawnData.GetWavePackReleaseDuration(waveIndex);
                var dormantPacks = _monsters.Values
                    .Where(state => !state.Definition.IsAmbientCorridor && !_closedAreas.Contains(state.Definition.Area))
                    .GroupBy(state => state.Definition.ClusterId)
                    .Where(pack => pack.Any(state => state.Definition.IsCore) &&
                                   pack.Where(state => state.Definition.IsCore).All(state => !state.IsAlive) &&
                                   !pendingClusterIds.Contains(pack.Key))
                    .OrderBy(pack => pack.Any(state => state.Definition.StartsActive) ? 0 : 1)
                    .ThenBy(pack => pack.Min(state => state.Definition.SpawnPriority))
                    .ThenBy(pack => pack.Min(state => state.Definition.MonsterId));

                int scheduledCount = 0;
                foreach (var pack in dormantPacks)
                {
                    if (spawnBudget <= 0) break;
                    var first = pack.First();
                    AreaType area = first.Definition.Area;
                    if (activeCoreAreas.Contains(area)) continue;

                    activeCoreAreas.Add(area);
                    double releaseOffsetSeconds = requestedSpawnBudget == 1
                        ? 0d
                        : releaseDuration.TotalSeconds * scheduledCount / (requestedSpawnBudget - 1d);
                    _pendingWavePacks.Add(new PendingWavePack(
                        first.Definition.ClusterId,
                        nowUtc.AddSeconds(releaseOffsetSeconds),
                        nowUtc + releaseDuration + TimeSpan.FromSeconds(8), waveIndex + 1));
                    scheduledCount++;
                    spawnBudget--;
                    changed = true;
                }
                return changed;
            }
        }
        public MonsterTickResult Tick(IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc)
        {
            var targetsByArea = BuildTargetBuckets(possibleTargets);
            lock (_sync)
            {
                var changed = new List<MonsterRuntimeInfo>();
                var spawned = new List<MonsterRuntimeInfo>();
                ReleaseDueWavePacks(possibleTargets, nowUtc, changed, spawned);
                var reinforcementReleases = ReleaseDueReinforcements(
                    targetsByArea, nowUtc, changed, spawned);
                var attacks = new List<MonsterAttack>();
                int spawnedAmbientMonsterId = SpawnAmbientCorridorMonster(possibleTargets, nowUtc, changed);
                foreach (var state in _monsters.Values)
                {
                    if (!state.IsAlive || state.Definition.MonsterId == spawnedAmbientMonsterId) continue;

                    float elapsedSeconds = (float)(nowUtc - state.LastUpdatedAtUtc).TotalSeconds;
                    state.LastUpdatedAtUtc = nowUtc;
                    elapsedSeconds = Math.Clamp(elapsedSeconds, 0f, 0.1f);
                    if (!targetsByArea.TryGetValue((state.Definition.MapId, state.Definition.Area), out var targets) ||
                        targets.Count == 0)
                    {
                        if (state.Definition.IsAmbientCorridor)
                        {
                            if (nowUtc - state.LastTargetSeenAtUtc >= AmbientCorridorDespawnDelay)
                            {
                                state.Deactivate();
                                changed.Add(ToRuntimeInfo(state));
                                continue;
                            }
                        }

                        bool movedHome = MoveTowards(state, GetIdleDestination(state, nowUtc), elapsedSeconds);
                        if (state.CurrentHealth < state.MaxHealth && nowUtc - state.LastDamagedAtUtc >= ResetDelay)
                        {
                            // 체력 회복과 누적 피해 초기화는 문턱 왕복 딜 누적을 막는 규칙이다.
                            // 직전 교전 상대만 기억해 재진입 시 타겟이 흔들리지 않게 한다.
                            state.CurrentHealth = state.MaxHealth;
                            state.DamageByPlayer.Clear();
                            movedHome = true;
                        }
                        if (movedHome) changed.Add(ToRuntimeInfo(state));
                        continue;
                    }

                    state.LastTargetSeenAtUtc = nowUtc;

                    // A same-area monster is a persistent local threat, not an
                    // interaction prompt: it notices every player in its room.
                    var target = SelectTarget(state, targets);
                    Vector3f chaseDestination = GetChaseDestination(state, target.Position, nowUtc);
                    bool moved = MoveTowards(state, chaseDestination, elapsedSeconds);
                    if (moved) changed.Add(ToRuntimeInfo(state));
                    if (nowUtc < state.NextAttackAtUtc ||
                        !IsWithinRange(state.Position, target.Position, state.Definition.AttackRange)) continue;

                    state.NextAttackAtUtc = nowUtc.AddSeconds(state.Definition.AttackIntervalSeconds);
                    attacks.Add(new MonsterAttack(state.Definition.MonsterId, target.PlayerId, state.Definition.Area,
                        state.AttackDamage));
                }
                return new MonsterTickResult(changed, attacks, spawned, reinforcementReleases);
            }
        }

        private IReadOnlyList<MonsterReinforcementRelease> ReleaseDueReinforcements(
            IReadOnlyDictionary<(MapId MapId, AreaType Area), List<MonsterSpatialTarget>> targetsByArea,
            DateTime nowUtc,
            ICollection<MonsterRuntimeInfo> changed,
            ICollection<MonsterRuntimeInfo> spawned)
        {
            var releases = new List<MonsterReinforcementRelease>();
            foreach (AreaType area in _activeHotspotAreas.OrderBy(value => value).ToList())
            {
                if (_closedAreas.Contains(area) || !_reinforcements.TryGetValue(area, out var reinforcement))
                    continue;

                if (!targetsByArea.TryGetValue((Config.SWARM_MATCH_MAP, area), out var areaTargets) || areaTargets.Count == 0)
                {
                    reinforcement.PendingReleaseAtUtc = null;
                    continue;
                }

                int aliveCount = CountAliveRoomMonsters(area);
                int releaseThreshold = Math.Max(
                    0,
                    reinforcement.TargetAliveCount - EmotionAfterimageMonsterSpawnData.ReinforcementBatchSize);
                // 몸 예산은 없다 — 생존 상한 아래로 떨어지면 무조건 리필한다. 죽은 증원
                // 슬롯이 재사용되므로 공급은 무한이고, 유한한 것은 보상 예산뿐이다.
                if (aliveCount > releaseThreshold)
                {
                    reinforcement.PendingReleaseAtUtc = null;
                    continue;
                }

                if (!reinforcement.PendingReleaseAtUtc.HasValue)
                {
                    reinforcement.PendingReleaseAtUtc =
                        nowUtc + EmotionAfterimageMonsterSpawnData.ReinforcementReleaseInterval;
                    continue;
                }
                if (nowUtc < reinforcement.PendingReleaseAtUtc.Value)
                    continue;

                int releaseCount = Math.Min(
                    EmotionAfterimageMonsterSpawnData.ReinforcementBatchSize,
                    reinforcement.TargetAliveCount - aliveCount);
                var candidates = _monsters.Values
                    .Where(state => !state.IsAlive && state.Definition.IsReinforcement &&
                                    state.Definition.Area == area)
                    .Select(state => new
                    {
                        State = state,
                        MinDistanceSquared = areaTargets.Min(target =>
                            DistanceSquared(GetHomePosition(state.Definition), target.Position))
                    })
                    .Where(candidate => candidate.MinDistanceSquared >=
                                        AmbientCorridorSafeSpawnDistance * AmbientCorridorSafeSpawnDistance)
                    .OrderByDescending(candidate => candidate.MinDistanceSquared)
                    .ThenBy(candidate => candidate.State.Definition.MonsterId)
                    .Take(releaseCount)
                    .Select(candidate => candidate.State)
                    .ToList();

                foreach (var candidate in candidates)
                {
                    // 유리 떼 데미지 곡선이 증원에도 붙도록 스테이지 티어를 전달한다.
                    candidate.ActivateAtHome(reinforcement.PhaseIndex);
                    var runtime = ToRuntimeInfo(candidate);
                    changed.Add(runtime);
                    spawned.Add(runtime);
                }

                int releasedCount = candidates.Count;
                if (releasedCount > 0)
                {
                    reinforcement.TotalReleased += releasedCount;
                    releases.Add(new MonsterReinforcementRelease(
                        area,
                        reinforcement.PhaseIndex,
                        releasedCount,
                        reinforcement.RewardBudgetRemaining,
                        aliveCount + releasedCount));
                }
                reinforcement.PendingReleaseAtUtc = null;
            }

            return releases;
        }

        public IReadOnlyList<MonsterDensitySample> SampleDensity(
            IReadOnlySet<AreaType> occupiedAreas,
            IReadOnlySet<AreaType> attackableMonsterAreas,
            DateTime nowUtc)
        {
            lock (_sync)
            {
                if (nowUtc < _nextDensitySampleAtUtc)
                    return [];

                _nextDensitySampleAtUtc = nowUtc + DensitySampleInterval;
                int globalAliveCount = _monsters.Values.Count(state => state.IsAlive);
                var samples = new List<MonsterDensitySample>();
                foreach (AreaType area in _activeHotspotAreas.OrderBy(value => value))
                {
                    if (_closedAreas.Contains(area) || !occupiedAreas.Contains(area))
                        continue;

                    int aliveCount = CountAliveRoomMonsters(area);
                    int remainingBudget = _reinforcements.TryGetValue(area, out var reinforcement)
                        ? reinforcement.RewardBudgetRemaining
                        : 0;
                    samples.Add(new MonsterDensitySample(
                        area,
                        _waveIndex,
                        aliveCount,
                        remainingBudget,
                        globalAliveCount,
                        attackableMonsterAreas.Contains(area)));
                }
                return samples;
            }
        }

        private int CountAliveRoomMonsters(AreaType area) =>
            _monsters.Values.Count(state => state.IsAlive && !state.Definition.IsAmbientCorridor &&
                                            state.Definition.Area == area);

        private void ActivateHotspot(AreaType area, int phaseIndex)
        {
            if (_closedAreas.Contains(area))
                return;

            _activeHotspotAreas.Add(area);
            _reinforcements[area] = new ReinforcementAreaState(
                area,
                phaseIndex,
                EmotionAfterimageMonsterSpawnData.GetAreaRewardBudget(phaseIndex),
                EmotionAfterimageMonsterSpawnData.GetReinforcementAliveTarget(phaseIndex));
        }

        private void RefreshOpenHotspotBudgets(int phaseIndex)
        {
            foreach (AreaType area in _activeHotspotAreas.Where(area => !_closedAreas.Contains(area)).ToList())
                ActivateHotspot(area, phaseIndex);
        }

        private void ReleaseDueWavePacks(IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc,
            ICollection<MonsterRuntimeInfo> changed, ICollection<MonsterRuntimeInfo> spawned)
        {
            for (int index = _pendingWavePacks.Count - 1; index >= 0; index--)
            {
                PendingWavePack pending = _pendingWavePacks[index];
                if (nowUtc < pending.ReleaseAtUtc)
                    continue;

                var pack = _monsters.Values
                    .Where(state => state.Definition.ClusterId == pending.ClusterId)
                    .OrderBy(state => state.Definition.MonsterId)
                    .ToList();
                if (pack.Count == 0 || pack.Any(state => _closedAreas.Contains(state.Definition.Area)))
                {
                    _pendingWavePacks.RemoveAt(index);
                    continue;
                }

                bool playerIsTooClose = possibleTargets.Any(target =>
                    target.MapId == pack[0].Definition.MapId && target.Area == pack[0].Definition.Area &&
                    pack.Any(state => DistanceSquared(state.Definition.Position, target.Position) <
                        AmbientCorridorSafeSpawnDistance * AmbientCorridorSafeSpawnDistance));
                if (playerIsTooClose && nowUtc < pending.ForceAtUtc)
                    continue;

                foreach (var state in pack)
                {
                    state.ActivateAtHome(pending.StrengthTier);
                    var runtime = ToRuntimeInfo(state);
                    changed.Add(runtime);
                    spawned.Add(runtime);
                }
                if (pack.Any(state => state.Definition.IsCore))
                    ActivateHotspot(pack[0].Definition.Area, pending.StrengthTier);
                _pendingWavePacks.RemoveAt(index);
            }
        }
        private int SpawnAmbientCorridorMonster(IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc,
            ICollection<MonsterRuntimeInfo> changed)
        {
            if (!_ambientCorridorEnabled || nowUtc < _nextAmbientCorridorSpawnAtUtc)
                return 0;

            var corridorTargets = possibleTargets
                // #272 School2: 복도(1~8)에 있는 플레이어가 압박 트리거다.
                .Where(target => target.MapId == Config.SWARM_MATCH_MAP && target.Area.IsCorridor())
                .ToList();
            if (corridorTargets.Count == 0)
                return 0;

            int aliveCount = _monsters.Values.Count(state => state.IsAlive && state.Definition.IsAmbientCorridor);
            if (aliveCount >= GetAmbientCorridorAliveLimit(_waveIndex))
                return 0;

            // #272 School2: 앵커는 타깃이 있는 복도의 것만 후보다 — 압박 몹은 같은 구역 타깃이
            // 있어야 유지되는데(무타깃 4초 소멸), School의 단일 회랑과 달리 복도가 8개로 나뉘어
            // 다른 복도 앵커는 스폰 즉시 헛돌았다.
            var targetAreas = new HashSet<AreaType>(corridorTargets.Select(target => target.Area));
            var candidate = _monsters.Values
                .Where(state => !state.IsAlive && state.Definition.IsAmbientCorridor &&
                                targetAreas.Contains(state.Definition.Area))
                .OrderBy(state => state.Definition.MonsterId)
                .FirstOrDefault(state => corridorTargets.All(target =>
                    DistanceSquared(state.Definition.Position, target.Position) >=
                    AmbientCorridorSafeSpawnDistance * AmbientCorridorSafeSpawnDistance));
            _nextAmbientCorridorSpawnAtUtc = nowUtc + AmbientCorridorSpawnInterval;
            if (candidate == null)
                return 0;

            candidate.ActivateAmbientCorridor(_waveIndex);
            candidate.LastTargetSeenAtUtc = nowUtc;
            changed.Add(ToRuntimeInfo(candidate));
            return candidate.Definition.MonsterId;
        }

        private static Dictionary<(MapId MapId, AreaType Area), List<MonsterSpatialTarget>> BuildTargetBuckets(
            IReadOnlyList<MonsterSpatialTarget> possibleTargets)
        {
            var targetsByArea = new Dictionary<(MapId, AreaType), List<MonsterSpatialTarget>>();
            foreach (var target in possibleTargets)
            {
                var key = (target.MapId, target.Area);
                if (!targetsByArea.TryGetValue(key, out var targets))
                {
                    targets = new List<MonsterSpatialTarget>();
                    targetsByArea[key] = targets;
                }
                targets.Add(target);
            }
            return targetsByArea;
        }

        private static MonsterSpatialTarget SelectTarget(MonsterState state,
            IReadOnlyList<MonsterSpatialTarget> targets)
        {
            var selected = targets[0];
            var selectedPriority = BuildTargetPriority(state, selected);

            for (int index = 1; index < targets.Count; index++)
            {
                var candidate = targets[index];
                var candidatePriority = BuildTargetPriority(state, candidate);
                if (candidatePriority.CompareTo(selectedPriority) >= 0)
                    continue;

                selected = candidate;
                selectedPriority = candidatePriority;
            }

            return selected;
        }

        /// <summary>
        ///     타겟 우선순위. 값이 작을수록 우선한다.
        ///     누적 피해가 같을 때 직전 교전 상대를 유지하는 이유는, 문을 오가며 타겟이 바뀌면
        ///     잔상이 두 대상 사이에서 진동하기 때문이다. 체력 회복과 누적 피해 초기화는
        ///     그대로 두므로 문턱 왕복으로 피해를 누적하는 경로는 여전히 막혀 있다.
        /// </summary>
        private static (int NegativeDamage, int NotLastEngaged, float DistanceSquared, long PlayerId)
            BuildTargetPriority(MonsterState state, MonsterSpatialTarget target)
        {
            return (
                -state.DamageByPlayer.GetValueOrDefault(target.PlayerId),
                target.PlayerId == state.LastAttackerPlayerId ? 0 : 1,
                DistanceSquared(state.Definition.Position, target.Position),
                target.PlayerId);
        }

        private static bool IsEscort(MonsterDefinition definition) =>
            definition.IsReinforcement ||
            (!definition.IsAmbientCorridor && !definition.IsCore &&
             definition.ClusterMemberIndex < definition.ClusterSize - 3);

        private static Vector3f GetChaseDestination(MonsterState state, Vector3f targetPosition, DateTime nowUtc)
        {
            Vector3f offset = state.Definition.FormationOffset;
            bool isEscort = IsEscort(state.Definition);
            if (!isEscort)
                return new Vector3f(
                    targetPosition.X + offset.X,
                    targetPosition.Y + offset.Y,
                    targetPosition.Z);

            // Escorts do not share a single pursuit point. Their deterministic orbit
            // keeps the group loose while retaining enough overlap for close pressure.
            float seconds = (float)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
            float angle = seconds * (0.7f + state.Definition.ClusterMemberIndex * 0.025f) +
                          state.Definition.ClusterId * 0.73f + state.Definition.ClusterMemberIndex * 1.17f;
            float sway = 0.24f + (state.Definition.ClusterMemberIndex % 3) * 0.04f;
            return new Vector3f(
                targetPosition.X + offset.X + MathF.Cos(angle) * sway,
                targetPosition.Y + offset.Y + MathF.Sin(angle) * sway,
                targetPosition.Z);
        }

        private static Vector3f GetHomePosition(MonsterDefinition definition) => new(
            definition.Position.X + definition.FormationOffset.X,
            definition.Position.Y + definition.FormationOffset.Y,
            definition.Position.Z);

        private static Vector3f GetIdleDestination(MonsterState state, DateTime nowUtc)
        {
            Vector3f home = GetHomePosition(state.Definition);
            bool isEscort = IsEscort(state.Definition);
            if (!isEscort)
                return home;

            // Escorts keep their own small patrol loops around distinct home slots.
            // This reads as a guard line rather than nine copies idling on one point.
            // 유닉스 초를 float로 바로 쓰면 2026년대(~1.8e9초)의 해상도가 약 128초라 순찰 시계가 멈춘다
            // — double로 누적한 뒤 2π로 접어서 float 정밀도 안으로 가져온다.
            double totalSeconds = (nowUtc - DateTime.UnixEpoch).TotalSeconds;
            double rawAngle = totalSeconds * (0.85d + state.Definition.ClusterMemberIndex * 0.03d) +
                              state.Definition.ClusterId * 0.73d + state.Definition.ClusterMemberIndex * 1.17d;
            float angle = (float)(rawAngle % (2d * Math.PI));
            float radius = 0.13f + (state.Definition.ClusterMemberIndex % 3) * 0.025f;
            return new Vector3f(
                home.X + MathF.Cos(angle) * radius,
                home.Y + MathF.Sin(angle) * radius,
                home.Z);
        }

        private static MonsterRuntimeInfo ToRuntimeInfo(MonsterState state) => new()
        {
            MonsterId = state.Definition.MonsterId,
            AreaType = state.Definition.Area,
            PositionX = state.Position.X,
            PositionY = state.Position.Y,
            MaxHealth = state.MaxHealth,
            CurrentHealth = state.CurrentHealth,
            IsAlive = state.IsAlive,
            RewardItemId = state.Definition.RewardItemId,
            IsCore = state.Definition.IsCore,
            SummonStoneReward = state.SummonStoneReward
        };

        private static bool MoveTowards(MonsterState state, Vector3f destination, float elapsedSeconds)
        {
            if (elapsedSeconds <= 0f) return false;
            float maxDistance = state.Definition.MoveSpeed * elapsedSeconds;
            float deltaX = destination.X - state.Position.X;
            float deltaY = destination.Y - state.Position.Y;
            float distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared <= 0.0001f) return false;

            float distance = MathF.Sqrt(distanceSquared);
            float ratio = Math.Min(1f, maxDistance / distance);
            state.Position = new Vector3f(state.Position.X + deltaX * ratio, state.Position.Y + deltaY * ratio,
                state.Position.Z);
            return true;
        }
    }

    // #272 경계 토출 워크인 (2026-08-27 유저 결정 "잔상 팩도 자기장에서 나오게"): 팩·증원의
    // 활성화 위치 = 그 구역에서 자기장 중심으로부터 가장 먼 띠(경계가 올 방향)의 한 점.
    // 유휴 이동이 홈 슬롯으로 걷는 기존 로직이 그대로 워크인 연출이 된다. 같은 클러스터는
    // 같은 진입점(clusterId 결정적)이라 무리가 한 덩어리로 걸어 들어온다.
    private const double FieldEntryBandCells = 3d;
    private static readonly Dictionary<AreaType, List<Vector3f>> FieldEntryBandCache = new();

    private static Vector3f ResolveActivatePosition(MonsterDefinition definition)
    {
        var home = new Vector3f(
            definition.Position.X + definition.FormationOffset.X,
            definition.Position.Y + definition.FormationOffset.Y,
            definition.Position.Z);
        // 복도 배회 앵커는 제자리 등장 유지 — 진입 연출 대상이 아니다.
        if (definition.IsAmbientCorridor) return home;

        List<Vector3f>? band;
        lock (FieldEntryBandCache)
        {
            if (!FieldEntryBandCache.TryGetValue(definition.Area, out band))
            {
                double maxDistance = 0d;
                var areaCells = new List<((int X, int Y) Cell, int Distance)>();
                foreach (var entry in SwarmPressureField.DistancesByCell)
                {
                    var cell = new Cell(entry.Key.X, entry.Key.Y);
                    if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) != definition.Area)
                        continue;
                    areaCells.Add((entry.Key, entry.Value));
                    if (entry.Value > maxDistance) maxDistance = entry.Value;
                }

                band = areaCells
                    .Where(pair => pair.Distance > maxDistance - FieldEntryBandCells)
                    .Select(pair => MapCoordinateConverter.CellToWorld(
                        Config.SWARM_MATCH_MAP, new Cell(pair.Cell.X, pair.Cell.Y)))
                    .ToList();
                FieldEntryBandCache[definition.Area] = band;
            }
        }

        if (band.Count == 0) return home;
        var entryPoint = band[Math.Abs(definition.ClusterId * 31) % band.Count];
        return new Vector3f(
            entryPoint.X + definition.FormationOffset.X,
            entryPoint.Y + definition.FormationOffset.Y,
            entryPoint.Z);
    }

    private static bool IsWithinRange(Vector3f left, Vector3f right, float range) =>
        DistanceSquared(left, right) <= range * range;

    private static float DistanceSquared(Vector3f left, Vector3f right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        return x * x + y * y;
    }

    private sealed class MonsterState(MonsterDefinition definition)
    {
        public MonsterDefinition Definition { get; } = definition;
        public Vector3f Position { get; set; } = new(
            definition.Position.X + definition.FormationOffset.X,
            definition.Position.Y + definition.FormationOffset.Y,
            definition.Position.Z);
        public int MaxHealth { get; private set; } = definition.MaxHealth;
        public int AttackDamage { get; private set; } = definition.AttackDamage;
        public int SummonStoneReward { get; private set; } = definition.SummonStoneReward;
        public int AppliedAmbientCorridorPhase { get; private set; } = -1;
        public int CurrentHealth { get; set; } = definition.MaxHealth;
        public bool IsAlive { get; set; } = definition.StartsActive;
        public long FirstAttackerPlayerId { get; set; }
        public long LastAttackerPlayerId { get; set; }
        public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime NextAttackAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastTargetSeenAtUtc { get; set; } = DateTime.MinValue;
        public Dictionary<long, int> DamageByPlayer { get; } = new();

        public void ActivateAtHome(int strengthTier = 0)
        {
            int tier = Math.Clamp(strengthTier, 0, 3);
            int coreStrengthTier = Definition.IsCore ? tier : 0;
            MaxHealth = Definition.MaxHealth + coreStrengthTier * 24;
            // 유리 떼: 일반 잔상은 스테이지가 오를수록 아파지지만 물러야 한다. HP를 같이 올리면
            // 후반의 쓸어버리는 감각이 벽이 되므로 데미지만 올린다. 강도는 킬이 아니라 스테이지에
            // 묶는다 — 잘 클수록 세계가 따라 세지면 성장이 체감에서 지워진다. 핵은 기존 강화 유지.
            AttackDamage = Definition.IsCore
                ? Definition.AttackDamage + coreStrengthTier * 2
                : Definition.AttackDamage + tier;
            SummonStoneReward = Definition.SummonStoneReward + coreStrengthTier * 2;
            AppliedAmbientCorridorPhase = -1;
            // #272 경계 토출 워크인: 자기장이 올 방향의 구역 바깥 띠에서 등장해 홈으로 걷는다.
            Position = ResolveActivatePosition(Definition);
            CurrentHealth = MaxHealth;
            IsAlive = true;
            FirstAttackerPlayerId = 0;
            LastAttackerPlayerId = 0;
            LastDamagedAtUtc = DateTime.MinValue;
            NextAttackAtUtc = DateTime.MinValue;
            LastUpdatedAtUtc = DateTime.MinValue;
            LastTargetSeenAtUtc = DateTime.MinValue;
            DamageByPlayer.Clear();
        }

        /// <summary>개전 활성 개체의 경계 진입 배치 — ActivateAtHome을 거치지 않는 생성자 경로용.</summary>
        public void PlaceAtFieldEntry()
        {
            Position = ResolveActivatePosition(Definition);
        }

        public void ActivateAmbientCorridor(int closurePhase)
        {
            ActivateAtHome();
            ApplyAmbientCorridorPhase(closurePhase);
        }

        public bool ApplyAmbientCorridorPhase(int closurePhase)
        {
            if (!Definition.IsAmbientCorridor)
                return false;

            int normalizedPhase = Math.Max(0, closurePhase);
            if (AppliedAmbientCorridorPhase == normalizedPhase)
                return false;

            int multiplier = normalizedPhase >= 30 ? int.MaxValue : 1 << normalizedPhase;

            // 보상과 피해만 올리면 후반 복도가 즉사 파밍터가 된다. 실제로 사람이 소환석의
            // 97%를 복도에서 얻었다 (2026-07-31 match-2015: 425석 중 412석).
            // 체력을 같은 배율로 올려 잡는 데 드는 시간이 보상과 함께 커지게 한다.
            MaxHealth = Math.Max(1, SaturatingMultiply(Definition.MaxHealth, multiplier));
            CurrentHealth = MaxHealth;
            AttackDamage = Math.Max(1, SaturatingMultiply(Definition.AttackDamage, multiplier));

            // 사거리는 올리지 않는다. 몸집에 맞춰 함께 키웠더니 페이즈 3에서 5.2셀, 4에서
            // 상한 9셀이 되어 화면 밖에서 맞는 상황이 나왔다 (2026-08-01 match-2043:
            // 피해 4~8이 시야 밖에서 들어옴). 복도 잔상은 근접 위협으로 유지한다.

            SummonStoneReward = Math.Max(1, SaturatingMultiply(Definition.SummonStoneReward, multiplier));
            AppliedAmbientCorridorPhase = normalizedPhase;
            return true;
        }

        private static int SaturatingMultiply(int value, int multiplier)
        {
            long result = (long)value * multiplier;
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }

        public void Deactivate()
        {
            IsAlive = false;
            NextAttackAtUtc = DateTime.MaxValue;
            LastTargetSeenAtUtc = DateTime.MinValue;
            DamageByPlayer.Clear();
        }
    }

    private sealed class ReinforcementAreaState(
        AreaType area,
        int phaseIndex,
        int rewardBudget,
        int targetAliveCount)
    {
        public AreaType Area { get; } = area;
        public int PhaseIndex { get; } = phaseIndex;

        /// <summary>
        ///     방·페이즈당 보상 예산. 몸 예산은 폐기됐다 — 증원은 생존 상한 기준으로 무한 리필되고,
        ///     이 예산 안의 처치만 소환석을 지급한다. 스냅샷·릴리즈 레코드의 RemainingBudget 필드는
        ///     텔레메트리 연속성을 위해 이 값을 그대로 싣는다.
        /// </summary>
        public int RewardBudgetRemaining { get; set; } = rewardBudget;

        public int TargetAliveCount { get; } = targetAliveCount;
        public int TotalReleased { get; set; }
        public DateTime? PendingReleaseAtUtc { get; set; }

        public MonsterReinforcementState ToSnapshot() =>
            new(Area, PhaseIndex, RewardBudgetRemaining, TargetAliveCount, TotalReleased,
                PendingReleaseAtUtc.HasValue);
    }

    private readonly record struct PendingWavePack(int ClusterId, DateTime ReleaseAtUtc, DateTime ForceAtUtc, int StrengthTier);
}

public readonly record struct MonsterDefinition(int MonsterId, MapId MapId, AreaType Area, Vector3f Position,
    int MaxHealth, int AttackDamage, float AttackRange, float AttackIntervalSeconds, int RewardItemId,
    bool IsCore, int SummonStoneReward, float MoveSpeed, float LeashRange, int AreaAliveLimit,
    bool StartsActive, int SpawnPriority, int ClusterId, int ClusterMemberIndex, int ClusterSize,
    Vector3f FormationOffset, bool IsAmbientCorridor = false, bool IsReinforcement = false);

public readonly record struct MonsterCombatTarget(
    int MonsterId,
    MapId MapId,
    AreaType Area,
    Vector3f Position,
    int RewardItemId,
    int ClusterId = 0,
    int ClusterMemberIndex = 0,
    int ClusterSize = 1,
    bool IsCore = false);
public readonly record struct MonsterRewardAreaState(
    AreaType Area,
    int AliveMonsterCount,
    int AliveCoreCount,
    int RemainingSummonStoneReward);
public readonly record struct MonsterRewardAreaSnapshot(
    int PhaseIndex,
    IReadOnlyList<MonsterRewardAreaState> Areas)
{
    public static MonsterRewardAreaSnapshot Empty => new(0, []);
}
public readonly record struct MonsterSpatialTarget(long PlayerId, MapId MapId, AreaType Area, Vector3f Position);
public readonly record struct MonsterAttack(int MonsterId, long TargetPlayerId, AreaType Area, int Damage);
public readonly record struct MonsterDamageResult(
    MonsterRuntimeInfo? State,
    bool Killed,
    int SummonStoneReward,
    bool StateChanged,
    long FirstAttackerPlayerId,
    long LastAttackerPlayerId,
    IReadOnlyDictionary<long, int>? DamageByPlayer,
    bool IsReinforcement = false)
{
    public static MonsterDamageResult None => new(null, false, 0, false, 0, 0, null, false);
}
public readonly record struct MonsterTickResult(IReadOnlyList<MonsterRuntimeInfo> ChangedStates,
    IReadOnlyList<MonsterAttack> Attacks,
    IReadOnlyList<MonsterRuntimeInfo> SpawnedStates,
    IReadOnlyList<MonsterReinforcementRelease> ReinforcementReleases)
{
    public static MonsterTickResult None => new([], [], [], []);
}

public readonly record struct MonsterReinforcementState(
    AreaType Area,
    int PhaseIndex,
    int RemainingBudget,
    int TargetAliveCount,
    int TotalReleased,
    bool IsReleasePending);

public readonly record struct MonsterReinforcementRelease(
    AreaType Area,
    int PhaseIndex,
    int ReleasedCount,
    int RemainingBudget,
    int AliveCountAfterRelease);

public readonly record struct MonsterDensitySample(
    AreaType Area,
    int PhaseIndex,
    int AliveMonsterCount,
    int ReinforcementRemainingBudget,
    int GlobalAliveMonsterCount,
    bool HasAttackableMonster);
