namespace demo_regression_tests;

public sealed class SwarmArenaTickOrderTests
{
    [Fact]
    public void ProximityAutoCombatTimer_UsesFiftyMillisecondMatchLockPulse()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(
            root, "game_server", "GameServer.ProximityAutoCombat.cs");

        Assert.Contains("private const int ProximityAutoCombatTickIntervalMs = 50;", source);
        Assert.Contains("ProcessProximityAutoCombatTick,", source);
        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs)"));

        string tickBody = ReadMethodSlice(
            source,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");
        // 바쁜 매치는 이 펄스를 버리고(TryEnter), 들어간 매치는 잠금 안에서 틱을 돌린다.
        AssertInOrder(
            tickBody,
            "_sessionRegistry.SnapshotWhere(",
            "MatchRuntimes.ActiveIds()",
            "MatchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "continue;",
            "using (scope)",
            "scope.Runtime.IsTerminal",
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);");
        Assert.DoesNotContain("MatchRuntimes.Enter(", tickBody);

        string matchingBody = ReadMethodSlice(
            source,
            "private void ProcessProximityAutoCombatForMatching(",
            "private static void AddInventoryCombatActors(");
        Assert.Contains(
            "ProcessSwarmArenaForMatching(matchingId, activeSessions);",
            matchingBody);
    }

    [Fact]
    public void CombatEntrypoints_KeepResourceCombatBeforeSettlementInsideMatchGate()
    {
        string root = FindRepositoryRoot();
        string proximity = ReadNormalizedSource(
            root, "game_server", "GameServer.ProximityAutoCombat.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string settlement = ReadNormalizedSource(
            root, "game_server", "GameServer.MatchSettlement.cs");

        string proximityTick = ReadMethodSlice(
            proximity,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");
        AssertInOrder(
            proximityTick,
            "_sessionRegistry.SnapshotWhere(",
            "activeMatchingIds = MatchRuntimes.ActiveIds();",
            "Proximity auto combat snapshot failed",
            "foreach (long matchingId in activeMatchingIds)",
            "MatchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "try",
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);",
            "catch (Exception ex)",
            "MatchingId={MatchingId}");
        Assert.DoesNotContain("_proximityAutoCombatProcessing", proximity);
        Assert.DoesNotContain("Interlocked.Exchange(", proximityTick);
        Assert.DoesNotContain("Volatile.Write(", proximityTick);
        Assert.DoesNotContain("PrepareAndDispatch", proximity);
        Assert.DoesNotContain("Coordinator", proximity);

        // 5초 정산 틱은 잠금을 기다린다 — 전투 펄스보다 드물어 버릴 이유가 없다.
        string resourceTick = ReadMethodSlice(
            server,
            "private void ProcessResourceTick(object? state)",
            "/// <summary>\n    ///     #26: 봇 탈락 처리 + 게임 종료 판정.");
        AssertInOrder(
            resourceTick,
            "_sessionRegistry.SnapshotWhere(",
            "MatchRuntimes.ActiveIds()",
            "if (!MatchStartGate.IsGameplayActive(matchingId))",
            "MatchRuntimes.Enter(matchingId, out MatchScope scope)",
            "scope.Runtime.IsTerminal",
            "ProcessResourceTickForMatching(matchingId, activeSessions);",
            "catch (Exception ex)");
        Assert.DoesNotContain("TryEnter", resourceTick);

        string matchingSettlement = ReadMethodSlice(
            settlement,
            "private void ProcessResourceTickForMatching(",
            "private void CleanupMatchSettlementState(");
        AssertInOrder(
            matchingSettlement,
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);",
            "var humans = activeSessions",
            "var bots = _botPlayerManager.GetBots(matchingId)",
            "target.Session.ModifyStats(",
            "var eliminatedTargets = targets",
            "foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())",
            "target.Session.EliminateForSettlement(",
            "ProcessBotElimination(",
            "_matchRosterManager.CheckGameOver(matchingId)",
            "resultHost.TryEndMatch(winnerId.Value, resolution.DecisiveCriterion);");
        Assert.DoesNotContain("Enter(", matchingSettlement);
        Assert.DoesNotContain("PublicationTurn", matchingSettlement);
    }

    [Fact]
    public void SwarmArenaTick_RunsDirectorBeforeGameplayGate()
    {
        string arenaTick = ReadSwarmArenaTick();
        string arenaCode = MaskCommentsAndLiterals(arenaTick);

        AssertInOrder(
            arenaCode,
            "_swarmMonsterDirector.Tick(",
            "if (!MatchStartGate.IsGameplayActive(matchingId))");

        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTick,
                "if (!MatchStartGate.IsGameplayActive(matchingId))"));
        AssertInOrder(
            inactiveGameplayBranch,
            "BroadcastMonsterMinimapSnapshot(",
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
            "BroadcastMonsterMinimapSnapshot(",
            "return;");

        AssertInOrder(
            arenaTick,
            "GrantSwarmStartingOrbs(matchingId, session.PlayerId.Value, session);",
            "session.SendSummonStoneState();",
            "SetupSwarmCutDummy(matchingId);",
            "session.Send(leavePacket);",
            "_swarmMonsterDirector.Tick(",
            "if (!MatchStartGate.IsGameplayActive(matchingId))",
            "UpdateSwarmOrbTrails(",
            "ProcessSwarmTrailCuts(",
            "ProcessSwarmRetaliationWindows(",
            "ProcessSwarmWaveBombs(",
            "ProcessSwarmWindBlades(",
            "ProcessSwarmSunBurns(",
            "ApplySwarmParticipantDamage(",
            "ProcessSwarmBotRecovery(",
            "ProcessSwarmSleepRecovery(",
            "ProcessSwarmBotExplores(",
            "ProcessSwarmBotDoorUnlocks(",
            "BroadcastMonsterMinimapSnapshot(",
            "BuildSwarmArenaCombatActors(",
            "ProcessOrbRecovery(",
            "DispatchOrbVisualStatePublications(",
            "BroadcastSwarmOrbRankings(",
            "ProcessSwarmGrowthOffers(",
            "ProcessSwarmScoreTimeout(",
            "ProcessPendingSwarmMonsterHits(",
            "ProcessSwarmCrossfires(",
            "ApplySwarmPvpAttack(matchingId, pending.Attack",
            "CollectSwarmCrossfireCappedOwners(",
            "CollectSwarmCrossfireAnchoredTargets(",
            "_proximityAutoCombatResolver.Resolve(",
            "TryScheduleSwarmCrossfire(",
            "attackerSession?.SendSwarmAfterimageMonsterAttackFeedback(",
            "BroadcastSwarmAttackVfxToTargetAndObservers(",
            "_botPlayerManager.TryFinalizeProximityAutoCombatElimination(",
            "ProcessBotElimination(");
    }

    [Fact]
    public void CombatPublicationSeams_CharacterizeStateCouplingAndFailureBoundaries()
    {
        string root = FindRepositoryRoot();
        string playerState = ReadNormalizedSource(
            root, "game_server", "Network", "GameClientSession.PlayerState.cs");
        string sessionCombat = ReadNormalizedSource(
            root, "game_server", "Network", "GameClientSession.ProximityAutoCombat.cs");
        string sessionMatchEnd = ReadNormalizedSource(
            root, "game_server", "Network", "GameClientSession.MatchEnd.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string proximity = ReadNormalizedSource(
            root, "game_server", "GameServer.ProximityAutoCombat.cs");

        string modifyStats = ReadMethodSlice(
            playerState,
            "public void ModifyStats(",
            "private void SendPlayerStatsUpdate(");
        AssertInOrder(
            modifyStats,
            "Corruption = Math.Clamp(",
            "SendPlayerStatsUpdate(",
            "CheckResourceElimination(");

        string applyProximityHit = ReadMethodSlice(
            sessionCombat,
            "internal void ApplyProximityAutoCombatHit(",
            "internal void SendProximityAutoCombatAttackFeedback(");
        AssertInOrder(
            applyProximityHit,
            "_gameEventLogManager.LogHit(",
            "ModifyStats(corruptionDelta: damage, attackerPlayerId: sourcePlayerId);",
            "SendEncounterEvent(");

        string orbPublicationSteps = ReadMethodSlice(
            proximity,
            "private void DispatchOrbVisualStatePublications(",
            "private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(");
        AssertInOrder(
            orbPublicationSteps,
            "foreach (SwarmOrbVisualPublication publication in publications)",
            "CommitAndDispatchOrbVisualStatePublication(publication)",
            "_orbVisualStates[key] = state;",
            "Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE)",
            "publication.Recipient!.Send(packet);");
        Assert.False(ContainsCodeToken(orbPublicationSteps, "catch"));

        string botElimination = ReadMethodSlice(
            server,
            "private void ProcessBotElimination(",
            "private void DropBotInventoryAtCurrentPosition(");
        AssertInOrder(
            botElimination,
            "try",
            "_matchRosterManager.TryEliminatePlayer(",
            "DropBotInventoryAtCurrentPosition(",
            "foreach (var s in matchingSessions) s.Send(eliminatedPacket);",
            "catch (Exception ex)");
        Assert.DoesNotContain("BestEffortGroup", botElimination);

        string humanElimination = ReadMethodSlice(
            sessionMatchEnd,
            "private void ProcessElimination(",
            "internal void EliminateForSettlement(");
        AssertInOrder(
            humanElimination,
            "_matchRosterManager.TryEliminatePlayer(",
            "eliminatedSession.DropAllInventoryAtCurrentPosition();",
            "session.Send(resultPacket);",
            "session.Send(eliminatedPacket);",
            "session.Send(leavePacket);",
            "_matchRosterManager.CheckGameOver(",
            "SendGameResult(");
        Assert.False(ContainsCodeToken(humanElimination, "catch"));
    }

    [Fact]
    public void GrowthOfferFlow_DelegatesLifecycleAndOwnershipToCoordinator()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string tickBody = ReadMethodSlice(
            source,
            "private void ProcessSwarmGrowthOffers(",
            "private int GetSwarmTopOrbCount(");
        string pickBody = ReadMethodSlice(
            source,
            "internal void HandleSwarmGrowthPick(",
            "private bool ApplySwarmGrowthCard(");

        AssertInOrder(
            tickBody,
            ".EvaluateStanding(",
            ".EvaluateFunding(",
            ".AllocateOfferId()",
            ".RegisterOffer(");
        Assert.Contains(".TryApplyPick(", pickBody);
        Assert.DoesNotContain(".GrowthOffers.Offers", tickBody);
        Assert.DoesNotContain(".GrowthOffers.Offers", pickBody);
    }

    [Fact]
    public void SwarmCleanup_AlwaysDropsMatchOwnedRuntimeAfterMonsterCleanup()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string cleanupBody = ReadMethodSlice(
            source,
            "private void CleanupSwarmArenaState(",
            "private List<ProximityCombatActor> BuildSwarmArenaCombatActors(");

        AssertInOrder(
            cleanupBody,
            "try",
            "_swarmMonsterDirector.RemoveMatching(matchingId);",
            "finally",
            "_swarmMatchRuntimes.Remove(matchingId);");
        Assert.DoesNotContain("ClearSwarmCrossfireState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmWindBladeState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmOrbBoardState", cleanupBody);
    }

    [Fact]
    public void ScheduledClosureTick_CommitsStateThenPublishesInsideMatchLock()
    {
        string root = FindRepositoryRoot();
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string tick = ReadMethodSlice(
            server,
            "private void ProcessAreaClosureTick(object? state)",
            "// ===== 타겟 위치 전송 =====");
        string prepare = ReadMethodSlice(
            arena,
            "private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(",
            "private static ImmutableArray<int> CaptureSwarmClosureRecipientOrdinals(");
        string dispatch = ReadMethodSlice(
            arena,
            "private void DispatchSwarmClosurePublicationPlan(",
            "private void PrepareDestroySwarmOrbsInClosedAreas(");
        string orbPrepare = ReadMethodSlice(
            arena,
            "private void PrepareDestroySwarmOrbsInClosedAreas(",
            "// 쌍 깔때기:");

        // 매치 잠금 안에서 상태 확정 → 같은 순서로 송신 (#331). 폐쇄는 1초 틱이라 잠금을 기다린다.
        AssertInOrder(
            tick,
            "MatchRuntimes.Enter(matchingId, out MatchScope scope)",
            "scope.Runtime.IsTerminal",
            "GetSessionsByMatch(matchingId).ToArray();",
            "PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);",
            "DispatchSwarmClosurePublicationPlan(plan, sessionSnapshot)");
        Assert.DoesNotContain("TryEnter", tick);
        Assert.DoesNotContain(".Send(", tick);

        AssertInOrder(
            prepare,
            "_areaClosureManager.InitializeMatching(",
            "new SwarmFieldStateOutbound(",
            "_areaClosureManager.CheckClosureSchedule(matchingId)",
            "new SwarmClosureWarningOutbound(",
            "_gameEventLogManager.LogClosure(",
            "new SwarmAreaClosedOutbound(",
            "_doorStateManager.CloseDoorsForAreas(",
            "new SwarmDoorStateOutbound(",
            "PrepareDestroySwarmOrbsInClosedAreas(",
            "new SwarmClosurePublicationPlan(");
        Assert.DoesNotContain("Packet.Create(", prepare);
        Assert.DoesNotContain("PacketMaker.", prepare);
        Assert.DoesNotContain(".Send(", prepare);
        Assert.Contains("Transport failure never rolls back", arena);

        AssertInOrder(
            orbPrepare,
            "DestroySwarmOrbsFromOrdinal(",
            ".OrbDurabilityBonus.Remove(",
            "new SwarmInventoryUpdateOutbound(",
            "new SwarmRingVfxOutbound(",
            "_gameEventLogManager.LogSystem(");
        Assert.DoesNotContain("Packet.Create(", orbPrepare);
        Assert.DoesNotContain("PacketMaker.", orbPrepare);
        Assert.DoesNotContain(".Send(", orbPrepare);
        Assert.DoesNotContain("SendInGameInventoryUpdate(", orbPrepare);
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
            "session.SendInGameInventoryUpdate(",
            "case SwarmRingVfxOutbound",
            "Protocol.G_TO_C_SWARM_ENCIRCLE_VFX");
        Assert.Contains("SendToCapturedRecipients(", dispatch);
        Assert.DoesNotContain("catch", dispatch);
        Assert.Contains("first transport exception", arena);
    }

    [Fact]
    public void WindBladeAndOrbBoardState_AreOwnedBySwarmMatchRuntime()
    {
        string root = FindRepositoryRoot();
        string windBlade = ReadNormalizedSource(root, "game_server", "GameServer.SwarmWindBlade.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "GameServer.SwarmOrbBoard.cs");

        Assert.DoesNotContain("_swarmWindBladeNextTickAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeEngagedAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeVictimImmuneUntilUtc", windBlade);
        Assert.DoesNotContain("_swarmWindWoundsUntilUtc", crossfire);
        Assert.DoesNotContain("_swarmFamilyUpgradeCounts", orbBoard);
        Assert.Contains("GetSwarmMatchRuntime(matchingId).WindBlade", windBlade);
        Assert.Contains("GetSwarmMatchRuntime(matchingId).OrbBoard", orbBoard);
    }

    [Fact]
    public void SwarmCrossfireState_IsMatchOwnedAndDodgeLookupDoesNotCreateRuntime()
    {
        string root = FindRepositoryRoot();
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");
        string botDodge = ReadNormalizedSource(root, "game_server", "GameServer.SwarmBotDodge.cs");
        string runtimeStates = ReadNormalizedSource(root, "game_server", "Services", "SwarmArenaStates.cs");

        Assert.DoesNotContain("_swarmCrossfireShapes", crossfire);
        Assert.DoesNotContain("_swarmCrossfireDodgeSnapshot", crossfire);
        Assert.DoesNotContain("_swarmCrossfireEventSeq", crossfire);
        Assert.DoesNotContain("_swarmSunBurns", crossfire);
        Assert.DoesNotContain("_swarmCrossfireConvergeWindows", crossfire);
        Assert.DoesNotContain("ClearSwarmCrossfireState(", crossfire);
        Assert.Contains("SwarmCrossfireState crossfire = GetSwarmMatchRuntime(matchingId).Crossfire;", crossfire);
        Assert.Contains("public SwarmCrossfireState Crossfire { get; }", runtimeStates);

        Assert.Contains("_swarmMatchRuntimes.TryGet(", botDodge);
        Assert.Contains("runtime.Crossfire.DodgeSnapshot", botDodge);
        Assert.DoesNotContain("GetSwarmMatchRuntime(", botDodge);
        Assert.DoesNotContain("GetOrCreate(", botDodge);
    }

    [Fact]
    public void MatchExecutionBoundary_UsesMatchOwnedRandomAndThreadSafeCachesWithoutGlobalLock()
    {
        string root = FindRepositoryRoot();
        string store = ReadNormalizedSource(
            root, "game_server", "Services", "MatchRuntimeStore.cs");
        string runtimeStates = ReadNormalizedSource(
            root, "game_server", "Services", "SwarmArenaStates.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");

        // 매치 하나에 모니터 하나 — 블로킹 진입과 펄스용 TryEnter가 같은 잠금 객체를 쓴다.
        Assert.DoesNotContain("_globalExecutionLock", store);
        Assert.Contains("Monitor.Enter(runtime.Sync);", store);
        Assert.Contains("Monitor.TryEnter(runtime.Sync, ref lockTaken);", store);
        Assert.Contains("Monitor.Exit(runtime.Sync);", store);
        Assert.Contains("if (IsMatchTerminal(matchingId))", arena);

        Assert.DoesNotContain("_swarmCriticalRng", arena);
        Assert.DoesNotContain("_swarmCriticalRng", crossfire);
        Assert.Contains("private readonly Random _criticalRng = new();", runtimeStates);
        Assert.Contains(".Pacing.RollCritical(", arena);
        Assert.Contains("runtime.Pacing.RollCritical(", crossfire);

        Assert.Contains(
            "Lazy<IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>>",
            arena);
        Assert.Contains(
            "Lazy<IReadOnlyList<ClosureWaveDefinition>> _swarmFieldDerivedWaves",
            arena);
        Assert.Equal(
            2,
            CountOccurrences(arena, "LazyThreadSafetyMode.ExecutionAndPublication"));
        Assert.DoesNotContain("_swarmAreaCellsByDistance == null", arena);
        Assert.DoesNotContain("_swarmFieldDerivedWaves ??=", arena);
    }

    private static string ReadSwarmArenaTick()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        return ReadMethodSlice(
            source,
            "private void ProcessSwarmArenaForMatching(",
            "private double GetSwarmSafeDistance(");
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
            .Replace("\r\n", "\n");
    }
}
