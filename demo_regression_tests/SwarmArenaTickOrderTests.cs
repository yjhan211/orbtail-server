using game_server.matches;
using game_server.matches.combat;
using game_server.matches.entry;
using game_server.matches.field;
using game_server.matches.monsters;
using game_server.matches.results;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
namespace demo_regression_tests;

public sealed class SwarmArenaTickOrderTests
{
    [Fact]
    public void ProximityAutoCombatTimer_UsesFiftyMillisecondMatchLockPulse()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string runner = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        string timers = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        Assert.Contains("TimeSpan.FromMilliseconds(50)", timers);
        Assert.Contains("private readonly PeriodicTimer _timer = new(", timers);
        Assert.Contains("tickService.StopAsync()", source);
        string composition = ReadNormalizedSource(root, "game_server", "Program.cs");
        Assert.Contains("tickService.Start();", source);
        Assert.DoesNotContain("new MatchTickRunner(", source);
        AssertInOrder(composition,
            "services.AddSingleton<Func<MatchRuntime, TimeProvider, MatchTickLoop>>",
            "return (runtime, clock) =>",
            "entryFailureHandler, combat, environment, botMovement, botDecisions, zones, clock);",

            "services.AddSingleton<MatchTickService>");
        AssertInOrder(runner,
            "using var scope = runtime.Enter();",
            "runtime.IsEnded",
            "combat.ProcessTick(runtime);");
        Assert.DoesNotContain("matchRuntimes.Enter(", runner);
    }

    [Fact]
    public void CombatEntrypoints_KeepResourceCombatBeforeSettlementInsideMatchGate()
    {
        string root = FindRepositoryRoot();
        string proximity = ReadNormalizedSource(
            root, "game_server", "Sessions", "OrbVisualStatePublisher.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string settlement = ReadNormalizedSource(
            root, "game_server", "Matches", "Field", "MatchEnvironmentService.cs");

        string proximityTick = ReadMethodSlice(
            ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs"),
            "internal void ProcessTick()",
            "\n}");
        AssertInOrder(
            proximityTick,
            "using var scope = runtime.Enter();",
            "runtime.GetSessions()",
            "runtime.IsEntryTimedOut(utcNow)",
            "combat.ProcessTick(runtime);");
        Assert.DoesNotContain("catch (", proximityTick);
        string loopSource = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        AssertInOrder(loopSource,
            "ProcessTick();",
            "catch (Exception ex)",
            "Match tick failed: MatchingId={MatchingId}");
        Assert.DoesNotContain("_proximityAutoCombatProcessing", proximity);
        Assert.DoesNotContain("Interlocked.Exchange(", proximityTick);
        Assert.DoesNotContain("Volatile.Write(", proximityTick);
        Assert.DoesNotContain("PrepareAndDispatch", proximity);
        Assert.DoesNotContain("Coordinator", proximity);

        // 환경 정산은 같은 50ms 펄스 안에서 전투 다음, 봇 걸음 전에 실행한다.
        Assert.DoesNotContain("_resourceTickTimer", server);
        Assert.DoesNotContain("StartResourceTickTimer", server);
        AssertInOrder(
            proximityTick,
            "using var scope = runtime.Enter();",
            "combat.ProcessTick(runtime);",
            "runtime.StartsAtUtc",
            "if (currentEnvironmentInterval > _lastEnvironmentInterval)",
            "environment.ProcessTick(runtime);",
            "runtime.IsEnded ||",
            "botMovement.ProcessTick(runtime, botDecisions.DecideMovement);");

        string matchingSettlement = ReadMethodSlice(
            settlement,
            "public void ProcessTick(",
            "private sealed record EnvironmentalTarget(");
        AssertInOrder(
            matchingSettlement,
            "long matchingId = match.MatchingId;",
            "var players = match.GetAlivePlayers()",
            "healthService.ApplyDamage(match, player, totalDelta, handleElimination: false);",
            "var eliminatedTargets = targets",
            "foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())",
            "matchEliminations.EliminatePlayer(",
            "CheckGameOver()",
            "matchResults.FinalizeMatch(matchingId, winnerId.Value, MatchEndReason.PressureFieldSettlement, resolution.DecisiveCriterion);");
        Assert.DoesNotContain("Enter(", matchingSettlement);
        Assert.DoesNotContain("ProcessProximityAutoCombatForMatching(", matchingSettlement);
        Assert.DoesNotContain("PublicationTurn", matchingSettlement);
    }

    [Fact]
    public void SwarmArenaTick_RunsDirectorBeforeGameplayGate()
    {
        string arenaTick = ReadSwarmArenaTick();
        string arenaCode = MaskCommentsAndLiterals(arenaTick);

        AssertInOrder(
            arenaCode,
            "runtime.Monsters.Tick(",
            "if (!runtime.IsGameplayActive())");

        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTick,
                "if (!runtime.IsGameplayActive())"));
        AssertInOrder(
            inactiveGameplayBranch,
            "MonsterSnapshotPublisher.Broadcast(",
            "return;");
    }

    [Fact]
    public void SwarmArenaTick_KeepsAuthoritativeGameplayPhaseOrder()
    {
        string arenaTickSource = ReadSwarmArenaTick();
        string arenaTick = MaskCommentsAndLiterals(arenaTickSource);
        Assert.DoesNotContain("matchRuntimes.GetOrThrow", arenaTick);
        Assert.DoesNotContain("sessions.Any", arenaTick);
        Assert.DoesNotContain("aliveSessions", arenaTick);
        Assert.Contains("runtime.GetAlivePlayers()", arenaTick);
        Assert.Contains("runtime.IsEnded", arenaTick);
        Assert.DoesNotContain("GrantStartingSummonStones", arenaTick);
        Assert.DoesNotContain("StartingOrbGrantedPlayers", arenaTick);
        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTickSource,
                "if (!runtime.IsGameplayActive())"));
        AssertInOrder(
            inactiveGameplayBranch,
            "MonsterSnapshotPublisher.Broadcast(",
            "return;");

        AssertInOrder(
            arenaTick,
            "runtime.Monsters.Tick(",
            "if (!runtime.IsGameplayActive())",
            "UpdateSwarmOrbTrails(",
            "ProcessSwarmTrailCuts(",
            "ProcessSwarmRetaliationWindows(",
            "waveOrbAttacks.ProcessTick(",
            "playerOrbs.ActivateWaveOrbs(",
            "playerOrbs.ActivateWindOrbs(",
            "ProcessSwarmSunBurns(",
            "ApplySwarmParticipantDamage(",
            "botDecisions.UpdateSleep(",
            "ProcessSleepRecovery(",
            "ProcessSwarmBotDoorUnlocks(",
            "MonsterSnapshotPublisher.Broadcast(",
            "BuildSwarmArenaCombatActors(",
            "playerOrbs.ProcessOrbRecovery(",
            "orbVisuals.Publish(",
            "BroadcastSwarmOrbRankings(",
            "botDecisions.ProcessBotOrbGrowth(",
            "ProcessSwarmScoreTimeout(",
            "ProcessPendingMonsterHits(",
            "ProcessSwarmCrossfires(",
            "ProcessPendingPvpHits(",
            "CollectSwarmCrossfireCappedOwners(",
            "CollectSwarmCrossfireAnchoredTargets(",
            "AutoAttack.ResolveAttacks(",
            "TryStartSunCrossfire(",
            "combatDamage.SendMonsterHitNotification(runtime, attacker,",
            "BroadcastSwarmAttackVfxToTargetAndObservers(");
    }

    [Fact]
    public void CombatPublicationSeams_CharacterizeStateCouplingAndFailureBoundaries()
    {
        string root = FindRepositoryRoot();
        string playerState = ReadNormalizedSource(
            root, "game_server", "Sessions", "GameClientSession.PlayerState.cs");
        string sessionCombat = ReadNormalizedSource(
            root, "game_server", "Matches", "Combat", "MatchCombatDamageService.cs");
        string sessionMatchEnd = ReadNormalizedSource(
            root, "game_server", "Players", "PlayerEliminationService.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string proximity = ReadNormalizedSource(
            root, "game_server", "Sessions", "OrbVisualStatePublisher.cs");

        string healthNotification = ReadBracedBlockAfterMarker(
            ReadNormalizedSource(root, "game_server", "Players", "PlayerHealthService.cs"),
            "public void ApplyDamage(");
        AssertInOrder(
            healthNotification,
            "var change = player.ApplyDamage(damage);",
            "eventLogs.LogResource(match.MatchingId,",
            "player.Session?.SendHealth(change);",
            "eliminations.EliminatePlayer(");

        string applyProximityHit = ReadMethodSlice(
            sessionCombat,
            "public void ApplyProximityAutoCombatHit(",
            "public void ApplySwarmAfterimageMonsterHit(");
        AssertInOrder(
            applyProximityHit,
            "eventLogs.LogHit(",
            "healthService.ApplyDamage(runtime, victim, damage, sourcePlayerId);",
            "PacketMaker.G_TO_C_COMBAT_HIT(",
            "session.TrySend(packet);");

        string orbPublicationSteps = ReadMethodSlice(
            proximity,
            "private void DispatchOrbVisualStatePublications(",
            "private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(");
        AssertInOrder(
            orbPublicationSteps,
            "foreach (SwarmOrbVisualPublication publication in publications)",
            "CommitAndDispatchOrbVisualStatePublication(publication)",
            "visualStates[key] = state;",
            "Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE)",
            "publication.Recipient!.TrySend(packet);");
        Assert.False(ContainsCodeToken(orbPublicationSteps, "catch"));

        string elimination = ReadBracedBlockAfterMarker(
            sessionMatchEnd,
            "public void EliminatePlayer(");
        AssertInOrder(
            elimination,
            "TryEliminatePlayer(",
            "eliminatedBot.Path.Clear();",
            "eliminatedPlayer.Orbs.TakeAllItems(",
            "session.TrySend(eliminatedPacket);",
            "CheckGameOver()");

        string humanElimination = ReadBracedBlockAfterMarker(
            sessionMatchEnd,
            "public void EliminatePlayer(");
        AssertInOrder(
            humanElimination,
            "TryEliminatePlayer(",
            "eliminatedPlayer.Orbs.TakeAllItems(",
            "session.TrySend(eliminatedPacket);",
            "CheckGameOver(",
            "FinalizeMatch(");
        Assert.False(ContainsCodeToken(humanElimination, "catch"));
    }

    [Fact]
    public void GrowthFlow_OnlyBotsChooseSummonOrUpgrade()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Players", "Bots", "BotDecisionService.cs");
        string tickBody = ReadMethodSlice(
            source,
            "public void ProcessBotOrbGrowth(",
            "public void ProcessSwarmBotDoorUnlocks(");
        Assert.DoesNotContain("public void HandlePick(", source);

        AssertInOrder(
            tickBody,
            "foreach (var bot in aliveBots)",
            "Summon(",
            "TryUpgradeForBot(");
        Assert.DoesNotContain("foreach (var session in aliveSessions)", tickBody);
        Assert.DoesNotContain("SendSwarmGrowthOffer", tickBody);
        Assert.DoesNotContain("OfferId", tickBody);
    }

    [Fact]
    public void SwarmCleanup_AlwaysDropsMatchOwnedRuntimeAfterMonsterCleanup()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        string cleanupBody = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        Assert.Contains("Monsters.Release();", cleanupBody);
        Assert.DoesNotContain("CleanupSwarmArenaState", source);
        Assert.DoesNotContain("_swarmMatchRuntimes", source);
        Assert.DoesNotContain("ClearSunOrbAttackState", cleanupBody);
        Assert.DoesNotContain("ClearWindOrbAttackState", cleanupBody);
        Assert.DoesNotContain("ClearOrbUpgradeState", cleanupBody);
    }

    [Fact]
    public void ScheduledClosureTick_CommitsStateThenPublishesInsideMatchLock()
    {
        string root = FindRepositoryRoot();
        string composition = ReadNormalizedSource(root, "game_server", "Program.cs");
        string field = ReadNormalizedSource(root, "game_server", "Matches", "Field", "MatchZoneService.cs");
        Assert.Contains("botDecisions, zones, clock)", composition);
        string tick = ReadMethodSlice(
            field,
            "public void ProcessTick(",
            "    // #272 경계 토출 스폰: 구역별");
        string prepare = ReadMethodSlice(
            field,
            "private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(",
            "private void DispatchSwarmClosurePublicationPlan(");
        string dispatch = ReadMethodSlice(
            field,
            "private void DispatchSwarmClosurePublicationPlan(",
            "private void PrepareDestroySwarmOrbsInClosedAreas(");
        string orbPrepare = field[field.IndexOf("private void PrepareDestroySwarmOrbsInClosedAreas(", StringComparison.Ordinal)..];

        // 독립 루프의 매치 잠금 안에서 1초 주기를 확인하고 상태 확정 → 송신한다.
        string runner = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        AssertInOrder(runner,
            "using var scope = runtime.Enter();",
            "_lastAreaClosureSecond = elapsedSeconds;",
            "zones.ProcessTick(matchingId, playerSessions.ToArray());");
        AssertInOrder(
            tick,
            "PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);",
            "DispatchSwarmClosurePublicationPlan(plan)");
        Assert.DoesNotContain("TryEnter", tick);
        Assert.DoesNotContain(".TrySend(", tick);

        AssertInOrder(
            prepare,
            "Closures.InitializeMatching(",
            "new SwarmFieldStateOutbound(",
            "Closures.CheckClosureSchedule()",
            "new SwarmClosureWarningOutbound(",
            "eventLogs.LogClosure(",
            "new SwarmAreaClosedOutbound(",
            "matchRuntimes.GetOrNull(matchingId)?.Doors.CloseDoorsForAreas(",
            "new SwarmDoorStateOutbound(",
            "PrepareDestroySwarmOrbsInClosedAreas(",
            "new SwarmClosurePublicationPlan(");
        Assert.DoesNotContain("Packet.Create(", prepare);
        Assert.DoesNotContain("PacketMaker.", prepare);
        Assert.DoesNotContain(".TrySend(", prepare);
        Assert.Contains("Transport failure never rolls back", field);

        AssertInOrder(
            orbPrepare,
            "DestroyOrbsFromOrdinal(",
            ".OrbDurabilityBonus.Remove(",
            "new SwarmInventoryUpdateOutbound(",
            "new SwarmRingVfxOutbound(",
            "eventLogs.LogSystem(");
        Assert.DoesNotContain("Packet.Create(", orbPrepare);
        Assert.DoesNotContain("PacketMaker.", orbPrepare);
        Assert.DoesNotContain(".TrySend(", orbPrepare);
        Assert.DoesNotContain("SendOrbUpdate(", orbPrepare);
        Assert.DoesNotContain("SendOrbRingEffect(", orbPrepare);
        Assert.Contains("foreach (var player in runtime.GetAlivePlayers())", orbPrepare);
        Assert.DoesNotContain("Bots.GetBots", orbPrepare);
        Assert.DoesNotContain("owners.Add", orbPrepare);

        AssertInOrder(
            dispatch,
            "case SwarmFieldStateOutbound",
            "Protocol.G_TO_C_SWARM_FIELD_STATE",
            "case SwarmClosureWarningOutbound",
            "Protocol.G_TO_C_AREA_CLOSURE_WARNING",
            "case SwarmAreaClosedOutbound",
            "Protocol.G_TO_C_AREA_CLOSED",
            "case SwarmDoorStateOutbound",
            "PacketMaker.G_TO_C_DOOR_STATE_UPDATE(",
            "case SwarmInventoryUpdateOutbound",
            "session.SendOrbUpdate(",
            "case SwarmRingVfxOutbound",
            "Protocol.G_TO_C_ORB_RING_EFFECT");
        Assert.Contains("session.TrySend(packet)", dispatch);
        Assert.DoesNotContain("catch", dispatch);
        Assert.Contains("first transport exception", field);
    }

    [Fact]
    public void OrbServices_UsePlayerTimingAndMatchOwnedVictimEffects()
    {
        string root = FindRepositoryRoot();
        string windBlade = ReadNormalizedSource(root, "game_server", "Players", "PlayerOrbService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "SunOrbAttackService.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "Players", "PlayerOrbGrowthService.cs");

        Assert.DoesNotContain("_swarmWindBladeNextTickAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeEngagedAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeVictimImmuneUntilUtc", windBlade);
        Assert.DoesNotContain("_swarmWindWoundsUntilUtc", crossfire);
        Assert.DoesNotContain("_swarmFamilyUpgradeCounts", orbBoard);
        Assert.Contains("runtime.WindOrbAttacks", windBlade);
        Assert.Contains("owner.TryBeginWindOrbTick(", windBlade);
        Assert.Contains("owner.HasCompletedWindOrbSpinup(", windBlade);
        Assert.Contains("player.GetOrbUpgradeCount(orbGroupId)", orbBoard);
        Assert.DoesNotContain("MatchRuntimeStore", orbBoard);
    }

    [Fact]
    public void SunOrbAttackState_IsMatchOwnedAndDodgeLookupDoesNotCreateRuntime()
    {
        string root = FindRepositoryRoot();
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "SunOrbAttackService.cs");
        string botDodge = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        string runtimeStates = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");

        Assert.DoesNotContain("_swarmCrossfireShapes", crossfire);
        Assert.DoesNotContain("_swarmCrossfireDodgeSnapshot", crossfire);
        Assert.DoesNotContain("_swarmCrossfireEventSeq", crossfire);
        Assert.DoesNotContain("_swarmSunBurns", crossfire);
        Assert.DoesNotContain("_swarmCrossfireConvergeWindows", crossfire);
        Assert.DoesNotContain("ClearSunOrbAttackState(", crossfire);
        Assert.Contains("SunOrbAttackState sunOrbAttacks = runtime.SunOrbAttacks;", crossfire);
        Assert.DoesNotContain("MatchRuntimeStore matchRuntimes", crossfire);
        Assert.Contains("public SunOrbAttackState SunOrbAttacks { get; }", botDodge);

        Assert.Contains("new BotPlayerManager(matchingId, logger, Doors, SunOrbAttacks, eventLogs)", botDodge);
        string botMovement = ReadNormalizedSource(root, "game_server", "Players", "Bots", "BotPlayerManager.Movement.cs");
        Assert.Contains("_sunOrbAttacks.DodgeSnapshot, matchingId, bot.PlayerId, bot.Player.Position!, bot.Player.CurrentArea, now", botMovement);
        Assert.DoesNotContain("matchRuntimes.GetOrThrow(matchingId).Swarm", botDodge);
        Assert.False(File.Exists(Path.Combine(root, "game_server", "GameServer.SwarmBotDodge.cs")));
    }

    [Fact]
    public void MatchExecutionBoundary_UsesMatchOwnedRandomAndThreadSafeCachesWithoutGlobalLock()
    {
        string root = FindRepositoryRoot();
        string store = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchRuntime.cs");
        string runtimeStates = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchRuntime.cs");
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "SunOrbAttackService.cs");

        // 매치 하나에 모니터 하나 — 블로킹 진입과 펄스용 TryEnter가 같은 잠금 객체를 쓴다.
        Assert.DoesNotContain("_globalExecutionLock", store);
        Assert.Contains("Monitor.Enter(MatchLock);", store);
        Assert.Contains("Monitor.TryEnter(MatchLock, ref lockTaken);", store);
        Assert.Contains("Monitor.Exit(MatchLock);", store);
        string wave = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "WaveOrbAttackService.cs");
        Assert.Contains("if (runtime.IsEnded) return;", wave);

        Assert.DoesNotContain("_swarmCriticalRng", combat);
        Assert.DoesNotContain("_swarmCriticalRng", crossfire);
        Assert.DoesNotContain("_criticalRng", runtimeStates);
        string damage = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatDamageService.cs");
        Assert.Contains("runtime.CombatDamage.CriticalRng.NextDouble()", damage);
        Assert.DoesNotContain("new Random()", damage);
        Assert.Contains("RollCritical(runtime, Config.SWARM_WIND_WOUND_CRIT_CHANCE)", damage);
        Assert.DoesNotContain("MatchRuntimeStore", damage);

        string field = ReadNormalizedSource(root, "game_server", "Matches", "Field", "MatchZoneService.cs");
        Assert.Contains(
            "Lazy<IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>>",
            field);
        Assert.Contains(
            "Lazy<IReadOnlyList<ClosureWaveDefinition>> _swarmFieldDerivedWaves",
            field);
        Assert.Equal(
            2,
            CountOccurrences(field, "LazyThreadSafetyMode.ExecutionAndPublication"));
        Assert.DoesNotContain("_swarmAreaCellsByDistance == null", field);
        Assert.DoesNotContain("_swarmFieldDerivedWaves ??=", field);
    }

    private static string ReadSwarmArenaTick()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        return ReadMethodSlice(
            source,
            "public void ProcessTick(",
            "// 쌍 깔때기:");
    }

    private static string ReadBracedBlockAfterMarker(string source, string marker)
    {
        string codeOnly = MaskCommentsAndLiterals(source);
        int markerIndex = codeOnly.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Could not find block marker '{marker}'.");

        int openBraceIndex = codeOnly.IndexOf(
            '{',
            markerIndex + marker.Length);
        Assert.True(openBraceIndex >= 0, $"Could not find opening brace after '{marker}'.");

        int depth = 0;
        for (int index = openBraceIndex; index < codeOnly.Length; index++)
        {
            if (codeOnly[index] == '{')
            {
                depth++;
                continue;
            }

            if (codeOnly[index] != '}')
                continue;

            depth--;
            if (depth == 0)
                return source[(openBraceIndex + 1)..index];
        }

        Assert.Fail($"Could not find closing brace after '{marker}'.");
        return string.Empty;
    }

    private static bool ContainsCodeToken(string source, string token)
    {
        string codeOnly = MaskCommentsAndLiterals(source);
        int searchStart = 0;
        while (true)
        {
            int index = codeOnly.IndexOf(token, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            int end = index + token.Length;
            bool validStart = index == 0 || !IsIdentifierPart(codeOnly[index - 1]);
            bool validEnd = end == codeOnly.Length || !IsIdentifierPart(codeOnly[end]);
            if (validStart && validEnd)
                return true;

            searchStart = index + 1;
        }
    }

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string MaskCommentsAndLiterals(string source)
    {
        char[] masked = source.ToCharArray();
        int index = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                int end = index + 2;
                while (end < source.Length && source[end] != '\r' && source[end] != '\n')
                    end++;
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                int end = index + 2;
                while (end + 1 < source.Length &&
                       (source[end] != '*' || source[end + 1] != '/'))
                {
                    end++;
                }
                end = end + 1 < source.Length ? end + 2 : source.Length;
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (source[index] == '\'')
            {
                int end = index + 1;
                while (end < source.Length)
                {
                    if (source[end] == '\\')
                    {
                        end = Math.Min(source.Length, end + 2);
                        continue;
                    }

                    if (source[end++] == '\'')
                        break;
                }
                MaskRange(masked, index, end);
                index = end;
                continue;
            }

            if (source[index] != '"')
            {
                index++;
                continue;
            }

            int delimiterLength = 1;
            while (index + delimiterLength < source.Length &&
                   source[index + delimiterLength] == '"')
            {
                delimiterLength++;
            }

            int literalEnd;
            if (delimiterLength >= 3)
            {
                literalEnd = index + delimiterLength;
                while (literalEnd < source.Length)
                {
                    int quoteRun = 0;
                    while (literalEnd + quoteRun < source.Length &&
                           source[literalEnd + quoteRun] == '"')
                    {
                        quoteRun++;
                    }
                    if (quoteRun >= delimiterLength)
                    {
                        literalEnd += delimiterLength;
                        break;
                    }
                    literalEnd++;
                }
            }
            else
            {
                bool verbatim =
                    index > 0 && source[index - 1] == '@' ||
                    index > 1 && source[index - 2] == '@' && source[index - 1] == '$';
                literalEnd = index + 1;
                while (literalEnd < source.Length)
                {
                    if (verbatim && source[literalEnd] == '"' &&
                        literalEnd + 1 < source.Length && source[literalEnd + 1] == '"')
                    {
                        literalEnd += 2;
                        continue;
                    }
                    if (!verbatim && source[literalEnd] == '\\')
                    {
                        literalEnd = Math.Min(source.Length, literalEnd + 2);
                        continue;
                    }
                    if (source[literalEnd++] == '"')
                        break;
                }
            }

            MaskRange(masked, index, literalEnd);
            index = literalEnd;
        }

        return new string(masked);
    }

    private static void MaskRange(char[] target, int start, int end)
    {
        for (int index = start; index < end; index++)
            target[index] = ' ';
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        string? previousMarker = null;

        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(
                marker,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(
                currentIndex > previousIndex,
                $"Expected '{marker}' after '{previousMarker ?? "method start"}'.");
            previousIndex = currentIndex;
            previousMarker = marker;
        }
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int searchStart = 0;

        while (true)
        {
            int index = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return count;

            count++;
            searchStart = index + marker.Length;
        }
    }

    private static string ReadMethodSlice(
        string source,
        string startMarker,
        string endMarker)
    {
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find method start marker '{startMarker}'.");

        int endIndex = source.IndexOf(
            endMarker,
            startIndex + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find method end marker '{endMarker}'.");

        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts))
            .Replace("\r\n", "\n").Replace("public virtual void ", "public void ", StringComparison.Ordinal)
            // 표기 차이만 정규화하고 잠금·상태 확정·발행 순서 검사는 유지한다.
            .Replace("matchRuntimes.Enter(matchingId, out var scope)",
                "matchRuntimes.Enter(matchingId, out MatchLockScope scope)", StringComparison.Ordinal);
    }
}
