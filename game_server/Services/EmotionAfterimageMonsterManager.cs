using System.Collections.Concurrent;
using network.common;
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
    private static readonly TimeSpan AmbientCorridorSpawnInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AmbientCorridorDespawnDelay = TimeSpan.FromSeconds(4);
    private const float AmbientCorridorSafeSpawnDistance = 3f;
    private readonly ConcurrentDictionary<long, MatchingMonsterState> _matchingStates = new();
    private Action<long>? _matchingStateRemoved;

    public void InitializeMatching(long matchingId)
    {
        if (matchingId <= 0) return;
        _matchingStates.GetOrAdd(matchingId,
            id => new MatchingMonsterState(EmotionAfterimageMonsterSpawnData.CreateDefinitionsForMatching(id)));
    }

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

    public IReadOnlyList<MonsterCombatTarget> GetAliveTargets(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state) ? state.GetAliveTargets() : [];

    public bool HasAliveMonsterInArea(long matchingId, AreaType area) =>
        _matchingStates.TryGetValue(matchingId, out var state) && state.HasAliveMonsterInArea(area);

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

    /// <summary>
    /// A wave is consumed only when a new area actually reaches the closed state.
    /// Closing an area despawns its live monsters without rewards; only empty nodes
    /// in still-open areas may be filled.
    /// </summary>
    public bool ApplyAreaClosureAndSpawnWave(long matchingId, IReadOnlyCollection<AreaType> closedAreas) =>
        closedAreas.Count > 0 && _matchingStates.TryGetValue(matchingId, out var state)
        && state.ApplyAreaClosureAndSpawnWave(closedAreas);

    private sealed class MatchingMonsterState
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, MonsterState> _monsters;
        private readonly HashSet<AreaType> _closedAreas = [];
        private int _waveIndex;
        private DateTime _nextAmbientCorridorSpawnAtUtc = DateTime.MinValue;

        public MatchingMonsterState(IEnumerable<MonsterDefinition> definitions)
        {
            _monsters = definitions.ToDictionary(definition => definition.MonsterId,
                definition => new MonsterState(definition));
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

        public MonsterDamageResult ApplyDamage(int monsterId, long attackerPlayerId, int damage, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (!_monsters.TryGetValue(monsterId, out var state) || !state.IsAlive)
                    return MonsterDamageResult.None;

                state.LastDamagedAtUtc = nowUtc;
                state.LastAttackerPlayerId = attackerPlayerId;
                state.DamageByPlayer.TryGetValue(attackerPlayerId, out int accumulatedDamage);
                state.DamageByPlayer[attackerPlayerId] = accumulatedDamage + damage;
                state.CurrentHealth = Math.Max(0, state.CurrentHealth - damage);
                if (state.CurrentHealth > 0)
                    return new MonsterDamageResult(ToRuntimeInfo(state), false, 0, true);

                state.IsAlive = false;
                state.NextAttackAtUtc = DateTime.MaxValue;
                return new MonsterDamageResult(ToRuntimeInfo(state), true,
                    state.Definition.SummonStoneReward, true);
            }
        }

        public bool ApplyAreaClosureAndSpawnWave(IReadOnlyCollection<AreaType> closedAreas)
        {
            lock (_sync)
            {
                bool addedClosure = false;
                foreach (var area in closedAreas) addedClosure |= _closedAreas.Add(area);
                if (!addedClosure) return false;

                bool changed = false;
                foreach (var state in _monsters.Values.Where(state => state.IsAlive && !state.Definition.IsAmbientCorridor && _closedAreas.Contains(state.Definition.Area)))
                {
                    state.IsAlive = false;
                    state.NextAttackAtUtc = DateTime.MaxValue;
                    changed = true;
                }

                int spawnBudget = EmotionAfterimageMonsterSpawnData.GetWavePackSpawnBudget(_waveIndex++);
                if (spawnBudget == 0) return changed;

                var aliveByArea = _monsters.Values.Where(state => state.IsAlive && !state.Definition.IsAmbientCorridor)
                    .GroupBy(state => state.Definition.Area)
                    .ToDictionary(group => group.Key, group => group.Count());
                var dormantPacks = _monsters.Values
                    .Where(state => !state.Definition.IsAmbientCorridor && !_closedAreas.Contains(state.Definition.Area))
                    .GroupBy(state => state.Definition.ClusterId)
                    .Where(pack => pack.All(state => !state.IsAlive))
                    .OrderBy(pack => pack.Min(state => state.Definition.SpawnPriority))
                    .ThenBy(pack => pack.Min(state => state.Definition.MonsterId));
                foreach (var pack in dormantPacks)
                {
                    if (spawnBudget <= 0) break;
                    var first = pack.First();
                    int packSize = pack.Count();
                    AreaType area = first.Definition.Area;
                    aliveByArea.TryGetValue(area, out int aliveCount);
                    if (aliveCount + packSize > first.Definition.AreaAliveLimit) continue;

                    foreach (var state in pack)
                        state.ActivateAtHome();
                    aliveByArea[area] = aliveCount + packSize;
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
                        if (state.CurrentHealth < state.Definition.MaxHealth && nowUtc - state.LastDamagedAtUtc >= ResetDelay)
                        {
                            state.CurrentHealth = state.Definition.MaxHealth;
                            state.LastAttackerPlayerId = 0;
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
                        state.Definition.AttackDamage));
                }
                return new MonsterTickResult(changed, attacks);
            }
        }

        private int SpawnAmbientCorridorMonster(IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc,
            ICollection<MonsterRuntimeInfo> changed)
        {
            if (nowUtc < _nextAmbientCorridorSpawnAtUtc)
                return 0;

            var corridorTargets = possibleTargets
                .Where(target => target.MapId == MapId.School && target.Area == AreaType.Corridor)
                .ToList();
            if (corridorTargets.Count == 0)
                return 0;

            int aliveCount = _monsters.Values.Count(state => state.IsAlive && state.Definition.IsAmbientCorridor);
            if (aliveCount >= 3)
                return 0;

            var candidate = _monsters.Values
                .Where(state => !state.IsAlive && state.Definition.IsAmbientCorridor)
                .OrderBy(state => state.Definition.MonsterId)
                .FirstOrDefault(state => corridorTargets.All(target =>
                    DistanceSquared(state.Definition.Position, target.Position) >=
                    AmbientCorridorSafeSpawnDistance * AmbientCorridorSafeSpawnDistance));
            _nextAmbientCorridorSpawnAtUtc = nowUtc + AmbientCorridorSpawnInterval;
            if (candidate == null)
                return 0;

            candidate.ActivateAtHome();
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
            int selectedDamage = state.DamageByPlayer.GetValueOrDefault(selected.PlayerId);
            float selectedDistance = DistanceSquared(state.Definition.Position, selected.Position);

            for (int index = 1; index < targets.Count; index++)
            {
                var candidate = targets[index];
                int candidateDamage = state.DamageByPlayer.GetValueOrDefault(candidate.PlayerId);
                if (candidateDamage < selectedDamage)
                    continue;

                float candidateDistance = DistanceSquared(state.Definition.Position, candidate.Position);
                if (candidateDamage == selectedDamage && candidateDistance > selectedDistance)
                    continue;
                if (candidateDamage == selectedDamage && candidateDistance.Equals(selectedDistance) &&
                    candidate.PlayerId >= selected.PlayerId)
                {
                    continue;
                }

                selected = candidate;
                selectedDamage = candidateDamage;
                selectedDistance = candidateDistance;
            }

            return selected;
        }

        private static bool IsEscort(MonsterDefinition definition) =>
            !definition.IsAmbientCorridor && !definition.IsCore &&
            definition.ClusterMemberIndex < definition.ClusterSize - 3;

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
            float seconds = (float)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
            float angle = seconds * (0.85f + state.Definition.ClusterMemberIndex * 0.03f) +
                          state.Definition.ClusterId * 0.73f + state.Definition.ClusterMemberIndex * 1.17f;
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
            MaxHealth = state.Definition.MaxHealth,
            CurrentHealth = state.CurrentHealth,
            IsAlive = state.IsAlive,
            RewardItemId = state.Definition.RewardItemId,
            IsCore = state.Definition.IsCore,
            SummonStoneReward = state.Definition.SummonStoneReward
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
        public int CurrentHealth { get; set; } = definition.MaxHealth;
        public bool IsAlive { get; set; } = definition.StartsActive;
        public long LastAttackerPlayerId { get; set; }
        public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime NextAttackAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastTargetSeenAtUtc { get; set; } = DateTime.MinValue;
        public Dictionary<long, int> DamageByPlayer { get; } = new();

        public void ActivateAtHome()
        {
            Position = new Vector3f(
                Definition.Position.X + Definition.FormationOffset.X,
                Definition.Position.Y + Definition.FormationOffset.Y,
                Definition.Position.Z);
            CurrentHealth = Definition.MaxHealth;
            IsAlive = true;
            LastAttackerPlayerId = 0;
            LastDamagedAtUtc = DateTime.MinValue;
            NextAttackAtUtc = DateTime.MinValue;
            LastUpdatedAtUtc = DateTime.MinValue;
            LastTargetSeenAtUtc = DateTime.MinValue;
            DamageByPlayer.Clear();
        }

        public void Deactivate()
        {
            IsAlive = false;
            NextAttackAtUtc = DateTime.MaxValue;
            LastTargetSeenAtUtc = DateTime.MinValue;
            DamageByPlayer.Clear();
        }
    }
}

public readonly record struct MonsterDefinition(int MonsterId, MapId MapId, AreaType Area, Vector3f Position,
    int MaxHealth, int AttackDamage, float AttackRange, float AttackIntervalSeconds, int RewardItemId,
    bool IsCore, int SummonStoneReward, float MoveSpeed, float LeashRange, int AreaAliveLimit,
    bool StartsActive, int SpawnPriority, int ClusterId, int ClusterMemberIndex, int ClusterSize,
    Vector3f FormationOffset, bool IsAmbientCorridor = false);

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
public readonly record struct MonsterSpatialTarget(long PlayerId, MapId MapId, AreaType Area, Vector3f Position);
public readonly record struct MonsterAttack(int MonsterId, long TargetPlayerId, AreaType Area, int Damage);
public readonly record struct MonsterDamageResult(MonsterRuntimeInfo? State, bool Killed, int SummonStoneReward,
    bool StateChanged)
{
    public static MonsterDamageResult None => new(null, false, 0, false);
}
public readonly record struct MonsterTickResult(IReadOnlyList<MonsterRuntimeInfo> ChangedStates,
    IReadOnlyList<MonsterAttack> Attacks)
{
    public static MonsterTickResult None => new([], []);
}