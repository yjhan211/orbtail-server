using System.Reflection;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.packets;

namespace demo_regression_tests;

/// <summary>
///     입장 성공 ACK 경계 (#331): 인증 커밋은 매치 잠금 안에서 원자적으로, 느릴 수 있는 큐 적재는 잠금 밖에서.
///     소켓 대신 private 패킷/발행 경계를 직접 부른다.
/// </summary>
public sealed class GameClientSessionConnectPublicationTests
{
    [Theory]
    [InlineData("HandleMove")]
    [InlineData("HandleSocialAction")]
    [InlineData("HandleMatchStartReady")]
    public async Task GameplayHandler_WaitsForMatchLock_AndRejectsMatchEndedWhileWaiting(string handlerName)
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74010, 8110, _ => true);
        var runtime = fixture.Store.GetRequired(74010);
        var method = typeof(GameClientSession).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = handlerName switch
        {
            "HandleMove" => [new C_TO_G_MOVE()],
            "HandleSocialAction" => [new C_TO_G_SOCIAL_ACTION()],
            _ => []
        };
        using var started = new ManualResetEventSlim();
        Task request;
        using (runtime.Enter())
        {
            request = Task.Run(async () =>
            {
                started.Set();
                await (Task)method.Invoke(session, arguments)!;
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(request.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.True(runtime.TryMarkTerminal());
        }
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Store.Get(74010));
        Assert.Null(session.LastValidatedPosition);
        Assert.False((bool)typeof(GameClientSession).GetField("_hasProcessedMoveInputSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!);
    }

    [Fact]
    public void SessionSource_KeepsConnectionLifecycleTogetherAndSeparatesGameplayMethods()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "game_server", "Sessions");
        string main = File.ReadAllText(Path.Combine(directory, "GameClientSession.cs"));
        Assert.False(File.Exists(Path.Combine(directory, "GameClientSession.Connection.cs")));
        Assert.Contains("private async Task HandleConnect(", main);
        Assert.Contains("public override void OnDisconnect()", main);
        Assert.Contains("public override void OnRemoved()", main);
        Assert.DoesNotContain("private Task HandleSocialAction(", main);
        Assert.DoesNotContain("private void AdvanceOrbOrbit(", main);
        Assert.DoesNotContain("internal Action? MarkGameEndedAndPrepareLifecyclePublication()", main);
        Assert.DoesNotContain("private Task BroadcastPlayerJoin()", main);
        Assert.Contains("private Task HandleSocialAction(", File.ReadAllText(Path.Combine(directory, "GameClientSession.Social.cs")));
        Assert.Contains("private void AdvanceOrbOrbit(", File.ReadAllText(Path.Combine(directory, "GameClientSession.Movement.cs")));
        Assert.Contains("internal Action? MarkGameEndedAndPrepareLifecyclePublication()", File.ReadAllText(Path.Combine(directory, "GameClientSession.MatchEnd.cs")));
        Assert.Contains("private Task BroadcastPlayerJoin()", File.ReadAllText(Path.Combine(directory, "GameClientSession.Snapshots.cs")));
    }

    [Fact]
    public async Task SessionKeepsOriginalRuntime_AndRejectsItAfterCleanupEvenIfIdIsRecreated()
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74009, 8109, _ => true);
        var original = fixture.Store.GetRequired(74009);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var run = typeof(GameClientSession).GetMethod("RunWithMatchLock", flags)!;
        bool executed = false;
        bool rejected = false;
        Func<Task> action = () =>
        {
            Assert.True(Monitor.IsEntered(original.Sync));
            executed = true;
            return Task.CompletedTask;
        };
        Action reject = () => rejected = true;
        await (Task)run.Invoke(session, [action, reject])!;
        Assert.True(executed);
        Assert.False(rejected);

        using (original.Enter())
            Assert.True(original.TryMarkTerminal());
        Assert.Null(fixture.Store.Get(74009));
        var replacement = fixture.Store.GetOrCreate(74009);
        Assert.NotSame(original, replacement);
        Assert.Same(original, typeof(GameClientSession).GetField("_match", flags)!.GetValue(session));
        executed = false;
        await (Task)run.Invoke(session, [action, reject])!;
        Assert.False(executed);
        Assert.True(rejected);
    }

    [Fact]
    public void ObjectSnapshot_CopiesAuthoritativeCoordinatesWithoutLoadingPlayerInfo()
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74006, 8105, _ => true);
        var position = new Vector3f(10.25f, 20.75f, 0f);
        var velocity = new Vector3f(2f, 3f, 0f);
        var cell = new Cell(10, 20);
        typeof(GameClientSession).GetField("_lastValidatedPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, position);
        typeof(GameClientSession).GetField("_lastValidatedVelocity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, velocity);
        typeof(GameClientSession).GetField("_lastValidCell", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, cell);
        var snapshot = session.CaptureGameObjectInfo();
        position.X = 999;
        velocity.X = 999;
        cell.X = 999;
        Assert.Equal(8105, snapshot.ObjectId);
        Assert.Equal(74006, snapshot.MapSubId);
        Assert.Equal(10.25f, snapshot.Position.X);
        Assert.Equal(20.75f, snapshot.Position.Y);
        Assert.Equal(2f, snapshot.Velocity.X);
        Assert.Equal(10, snapshot.Cell.X);
    }

    [Fact]
    public void CommittedSuccessAck_HasExactPayloadAndPlayerId_AfterAtomicAuthenticationCommit()
    {
        const long matchingId = 74_001;
        const long playerId = 8_101;
        using var fixture = new ConnectFixture();
        GameClientSession session = fixture.CreateSession(
            matchingId,
            playerId,
            packet => { fixture.Record(packet); return true; });

        using Packet packet = CreateSuccessPacket(session);
        (Protocol protocol, long packetPlayerId, G_TO_C_CONNECT_RESULT body) =
            DeserializeConnectResult(packet);
        Assert.Equal(Protocol.G_TO_C_CONNECT_RESULT, protocol);
        Assert.Equal(playerId, packetPlayerId);
        Assert.True(body.Success);
        Assert.Equal(ErrorCode.SUCCESS, body.ErrorCode);
        Assert.DoesNotContain("\"message\"", MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(body)));

        Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Connection, session));
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));

        Assert.True(PublishCommittedSuccess(session, packet));
        Assert.Single(fixture.SentPackets);
        Assert.Equal(Protocol.G_TO_C_CONNECT_RESULT, fixture.SentPackets[0].Protocol);
    }

    [Fact]
    public async Task BlockedSuccessAck_IsOutsideMatchLock_AndDoesNotDelayTerminalCleanup()
    {
        const long matchingId = 74_002;
        const long playerId = 8_102;
        using var fixture = new ConnectFixture();
        using var senderEntered = new ManualResetEventSlim();
        using var releaseSender = new ManualResetEventSlim();
        GameClientSession session = fixture.CreateSession(
            matchingId,
            playerId,
            packet =>
            {
                fixture.Record(packet);
                senderEntered.Set();
                Assert.True(releaseSender.Wait(TimeSpan.FromSeconds(5)));
                return true;
            });
        int cleanupCount = 0;
        fixture.CleanupSteps.Add(new MatchCleanupStep("count", _ => Interlocked.Increment(ref cleanupCount)));

        Task<bool> connect = Task.Run(() =>
        {
            using Packet packet = CreateSuccessPacket(session);
            Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Connection, session));
            return PublishCommittedSuccess(session, packet);
        });

        Assert.True(senderEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));

        // 큐 적재가 막혀 있어도 매치 잠금은 비어 있다 — 같은 매치 작업과 터미널 정리가 그대로 진행된다.
        Assert.True(fixture.Store.TryEnter(matchingId, out MatchScope probe));
        using (probe)
        {
            Assert.True(probe.Runtime.TryMarkTerminal());
        }

        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        Assert.Null(fixture.Store.Get(matchingId));

        releaseSender.Set();
        Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCommittedSuccessAck_FailsForwardWithoutNegativeAckOrEntryRollback(bool senderThrows)
    {
        const long matchingId = 74_004;
        using var fixture = new ConnectFixture();
        int senderCalls = 0;
        GameClientSession session = fixture.CreateSession(
            matchingId,
            8_103,
            _ =>
            {
                Interlocked.Increment(ref senderCalls);
                if (senderThrows)
                    throw new InvalidOperationException("transport failure");
                return false;
            });

        Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Connection, session));
        using Packet packet = CreateSuccessPacket(session);

        Assert.False(PublishCommittedSuccess(session, packet));
        Assert.Equal(1, Volatile.Read(ref senderCalls));
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));
        Assert.Equal(0, GetIntField(session, "_entryFailureReported"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void TerminalBeforeCommit_RejectsSuccessWithoutAuthenticationEntryOrAck()
    {
        const long matchingId = 74_005;
        using var fixture = new ConnectFixture();
        GameClientSession session = fixture.CreateSession(matchingId, 8_104, _ => true);

        MatchRuntime runtime = fixture.Store.GetOrCreate(matchingId);
        using (fixture.Store.Enter(runtime))
        {
            Assert.True(runtime.TryMarkTerminal());
        }

        Assert.Null(fixture.Store.Get(matchingId));
        Assert.False(CommitAuthentication(fixture.Store, matchingId, fixture.Connection, session));
        Assert.Equal(0, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(0, GetIntField(session, "_entryCompleted"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void ThrowingEntryAbortHook_ReleasesOnlyItsReservations_ForDisconnectRetry()
    {
        const long matchingId = 74_006;
        int hookCalls = 0;
        using var fixture = new ConnectFixture();
        GameClientSession session = fixture.CreateSession(
            matchingId,
            8_105,
            _ => true,
            _ =>
            {
                if (Interlocked.Increment(ref hookCalls) == 1)
                    throw new InvalidOperationException("deferred abort unavailable");
            });

        Assert.False(ReportEntryFailure(session));
        Assert.Equal(0, GetIntField(session, "_entryFailureReported"));
        Assert.Equal(0, GetIntField(session, "_matchingLifecycleTerminalReported"));

        session.OnDisconnect();

        Assert.Equal(2, Volatile.Read(ref hookCalls));
        Assert.Equal(1, GetIntField(session, "_entryFailureReported"));
        Assert.Equal(1, GetIntField(session, "_matchingLifecycleTerminalReported"));
    }

    [Fact]
    public void EntryFailure_WhenAnotherTerminalPathOwnsFlag_CanRetryAfterThatPathFails()
    {
        int hookCalls = 0;
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74_007, 8_106, _ => true, _ => hookCalls++);
        SetIntField(session, "_matchingLifecycleTerminalReported", 1);

        Assert.True(ReportEntryFailure(session));
        Assert.Equal(0, hookCalls);
        Assert.Equal(0, GetIntField(session, "_entryFailureReported"));
        Assert.Equal(1, GetIntField(session, "_matchingLifecycleTerminalReported"));

        // 선점한 다른 종료 경로가 실패하여 자기 플래그를 반납한 상황.
        SetIntField(session, "_matchingLifecycleTerminalReported", 0);
        Assert.True(ReportEntryFailure(session));
        Assert.True(ReportEntryFailure(session));
        Assert.Equal(1, hookCalls);
    }

    [Fact]
    public void EntryFailure_ReentrantDisconnect_DoesNotInvokeHandlerTwice()
    {
        int hookCalls = 0;
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74_008, 8_107, _ => true, current =>
        {
            hookCalls++;
            current.OnDisconnect();
        });

        Assert.True(ReportEntryFailure(session));
        Assert.Equal(1, hookCalls);
    }

    [Fact]
    public void ConnectHandler_WiresConnectProtocol_AndQueuesCountdownBeforeCommittedAck()
    {
        string root = FindRepositoryRoot();
        string sessionSource = File.ReadAllText(
            Path.Combine(root, "game_server", "Sessions", "GameClientSession.cs"));
        string connectionSource = File.ReadAllText(
            Path.Combine(root, "game_server", "Sessions", "GameClientSession.cs"));

        Assert.Contains("ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT", sessionSource);
        Assert.Contains("async bytes => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect)", sessionSource);
        Assert.Contains("trySendConnectSuccessResponse ?? Connection.TrySend", sessionSource);
        Assert.Contains("Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1))", connectionSource);

        // 세션 등록·초기화 블록·인증 커밋은 매치 잠금 안에서, 성공 ACK 큐 적재는 잠금 밖에서.
        int registration = connectionSource.IndexOf("_matchEntry.GetOrCreateMatch(matchingId)", StringComparison.Ordinal);
        int registerCallback = connectionSource.IndexOf("_registerSessionCallback(playerId, this)", registration, StringComparison.Ordinal);
        // 등록 콜백은 이전 세션을 반환하고, 매치·연결 잠금을 벗어난 뒤 이전 연결을 끊는다.
        string normalized = connectionSource.Replace("\r\n", "\n");
        Assert.Contains(
            "registered = true;\n            }\n            // 이전 연결 종료는 새 연결의 상태 잠금과 매치 잠금을 벗어난 뒤 실행한다.",
            normalized);
        int disconnect = connectionSource.IndexOf("previousSession.ForceDisconnect();", registerCallback, StringComparison.Ordinal);
        int markServerDisconnect = connectionSource.IndexOf("previousSession.MarkServerInitiatedDisconnect();", registerCallback, StringComparison.Ordinal);
        Assert.True(registerCallback < markServerDisconnect && markServerDisconnect < disconnect);
        Assert.Contains("sessions.Register,", File.ReadAllText(Path.Combine(root, "game_server", "GameServer.cs")));
        int countdown = connectionSource.IndexOf("SendMatchStartCountdown(matchingId);", disconnect, StringComparison.Ordinal);
        int response = connectionSource.IndexOf("CreateConnectResultPacket(", countdown, StringComparison.Ordinal);
        int commitScope = connectionSource.IndexOf("InitializeWithMatchLock(runtime, () =>", response, StringComparison.Ordinal);
        int authentication = connectionSource.IndexOf("Connection.TryMarkAuthenticated", commitScope, StringComparison.Ordinal);
        int publication = connectionSource.IndexOf("TryPublishCommittedConnectResult(successResponse)", authentication, StringComparison.Ordinal);
        Assert.True(registration >= 0 && registration < registerCallback && registerCallback < countdown &&
                    countdown < response && response < commitScope && commitScope < authentication &&
                    authentication < publication);

        int registeredFailure = connectionSource.IndexOf("if (registered)", publication, StringComparison.Ordinal);
        int deferredAbort = connectionSource.IndexOf("ReportEntryFailureOnce()", registeredFailure, StringComparison.Ordinal);
        int earlyFailureResponse = connectionSource.IndexOf(
            "SendConnectResult(false, ErrorCode.GAME_ENTRY_FAILED",
            deferredAbort,
            StringComparison.Ordinal);
        Assert.True(registeredFailure >= 0 &&
                    registeredFailure < deferredAbort &&
                    deferredAbort < earlyFailureResponse);
        Assert.Contains("throw new OperationCanceledException(\"Match became terminal during game entry.\")", connectionSource);
    }

    private static bool CommitAuthentication(
        MatchRuntimeStore store,
        long matchingId,
        TcpConnection connection,
        GameClientSession session)
    {
        MatchRuntime? runtime = store.Get(matchingId);
        if (runtime == null)
            return false;

        using MatchScope scope = store.Enter(runtime);
        if (runtime.IsTerminal)
            return false;

        if (!connection.TryMarkAuthenticated(
                () => SetIntField(session, "_entryCompleted", 1)))
        {
            throw new OperationCanceledException("Connection closed before authentication commit.");
        }

        return true;
    }

    [Theory]
    [InlineData(ErrorCode.ALREADY_AUTHENTICATED)]
    [InlineData(ErrorCode.GAME_ENTRY_TICKET_INVALID)]
    [InlineData(ErrorCode.GAME_ALREADY_ENDED)]
    [InlineData(ErrorCode.GAME_ENTRY_FAILED)]
    public void ConnectFailurePacket_ContainsErrorCodeWithoutMessage(ErrorCode errorCode)
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74_009, 8_108, _ => true);
        using var packet = (Packet)typeof(GameClientSession).GetMethod(
            "CreateConnectResultPacket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [false, errorCode, 0L, null])!;
        using var wire = Packet.Create(packet.ToBytes());
        Assert.Equal((int)Protocol.G_TO_C_CONNECT_RESULT, wire.PopProtocolId());
        wire.PopPlayerId();
        byte[] bodyBytes = wire.PopBody();
        var body = MessagePackSerializer.Deserialize<G_TO_C_CONNECT_RESULT>(bodyBytes);
        Assert.False(body.Success);
        Assert.Equal(errorCode, body.ErrorCode);
        Assert.DoesNotContain("\"message\"", MessagePackSerializer.ConvertToJson(bodyBytes));

        using var makerPacket = PacketMaker.G_TO_C_CONNECT_RESULT(false, errorCode);
        Assert.Equal(errorCode, DeserializeConnectResult(makerPacket).Body.ErrorCode);
    }

    [Fact]
    public void PlayerProfileLoad_ChecksConnectionBeforeApplyingSpawnOrSendingSnapshots()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "game_server", "Sessions", "GameClientSession.cs"));
        int load = source.IndexOf("await PlayerInfo.Load(", StringComparison.Ordinal);
        Assert.True(load >= 0);
        int statementEnd = source.IndexOf(';', load);
        Assert.StartsWith("EnsureConnectionActive();", source[(statementEnd + 1)..].TrimStart());
        int inventorySnapshot = source.IndexOf("SendInGameInventoryList();", statementEnd, StringComparison.Ordinal);
        Assert.True(inventorySnapshot > statementEnd);
        string initialization = source[statementEnd..inventorySnapshot];
        Assert.Equal(1, initialization.Split("EnsureConnectionActive();").Length - 1);
    }

    private static Packet CreateSuccessPacket(GameClientSession session) =>
        Assert.IsType<Packet>(typeof(GameClientSession).GetMethod(
            "CreateConnectResultPacket",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            session,
            [true, ErrorCode.SUCCESS, 0L, null]));

    private static bool PublishCommittedSuccess(GameClientSession session, Packet packet) =>
        Assert.IsType<bool>(typeof(GameClientSession).GetMethod(
            "TryPublishCommittedConnectResult",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [packet]));

    private static bool ReportEntryFailure(GameClientSession session) =>
        Assert.IsType<bool>(typeof(GameClientSession).GetMethod(
            "ReportEntryFailureOnce",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null));

    private static (Protocol Protocol, long PlayerId, G_TO_C_CONNECT_RESULT Body) DeserializeConnectResult(Packet packet)
    {
        using Packet wirePacket = Packet.Create(packet.ToBytes());
        Protocol protocol = (Protocol)wirePacket.PopProtocolId();
        long playerId = wirePacket.PopPlayerId();
        return (protocol, playerId, MessagePackSerializer.Deserialize<G_TO_C_CONNECT_RESULT>(wirePacket.PopBody()));
    }

    private static int GetIntField(object target, string name)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
                return (int)field.GetValue(target)!;
            type = type.BaseType;
        }

        throw new MissingFieldException(target.GetType().FullName, name);
    }

    private static void SetIntField(object target, string name, int value)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            type = type.BaseType;
        }

        throw new MissingFieldException(target.GetType().FullName, name);
    }

    private static void SetIdentity(GameClientSession session, long matchingId, long playerId)
    {
        SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
        SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
        SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
        TestGameSessionServices.BindMatch(session, matchingId);
    }

    private static void SetProperty(GameClientSession session, string name, object value) =>
        typeof(GameClientSession).GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

    private static void Activate(TcpConnection connection)
    {
        int active = (int)typeof(TcpConnection).GetField(
            "StateActive",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        SetIntField(connection, "_state", active);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "game_server", "Sessions")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class ConnectFixture : IDisposable
    {
        private readonly List<SentPacket> _sentPackets = [];

        public ConnectFixture()
        {
            Store = new MatchRuntimeStore(NullLogger.Instance, cleanupSteps: CleanupSteps);
        }

        public List<MatchCleanupStep> CleanupSteps { get; } = [];
        public MatchRuntimeStore Store { get; }
        public TcpConnection Connection { get; } = new();
        public IReadOnlyList<SentPacket> SentPackets => _sentPackets.ToList();

        public GameClientSession CreateSession(
            long matchingId,
            long playerId,
            Func<Packet, bool> sender,
            Action<GameClientSession>? recordEntryFailure = null)
        {
            Activate(Connection);
            Store.GetOrCreate(matchingId);
            var session = new GameClientSession(
                Connection,
                NullLogger.Instance,
                null!,
                TestGameSessionServices.CreateLeaveHandler(),
                static (_, _) => null,
                static _ => [],

                TestGameEventLogs.Create(),
                TestGameSessionServices.CreateEliminationService(Store, TestGameEventLogs.Create(),
                    new MatchSummaryFileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
                    GameServerDevOptions.Disabled, static _ => [], NullLogger.Instance),
                new FakePlayerGrowthHandler(),

                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(recordEntryFailure),
                TestGameSessionServices.CreateEntryService(null!, Store, GameServerDevOptions.Disabled, NullLogger.Instance),
                new ItemCombinationService(TestGameEventLogs.Create()),
                new MovementValidationService(NullLogger<MovementValidationService>.Instance),
                sender);
            Connection.SetSession(session);
            SetIdentity(session, matchingId, playerId);
            return session;
        }

        public void Record(Packet packet)
        {
            using Packet wirePacket = Packet.Create(packet.ToBytes());
            _sentPackets.Add(new SentPacket(
                (Protocol)wirePacket.PopProtocolId(),
                wirePacket.PopPlayerId(),
                wirePacket.PopBody()));
        }

        public void Dispose()
        {
        }
    }

    private sealed record SentPacket(Protocol Protocol, long PlayerId, byte[] Body);
}
