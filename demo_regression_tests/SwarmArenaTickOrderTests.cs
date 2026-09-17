namespace demo_regression_tests;

public sealed class SwarmArenaTickOrderTests
{
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
                "if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation || !runtime.IsGameplayActive(nowUtc))"));
        AssertInOrder(
            inactiveGameplayBranch,
            "return;");

        AssertInOrder(
            arenaTick,
            "!runtime.IsGameplayActive(nowUtc)",
            "orbTrails.UpdateTrails(",
            "trailCuts.ProcessTick(",
            "orbAttacks.ProcessTick(",
            "monsterAttacks.ProcessTick(",
            "botBehavior.UpdateSleep(",
            "ApplySleepRecovery(",
            "ProcessDoorInteractions(",
            "botBehavior.ProcessOrbGrowth(",
            "matchResults.TryEndOnScoreTimeout(");

        // 오브 공격 단계 안의 순서: 이미 깔린 공격을 먼저 정산하고, 생존자가 발동한 바람 칼날은 같은 틱에 적용한다.
        string orbAttackTick = ReadMethodSlice(
            ReadNormalizedSource(FindRepositoryRoot(), "game_server", "Matches", "MatchOrbAttackService.cs"),
            "public void ProcessTick(",
            "    internal void ProcessWaveAttacks(");
        AssertInOrder(
            orbAttackTick,
            "ProcessWaveAttacks(runtime,",
            "ProcessSunAttacks(runtime,",
            "ProcessSunBurns(runtime,",
            "playerOrbs.ActivateOrbs(",
            "ProcessWindAttacks(runtime,");

        // 오브 표시와 순위는 틱 끝 동기화가 수집해 보낸다. 표시는 입장 뒤, 순위는 맨 끝에 나간다.
        string syncSource = ReadNormalizedSource(FindRepositoryRoot(), "game_server", "Matches", "MatchSynchronizationService.cs");
        AssertInOrder(
            ReadMethodSlice(syncSource, "public void ProcessTick(", "    internal void CollectInteractableUpdates("),
            "CollectOrbVisuals(runtime,",
            "CollectOrbRankings(runtime,",
            "SendBatch(runtime,");
        AssertInOrder(
            ReadMethodSlice(syncSource, "internal void SendBatch(", "    internal static void SendPendingCombatHits("),
            "session.SendObjectEntries(",
            "session.SendOrbVisualStates(",
            "Protocol.G_TO_C_ORB_RANKINGS");
    }

    [Fact]
    public void ScheduledClosureTick_SendsEachStepInWireOrderInsideMatchLock()
    {
        string root = FindRepositoryRoot();
        string composition = ReadNormalizedSource(root, "game_server", "Program.cs");
        string field = ReadNormalizedSource(root, "game_server", "Matches", "MatchFieldService.cs");
        Assert.Contains("combat, field, movement, monsterSpawns, synchronization, clock)", composition);
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

        // 폐쇄 처리는 문 상태만 바꾼다. 문 변경 전송은 틱 끝 동기화가 담당한다.
        Assert.DoesNotContain("PacketMaker.G_TO_C_DOOR_STATE_UPDATE(", tick);
        AssertInOrder(
            tick,
            "closures.InitializeMatching(",
            "Protocol.G_TO_C_SWARM_FIELD_STATE",
            "closures.CloseDueAreas()",
            "Area closed:",
            "Protocol.G_TO_C_AREA_CLOSED",
            "runtime.Doors.CloseDoorsForAreas(",
            "DestroyOrbsFromOrdinal(",
            "SendOrbUpdate(",
            "Protocol.G_TO_C_ORB_TAIL_CUT");
        Assert.DoesNotContain("catch", tick);
    }

    private static string ReadSwarmArenaTick()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "Matches", "MatchCombatService.cs");
        return ReadMethodSlice(
            source,
            "public void ProcessTick(",
            "\n}");
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
