using game_server.matches;
using System.Collections.Immutable;
using game_server;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmBotMovementPlanTests
{
    [Fact]
    public void PrepareExternalMovement_FreezesMutableMovementAndRecipientOrder()
    {
        const long matchingId = 44_001;
        var position = new Vector3f(10f, 20f, 0f);
        var velocity = new Vector3f(3f, 4f, 0f);
        var fromCell = new Cell(1, 2);
        var toCell = new Cell(3, 4);
        var movement = new BotMovementEvent
        {
            BotPlayerId = -10,
            FromArea = AreaType.S2Gym1,
            ToArea = AreaType.S2Ground,
            FromCell = fromCell,
            ToCell = toCell,
            Position = position,
            Velocity = velocity,
            Rotation = 17f,
            IsAreaTransition = true
        };
        var observers = new List<SwarmBotObserverSnapshot>
        {
            new(0, 101, AreaType.S2Gym1, false, null),
            new(1, 201, AreaType.S2Ground, false, null),
            new(2, 102, AreaType.S2Gym1, false, null),
            new(3, 202, AreaType.S2Ground, false, null)
        };

        SwarmBotMovementPlan plan = CreateCoordinator().PrepareExternalMovement(
            TestGameEventLogs.Create(),
            movement,
            observers);

        position.X = 999f;
        velocity.Y = 999f;
        fromCell.X = 999;
        toCell.Y = 999;
        movement.Rotation = 999f;
        observers.Clear();

        SwarmBotMovementDispatch publication = Assert.Single(plan.Movements);
        Assert.Equal(new SwarmVectorSnapshot(10f, 20f, 0f), publication.Position);
        Assert.Equal(new SwarmVectorSnapshot(3f, 4f, 0f), publication.Velocity);
        Assert.Equal(new SwarmCellSnapshot(3, 4), publication.ToCell);
        Assert.Equal(17f, publication.Rotation);
        Assert.Equal([0, 2], publication.LeaveRecipientOrdinals.ToArray());
        Assert.Equal([1, 3], publication.DestinationRecipientOrdinals.ToArray());
    }

    [Fact]
    public void PlayerInfoSnapshot_FreezesNestedCollectionsAndCell()
    {
        var source = new PlayerInfo
        {
            PlayerId = -20,
            Name = "Bot",
            WearItemIdList = [101, 202]
        };
        var sourceObject = new GameObjectInfo { Cell = new Cell(7, 8), Position = new Vector3f(1.25f, 2.75f, 0) };

        SwarmBotPlayerInfoSnapshot snapshot = SwarmBotPlayerInfoSnapshot.Capture(source, sourceObject);
        source.WearItemIdList[0] = 999;
        sourceObject.Cell.X = 999;
        sourceObject.Position.X = 999;

        PlayerInfo firstProjection = snapshot.ToPlayerInfo();
        GameObjectInfo firstObject = snapshot.ToGameObjectInfo();
        Assert.Equal([101, 202], firstProjection.WearItemIdList);
        Assert.Equal(new Cell(7, 8), firstObject.Cell);
        Assert.Equal(1.25f, firstObject.Position.X);

        firstProjection.WearItemIdList.Clear();
        firstObject.Cell.Y = 999;
        firstObject.Position.Y = 999;
        PlayerInfo secondProjection = snapshot.ToPlayerInfo();
        GameObjectInfo secondObject = snapshot.ToGameObjectInfo();
        Assert.Equal([101, 202], secondProjection.WearItemIdList);
        Assert.Equal(new Cell(7, 8), secondObject.Cell);
        Assert.Equal(2.75f, secondObject.Position.Y);
    }

    [Fact]
    public void CapturedOrdinalLookup_PreservesOrderAndSkipsInvalidSlots()
    {
        string[] snapshot = ["first", "second", "third"];
        ImmutableArray<int> ordinals = [2, -1, 0, 99, 2];
        var resolved = new List<string>();

        foreach (int ordinal in ordinals)
        {
            if (game_server.network.SessionSnapshotDelivery.TryGetCapturedValue(snapshot, ordinal, out string value))
                resolved.Add(value);
        }

        Assert.Equal(["third", "first", "third"], resolved);
    }

    [Fact]
    public void MovementSources_KeepPrepareTransportFreeAndDispatchInPacketOrder()
    {
        string root = FindRepositoryRoot();
        string coordinator = ReadNormalizedSource(
            root, "game_server", "Services", "Bots", "SwarmBotMovementCoordinator.cs");
        string server = ReadNormalizedSource(root, "game_server", "Services", "Bots", "BotMovementService.cs");
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickRunner.cs");
        string tick = ReadMethodSlice(
            combat,
"public void Run(MatchRuntime runtime)",
            "private static bool ShouldMoveBots(");
        string process = ReadMethodSlice(
            server,
            "public void Process(",
            "private void PublishBotMovementMetrics(");
        string dispatch = ReadMethodSlice(
            server,
            "private void DispatchSwarmBotMovementPlan(",
            "public void DispatchExternalMovement(");

        Assert.DoesNotContain("PacketMaker", coordinator);
        Assert.DoesNotContain("MessagePackSerializer", coordinator);
        Assert.DoesNotContain("GameClientSession", coordinator);
        Assert.DoesNotContain(".TrySend(", coordinator);
        Assert.DoesNotContain("ReservePublication", coordinator);
        Assert.DoesNotContain("DispatchInOrder", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", server);
        // 봇 걸음은 별도 타이머가 아니라 50ms 매치 틱이 전투 뒤에 같은 잠금 안에서 잇는다 — 두 타이머가
        // 같은 주기로 맞물려 뒤에 오는 쪽이 매 펄스 잠금을 놓치던 문제의 재발 방지.
        Assert.DoesNotContain("StartBotMovementTimer", server);
        Assert.DoesNotContain("Task.Run(", server);
        // 바쁜 펄스는 버리고(따라잡기 없음) 스킵만 센다; 잠금 안에서 전투→걸음, 준비→송신이 한 순서다.
        AssertInOrder(
            tick,
            "matchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "RecordBotTickBusySkip(matchingId);",
            "return;",
            "using (scope)",
            "scope.Runtime.IsTerminal",
            "processCombat(matchingId, activeSessions);",
            "ShouldMoveBots(scope.Runtime)",
            "moveBots(scope.Runtime);");
        AssertInOrder(
            process,
            "runtime.Sessions.Snapshot()",
            "CaptureSwarmBotObservers(matchingId, sessionSnapshot)",
            "BotMovement.PrepareTick(",
            "DispatchSwarmBotMovementPlan(plan, sessionSnapshot)",
            "BotTickMetrics.Record(",
            "PublishBotMovementMetrics(batch)");
        Assert.Contains("session.CurrentMapId == Config.SWARM_MATCH_MAP", process);
        Assert.DoesNotContain(".TrySend(", process);
        AssertInOrder(
            dispatch,
            "G_TO_C_AREA_PLAYER_LEAVE",
            "G_TO_C_AREA_PLAYER_ENTER",
            "G_TO_C_MOVE",
            "G_TO_C_ENCOUNTER_REVEAL",
            "G_TO_C_GROUND_ITEM_REMOVED");
        Assert.DoesNotContain("plan.AutoEquips", dispatch);
    }

    [Fact]
    public void DummySetup_UsesLockedExternalPlanWithoutOrbitAdvance()
    {
        string root = FindRepositoryRoot();
        string arena = ReadNormalizedSource(root, "game_server", "Matches", "MatchArenaService.cs");
        string coordinator = ReadNormalizedSource(
            root, "game_server", "Services", "Bots", "SwarmBotMovementCoordinator.cs");
        string adminSetup = ReadMethodSlice(
            arena,
            "public object SetupSwarmCutDummy(long matchingId)",
            "private object SetupSwarmCutDummyCore(");
        string external = ReadMethodSlice(
            ReadNormalizedSource(root, "game_server", "Services", "Bots", "BotMovementService.cs"),
            "public void DispatchExternalMovement(",
            "private static double CalculatePercentile(");
        string externalPrepare = ReadMethodSlice(
            coordinator,
            "public SwarmBotMovementPlan PrepareExternalMovement(",
            "private SwarmBotMovementPlan PrepareResult(");

        AssertInOrder(
            adminSetup,
            "matchRuntimes.Enter(matchingId, out MatchScope scope)",
            "scope.Runtime.IsTerminal",
            "SetupSwarmCutDummyCore(",
            "botMovement.DispatchExternalMovement(scope.Runtime, movement)");
        AssertInOrder(
            external,
            "runtime.Sessions.Snapshot()",
            "CaptureSwarmBotObservers(matchingId, sessionSnapshot)",
            "PrepareExternalMovement(",
            "DispatchSwarmBotMovementPlan(plan, sessionSnapshot)");
        Assert.Contains("advanceOrbOrbit: false", externalPrepare);
        Assert.Contains("session.CurrentMapId == Config.SWARM_MATCH_MAP", external);
        Assert.DoesNotContain("PacketMaker", external);
        Assert.DoesNotContain("BroadcastBotMovement(", arena);
    }

    private static SwarmBotMovementCoordinator CreateCoordinator()
    {
        var matches = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        return matches.GetOrCreate(44_001).BotMovement;
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(
                marker,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"Expected '{marker}' in order.");
            previousIndex = currentIndex;
        }
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find start marker '{startMarker}'.");
        int endIndex = source.IndexOf(
            endMarker,
            startIndex + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find end marker '{endMarker}'.");
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
        return File.ReadAllText(Path.Combine(fullPathParts)).Replace("\r\n", "\n");
    }
}
