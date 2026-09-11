using System.Collections.Immutable;
using game_server;
using game_server.matches;
using game_server.matches.combat;
using game_server.players.bots;
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
        GameClientSession[] recipients = Enumerable.Range(0, 4).Select(_ => TestGameSessionServices.CreateRecipientSession()).ToArray();
        var observers = new List<SwarmBotObserverSnapshot>
        {
            new(recipients[0], 101, AreaType.S2Gym1),
            new(recipients[1], 201, AreaType.S2Ground),
            new(recipients[2], 102, AreaType.S2Gym1),
            new(recipients[3], 202, AreaType.S2Ground)
        };

        var match = CreateMatch();
        SwarmBotMovementPlan plan = match.Bots.PrepareExternalMovement(
            TestGameEventLogs.Create(),
            movement,
            [],
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
        Assert.Equal([recipients[0], recipients[2]], publication.LeaveRecipients.ToArray());
        Assert.Equal([recipients[1], recipients[3]], publication.DestinationRecipients.ToArray());
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
    public void MovementSources_KeepPrepareTransportFreeAndDispatchInPacketOrder()
    {
        string root = FindRepositoryRoot();
        string coordinator = ReadNormalizedSource(
            root, "game_server", "Players", "Bots", "BotPlayerManager.MovementPlan.cs");
        string server = ReadNormalizedSource(root, "game_server", "Players", "Bots", "BotMovementService.cs");
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "MatchTickLoop.cs");
        string tick = ReadMethodSlice(
            combat,
"internal void ProcessTick()",
            "\n}");
        string process = ReadMethodSlice(
            server,
            "public void ProcessTick(",
            "private void PublishBotMovementMetrics(");
        string dispatch = ReadMethodSlice(
            server,
            "private void DispatchSwarmBotMovementPlan(",
            "public void DispatchExternalMovement(");

        Assert.DoesNotContain("MatchRuntime", coordinator);
        Assert.DoesNotContain("PacketMaker", coordinator);
        Assert.DoesNotContain("MessagePackSerializer", coordinator);
        Assert.DoesNotContain(".TrySend(", coordinator);
        Assert.DoesNotContain("ReservePublication", coordinator);
        Assert.DoesNotContain("DispatchInOrder", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", server);
        // 봇 걸음은 별도 타이머가 아니라 50ms 매치 틱이 전투 뒤에 같은 잠금 안에서 잇는다 — 두 타이머가
        // 같은 주기로 맞물려 뒤에 오는 쪽이 매 펄스 잠금을 놓치던 문제의 재발 방지.
        Assert.DoesNotContain("StartBotMovementTimer", server);
        Assert.DoesNotContain("Task.Run(", server);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tick, @"runtime\.IsGameplayActive\("));
        // 잠금을 기다린 뒤 종료를 다시 확인한다. 전투→걸음, 준비→송신은 같은 잠금 안에서 실행한다.
        AssertInOrder(
            tick,
            "using var scope = runtime.Enter();",
            "runtime.IsEnded",
            "combat.ProcessTick(runtime);",
            "!isGameplayActive",
            "!runtime.Bots.HasBots()",
            "botMovement.ProcessTick(runtime, botPlayerId => botDecisions.DecideMovement(runtime, botPlayerId));");
        AssertInOrder(
            process,
            "runtime.GetSessions()",
            "CaptureSwarmBotObservers(matchingId, sessionSnapshot)",
            "Bots.PrepareMovementTick(",
            "DispatchSwarmBotMovementPlan(plan)",
            "BotTickMetrics.Record(",
            "PublishBotMovementMetrics(batch)");
        Assert.DoesNotContain("session.Player.MapId", process);
        Assert.DoesNotContain(".TrySend(", process);
        AssertInOrder(
            dispatch,
            "G_TO_C_AREA_PLAYER_LEAVE",
            "G_TO_C_AREA_PLAYER_ENTER",
            "G_TO_C_MOVE");
        Assert.DoesNotContain("G_TO_C_GROUND_ITEM_REMOVED", dispatch);
        Assert.Contains("playerPickups.PickUp(runtime, runtime.GetAlivePlayers())", tick);
        Assert.DoesNotContain("plan.AutoEquips", dispatch);
    }

    private static MatchRuntime CreateMatch()
    {
        var matches = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        return matches.GetOrCreate(44_001);
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
        return File.ReadAllText(Path.Combine(fullPathParts)).Replace("\r\n", "\n").Replace("public virtual void ", "public void ", StringComparison.Ordinal);
    }
}
