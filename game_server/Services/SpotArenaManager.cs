using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Server-authoritative rules for issue #216. This manager owns only the four-spot
/// ring, marching waves, respawns, and match resolution. Legacy closure, orb, and
/// elimination systems remain outside this state machine.
/// </summary>
public sealed class SpotArenaManager
{
    public const int MatchDurationSeconds = 180;
    public const int SpotMaxHealth = 360;
    public const int WaveMaxHealth = 12;
    public const int WavePlayerDamage = 4;
    public const int WaveSpotDamage = 2;
    public const int WaveSize = 3;
    public const double WaveIntervalSeconds = 8d;
    public const int WaveCapPerDirection = 6;
    public const int WaveTtlSeconds = 30;
    public const int RespawnSeconds = 5;
    public const int RespawnInvulnerabilitySeconds = 2;
    public const float WaveMoveSpeed = 4.5f;
    public const float WaveSpotAttackIntervalSeconds = 1f;
    public const int WaveClashDamage = 1;
    public const float WaveClashRange = 1.5f;
    public const float WaveClashAttackIntervalSeconds = 2f;
    public const float WaveRetaliationSeconds = 2f;
    public const float WaveRetaliationRange = 2.5f;
    public const float BotDefendProximity = 8f;

    private const int FirstWaveMonsterId = 5_000_000;
    private const int FirstSpotMonsterId = 6_000_000;
    private const long FirstWaveCombatTargetId = -2_000_000_000_000_000_000L;
    private const long FirstSpotCombatTargetId = -3_000_000_000_000_000_000L;
    private static readonly (int OrbItemId, int CorridorAnchorNumber)[] SpotDefinitions =
    [
        // 체인 순서는 복도 링의 순환 방향을 따라야 한다. 대각선 배선([3,8,6,2] 등)은 관계
        // 차선이 중간 복도에서 정면으로 겹쳐 웨이브 교전 규칙과 만나 영구 전선이 생긴다.
        (107000010, 3), // Sun: SR3
        (107000020, 2), // Wind: SR2
        (107000030, 8), // Wave: SR8
        (107000040, 6)  // Recovery: SR6
    ];

    private readonly ConcurrentDictionary<long, MatchState> _matches = new();
    private readonly Func<DateTime> _utcNow;

