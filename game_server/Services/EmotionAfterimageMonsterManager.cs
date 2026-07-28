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
    private static readonly MonsterDefinition[] Definitions = EmotionAfterimageMonsterSpawnData.Definitions;
    private static readonly TimeSpan ResetDelay = TimeSpan.FromSeconds(6);
    private readonly ConcurrentDictionary<long, MatchingMonsterState> _matchingStates = new();

    public void InitializeMatching(long matchingId)
    {
        if (matchingId <= 0) return;
        _matchingStates.GetOrAdd(matchingId, _ => new MatchingMonsterState(Definitions));
    }

    public void RemoveMatchingState(long matchingId) => _matchingStates.TryRemove(matchingId, out _);

    public IReadOnlyList<MonsterRuntimeInfo> GetSnapshot(long matchingId) =>
        _matchingStates.TryGetValue(matchingId, out var state) ? state.GetSnapshot() : [];

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

        public IReadOnlyList<MonsterCombatTarget> GetAliveTargets()
        {
            lock (_sync)
                return _monsters.Values.Where(state => state.IsAlive).OrderBy(state => state.Definition.MonsterId)
                    .Select(state => new MonsterCombatTarget(state.Definition.MonsterId, state.Definition.MapId,
                        state.Definition.Area, state.Position)).ToList();
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
                return new MonsterDamageResult(ToRuntimeInfo(state), true, state.Definition.RewardItemId, true);
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
                foreach (var state in _monsters.Values.Where(state => state.IsAlive && _closedAreas.Contains(state.Definition.Area)))
                {
                    state.IsAlive = false;
                    state.NextAttackAtUtc = DateTime.MaxValue;
                    changed = true;
                }

                int spawnBudget = EmotionAfterimageMonsterSpawnData.GetWaveSpawnBudget(_waveIndex++);
                if (spawnBudget == 0) return changed;

                var aliveByArea = _monsters.Values.Where(state => state.IsAlive)
                    .GroupBy(state => state.Definition.Area)
                    .ToDictionary(group => group.Key, group => group.Count());
                foreach (var state in _monsters.Values
                             .Where(state => !state.IsAlive && !_closedAreas.Contains(state.Definition.Area))
                             .OrderBy(state => state.Definition.SpawnPriority)
                             .ThenBy(state => state.Definition.MonsterId))
                {
                    if (spawnBudget <= 0) break;
                    aliveByArea.TryGetValue(state.Definition.Area, out int aliveCount);
                    if (aliveCount >= state.Definition.AreaAliveLimit) continue;

                    state.ActivateAtHome();
                    aliveByArea[state.Definition.Area] = aliveCount + 1;
                    spawnBudget--;
                    changed = true;
                }
                return changed;
            }
        }

        public MonsterTickResult Tick(IReadOnlyList<MonsterSpatialTarget> possibleTargets, DateTime nowUtc)
        {
            lock (_sync)
            {
                var changed = new List<MonsterRuntimeInfo>();
                var attacks = new List<MonsterAttack>();
                foreach (var state in _monsters.Values)
                {
                    if (!state.IsAlive) continue;

                    float elapsedSeconds = (float)(nowUtc - state.LastUpdatedAtUtc).TotalSeconds;
                    state.LastUpdatedAtUtc = nowUtc;
                    elapsedSeconds = Math.Clamp(elapsedSeconds, 0f, 0.1f);
                    var targets = possibleTargets
                        .Where(target => target.MapId == state.Definition.MapId && target.Area == state.Definition.Area)
                        .Where(target => IsWithinRange(state.Definition.Position, target.Position, state.Definition.LeashRange))
                        .Where(target => state.DamageByPlayer.ContainsKey(target.PlayerId))
                        .OrderByDescending(target => state.DamageByPlayer[target.PlayerId])
                        .ThenBy(target => DistanceSquared(state.Definition.Position, target.Position))
                        .ThenBy(target => target.PlayerId).ToList();
                    if (targets.Count == 0)
                    {
                        bool movedHome = MoveTowards(state, state.Definition.Position, elapsedSeconds);
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

                    var target = targets[0];
                    bool moved = MoveTowards(state, target.Position, elapsedSeconds);
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

        private static MonsterRuntimeInfo ToRuntimeInfo(MonsterState state) => new()
        {
            MonsterId = state.Definition.MonsterId,
            AreaType = state.Definition.Area,
            PositionX = state.Position.X,
            PositionY = state.Position.Y,
            MaxHealth = state.Definition.MaxHealth,
            CurrentHealth = state.CurrentHealth,
            IsAlive = state.IsAlive,
            RewardItemId = state.Definition.RewardItemId
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
        public Vector3f Position { get; set; } = definition.Position;
        public int CurrentHealth { get; set; } = definition.MaxHealth;
        public bool IsAlive { get; set; } = definition.StartsActive;
        public long LastAttackerPlayerId { get; set; }
        public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime NextAttackAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.MinValue;
        public Dictionary<long, int> DamageByPlayer { get; } = new();

        public void ActivateAtHome()
        {
            Position = Definition.Position;
            CurrentHealth = Definition.MaxHealth;
            IsAlive = true;
            LastAttackerPlayerId = 0;
            LastDamagedAtUtc = DateTime.MinValue;
            NextAttackAtUtc = DateTime.MinValue;
            LastUpdatedAtUtc = DateTime.MinValue;
            DamageByPlayer.Clear();
        }
    }
}

public readonly record struct MonsterDefinition(int MonsterId, MapId MapId, AreaType Area, Vector3f Position,
    int MaxHealth, int AttackDamage, float AttackRange, float AttackIntervalSeconds, int RewardItemId,
    float MoveSpeed, float LeashRange, int AreaAliveLimit, bool StartsActive, int SpawnPriority);

public readonly record struct MonsterCombatTarget(int MonsterId, MapId MapId, AreaType Area, Vector3f Position);
public readonly record struct MonsterSpatialTarget(long PlayerId, MapId MapId, AreaType Area, Vector3f Position);
public readonly record struct MonsterAttack(int MonsterId, long TargetPlayerId, AreaType Area, int Damage);
public readonly record struct MonsterDamageResult(MonsterRuntimeInfo? State, bool Killed, int RewardItemId, bool StateChanged)
{
    public static MonsterDamageResult None => new(null, false, 0, false);
}
public readonly record struct MonsterTickResult(IReadOnlyList<MonsterRuntimeInfo> ChangedStates,
    IReadOnlyList<MonsterAttack> Attacks)
{
    public static MonsterTickResult None => new([], []);
}