using game_server.matches.results;
using game_server.matches;
using System.Reflection;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.core;
using network.packets;
using network.routing;

namespace demo_regression_tests;

public sealed class SessionPacketProcessingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task GameSessionPacketEntry_PreservesEveryMovementAndActionInReceiveOrder()
    {
        var session = CreateSession();
        var router = (ProtocolRouter)typeof(SessionBase).GetField("ProtocolRouter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.RegisterHandler(Protocol.C_TO_G_HEART_BEAT, _ => release.Task);
        var calls = new List<string>();
        router.RegisterHandler(Protocol.C_TO_G_MOVE, bytes =>
        {
            var move = MessagePackSerializer.Deserialize<C_TO_G_MOVE>(bytes);
            calls.Add($"move:{move.Position.X}");
            return Task.CompletedTask;
        });
        router.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE, _ =>
        {
            calls.Add("action");
            return Task.CompletedTask;
        });
        Task Move(float x) => Send(session, Protocol.C_TO_G_MOVE,
            new C_TO_G_MOVE { Position = new Vector3f(x, 0, 0), Velocity = new Vector3f() });

        Task first = Send(session, Protocol.C_TO_G_HEART_BEAT, 0);
        Task move1 = Move(1);
        Task move2 = Move(2);
        Task action = Send(session, Protocol.C_TO_G_PLAYER_STATE, new C_TO_G_PLAYER_STATE());
        Task stop = Move(3);
        Assert.Empty(calls);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, move1, move2, action, stop).WaitAsync(TestTimeout);
        Assert.Equal(new[] { "move:1", "move:2", "action", "move:3" }, calls);
    }

    [Fact]
    public void MovementValidationStaysInHandler()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        string root = directory!.FullName;
        string main = File.ReadAllText(Path.Combine(root, "game_server", "Sessions", "GameClientSession.cs"));
        string move = File.ReadAllText(Path.Combine(root, "game_server", "Sessions", "GameClientSession.Movement.cs"));
        Assert.DoesNotContain("ScheduleMessageAsync", main);
        Assert.DoesNotContain("ScheduleMessageAsync", move);
        int check = move.IndexOf("!MovementValidationPolicy.IsFinite(msg.Position)", StringComparison.Ordinal);
        int apply = move.IndexOf("_playerMovement.Apply(msg, deltaTime)", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < apply);
    }
    [Fact]
    public void MovementWireContracts_DoNotContainInputSequence()
    {
        string request = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
            new C_TO_G_MOVE { Position = new Vector3f(), Velocity = new Vector3f() }));
        string response = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
            new G_TO_C_MOVE { Position = new Vector3f(), Velocity = new Vector3f(), Cell = new Cell(0, 0) }));
        Assert.DoesNotContain("inputSeq", request);
        Assert.DoesNotContain("lastProcessedInput", response);
        Assert.Contains("serverTime", response);
        Assert.Contains("orbPhase", response);
    }

    private static Task Send<T>(GameClientSession session, Protocol protocol, T body)
    {
        using var packet = Packet.Create((int)protocol);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        packet.RecordSize();
        return session.OnMessageFromClient(packet.ToBytes());
    }

    private static GameClientSession CreateSession()
    {
        // 소켓 없이 실제 SessionBase 파싱·세마포어 직렬화·라우팅 경계를 검사한다.
        // 이동 판정 자체 대신 기록 핸들러를 등록하여 맵 파일과 실제 시간에 의존하지 않는다.
        var connection = new TcpConnection();
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var active = (int)typeof(TcpConnection).GetField("StateActive", flags)!.GetRawConstantValue()!;
        typeof(TcpConnection).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var logs = TestGameEventLogs.Create();
        var session = new GameClientSession(
            connection,
            NullLogger.Instance,
            null!,
            static _ => false, TestGameSessionServices.CreateMatchCleanupService(),
            static (_, _) => null,

            logs,
            TestGameSessionServices.CreateEliminationService(store, logs,
                new MatchSummaryFileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
                GameServerDevOptions.Disabled, NullLogger.Instance),
            new FakePlayerGrowthHandler(),

            new FakeGameSessionLifecycle(),
            static () => false,
            new FakeMatchEntryFailureHandler(),
            matchEntry: TestGameSessionServices.CreateEntryService(null!, store, GameServerDevOptions.Disabled, NullLogger.Instance),
            movementValidation: new MovementValidationService(NullLogger<MovementValidationService>.Instance),
                orbInventory: new OrbInventoryService(logs));
        connection.SetSession(session);
        return session;
    }

}
