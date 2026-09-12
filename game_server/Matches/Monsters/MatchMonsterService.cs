using network.common.data;

namespace game_server.matches.monsters;

internal sealed class MatchMonsterService(MonsterSupplyService supply, MonsterMovementService movement)
{
    public const float ContactRange = 0.32f;
    private const long CombatTargetIdUpperBound = -1_000_000_000_000L;
    private const double DeadPruneAfterSeconds = 3d;
    private const double EscalationStage1AtSeconds = 120d;
    private const double EscalationStage2AtSeconds = 230d;
    private const float EscalationStage2MoveSpeedMultiplier = 1.1f;

    public static float ContactImmunitySeconds => SwarmConfigData.GetFloat("SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS", 0.6f);
    public static float BowlerSplashRadius => SwarmConfigData.GetFloat("SWARM_MONSTER_BOWLER_SPLASH_RADIUS", 1.5f);
    public static float WaveInsigniaSplashRadius => SwarmConfigData.GetFloat("SWARM_MONSTER_WAVE_SPLASH_RADIUS", 2.2f);

    public static bool HasWaveInsignia(MonsterInsignia insignia) => insignia == MonsterInsignia.Wave;
    public static float GetContactRadius(MonsterKind kind) => ContactRange * (SwarmMonsterData.Get((int)kind)?.ContactRadiusScale ?? 1f);
    public static bool IsCombatTargetId(long actorId) => actorId < CombatTargetIdUpperBound;
    internal static bool IsPlayerOrbless(MatchRuntime runtime, long playerId) => !runtime.GetOrbs(playerId).HasAnyOrb();
    private static int GetEscalationStage(double elapsedSeconds) => elapsedSeconds >= EscalationStage2AtSeconds ? 2 : elapsedSeconds >= EscalationStage1AtSeconds ? 1 : 0;