    public SpotArenaManager(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool HasMatching(long matchingId) => _matches.ContainsKey(matchingId);

    public bool InitializeMatching(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerRegistration> registrations,
        DateTime startsAtUtc)
    {
        if (matchingId <= 0 || registrations.Count != 4)
            return false;

        return _matches.TryAdd(matchingId, MatchState.Create(matchingId, registrations, startsAtUtc));
    }

    public SpotArenaSnapshot GetSnapshot(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SpotArenaSnapshot.Empty;

        lock (state.SyncRoot)
            return state.CreateSnapshot(_utcNow());
    }

    public SpotArenaTickResult Tick(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerSpatial> players,
        DateTime? nowUtc = null)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SpotArenaTickResult.Empty;

        DateTime now = nowUtc ?? _utcNow();
        lock (state.SyncRoot)
        {
            if (state.Ended)
                return new SpotArenaTickResult(state.CreateSnapshot(now));

            var result = new SpotArenaTickResult(state.CreateSnapshot(now));
            double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
            state.LastTickAtUtc = now;

            foreach (var respawn in state.Respawns.Values.ToArray())
            {
                if (now < respawn.RespawnAtUtc)
                    continue;

                state.Respawns.Remove(respawn.PlayerId);
                state.InvulnerableUntilUtc[respawn.PlayerId] =
                    now.AddSeconds(RespawnInvulnerabilitySeconds);
                if (state.Spots.TryGetValue(respawn.PlayerId, out var spot) && !spot.Destroyed)
                {
                    result.RespawnedPlayers.Add(new SpotArenaRespawnEvent(
                        respawn.PlayerId,
                        spot.Area,
                        Cell.Clone(spot.Cell),
                        now.AddSeconds(RespawnInvulnerabilitySeconds)));
                }
            }

            if (now >= state.EndsAtUtc)
            {
                ResolveTimeout(state);
                result.Snapshot = state.CreateSnapshot(now);
                result.MatchEnded = true;
                result.WinnerPlayerId = state.WinnerPlayerId;
                return result;
            }

            SpawnDueWaves(state, now, result);

            var engagements = FindWaveEngagements(state);
            var clashDamageByMonsterId = new Dictionary<int, int>();
            var playersById = players.ToDictionary(player => player.PlayerId);
            foreach (var wave in state.Waves.Values.ToArray())
            {
                if (!wave.Alive)
                    continue;

                if (engagements.TryGetValue(wave.MonsterId, out var opponent))
                {
                    wave.ExpiresAtUtc = now.AddSeconds(WaveTtlSeconds);
                    if (now >= wave.NextClashAttackAtUtc)
                    {
                        wave.NextClashAttackAtUtc = now.AddSeconds(WaveClashAttackIntervalSeconds);
                        clashDamageByMonsterId[opponent.MonsterId] =
                            clashDamageByMonsterId.GetValueOrDefault(opponent.MonsterId) + WaveClashDamage;
                        result.WaveClashes.Add(new SpotArenaWaveClashEvent(
                            wave.MonsterId,
                            opponent.MonsterId,
                            wave.OwnerPlayerId,
                            opponent.OwnerPlayerId,
                            wave.Area,
                            WaveClashDamage));
                    }
                    continue;
                }

                if (now >= wave.ExpiresAtUtc)
                {
                    KillWave(wave, result);
                    continue;
                }

                AdvanceWave(wave, deltaSeconds);
                bool stateChanged = deltaSeconds > 0d;

                if (wave.RetaliationPlayerId != 0 &&
                    now < wave.RetaliationUntilUtc &&
                    playersById.TryGetValue(wave.RetaliationPlayerId, out var retaliationTarget) &&
                    retaliationTarget.Area == wave.Area &&
                    !IsRespawning(state, retaliationTarget.PlayerId) &&
                    !IsInvulnerable(state, retaliationTarget.PlayerId, now) &&
                    DistanceSquared(wave.Position, retaliationTarget.Position) <=
                    WaveRetaliationRange * WaveRetaliationRange)
                {
                    if (now >= wave.NextAttackAtUtc)
                    {
                        wave.NextAttackAtUtc = now.AddSeconds(WaveSpotAttackIntervalSeconds);
                        result.PlayerDamage.Add(new SpotArenaPlayerDamage(
                            wave.MonsterId,
                            retaliationTarget.PlayerId,
                            wave.Area,
                            WavePlayerDamage));
                    }
                }
                else if (wave.PathIndex >= wave.Path.Count &&
                         state.Spots.TryGetValue(wave.TargetOwnerPlayerId, out var targetSpot) &&
                         !targetSpot.Destroyed &&
                         now >= wave.NextAttackAtUtc)
                {
                    wave.NextAttackAtUtc = now.AddSeconds(WaveSpotAttackIntervalSeconds);
                    ApplySpotDamageLocked(state, targetSpot, WaveSpotDamage, wave.OwnerPlayerId, result);
                }

                if (stateChanged && wave.Alive)
                    result.ChangedWaves.Add(wave.ToMonsterRuntimeInfo());
            }

            ApplyWaveClashDamage(state, clashDamageByMonsterId, result);

            if (state.AliveSpotCount <= 1 && !state.Ended)
                EndForLastSpot(state);

            result.Snapshot = state.CreateSnapshot(now);
            result.MatchEnded = state.Ended;
            result.WinnerPlayerId = state.WinnerPlayerId;
            return result;
        }
    }

    public SpotArenaDamageResult ApplyWaveDamage(
        long matchingId,
        int monsterId,
        long attackerPlayerId,
        int damage,
        DateTime? nowUtc = null)
    {
        if (damage <= 0 || !_matches.TryGetValue(matchingId, out var state))
            return SpotArenaDamageResult.None;

        DateTime now = nowUtc ?? _utcNow();
        lock (state.SyncRoot)
        {
            var wave = state.Waves.Values.FirstOrDefault(candidate =>
                candidate.MonsterId == monsterId && candidate.Alive);
            if (wave == null)
                return SpotArenaDamageResult.None;

            wave.Health = Math.Max(0, wave.Health - damage);
            wave.RetaliationPlayerId = attackerPlayerId;
            wave.RetaliationUntilUtc = now.AddSeconds(WaveRetaliationSeconds);
            bool killed = wave.Health == 0;
            if (killed)
                wave.Alive = false;

            return new SpotArenaDamageResult(
                true,
                killed,
                wave.ToMonsterRuntimeInfo(),
                wave.OwnerPlayerId,
                wave.TargetOwnerPlayerId);
        }
    }

    public SpotArenaDamageResult ApplySpotDamage(
        long matchingId,
        long spotCombatTargetId,
        long attackerPlayerId,
        int damage)
    {
        if (damage <= 0 || !_matches.TryGetValue(matchingId, out var state))
            return SpotArenaDamageResult.None;

        lock (state.SyncRoot)
        {
            var target = state.Spots.Values.FirstOrDefault(spot =>
                spot.CombatTargetId == spotCombatTargetId && !spot.Destroyed);
            if (target == null || !CanPlayerAttackSpotLocked(state, attackerPlayerId, target.OwnerPlayerId))
                return SpotArenaDamageResult.None;

            var tick = new SpotArenaTickResult(state.CreateSnapshot(_utcNow()));
            ApplySpotDamageLocked(state, target, damage, attackerPlayerId, tick);
            if (state.AliveSpotCount <= 1 && !state.Ended)
                EndForLastSpot(state);

            return new SpotArenaDamageResult(
                true,
                tick.DestroyedSpots.Count > 0,
                null,
                attackerPlayerId,
                target.OwnerPlayerId,
                tick.DestroyedSpots.FirstOrDefault());
        }
    }

