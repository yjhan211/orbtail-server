using System.Reflection;
using game_server;
using game_server.matches;
using game_server.players;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectInitialStateUsesMatchLockAndReleasesItOnFailure(bool failInitialSend)
    {
        using var fixture = new ConnectFixture();
        bool successSent = false;
        bool successHeldLock = false;
        var runtime = fixture.Store.GetOrCreate(74011);
        var session = fixture.CreateSession(74011, 8111, _ =>
        {
            successSent = true;
            successHeldLock = Monitor.IsEntered(runtime.MatchLock);
            return true;
        });
        var initialPackets = new List<(Protocol Protocol, bool HeldLock)>();
        bool spawnInitialized = false;
        ((AcceptingConnection)fixture.Connection).BeforeSend = packet =>
        {
            using var wire = Packet.Create(packet.ToBytes());
            var protocol = (Protocol)wire.PopProtocolId();
            if (protocol is Protocol.G_TO_C_MATCH_ROSTER or Protocol.G_TO_C_ORB_LIST or Protocol.G_TO_C_OBJECT_INFO or Protocol.G_TO_C_ORB_UPGRADE_INFO)
                initialPackets.Add((protocol, Monitor.IsEntered(runtime.MatchLock)));
            if (protocol == Protocol.G_TO_C_ORB_LIST)
            {
                spawnInitialized = session.Player.Position != null;
                if (failInitialSend)
                    throw new IOException("Initial state send failed.");
            }
        };

        await fixture.ConnectAsync(session, 74011, 8111);

        Assert.Contains(initialPackets, packet => packet.Protocol == Protocol.G_TO_C_MATCH_ROSTER);
        Assert.Contains(initialPackets, packet => packet.Protocol == Protocol.G_TO_C_ORB_LIST);
        if (!failInitialSend)
            Assert.Contains(initialPackets, packet => packet.Protocol == Protocol.G_TO_C_ORB_UPGRADE_INFO);
        Assert.All(initialPackets, packet => Assert.True(packet.HeldLock));
        Assert.True(spawnInitialized);
        Assert.Equal(!failInitialSend, successSent);
        Assert.False(successHeldLock);
        await Task.Run(() =>
        {
            bool acquired = Monitor.TryEnter(runtime.MatchLock, TimeSpan.FromSeconds(2));
            try { Assert.True(acquired); }
            finally { if (acquired) Monitor.Exit(runtime.MatchLock); }
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("HandleMove")]
    [InlineData("HandleSocialAction")]
    [InlineData("HandleMatchStartReady")]
    public async Task GameplayHandler_WaitsForMatchLock_AndRejectsMatchEndedWhileWaiting(string handlerName)
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74010, 8110, _ => true);
        var runtime = fixture.Store.GetOrThrow(74010);
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
            Assert.True(runtime.TryMarkEnded());
        }
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Store.GetOrNull(74010));
        Assert.Null(session.Player.Position);
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
        Assert.Contains("internal Action? MarkGameEndedAndPrepareLifecyclePublication()", main);
        Assert.Contains("private void SyncPlayersOnEntry()", main);
        Assert.Contains("private Task HandleSocialAction(", File.ReadAllText(Path.Combine(directory, "GameClientSession.Social.cs")));
        Assert.Contains("public void AdvanceOrbOrbit(", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "game_server", "Players", "Player.cs")));
        Assert.False(File.Exists(Path.Combine(directory, "GameClientSession.MatchEnd.cs")));
        Assert.False(File.Exists(Path.Combine(directory, "GameClientSession.Snapshots.cs")));
    }

    [Fact]
    public async Task SessionKeepsOriginalRuntime_AndRejectsItAfterCleanupEvenIfIdIsRecreated()
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74009, 8109, _ => true);
        var original = fixture.Store.GetOrThrow(74009);
        TestGameSessionServices.AttachSession(session);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var handle = typeof(GameClientSession).GetMethod("HandleMatchStartReady", flags)!;
        session.Match.PrepareEntry(8109);
        int sent = 0;
        ((AcceptingConnection)fixture.Connection).BeforeSend = _ =>
        {
            Assert.True(Monitor.IsEntered(original.MatchLock));
            sent++;
        };
        await (Task)handle.Invoke(session, null)!;
        Assert.Equal(1, sent);

        using (original.Enter())
            Assert.True(original.TryMarkEnded());
        Assert.Null(fixture.Store.GetOrNull(74009));
        var replacement = fixture.Store.GetOrCreate(74009);
        Assert.NotSame(original, replacement);
        Assert.Same(original, typeof(GameClientSession).GetField("_match", flags)!.GetValue(session));
        await (Task)handle.Invoke(session, null)!;
        Assert.Equal(1, sent);

    }

    [Fact]
    public void SpawnInitialization_IsOwnedByEachMovementService()
    {
        using var fixture = new ConnectFixture();
        var first = fixture.CreateSession(74026, 8125, _ => true);
        using var otherFixture = new ConnectFixture();
        var second = otherFixture.CreateSession(74026, 8126, _ => true);
        TestGameSessionServices.BindMatch(second, 74026, fixture.Store);
        var movement = TestGameSessionServices.GetMovement(first);
        var other = TestGameSessionServices.GetMovement(second);
        Assert.NotSame(movement, other);
        var spawn = new Cell(10, 20);
        using (first.Match.Enter())
        {
            first.Player.InitializeSpawn(spawn);
            spawn.X = 999;
            Assert.Equal(10, first.Player.Cell!.X);
            Assert.Equal(0f, first.Player.Velocity.Magnitude());
            Assert.Equal(0f, first.Player.Rotation);
            Assert.Same(first.Player.Position, first.Player.Position);
            Assert.Equal(first.Player.CurrentArea, first.Player.CurrentArea);
            Assert.Null(second.Player.Position);
            Assert.Null(second.Player.Cell);
        }
        Assert.Null(typeof(GameClientSession).GetProperty("Position"));
        Assert.Null(typeof(GameClientSession).GetProperty("CurrentArea"));
    }

    [Fact]
    public void AppliedMovement_UpdatesTheAuthoritativeSnapshotTogether()
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74016, 8115, _ => true);
        var movement = new PlayerMovementService.ValidatedMovement(
            new Vector3f(10.25f, 20.75f, 0f), new Vector3f(2f, 3f, 0f),
            new Cell(10, 20), false);

        using (session.Match.Enter())
        {
            session.Player.ApplyValidatedMovement(movement, 45f);
            var snapshot = session.Player.CreateGameObjectInfo();
            Assert.Equal(10.25f, snapshot.Position.X);
            Assert.Equal(20.75f, snapshot.Position.Y);
            Assert.Equal(2f, snapshot.Velocity.X);
            Assert.Equal(3f, snapshot.Velocity.Y);
            Assert.Equal(45f, snapshot.Rotation);
            Assert.Equal(10, snapshot.Cell.X);
            Assert.Equal(20, snapshot.Cell.Y);
        }
    }

    [Fact]
    public void ObjectSnapshot_CopiesAuthoritativeCoordinatesWithoutLoadingPlayerInfo()
    {
        using var fixture = new ConnectFixture();
        var session = fixture.CreateSession(74006, 8105, _ => true);
        var position = new Vector3f(10.25f, 20.75f, 0f);
        var velocity = new Vector3f(2f, 3f, 0f);
        var cell = new Cell(10, 20);
        using var scope = session.Match.Enter();
        session.Match.RegisterParticipant(session.Player);
        TestGameSessionServices.SetMovementProperty(session, "Position", position);
        TestGameSessionServices.SetMovementProperty(session, "Velocity", velocity);
        TestGameSessionServices.SetMovementProperty(session, "Cell", cell);
        var snapshot = session.Player.CreateGameObjectInfo();
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
    public async Task CommittedSuccessAck_HasExactPayloadAndPlayerId_AfterAtomicAuthenticationCommit()
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

        await fixture.ConnectAsync(session, matchingId, playerId);
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));

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


        Task connect = Task.Run(() => fixture.ConnectAsync(session, matchingId, playerId));

        Assert.True(senderEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));

        // 큐 적재가 막혀 있어도 매치 잠금은 비어 있다 — 같은 매치 작업과 터미널 정리가 그대로 진행된다.
        Assert.True(fixture.Store.TryEnter(matchingId, out MatchLockScope probe));
        using (probe)
        {
            Assert.True(probe.Runtime.TryMarkEnded());
        }

        Assert.False(session.Match.IsGameplayActive());
        Assert.Null(fixture.Store.GetOrNull(matchingId));

        releaseSender.Set();
        await connect.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommittedSuccessAck_FailsForwardWithoutNegativeAckOrEntryRollback(bool senderThrows)
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

        await fixture.ConnectAsync(session, matchingId, 8_103);
        Assert.Equal(1, Volatile.Read(ref senderCalls));
        Assert.Equal(1, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_entryCompleted"));
        Assert.Equal(0, GetIntField(session, "_entryFailureReported"));
        Assert.True(fixture.Connection.IsReleased);
        Assert.Equal(MatchingRedisKeys.EntryCompletedState,
            (await fixture.Redis.StringGetAsync(MatchingRedisKeys.EntryStateKey(matchingId))).ToString());
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void TerminalBeforeCommit_RejectsSuccessWithoutAuthenticationEntryOrAck()
    {
        const long matchingId = 74_005;
        using var fixture = new ConnectFixture();
        GameClientSession session = fixture.CreateSession(matchingId, 8_104, _ => true);

        MatchRuntime runtime = fixture.Store.GetOrCreate(matchingId);
        using (MatchRuntimeStore.Enter(runtime))
        {
            Assert.True(runtime.TryMarkEnded());
        }

        Assert.Null(fixture.Store.GetOrNull(matchingId));
        Assert.False(CommitAuthentication(fixture.Store, matchingId, fixture.Connection, session));
        Assert.Equal(0, GetIntField(fixture.Connection, "_authenticated"));
        Assert.Equal(0, GetIntField(session, "_entryCompleted"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void ThrowingEntryAbortHook_ClosesConnectionAndRetriesThroughDisconnect()
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

        HandleEntryFailure(session);
        Assert.True(fixture.Connection.IsReleased);
        // 내부 Disconnect가 OnDisconnect를 호출해 이미 한 번 재시도했다.
        Assert.Equal(2, Volatile.Read(ref hookCalls));

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

        HandleEntryFailure(session);
        Assert.Equal(0, hookCalls);
        Assert.Equal(0, GetIntField(session, "_entryFailureReported"));
        Assert.Equal(1, GetIntField(session, "_matchingLifecycleTerminalReported"));

        // 선점한 다른 종료 경로가 실패하여 자기 플래그를 반납한 상황.
        SetIntField(session, "_matchingLifecycleTerminalReported", 0);
        HandleEntryFailure(session);
        HandleEntryFailure(session);
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

        HandleEntryFailure(session);
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
            "registered = true;\n            }\n\n            if (previousSession != null)",
            normalized);
        int disconnect = connectionSource.IndexOf("previousSession.ForceDisconnect();", registerCallback, StringComparison.Ordinal);
        int markServerDisconnect = connectionSource.IndexOf("previousSession.MarkDisconnectedByServer();", registerCallback, StringComparison.Ordinal);
        Assert.True(registerCallback < markServerDisconnect && markServerDisconnect < disconnect);
        Assert.Contains("sessions.Register,", File.ReadAllText(Path.Combine(root, "game_server", "GameServer.cs")));
        int countdown = connectionSource.IndexOf("SendMatchStartCountdown(matchingId);", disconnect, StringComparison.Ordinal);
        int response = connectionSource.IndexOf("CreateConnectResultPacket(", countdown, StringComparison.Ordinal);
        int commitScope = connectionSource.IndexOf("using (runtime.Enter())", response, StringComparison.Ordinal);
        int authentication = connectionSource.IndexOf("Connection.TryMarkAuthenticated", commitScope, StringComparison.Ordinal);
        int publication = connectionSource.IndexOf("_trySendConnectSuccessResponse(successResponse)", authentication, StringComparison.Ordinal);
        Assert.True(registration >= 0 && registration < registerCallback && registerCallback < countdown &&
                    countdown < response && response < commitScope && commitScope < authentication &&
                    authentication < publication);

        int registeredFailure = connectionSource.IndexOf("if (registered)", publication, StringComparison.Ordinal);
        int deferredAbort = connectionSource.IndexOf("HandleEntryFailureOnce()", registeredFailure, StringComparison.Ordinal);
        int earlyFailureResponse = connectionSource.IndexOf(
            "SendConnectFailure(ErrorCode.GAME_ENTRY_FAILED",
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
        MatchRuntime? runtime = store.GetOrNull(matchingId);
        if (runtime == null)
            return false;

        using MatchLockScope scope = MatchRuntimeStore.Enter(runtime);
        if (runtime.IsEnded)
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
    public void CompositionLoad_ChecksConnectionAndSessionDoesNotReloadProfile()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "game_server", "Sessions", "GameClientSession.cs"));
        Assert.DoesNotContain("await PlayerInfo.Load(", source);
        int load = source.IndexOf("await _matchEntry.PrepareMatchAsync(", StringComparison.Ordinal);
        Assert.True(load >= 0);
        int statementEnd = source.IndexOf(';', load);
        Assert.StartsWith("EnsureConnectionActive();", source[(statementEnd + 1)..].TrimStart());
        int inventorySnapshot = source.IndexOf("SendOrbList();", statementEnd, StringComparison.Ordinal);
        Assert.True(inventorySnapshot > statementEnd);
        string initialization = source[statementEnd..inventorySnapshot];
        Assert.Equal(2, initialization.Split("EnsureConnectionActive();").Length - 1);
        Assert.Contains("using (runtime.Enter())", initialization);
        Assert.Contains("if (runtime.IsEnded)", initialization);
        int synchronization = source.IndexOf("SyncPlayersOnEntry();", inventorySnapshot, StringComparison.Ordinal);
        int commit = source.IndexOf("await _matchEntry.RecordEntryAsync(", synchronization, StringComparison.Ordinal);
        Assert.True(synchronization > inventorySnapshot && commit > synchronization);
        Assert.DoesNotContain("await ", source[inventorySnapshot..commit]);
        Assert.EndsWith("}", source[synchronization..commit].TrimEnd());
        Assert.DoesNotContain("UpdatePlayerProfile(", initialization);
    }

    [Fact]
    public void ClientMatchAppearanceUsesRosterAndIgnoresLateLobbyWearForGameCharacter()
    {
        string scripts = Path.Combine(FindRepositoryRoot(), "client", "Assets", "Scripts");
        string packets = File.ReadAllText(Path.Combine(scripts, "Managers", "Map", "MapManager.Packets.cs"));
        Assert.Contains("objectInfo.MapId == MapId.Camp &&", packets);
        string map = File.ReadAllText(Path.Combine(scripts, "Managers", "Map", "MapManager.cs"));
        Assert.Contains("SetPlayer(CreatePlayerInfoFromRoster(GameUser.Instance.ObjectInfo))", map);
        string inventory = File.ReadAllText(Path.Combine(scripts, "GameUser.Inventory.cs"));
        Assert.Contains("playerComponent && ObjectInfo?.MapId == MapId.Camp", inventory);
        string network = File.ReadAllText(Path.Combine(scripts, "GameUser.Network.cs"));
        Assert.Contains("WearItemIdList = new List<int>(ownProfile.Wear)", network);
    }

    [Fact]
    public void EntrySnapshotPublishesOnlyPressureFieldClock()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "game_server", "Sessions");
        string source = File.ReadAllText(Path.Combine(directory, "GameClientSession.cs"));
        string field = source[source.IndexOf("private void SendPressureFieldState()", StringComparison.Ordinal)..];
        Assert.Contains("Protocol.G_TO_C_SWARM_FIELD_STATE", field);
        Assert.Contains("Closures.GameStartTime", field);
        Assert.DoesNotContain("Protocol.G_TO_C_AREA_CLOSED", field);
        Assert.Contains("SendPressureFieldState();", File.ReadAllText(Path.Combine(directory, "GameClientSession.cs")));
    }

    private static Packet CreateSuccessPacket(GameClientSession session) =>
        Assert.IsType<Packet>(typeof(GameClientSession).GetMethod(
            "CreateConnectResultPacket",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            session,
            [true, ErrorCode.SUCCESS, 0L, null]));

    private static void HandleEntryFailure(GameClientSession session) =>
        typeof(GameClientSession).GetMethod(
            "HandleEntryFailureOnce",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);

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
            Store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        }

        public MatchRuntimeStore Store { get; }
        public TcpConnection Connection { get; } = new AcceptingConnection();
        public InMemoryRedisOperations Redis { get; } = new();
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
                static _ => false, TestGameSessionServices.CreateMatchCleanupService(),
                static (_, _) => null,

                TestGameSessionServices.CreatePlayerOrbGrowthService(),
                TestGameSessionServices.CreateMovementService(),
                new PlayerInteractionService(),

                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(recordEntryFailure),
                TestGameSessionServices.CreateEntryService(Redis, Store, NullLogger.Instance),
                trySendConnectSuccessResponse: sender);
            Connection.SetSession(session);
            SetIdentity(session, matchingId, playerId);
            return session;
        }

        public async Task ConnectAsync(GameClientSession session, long matchingId, long playerId)
        {
            UserServerMatchingTestData.EnsureGameDataLoaded();
            await new PlayerInfo(playerId, false) { Name = "Human" }.Save(Redis);
            await Redis.HashSetAsync(MatchingRedisKeys.Key(matchingId), MatchingRedisKeys.ManifestField,
                MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [playerId], BotCount = 0 }));
            await Redis.HashSetAsync(MatchingRedisKeys.Key(matchingId), MatchingRedisKeys.EntryReadyField,
                new byte[] { MatchingRedisKeys.EntryReadyValue });
            await Redis.StringSetAsync(MatchingRedisKeys.ReservationKey(playerId), matchingId, TimeSpan.FromMinutes(2));
            await Redis.StringSetAsync(MatchingRedisKeys.EntryStateKey(matchingId),
                MatchingRedisKeys.EntryPendingState, TimeSpan.FromMinutes(2));
            var tickets = new GameEntryTicketService(new RedisGameEntryTicketStore(Redis), new GameEntryTicketOptions());
            string ticket = await tickets.IssueAsync(new GameEntryContext
            {
                MatchingId = matchingId, PlayerId = playerId, GameServerNodeId = "game-server-test"
            });
            typeof(GameClientSession).GetProperty(nameof(GameClientSession.PlayerId))!.SetValue(session, null);
            SetProperty(session, nameof(GameClientSession.MatchingId), 0L);
            await (Task)typeof(GameClientSession).GetMethod("HandleConnect", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, [new C_TO_G_CONNECT { GameEntryTicket = ticket }])!;
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

    // 초기 스냅샷은 소켓 없이 수락하고 성공 응답은 생성자로 주입한 sender에서 별도 검증한다.
    private sealed class AcceptingConnection : TcpConnection
    {
        public Action<Packet>? BeforeSend { get; set; }
        public override bool TrySend(Packet packet)
        {
            BeforeSend?.Invoke(packet);
            return true;
        }
    }

    private sealed record SentPacket(Protocol Protocol, long PlayerId, byte[] Body);
}