    public MonsterTickResult Tick(MatchRuntime runtime, IReadOnlyCollection<PlayerPositionSnapshot> participants, bool isGameplayActive, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var result = new MonsterTickResult();
        var state = runtime.Monsters;
        if (!state.HasMatching())
        {
            return result;
        }

        var now = nowUtc;
        double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
        state.LastTickAtUtc = now;
        state.LastParticipants = participants.ToArray();

        bool preMatch = !isGameplayActive;
        double moveDeltaSeconds = GetEscalationStage((now - state.StartsAtUtc).TotalSeconds) >= 2
            ? deltaSeconds * EscalationStage2MoveSpeedMultiplier
            : deltaSeconds;

        supply.ProcessRegionSupply(runtime, state, now, result, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || now < monster.ActivatesAtUtc)
            {
                continue;
            }

            movement.UpdateSupplyMonsterMovement(state, monster, state.LastParticipants, now, moveDeltaSeconds, preMatch, playerId => IsPlayerOrbless(runtime, playerId));
            movement.RescueMonsterFromBlockedCell(monster);
            if (now < monster.NextContactAtUtc)
            {
                continue;
            }

            float attackRange = monster.Aggro && monster.AttackRangeValue > ContactRange ? monster.AttackRangeValue : GetContactRadius(monster.Kind);
            const float verticalScale = 2f;
            foreach (var participant in state.LastParticipants)
            {
                if (participant.Area != monster.Area)
                {
                    continue;
                }
                float dx = monster.Position.X - participant.Position.X;
                float dy = (monster.Position.Y - participant.Position.Y) * verticalScale;
                if (dx * dx + dy * dy > attackRange * attackRange)
                {
                    continue;
                }

                if (state.ContactImmuneUntilUtc.TryGetValue(participant.PlayerId, out var immuneUntil) && now < immuneUntil)
                {
                    continue;
                }

                monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
                state.ContactImmuneUntilUtc[participant.PlayerId] = now.AddSeconds(ContactImmunitySeconds);
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = participant.PlayerId;
                if (participant.PlayerId == state.HumanPlayerId)
                {
                    state.HitsTaken++;
                    state.InsigniaHits[monster.Insignia] = state.InsigniaHits.GetValueOrDefault(monster.Insignia) + 1;
                }

                result.PlayerDamage.Add(new MonsterContactDamage(monster.MonsterId, participant.PlayerId, monster.Area, monster.ContactDamageValue));
                bool waveInsignia = HasWaveInsignia(monster.Insignia);
                if (waveInsignia || monster.Kind == MonsterKind.Bowler)
                {
                    float splashRadius = waveInsignia ? WaveInsigniaSplashRadius : BowlerSplashRadius;
                    foreach (var splashed in state.LastParticipants)
                    {
                        if (splashed.PlayerId == participant.PlayerId || splashed.Area != monster.Area)
                        {
                            continue;
                        }
                        float sx = splashed.Position.X - participant.Position.X;
                        float sy = splashed.Position.Y - participant.Position.Y;
                        if (sx * sx + sy * sy > splashRadius * splashRadius)
                        {
                            continue;
                        }

                        if (state.ContactImmuneUntilUtc.TryGetValue(splashed.PlayerId, out var splashImmune) && now < splashImmune)
                        {
                            continue;
                        }
                        state.ContactImmuneUntilUtc[splashed.PlayerId] = now.AddSeconds(ContactImmunitySeconds);
                        result.PlayerDamage.Add(new MonsterContactDamage(monster.MonsterId, splashed.PlayerId, monster.Area, monster.ContactDamageValue));
                    }
                }
                break;
            }
        }
        var expired = new List<int>();
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive && (now - monster.DiedAtUtc).TotalSeconds > DeadPruneAfterSeconds)
            {
                expired.Add(monster.MonsterId);
            }
        }
        foreach (int monsterId in expired)
        {
            state.Entities.Remove(monsterId);
        }
        return result;
    }

    public MonsterDamageResult ApplyMonsterDamage(MatchRuntime runtime, long combatTargetId, long attackerPlayerId, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (damage <= 0 || !state.HasMatching())
        {
            return MonsterDamageResult.None;
        }

        var monster = state.Entities.Values.FirstOrDefault(candidate => candidate.CombatTargetId == combatTargetId);
        if (monster != null)
        {
            monster.PendingDamage = Math.Max(0, monster.PendingDamage - damage);
        }

        if (monster is not { Alive: true })
        {
            return MonsterDamageResult.None;
        }

        monster.Aggro = true;
        if (monster.OwnerPlayerId == 0)
        {
            monster.ChaseTargetPlayerId = attackerPlayerId;
        }
        foreach (var mate in state.Entities.Values)
        {
            if (!mate.Alive || mate.Aggro || mate.Area != monster.Area)
            {
                continue;
            }
            mate.Aggro = true;
            if (mate.OwnerPlayerId == 0)
            {
                mate.ChaseTargetPlayerId = attackerPlayerId;
            }
        }
        monster.Health = Math.Max(0, monster.Health - damage);
        bool killed = monster.Health == 0;
        var monsterInfo = monster.ToMonsterRuntimeInfo();
        if (killed)
        {
            var diedAtUtc = nowUtc;
            monster.Alive = false;
            monster.DiedAtUtc = diedAtUtc;
            if (attackerPlayerId == state.HumanPlayerId)
            {
                state.Kills++;
            }
            monsterInfo = monster.ToMonsterRuntimeInfo();
            monsterInfo.SummonStoneReward = supply.ConsumeSupplyStoneBudget(state, monster, diedAtUtc);
        }

        return new MonsterDamageResult(true, killed, monster.MonsterId, monsterInfo, monster.HeartReward, monster.BootsReward, monster.KeyReward, monster.Kind, AliveSeconds: killed ? (monster.DiedAtUtc - monster.SpawnedAtUtc).TotalSeconds : 0d, AttackEventCount: monster.AttackEventCount);
    }
}