    public bool BeginRespawn(long matchingId, long playerId, DateTime? nowUtc = null)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;

        DateTime now = nowUtc ?? _utcNow();
        lock (state.SyncRoot)
        {
            if (!state.Spots.TryGetValue(playerId, out var spot) || spot.Destroyed ||
                state.Respawns.ContainsKey(playerId))
                return false;

            state.Respawns[playerId] = new RespawnState(playerId, now.AddSeconds(RespawnSeconds));
            return true;
        }
    }

    public bool IsRespawning(long matchingId, long playerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
            return IsRespawning(state, playerId);
    }

    public bool IsInvulnerable(long matchingId, long playerId, DateTime? nowUtc = null)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
            return IsInvulnerable(state, playerId, nowUtc ?? _utcNow());
    }

    public bool CanPlayerAttackSpot(long matchingId, long attackerPlayerId, long ownerPlayerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
            return CanPlayerAttackSpotLocked(state, attackerPlayerId, ownerPlayerId);
    }
    public bool TryGetPlayerOrbItemId(long matchingId, long playerId, out int itemId)
    {
        itemId = 0;
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            if (!state.Spots.TryGetValue(playerId, out var spot))
                return false;

            itemId = spot.OrbItemId;
            return itemId > 0;
        }
    }

    public bool CanPlayerAttackWave(
        long matchingId,
        long attackerPlayerId,
        int waveMonsterId,
        long waveOwnerPlayerId,
        long waveTargetOwnerPlayerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            if (!state.Spots.TryGetValue(attackerPlayerId, out var attacker) || attacker.Destroyed)
                return false;

            // 내 좌우 차선의 적 웨이브는 전부 나를 향해 온다. 나를 향하지 않는 웨이브는
            // 무관계 차선이므로 공격할 수 없다.
            return waveTargetOwnerPlayerId == attackerPlayerId;
        }
    }

    public bool CanPlayersFight(long matchingId, long attackerPlayerId, long targetPlayerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            if (IsRespawning(state, attackerPlayerId) || IsRespawning(state, targetPlayerId))
                return false;
            if (!state.Spots.TryGetValue(attackerPlayerId, out var attacker) || attacker.Destroyed ||
                !state.Spots.TryGetValue(targetPlayerId, out var target) || target.Destroyed)
                return false;

            return attacker.TargetPlayerId == targetPlayerId ||
                   attacker.LeftTargetPlayerId == targetPlayerId ||
                   target.TargetPlayerId == attackerPlayerId ||
                   target.LeftTargetPlayerId == attackerPlayerId;
        }
    }

    public bool TryGetSpotTarget(long matchingId, long combatTargetId, out SpotArenaSpotSnapshot spot)
    {
        spot = default;
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            var runtime = state.Spots.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId && !candidate.Destroyed);
            if (runtime == null)
                return false;
            spot = runtime.ToSnapshot();
            return true;
        }
    }

    public bool TryGetWaveByCombatTarget(long matchingId, long combatTargetId, out SpotArenaWaveSnapshot wave)
    {
        wave = default;
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            var runtime = state.Waves.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId && candidate.Alive);
            if (runtime == null)
                return false;
            wave = runtime.ToSnapshot();
            return true;
        }
    }

    public IReadOnlyList<MonsterRuntimeInfo> GetVisualStates(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return [];

        lock (state.SyncRoot)
        {
            return state.Spots.Values.Select(spot => spot.ToMonsterRuntimeInfo())
                .Concat(state.Waves.Values.Select(wave => wave.ToMonsterRuntimeInfo()))
                .ToArray();
        }
    }
    public IReadOnlyList<SpotArenaCombatTarget> GetCombatTargets(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return [];

        lock (state.SyncRoot)
        {
            var targets = new List<SpotArenaCombatTarget>();
            targets.AddRange(state.Waves.Values
                .Where(wave => wave.Alive)
                .Select(wave => new SpotArenaCombatTarget(
                    wave.CombatTargetId,
                    wave.Area,
                    wave.Position,
                    SpotArenaCombatTargetKind.Wave,
                    wave.OwnerPlayerId,
                    wave.TargetOwnerPlayerId,
                    wave.MonsterId)));
            targets.AddRange(state.Spots.Values
                .Where(spot => !spot.Destroyed)
                .Select(spot => new SpotArenaCombatTarget(
                    spot.CombatTargetId,
                    spot.Area,
                    spot.Position,
                    SpotArenaCombatTargetKind.Spot,
                    spot.OwnerPlayerId,
                    spot.OwnerPlayerId,
                    0)));
            return targets;
        }
    }

    public SpotArenaBotDirective GetBotDirective(long matchingId, long playerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SpotArenaBotDirective.None;

        lock (state.SyncRoot)
        {
            if (IsRespawning(state, playerId) ||
                !state.Spots.TryGetValue(playerId, out var ownSpot) ||
                ownSpot.Destroyed)
                return SpotArenaBotDirective.None;

            // 전선이 웨이브를 계속 살려두므로 "들어오는 웨이브 수"는 상시 임계 이상이다.
            // 위협은 수가 아니라 내 스팟까지 밀려든 거리로 판정한다.
            WaveRuntime? nearestThreat = null;
            float nearestThreatSquared = float.MaxValue;
            foreach (var wave in state.Waves.Values)
            {
                if (!wave.Alive || wave.TargetOwnerPlayerId != playerId)
                    continue;

                float distanceSquared = DistanceSquared(wave.Position, ownSpot.Position);
                if (distanceSquared < nearestThreatSquared)
                {
                    nearestThreatSquared = distanceSquared;
                    nearestThreat = wave;
                }
            }

            if (nearestThreat != null &&
                nearestThreatSquared <= BotDefendProximity * BotDefendProximity)
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Defend,
                    nearestThreat.Area,
                    Cell.Clone(nearestThreat.Cell),
                    nearestThreat.Position);
            }

            int rightLaneWaves = state.Waves.Values.Count(wave =>
                wave.Alive &&
                wave.OwnerPlayerId == playerId &&
                wave.TargetOwnerPlayerId == ownSpot.TargetPlayerId);
            int leftLaneWaves = state.Waves.Values.Count(wave =>
                wave.Alive &&
                wave.OwnerPlayerId == playerId &&
                wave.TargetOwnerPlayerId == ownSpot.LeftTargetPlayerId);
            long laneTargetId = leftLaneWaves > rightLaneWaves
                ? ownSpot.LeftTargetPlayerId
                : ownSpot.TargetPlayerId;

            // 가담 지점은 상대 스팟이 아니라 내 차선 선두 웨이브(전선)다.
            WaveRuntime? leadWave = null;
            float leadDistanceSquared = -1f;
            foreach (var wave in state.Waves.Values)
            {
                if (!wave.Alive ||
                    wave.OwnerPlayerId != playerId ||
                    wave.TargetOwnerPlayerId != laneTargetId)
                    continue;

                float distanceSquared = DistanceSquared(wave.Position, ownSpot.Position);
                if (distanceSquared > leadDistanceSquared)
                {
                    leadDistanceSquared = distanceSquared;
                    leadWave = wave;
                }
            }

            if (leadWave != null)
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    leadWave.Area,
                    Cell.Clone(leadWave.Cell),
                    leadWave.Position);
            }

            return new SpotArenaBotDirective(
                SpotArenaBotMode.Return,
                ownSpot.Area,
                Cell.Clone(ownSpot.Cell),
                ownSpot.Position);
        }
    }

    public void RemoveMatching(long matchingId) => _matches.TryRemove(matchingId, out _);

    private static void SpawnDueWaves(MatchState state, DateTime now, SpotArenaTickResult result)
    {
        foreach (var owner in state.Spots.Values.Where(spot => !spot.Destroyed))
        {
            if (now < owner.NextWaveAtUtc)
                continue;

            owner.NextWaveAtUtc = now.AddSeconds(WaveIntervalSeconds);
            SpawnWaveBatch(state, owner, owner.TargetPlayerId, now, result);
            SpawnWaveBatch(state, owner, owner.LeftTargetPlayerId, now, result);
        }
    }

    private static void SpawnWaveBatch(
        MatchState state,
        SpotRuntime owner,
        long targetPlayerId,
        DateTime now,
        SpotArenaTickResult result)
    {
        if (!state.Spots.TryGetValue(targetPlayerId, out var target) || target.Destroyed)
            return;

        int activeLane = state.Waves.Values.Count(wave =>
            wave.Alive &&
            wave.OwnerPlayerId == owner.OwnerPlayerId &&
            wave.TargetOwnerPlayerId == target.OwnerPlayerId);
        int spawnCount = Math.Min(WaveSize, WaveCapPerDirection - activeLane);
        if (spawnCount <= 0)
            return;

        var path = BuildLanePath(owner, target);
        if (path is not { Count: > 0 })
            return;

        for (int index = 0; index < spawnCount; index++)
        {
            int serial = state.NextWaveSerial++;
            var wave = new WaveRuntime
            {
                MonsterId = FirstWaveMonsterId + serial,
                CombatTargetId = FirstWaveCombatTargetId - serial,
                OwnerPlayerId = owner.OwnerPlayerId,
                TargetOwnerPlayerId = target.OwnerPlayerId,
                OrbItemId = owner.OrbItemId,
                Area = owner.Area,
                Cell = Cell.Clone(owner.Cell),
                Position = OffsetSpawn(owner.Position, index),
                Health = WaveMaxHealth,
                Alive = true,
                SpawnedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(WaveTtlSeconds),
                NextAttackAtUtc = now,
                SummonStoneReward = owner.NextWaveGrantsSummonStone ? 1 : 0,
                Path = path
            };
            owner.NextWaveGrantsSummonStone = !owner.NextWaveGrantsSummonStone;
            state.Waves[serial] = wave;
            result.SpawnedWaves.Add(wave.ToMonsterRuntimeInfo());
        }
    }

    // 같은 차선의 양방향 경로는 웨이포인트 그래프 특성상 서로 다른 복도를 탈 수 있다.
    // 마주 보는 웨이브가 같은 복도에서 만나 전선을 만들도록, 차선당 정방향 경로 하나를
    // 기준으로 삼고 반대 방향은 그 경로를 뒤집어 쓴다.
    private static List<BotPathfinder.Step>? BuildLanePath(SpotRuntime owner, SpotRuntime target)
    {
        bool canonical = owner.OwnerPlayerId < target.OwnerPlayerId;
        var origin = canonical ? owner : target;
        var destination = canonical ? target : owner;
        var path = BotPathfinder.FindPath(
            MapId.School,
            origin.Area,
            origin.Cell,
            destination.Area,
            destination.Cell);
        if (path is not { Count: > 0 })
            return null;
        if (canonical)
            return path;

        var reversed = new List<BotPathfinder.Step>(path.Count);
        for (int index = path.Count - 2; index >= 0; index--)
            reversed.Add(new BotPathfinder.Step { Cell = path[index].Cell, Area = path[index].Area });
        reversed.Add(new BotPathfinder.Step { Cell = origin.Cell, Area = origin.Area });
        return reversed;
    }

    private static Dictionary<int, WaveRuntime> FindWaveEngagements(MatchState state)
    {
        var engagements = new Dictionary<int, WaveRuntime>();
        var aliveWaves = state.Waves.Values
            .Where(wave => wave.Alive)
            .OrderBy(wave => wave.MonsterId)
            .ToArray();
        float rangeSquared = WaveClashRange * WaveClashRange;

        foreach (var wave in aliveWaves)
        {
            WaveRuntime? nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (var candidate in aliveWaves)
            {
                if (candidate.MonsterId == wave.MonsterId ||
                    candidate.OwnerPlayerId == wave.OwnerPlayerId ||
                    candidate.Area != wave.Area ||
                    !AreRelatedWaves(wave, candidate))
                {
                    continue;
                }

                float distance = DistanceSquared(wave.Position, candidate.Position);
                if (distance > rangeSquared || distance >= nearestDistance)
                    continue;

                nearest = candidate;
                nearestDistance = distance;
            }

            if (nearest != null)
                engagements[wave.MonsterId] = nearest;
        }

        return engagements;
    }

    // 교전은 같은 차선의 마주 보는 웨이브만 성립한다. 스치는 이웃 차선까지 넓히면
    // 전선이 차선 밖으로 번져 링 전체가 한 덩어리로 엉긴다.
    private static bool AreRelatedWaves(WaveRuntime left, WaveRuntime right) =>
        left.TargetOwnerPlayerId == right.OwnerPlayerId &&
        right.TargetOwnerPlayerId == left.OwnerPlayerId;

    private static void ApplyWaveClashDamage(
        MatchState state,
        IReadOnlyDictionary<int, int> damageByMonsterId,
        SpotArenaTickResult result)
    {
        foreach (var (monsterId, damage) in damageByMonsterId)
        {
            var target = state.Waves.Values.FirstOrDefault(wave =>
                wave.MonsterId == monsterId && wave.Alive);
            if (damage <= 0 || target == null)
                continue;

            target.Health = Math.Max(0, target.Health - damage);
            if (target.Health == 0)
                KillWave(target, result);
            else
                result.ChangedWaves.Add(target.ToMonsterRuntimeInfo());
        }
    }

    private static void AdvanceWave(WaveRuntime wave, double deltaSeconds)
    {
        float remaining = (float)(WaveMoveSpeed * deltaSeconds);
        while (remaining > 0.0001f && wave.PathIndex < wave.Path.Count)
        {
            var step = wave.Path[wave.PathIndex];
            Vector3f target = BotPlayerManager.CellToWorldPosition(MapId.School, step.Cell);
            float dx = target.X - wave.Position.X;
            float dy = target.Y - wave.Position.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= remaining || distance < 0.01f)
            {
                wave.Position = target;
                wave.Cell = Cell.Clone(step.Cell);
                wave.Area = step.Area;
                wave.PathIndex++;
                remaining -= distance;
                continue;
            }

            wave.Position = new Vector3f(
                wave.Position.X + dx / distance * remaining,
                wave.Position.Y + dy / distance * remaining,
                0f);
            remaining = 0f;
        }
    }

    private static void ApplySpotDamageLocked(
        MatchState state,
        SpotRuntime target,
        int damage,
        long sourcePlayerId,
        SpotArenaTickResult result)
    {
        if (target.Destroyed || damage <= 0)
            return;

        target.Health = Math.Max(0, target.Health - damage);
        if (state.Spots.TryGetValue(sourcePlayerId, out var sourceSpot))
            sourceSpot.DamageDealtToTarget += damage;
        if (target.Health > 0)
            return;

        target.Destroyed = true;
        result.DestroyedSpots.Add(new SpotArenaSpotDestroyedEvent(
            target.OwnerPlayerId,
            sourcePlayerId,
            target.Area,
            target.TargetPlayerId));

        // 파괴된 스팟의 웨이브와 그 스팟을 향하던 웨이브(전선 양측)를 모두 해체한다.
        // 이동 중 웨이브를 새 이웃에게 급회전시키지 않는다. 다음 스폰부터 새 링을 쓴다.
        foreach (var wave in state.Waves.Values.Where(wave =>
                     wave.Alive &&
                     (wave.OwnerPlayerId == target.OwnerPlayerId ||
                      wave.TargetOwnerPlayerId == target.OwnerPlayerId)))
            KillWave(wave, result);

        if (state.Spots.TryGetValue(target.LeftTargetPlayerId, out var leftNeighbor) &&
            !leftNeighbor.Destroyed &&
            state.Spots.TryGetValue(target.TargetPlayerId, out var rightNeighbor) &&
            !rightNeighbor.Destroyed &&
            leftNeighbor.OwnerPlayerId != rightNeighbor.OwnerPlayerId)
        {
            leftNeighbor.TargetPlayerId = rightNeighbor.OwnerPlayerId;
            rightNeighbor.LeftTargetPlayerId = leftNeighbor.OwnerPlayerId;
            result.Reconnections.Add(new SpotArenaReconnectEvent(
                leftNeighbor.OwnerPlayerId,
                target.OwnerPlayerId,
                rightNeighbor.OwnerPlayerId));
        }
    }

    private static void KillWave(WaveRuntime wave, SpotArenaTickResult result)
    {
        if (!wave.Alive)
            return;
        wave.Alive = false;
        result.ChangedWaves.Add(wave.ToMonsterRuntimeInfo());
    }

    private static bool CanPlayerAttackSpotLocked(MatchState state, long attackerPlayerId, long ownerPlayerId)
    {
        return state.Spots.TryGetValue(attackerPlayerId, out var attacker) &&
               !attacker.Destroyed &&
               (attacker.TargetPlayerId == ownerPlayerId ||
                attacker.LeftTargetPlayerId == ownerPlayerId);
    }

    private static bool IsRespawning(MatchState state, long playerId) =>
        state.Respawns.ContainsKey(playerId);

    private static bool IsInvulnerable(MatchState state, long playerId, DateTime now) =>
        state.InvulnerableUntilUtc.TryGetValue(playerId, out var until) && now < until;

    private static void EndForLastSpot(MatchState state)
    {
        state.Ended = true;
        state.WinnerPlayerId = state.Spots.Values
            .Where(spot => !spot.Destroyed)
            .Select(spot => spot.OwnerPlayerId)
            .FirstOrDefault();
    }

    private static void ResolveTimeout(MatchState state)
    {
        state.Ended = true;
        var survivingSpots = state.Spots.Values
            .Where(spot => !spot.Destroyed)
            .ToList();

        if (survivingSpots.Count == 0)
        {
            state.WinnerPlayerId = 0;
            return;
        }

        int highestHealth = survivingSpots.Max(spot => spot.Health);
        var healthLeaders = survivingSpots
            .Where(spot => spot.Health == highestHealth)
            .ToList();
        long highestTargetDamage = healthLeaders.Max(spot => spot.DamageDealtToTarget);
        var finalists = healthLeaders
            .Where(spot => spot.DamageDealtToTarget == highestTargetDamage)
            .ToList();

        state.WinnerPlayerId = finalists.Count == 1
            ? finalists[0].OwnerPlayerId
            : 0;
    }

    private static Vector3f OffsetSpawn(Vector3f source, int index)
    {
        return index switch
        {
            0 => new Vector3f(source.X - 0.35f, source.Y, 0f),
            1 => new Vector3f(source.X + 0.35f, source.Y, 0f),
            _ => new Vector3f(source.X, source.Y + 0.25f, 0f)
        };
    }

    private static float DistanceSquared(Vector3f left, Vector3f right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private sealed class MatchState
    {
        public object SyncRoot { get; } = new();
        public long MatchingId { get; init; }
        public DateTime StartsAtUtc { get; init; }
        public DateTime EndsAtUtc { get; init; }
        public DateTime LastTickAtUtc { get; set; }
        public Dictionary<long, SpotRuntime> Spots { get; } = new();
        public Dictionary<int, WaveRuntime> Waves { get; } = new();
        public Dictionary<long, RespawnState> Respawns { get; } = new();
        public Dictionary<long, DateTime> InvulnerableUntilUtc { get; } = new();
        public int NextWaveSerial { get; set; }
        public bool Ended { get; set; }
        public long WinnerPlayerId { get; set; }
        public int AliveSpotCount => Spots.Values.Count(spot => !spot.Destroyed);

        public static MatchState Create(
            long matchingId,
            IReadOnlyCollection<SpotArenaPlayerRegistration> registrations,
            DateTime startsAtUtc)
        {
            var state = new MatchState
            {
                MatchingId = matchingId,
                StartsAtUtc = startsAtUtc,
                EndsAtUtc = startsAtUtc.AddSeconds(MatchDurationSeconds),
                LastTickAtUtc = startsAtUtc
            };

            var orderedRegistrations = registrations.ToList();
            for (int index = 0; index < orderedRegistrations.Count; index++)
            {
                var registration = orderedRegistrations[index];
                var spotDefinition = SpotDefinitions[index % SpotDefinitions.Length];
                Cell spotCell = SurvivorRoyaleSpawnData.GetCorridorAnchor(
                    spotDefinition.CorridorAnchorNumber);
                long leftPlayerId = orderedRegistrations[
                    (index - 1 + orderedRegistrations.Count) % orderedRegistrations.Count].PlayerId;
                state.Spots[registration.PlayerId] = new SpotRuntime
                {
                    MonsterId = FirstSpotMonsterId + index,
                    CombatTargetId = FirstSpotCombatTargetId - index,
                    OwnerPlayerId = registration.PlayerId,
                    TargetPlayerId = registration.TargetPlayerId,
                    LeftTargetPlayerId = leftPlayerId,
                    OrbItemId = spotDefinition.OrbItemId,
                    Area = AreaType.Corridor,
                    Cell = spotCell,
                    Position = BotPlayerManager.CellToWorldPosition(MapId.School, spotCell),
                    Health = SpotMaxHealth,
                    NextWaveAtUtc = startsAtUtc.AddSeconds(WaveIntervalSeconds)
                };
            }

            return state;
        }

        public SpotArenaSnapshot CreateSnapshot(DateTime now)
        {
            return new SpotArenaSnapshot(
                MatchingId,
                Math.Max(0, (int)Math.Ceiling((EndsAtUtc - now).TotalSeconds)),
                Spots.Values.Select(spot => spot.ToSnapshot()).OrderBy(spot => spot.OwnerPlayerId).ToArray(),
                Waves.Values.Where(wave => wave.Alive).Select(wave => wave.ToSnapshot()).ToArray(),
                Respawns.ToDictionary(
                    pair => pair.Key,
                    pair => Math.Max(0, (int)Math.Ceiling((pair.Value.RespawnAtUtc - now).TotalSeconds))),
                Ended,
                WinnerPlayerId);
        }
    }

    private sealed class SpotRuntime
    {
        public int MonsterId { get; init; }
        public long CombatTargetId { get; init; }
        public long OwnerPlayerId { get; init; }
        public long TargetPlayerId { get; set; }
        public long LeftTargetPlayerId { get; set; }
        public int OrbItemId { get; init; }
        public AreaType Area { get; init; }
        public Cell Cell { get; init; } = new(0, 0);
        public Vector3f Position { get; init; } = new(0f, 0f, 0f);
        public int Health { get; set; }
        public int DamageDealtToTarget { get; set; }
        public DateTime NextWaveAtUtc { get; set; }
        public bool NextWaveGrantsSummonStone { get; set; } = true;
        public bool Destroyed { get; set; }

        public MonsterRuntimeInfo ToMonsterRuntimeInfo() => new()
        {
            MonsterId = MonsterId,
            AreaType = Area,
            PositionX = Position.X,
            PositionY = Position.Y,
            MaxHealth = SpotMaxHealth,
            CurrentHealth = Health,
            IsAlive = !Destroyed,
            RewardItemId = OrbItemId,
            IsCore = true,
            SummonStoneReward = 0
        };

        public SpotArenaSpotSnapshot ToSnapshot() => new(
            MonsterId,
            CombatTargetId,
            OwnerPlayerId,
            TargetPlayerId,
            Area,
            Cell.Clone(Cell),
            Position,
            Health,
            SpotMaxHealth,
            Destroyed);
    }

    private sealed class WaveRuntime
    {
        public int MonsterId { get; init; }
        public long CombatTargetId { get; init; }
        public long OwnerPlayerId { get; init; }
        public long TargetOwnerPlayerId { get; set; }
        public int OrbItemId { get; init; }
        public AreaType Area { get; set; }
        public Cell Cell { get; set; } = new(0, 0);
        public Vector3f Position { get; set; } = new(0f, 0f, 0f);
        public int Health { get; set; }
        public bool Alive { get; set; }
        public DateTime SpawnedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime NextAttackAtUtc { get; set; }
        public DateTime NextClashAttackAtUtc { get; set; }
        public int SummonStoneReward { get; init; }
        public long RetaliationPlayerId { get; set; }
        public DateTime RetaliationUntilUtc { get; set; }
        public List<BotPathfinder.Step> Path { get; set; } = new();
        public int PathIndex { get; set; }

        public MonsterRuntimeInfo ToMonsterRuntimeInfo() => new()
        {
            MonsterId = MonsterId,
            AreaType = Area,
            PositionX = Position.X,
            PositionY = Position.Y,
            MaxHealth = WaveMaxHealth,
            CurrentHealth = Health,
            IsAlive = Alive,
            RewardItemId = OrbItemId,
            IsCore = false,
            SummonStoneReward = SummonStoneReward
        };

        public SpotArenaWaveSnapshot ToSnapshot() => new(
            MonsterId,
            CombatTargetId,
            OwnerPlayerId,
            TargetOwnerPlayerId,
            Area,
            Position,
            Health,
            Alive);
    }

    private readonly record struct RespawnState(long PlayerId, DateTime RespawnAtUtc);
}

