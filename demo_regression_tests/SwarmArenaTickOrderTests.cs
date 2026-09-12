using game_server.matches;
using game_server.matches.monsters;
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
            "entryFailureHandler, combat, field, botMovement, botDecisions, clock);",

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
            root, "game_server", "Matches", "MatchOrbVisual.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string settlement = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchFieldService.cs");

        string proximityTick = ReadMethodSlice(
            ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs"),
            "internal void ProcessTick()",
            "\n}");
        AssertInOrder(
            proximityTick,
            "using var scope = runtime.Enter();",
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
            "field.ProcessDamageTick(runtime);",
            "runtime.IsEnded ||",
            "botMovement.ProcessTick(runtime, botPlayerId => botDecisions.DecideMovement(runtime, botPlayerId));");

        string matchingSettlement = ReadMethodSlice(
            settlement,
            "public void ProcessDamageTick(",
            "internal static int GetDamagePerTick(");
        AssertInOrder(
            matchingSettlement,
            "long matchingId = runtime.MatchingId;",
            "var alivePlayers = runtime.GetAlivePlayers()",
            "healthService.ApplyDamage(runtime, target.Player, target.Damage, handleElimination: false);",
            "var lethalTargets = targets",
            "foreach (var candidate in eliminationBestToWorst.AsEnumerable().Reverse())",
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
            "monsters.Tick(runtime,",
            "if (!runtime.IsGameplayActive())");

        string inactiveGameplayBranch = MaskCommentsAndLiterals(
            ReadBracedBlockAfterMarker(
                arenaTick,
                "if (!runtime.IsGameplayActive())"));
        AssertInOrder(
            inactiveGameplayBranch,
            "SendMonsterSnapshots(",
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
            "SendMonsterSnapshots(",
            "return;");

        AssertInOrder(
            arenaTick,
            "monsters.Tick(runtime,",
            "if (!runtime.IsGameplayActive())",
            "orbTrails.UpdateTrails(",
            "trailCuts.ProcessTick(",
            "orbAttacks.ProcessWaveDetonations(",
            "playerOrbs.ActivateWaveOrbs(",
            "playerOrbs.ActivateWindOrbs(",
            "ProcessSunBurns(",
            "ApplySwarmParticipantDamage(",
            "botDecisions.UpdateSleep(",
            "ApplySleepRecovery(",
            "ProcessSwarmBotDoorUnlocks(",
            "SendMonsterSnapshots(",
            "actorBuilder.Build(",
            "playerOrbs.ProcessOrbRecovery(",
            "MatchOrbVisual.Build(",
            "matchResults.BroadcastOrbRankings(",
            "botDecisions.ProcessBotOrbGrowth(",
            "matchResults.TryEndOnScoreTimeout(",
            "ProcessPendingMonsterHits(",
            "ProcessSunCrossfires(",
            "ProcessPendingPvpHits(",
            "CollectSunCrossfireCappedOwners(",
            "CollectSunCrossfireAnchoredTargets(",
            "autoAttacks.UpdateAttacks(",
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
            root, "game_server", "Matches", "MatchCombatDamageService.cs");
        string sessionMatchEnd = ReadNormalizedSource(
            root, "game_server", "Players", "PlayerEliminationService.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string proximity = ReadNormalizedSource(
            root, "game_server", "Matches", "MatchOrbVisual.cs");

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
            ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.Orb.cs"),
            "internal void SendOrbVisualStateIfChanged(",
            "internal void ForgetOrbVisualState(");
        AssertInOrder(
            orbPublicationSteps,
            "_lastSentOrbVisualStates[visual.ActorPlayerId] = visual;",
            "Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE)",
            "TrySend(packet);");
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
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatService.cs");
        string cleanupBody = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        Assert.Contains("Monsters.Release();", cleanupBody);
        Assert.DoesNotContain("CleanupSwarmArenaState", source);
        Assert.DoesNotContain("_swarmMatchRuntimes", source);
        Assert.DoesNotContain("ClearSunOrbAttackState", cleanupBody);
        Assert.DoesNotContain("ClearWindOrbAttackState", cleanupBody);
        Assert.DoesNotContain("ClearOrbUpgradeState", cleanupBody);
    }

    [Fact]
    public void ScheduledClosureTick_SendsEachStepInWireOrderInsideMatchLock()
    {
        string root = FindRepositoryRoot();
        string composition = ReadNormalizedSource(root, "game_server", "Program.cs");
        string field = ReadNormalizedSource(root, "game_server", "Matches", "MatchFieldService.cs");
        Assert.Contains("combat, field, botMovement, botDecisions, clock)", composition);
        string tick = ReadMethodSlice(
            field,
            "public void ProcessClosureTick(",
            "    public void ProcessDamageTick(");

        // 독립 루프의 매치 잠금 안에서 1초 주기를 확인하고 폐쇄 틱을 돌린다.
        string runner = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        AssertInOrder(runner,
            "using var scope = runtime.Enter();",
            "_lastAreaClosureSecond = elapsedSeconds;",
            "field.ProcessClosureTick(runtime);");
        Assert.DoesNotContain("TryEnter", tick);

        // 같은 잠금 안에서 단계마다 상태를 바꾼 직후 그 패킷을 보낸다. 전송 실패는 상태를 되돌리지 않는다.
        AssertInOrder(
            tick,
            "closures.InitializeMatching(",
            "Protocol.G_TO_C_SWARM_FIELD_STATE",
            "closures.CloseDueAreas()",
            "eventLogs.LogClosure(",
            "Protocol.G_TO_C_AREA_CLOSED",
            "runtime.Doors.CloseDoorsForAreas(",
            "PacketMaker.G_TO_C_DOOR_STATE_UPDATE(",
            "DestroyOrbsFromOrdinal(",
            "SendOrbUpdate(",
            "Protocol.G_TO_C_ORB_RING_EFFECT",
            "eventLogs.LogSystem(");
        Assert.DoesNotContain("catch", tick);
    }

    [Fact]
    public void OrbServices_UsePlayerTimingAndMatchOwnedVictimEffects()
    {
        string root = FindRepositoryRoot();
        string windBlade = ReadNormalizedSource(root, "game_server", "Players", "PlayerOrbService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "MatchOrbAttackService.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "Players", "PlayerOrbGrowthService.cs");

        Assert.DoesNotContain("_swarmWindBladeNextTickAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeEngagedAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeVictimImmuneUntilUtc", windBlade);
        Assert.DoesNotContain("_swarmWindWoundsUntilUtc", crossfire);
        Assert.DoesNotContain("_swarmFamilyUpgradeCounts", orbBoard);
        Assert.Contains("participant.TryClaimWindShock(", windBlade);
        Assert.Contains("owner.TryBeginWindOrbTick(", windBlade);
        Assert.Contains("owner.HasCompletedWindOrbSpinup(", windBlade);
        Assert.Contains("player.GetOrbUpgradeCount(orbGroupId)", orbBoard);
        Assert.DoesNotContain("MatchRuntimeStore", orbBoard);
    }

    [Fact]
    public void SunCrossfireShapes_AreMatchOwnedAndDodgeLookupDoesNotCreateRuntime()
    {
        string root = FindRepositoryRoot();
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "MatchOrbAttackService.cs");
        string botDodge = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        string runtimeStates = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");

        Assert.DoesNotContain("_swarmCrossfireShapes", crossfire);
        Assert.DoesNotContain("DodgeSnapshot", crossfire);
        Assert.DoesNotContain("_swarmCrossfireEventSeq", crossfire);
        Assert.DoesNotContain("_swarmSunBurns", crossfire);
        Assert.DoesNotContain("_swarmCrossfireConvergeWindows", crossfire);
        Assert.DoesNotContain("ClearSunOrbAttackState(", crossfire);
        Assert.Contains("var shapes = runtime.SunCrossfireShapes;", crossfire);
        Assert.DoesNotContain("MatchRuntimeStore matchRuntimes", crossfire);
        Assert.Contains("public List<SwarmCrossfireShape> SunCrossfireShapes { get; } = new();", botDodge);

        Assert.Contains("new BotPlayerManager(matchingId, logger, Doors, SunCrossfireShapes, eventLogs)", botDodge);
        string botMovement = ReadNormalizedSource(root, "game_server", "Players", "Bots", "BotPlayerManager.Movement.cs");
        Assert.Contains("_sunCrossfireShapes, bot.PlayerId, bot.Player.Position!, bot.Player.CurrentArea, now", botMovement);
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
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatService.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "Matches", "MatchOrbAttackService.cs");

        // 매치 하나에 모니터 하나 — 블로킹 진입과 펄스용 TryEnter가 같은 잠금 객체를 쓴다.
        Assert.DoesNotContain("_globalExecutionLock", store);
        Assert.Contains("Monitor.Enter(MatchLock);", store);
        Assert.Contains("Monitor.TryEnter(MatchLock, ref lockTaken);", store);
        Assert.Contains("Monitor.Exit(MatchLock);", store);
        string wave = ReadNormalizedSource(root, "game_server", "Matches", "MatchOrbAttackService.cs");
        Assert.Contains("if (runtime.IsEnded) return;", wave);

        Assert.DoesNotContain("_swarmCriticalRng", combat);
        Assert.DoesNotContain("_swarmCriticalRng", crossfire);
        Assert.DoesNotContain("_criticalRng", runtimeStates);
        string damage = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatDamageService.cs");
        Assert.Contains("runtime.CombatDamage.CriticalRng.NextDouble()", damage);
        Assert.DoesNotContain("new Random()", damage);
        Assert.Contains("RollCritical(runtime, Config.SWARM_WIND_WOUND_CRIT_CHANCE)", damage);
        Assert.DoesNotContain("MatchRuntimeStore", damage);

        string field = ReadNormalizedSource(root, "game_server", "Matches", "MatchFieldService.cs");
        Assert.Contains(
            "Lazy<IReadOnlyList<(AreaType Area, int ClosureAtSeconds)>> SwarmFieldClosureSchedule",
            field);
        Assert.Equal(1, CountOccurrences(field, "LazyThreadSafetyMode.ExecutionAndPublication"));
        Assert.DoesNotContain("SwarmFieldClosureSchedule ??=", field);
    }

    private static string ReadSwarmArenaTick()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatService.cs");
        return ReadMethodSlice(
            source,
            "public void ProcessTick(",
            "    internal void ApplySwarmParticipantDamage(");
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
