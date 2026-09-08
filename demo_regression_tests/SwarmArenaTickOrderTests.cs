using game_server.players;
using game_server.matches;
using game_server.sessions;
namespace demo_regression_tests;

public sealed class SwarmArenaTickOrderTests
{
    [Fact]
    public void ProximityAutoCombatTimer_UsesFiftyMillisecondMatchLockPulse()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string runner = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickRunner.cs");
        string timers = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        Assert.Contains("TimeSpan.FromMilliseconds(50)", timers);
        Assert.Contains("new PeriodicTimer(", timers);
        Assert.Contains("tickService.StopAsync()", source);
        AssertInOrder(source,
            "var tickRunner = new MatchTickRunner(",
            "countdown.Broadcast,",
            "environmentService.Process,",
            "runtime => botMovement.Process(runtime, botDecisions.ResolveSwarmBotDirective),",
            "tickService.Start(tickRunner.Run);");
        AssertInOrder(runner,
            "matchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "return;",
            "using (scope)",
            "scope.Runtime.IsTerminal",
            "processCombat(matchingId, activeSessions);");
        Assert.DoesNotContain("matchRuntimes.Enter(", runner);
    }

    [Fact]
    public void CombatEntrypoints_KeepResourceCombatBeforeSettlementInsideMatchGate()
    {
        string root = FindRepositoryRoot();
        string proximity = ReadNormalizedSource(
            root, "game_server", "Services", "OrbVisualStatePublisher.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string settlement = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchEnvironmentService.cs");

        string proximityTick = ReadMethodSlice(
            ReadNormalizedSource(root, "game_server", "Matches", "MatchTickRunner.cs"),
            "public void Run(MatchRuntime runtime)",
            "private static bool ShouldMoveBots(");
        AssertInOrder(
            proximityTick,
            "matchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "runtime.Sessions.Snapshot()",
            "Match session snapshot failed",
            "try",
            "processCombat(matchingId, activeSessions);",
            "catch (Exception ex)",
            "MatchingId={MatchingId}");
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
            "using (scope)",
            "processCombat(matchingId, activeSessions);",
            "scope.Runtime.TryBeginEnvironmentalTick(",
            "MatchStartGate.GetGameplayStartedAtUtc(matchingId)",
            "processEnvironment(scope.Runtime, activeSessions);",
            "scope.Runtime.IsTerminal ||",
            "moveBots(scope.Runtime);");

        string matchingSettlement = ReadMethodSlice(
            settlement,
            "public void Process(",
            "private sealed record EnvironmentalTarget(");
        AssertInOrder(
            matchingSettlement,
            "long matchingId = match.MatchingId;",
            "var humans = activeSessions",
            "var bots = match.Bots.GetBots(matchingId)",
            "target.Session.HealthChanges.Handle(",
            "var eliminatedTargets = targets",
            "foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())",
            "matchEliminations.Process(",
            "botEliminations.Process(",
            "Roster.CheckGameOver()",
            "matchEliminations.EndMatch(matchingId, winnerId.Value, resolution.DecisiveCriterion);");
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
            "matchRuntimes.GetOrThrow(matchingId).Monsters.Tick(",
            "if (!MatchStartGate.IsGameplayActive(matchingId))");

        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTick,
                "if (!MatchStartGate.IsGameplayActive(matchingId))"));
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
        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTickSource,
                "if (!MatchStartGate.IsGameplayActive(matchingId))"));
        AssertInOrder(
            inactiveGameplayBranch,
            "MonsterSnapshotPublisher.Broadcast(",
            "return;");

        AssertInOrder(
            arenaTick,
            "orbUpgrades.GrantStartingOrbs(matchingId, session.PlayerId.Value, isBot: false);",
            "session.SendSummonStoneState();",
            "SetupSwarmCutDummy(matchingId);",
            "session.TrySend(leavePacket);",
            "matchRuntimes.GetOrThrow(matchingId).Monsters.Tick(",
            "if (!MatchStartGate.IsGameplayActive(matchingId))",
            "UpdateSwarmOrbTrails(",
            "ProcessSwarmTrailCuts(",
            "ProcessSwarmRetaliationWindows(",
            "ProcessSwarmWaveBombs(",
            "windBlades.Process(",
            "ProcessSwarmSunBurns(",
            "ApplySwarmParticipantDamage(",
            "ProcessSwarmBotRecovery(",
            "ProcessSwarmSleepRecovery(",
            "ProcessSwarmBotDoorUnlocks(",
            "MonsterSnapshotPublisher.Broadcast(",
            "BuildSwarmArenaCombatActors(",
            "orbRecovery.Process(",
            "orbVisuals.Publish(",
            "BroadcastSwarmOrbRankings(",
            "growth.ProcessOffers(",
            "ProcessSwarmScoreTimeout(",
            "ProcessPendingSwarmMonsterHits(",
            "ProcessSwarmCrossfires(",
            "ApplySwarmPvpAttack(matchingId, pending.Attack",
            "CollectSwarmCrossfireCappedOwners(",
            "CollectSwarmCrossfireAnchoredTargets(",
            "Combat.Resolve(",
            "TryScheduleSwarmCrossfire(",
            "combatDamage.SendMonsterHitNotification(attackerSession,",
            "BroadcastSwarmAttackVfxToTargetAndObservers(",
            "matchRuntimes.GetOrThrow(matchingId).Bots.TryFinalizeProximityAutoCombatElimination(",
            "botEliminations.Process(");
    }

    [Fact]
    public void CombatPublicationSeams_CharacterizeStateCouplingAndFailureBoundaries()
    {
        string root = FindRepositoryRoot();
        string playerState = ReadNormalizedSource(
            root, "game_server", "Sessions", "GameClientSession.PlayerState.cs");
        string sessionCombat = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchCombatDamageService.cs");
        string sessionMatchEnd = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchEliminationService.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string proximity = ReadNormalizedSource(
            root, "game_server", "Services", "OrbVisualStatePublisher.cs");

        string healthNotification = ReadBracedBlockAfterMarker(
            ReadNormalizedSource(root, "game_server", "Players", "PlayerHealthChangeService.cs"),
            "public void Handle(");
        AssertInOrder(
            healthNotification,
            "if (!change.Changed) return;",
            "session.SendHealth(",
            "eliminations.Process(");

        string applyProximityHit = ReadMethodSlice(
            sessionCombat,
            "public void ApplyProximityAutoCombatHit(",
            "public void ApplySwarmAfterimageMonsterHit(");
        AssertInOrder(
            applyProximityHit,
            "eventLogs.LogHit(",
            "victimSession.Condition.ApplyDamage(damage);",
            "victimSession.HealthChanges.Handle(change, attackerPlayerId: sourcePlayerId);",
            "PacketMaker.G_TO_C_COMBAT_HIT(",
            "victimSession.TrySend(packet);");

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

        string botElimination = ReadMethodSlice(
            ReadNormalizedSource(root, "game_server", "Services", "Bots", "BotEliminationService.cs"),
            "public void Process(",
            "private void DropBotInventoryAtCurrentPosition(");
        AssertInOrder(
            botElimination,
            "try",
            "Roster.TryEliminatePlayer(",
            "DropBotInventoryAtCurrentPosition(",
            "foreach (var s in matchingSessions) s.TrySend(eliminatedPacket);",
            "catch (Exception ex)");
        Assert.DoesNotContain("BestEffortGroup", botElimination);

        string humanElimination = ReadMethodSlice(
            sessionMatchEnd,
            "public void Process(",
            "public void EndMatch(");
        AssertInOrder(
            humanElimination,
            "Roster.TryEliminatePlayer(",
            "groundItemDrop.DropAll(eliminatedSession);",
            "session.TrySend(eliminatedPacket);",
            "session.TrySend(leavePacket);",
            "Roster.CheckGameOver(",
            "SendGameResult(");
        Assert.False(ContainsCodeToken(humanElimination, "catch"));
    }

    [Fact]
    public void GrowthOfferFlow_OnlyBotsGenerateOffers()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchGrowthService.cs");
        string tickBody = ReadMethodSlice(
            source,
            "public void ProcessOffers(",
            "public int GetTopOrbCount(");
        Assert.DoesNotContain("public void HandlePick(", source);

        AssertInOrder(
            tickBody,
            "foreach (var bot in aliveBots)",
            ".AllocateOfferId()",
            "ChooseSwarmBotGrowthCard(");
        Assert.DoesNotContain("foreach (var session in aliveSessions)", tickBody);
        Assert.DoesNotContain("SendSwarmGrowthOffer", tickBody);
        Assert.DoesNotContain(".GrowthOffers.Offers", tickBody);
    }

    [Fact]
    public void SwarmCleanup_AlwaysDropsMatchOwnedRuntimeAfterMonsterCleanup()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchArenaService.cs");
        string cleanupBody = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        Assert.Contains("Monsters.Release();", cleanupBody);
        Assert.DoesNotContain("CleanupSwarmArenaState", source);
        Assert.DoesNotContain("_swarmMatchRuntimes", source);
        Assert.DoesNotContain("ClearSwarmCrossfireState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmWindBladeState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmOrbBoardState", cleanupBody);
    }

    [Fact]
    public void ScheduledClosureTick_CommitsStateThenPublishesInsideMatchLock()
    {
        string root = FindRepositoryRoot();
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string field = ReadNormalizedSource(root, "game_server", "Matches", "MatchFieldService.cs");
        Assert.Contains("fieldService.Process", server);
        string tick = ReadMethodSlice(
            field,
            "public void Process(",
            "    // #272 경계 토출 스폰: 구역별");
        string prepare = ReadMethodSlice(
            field,
            "private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(",
            "private static ImmutableArray<int> CaptureSwarmClosureRecipientOrdinals(");
        string dispatch = ReadMethodSlice(
            field,
            "private void DispatchSwarmClosurePublicationPlan(",
            "private void PrepareDestroySwarmOrbsInClosedAreas(");
        string orbPrepare = field[field.IndexOf("private void PrepareDestroySwarmOrbsInClosedAreas(", StringComparison.Ordinal)..];

        // 독립 루프의 매치 잠금 안에서 1초 주기를 확인하고 상태 확정 → 송신한다.
        string runner = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickRunner.cs");
        AssertInOrder(runner,
            "matchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "using (scope)",
            "TryBeginAreaClosureTick(",
            "processAreaClosure(matchingId, countdownSessions.ToArray());");
        AssertInOrder(
            tick,
            "PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);",
            "DispatchSwarmClosurePublicationPlan(plan, sessionSnapshot)");
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
            "DestroySwarmOrbsFromOrdinal(",
            ".OrbDurabilityBonus.Remove(",
            "new SwarmInventoryUpdateOutbound(",
            "new SwarmRingVfxOutbound(",
            "eventLogs.LogSystem(");
        Assert.DoesNotContain("Packet.Create(", orbPrepare);
        Assert.DoesNotContain("PacketMaker.", orbPrepare);
        Assert.DoesNotContain(".TrySend(", orbPrepare);
        Assert.DoesNotContain("SendOrbUpdate(", orbPrepare);
        Assert.DoesNotContain("SendSwarmRingVfx(", orbPrepare);

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
            "Protocol.G_TO_C_SWARM_ENCIRCLE_VFX");
        Assert.Contains("SendToCapturedRecipients(", dispatch);
        Assert.DoesNotContain("catch", dispatch);
        Assert.Contains("first transport exception", field);
    }

    [Fact]
    public void WindBladeAndOrbBoardState_AreOwnedBySwarmMatchRuntime()
    {
        string root = FindRepositoryRoot();
        string windBlade = ReadNormalizedSource(root, "game_server", "Services", "WindBladeService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Services", "CrossfireService.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "Services", "OrbUpgradeService.cs");

        Assert.DoesNotContain("_swarmWindBladeNextTickAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeEngagedAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeVictimImmuneUntilUtc", windBlade);
        Assert.DoesNotContain("_swarmWindWoundsUntilUtc", crossfire);
        Assert.DoesNotContain("_swarmFamilyUpgradeCounts", orbBoard);
        Assert.Contains("matchRuntimes.GetOrThrow(matchingId).Swarm.WindBlade", windBlade);
        Assert.Contains("matchRuntimes.GetOrThrow(matchingId).Swarm.OrbBoard", orbBoard);
    }

    [Fact]
    public void SwarmCrossfireState_IsMatchOwnedAndDodgeLookupDoesNotCreateRuntime()
    {
        string root = FindRepositoryRoot();
        string crossfire = ReadNormalizedSource(root, "game_server", "Services", "CrossfireService.cs");
        string botDodge = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        string runtimeStates = ReadNormalizedSource(root, "game_server", "Services", "SwarmArenaStates.cs");

        Assert.DoesNotContain("_swarmCrossfireShapes", crossfire);
        Assert.DoesNotContain("_swarmCrossfireDodgeSnapshot", crossfire);
        Assert.DoesNotContain("_swarmCrossfireEventSeq", crossfire);
        Assert.DoesNotContain("_swarmSunBurns", crossfire);
        Assert.DoesNotContain("_swarmCrossfireConvergeWindows", crossfire);
        Assert.DoesNotContain("ClearSwarmCrossfireState(", crossfire);
        Assert.Contains("SwarmCrossfireState crossfire = matchRuntimes.GetOrThrow(matchingId).Swarm.Crossfire;", crossfire);
        Assert.Contains("public SwarmCrossfireState Crossfire { get; }", runtimeStates);

        Assert.Contains("Bots.SetSwarmDodgeResolver", botDodge);
        Assert.Contains("Swarm.Crossfire.DodgeSnapshot, id, botId, position, area, now", botDodge);
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
            root, "game_server", "Services", "SwarmArenaStates.cs");
        string arena = ReadNormalizedSource(root, "game_server", "Matches", "MatchArenaService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Services", "CrossfireService.cs");

        // 매치 하나에 모니터 하나 — 블로킹 진입과 펄스용 TryEnter가 같은 잠금 객체를 쓴다.
        Assert.DoesNotContain("_globalExecutionLock", store);
        Assert.Contains("Monitor.Enter(Sync);", store);
        Assert.Contains("Monitor.TryEnter(Sync, ref lockTaken);", store);
        Assert.Contains("Monitor.Exit(Sync);", store);
        Assert.Contains("if (IsMatchTerminal(matchingId))", arena);

        Assert.DoesNotContain("_swarmCriticalRng", arena);
        Assert.DoesNotContain("_swarmCriticalRng", crossfire);
        Assert.Contains("private readonly Random _criticalRng = new();", runtimeStates);
        string damage = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatDamageService.cs");
        Assert.Contains(".Pacing.RollCritical(", damage);
        Assert.Contains("runtime.Pacing.RollCritical(", damage);

        string field = ReadNormalizedSource(root, "game_server", "Matches", "MatchFieldService.cs");
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
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchArenaService.cs");
        return ReadMethodSlice(
            source,
            "public void ProcessSwarmArenaForMatching(",
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
            .Replace("\r\n", "\n")
            // 표기 차이만 정규화하고 잠금·상태 확정·발행 순서 검사는 유지한다.
            .Replace("matchRuntimes.Enter(matchingId, out var scope)",
                "matchRuntimes.Enter(matchingId, out MatchScope scope)", StringComparison.Ordinal);
    }
}