public readonly record struct SpotArenaPlayerRegistration(
    long PlayerId,
    long TargetPlayerId,
    AreaType Area,
    Cell Cell);

public readonly record struct SpotArenaPlayerSpatial(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct SpotArenaSpotSnapshot(
    int MonsterId,
    long CombatTargetId,
    long OwnerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    Cell Cell,
    Vector3f Position,
    int Health,
    int MaxHealth,
    bool Destroyed);

public readonly record struct SpotArenaWaveSnapshot(
    int MonsterId,
    long CombatTargetId,
    long OwnerPlayerId,
    long TargetOwnerPlayerId,
    AreaType Area,
    Vector3f Position,
    int Health,
    bool Alive);

public readonly record struct SpotArenaSnapshot(
    long MatchingId,
    int RemainingSeconds,
    IReadOnlyList<SpotArenaSpotSnapshot> Spots,
    IReadOnlyList<SpotArenaWaveSnapshot> Waves,
    IReadOnlyDictionary<long, int> RespawnSecondsByPlayer,
    bool Ended,
    long WinnerPlayerId)
{
    public static SpotArenaSnapshot Empty => new(
        0, 0, Array.Empty<SpotArenaSpotSnapshot>(), Array.Empty<SpotArenaWaveSnapshot>(),
        new Dictionary<long, int>(), false, 0);
}

public sealed class SpotArenaTickResult
{
    public static SpotArenaTickResult Empty => new(SpotArenaSnapshot.Empty);

    public SpotArenaTickResult(SpotArenaSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public SpotArenaSnapshot Snapshot { get; set; }
    public List<MonsterRuntimeInfo> SpawnedWaves { get; } = new();
    public List<MonsterRuntimeInfo> ChangedWaves { get; } = new();
    public List<SpotArenaWaveClashEvent> WaveClashes { get; } = new();
    public List<SpotArenaPlayerDamage> PlayerDamage { get; } = new();
    public List<SpotArenaRespawnEvent> RespawnedPlayers { get; } = new();
    public List<SpotArenaSpotDestroyedEvent> DestroyedSpots { get; } = new();
    public List<SpotArenaReconnectEvent> Reconnections { get; } = new();
    public bool MatchEnded { get; set; }
    public long WinnerPlayerId { get; set; }
}

public readonly record struct SpotArenaDamageResult(
    bool StateChanged,
    bool DestroyedOrKilled,
    MonsterRuntimeInfo? WaveState,
    long SourcePlayerId,
    long TargetOwnerPlayerId,
    SpotArenaSpotDestroyedEvent? DestroyedSpot = null)
{
    public static SpotArenaDamageResult None => new(false, false, null, 0, 0);
}

public readonly record struct SpotArenaWaveClashEvent(
    int AttackerMonsterId,
    int TargetMonsterId,
    long AttackerOwnerPlayerId,
    long TargetOwnerPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct SpotArenaPlayerDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct SpotArenaRespawnEvent(
    long PlayerId,
    AreaType Area,
    Cell Cell,
    DateTime InvulnerableUntilUtc);

public readonly record struct SpotArenaSpotDestroyedEvent(
    long OwnerPlayerId,
    long DestroyedByPlayerId,
    AreaType Area,
    long PreviousTargetPlayerId);

public readonly record struct SpotArenaReconnectEvent(
    long PredatorPlayerId,
    long RemovedPlayerId,
    long NewTargetPlayerId);

public enum SpotArenaCombatTargetKind
{
    Wave,
    Spot
}

public readonly record struct SpotArenaCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    SpotArenaCombatTargetKind Kind,
    long OwnerPlayerId,
    long TargetOwnerPlayerId,
    int MonsterId);

public enum SpotArenaBotMode
{
    None,
    Defend,
    Escort,
    Return
}

public readonly record struct SpotArenaBotDirective(
    SpotArenaBotMode Mode,
    AreaType DestinationArea,
    Cell DestinationCell,
    Vector3f DestinationPosition)
{
    public static SpotArenaBotDirective None => new(
        SpotArenaBotMode.None, AreaType.None, new Cell(0, 0), new Vector3f(0f, 0f, 0f));
}
