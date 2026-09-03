using System.Reflection;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using game_server.network;
using game_server.services;
using network.common.data.models;
using network.common.data;
using network.common;
using network.core;
using network.gamehandoff;
using network.packets;
using network.utils;

namespace demo_regression_tests;

/// <summary>
///     입장 성공 ACK 경계 (#331): 인증 커밋은 매치 잠금 안에서 원자적으로, 느릴 수 있는 큐 적재는 잠금 밖에서.
///     소켓 대신 private 패킷/발행 경계를 직접 부른다.
/// </summary>
public sealed class GameClientSessionConnectPublicationTests
{
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
        Assert.Equal("Connected to GameServer", body.Message);

        Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Token, session));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));

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
            Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Token, session));
            return PublishCommittedSuccess(session, packet);
        });

        Assert.True(senderEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));

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
    public void FailedCommittedSuccessAck_FailsForwardWithoutNegativeAckOrAdmissionRollback(bool senderThrows)
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

        Assert.True(CommitAuthentication(fixture.Store, matchingId, fixture.Token, session));
        using Packet packet = CreateSuccessPacket(session);

        Assert.False(PublishCommittedSuccess(session, packet));
        Assert.Equal(1, Volatile.Read(ref senderCalls));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));
        Assert.Equal(0, GetIntField(session, "_admissionFailureReported"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void TerminalBeforeCommit_RejectsSuccessWithoutAuthenticationAdmissionOrAck()
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
        Assert.False(CommitAuthentication(fixture.Store, matchingId, fixture.Token, session));
        Assert.Equal(0, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(0, GetIntField(session, "_admissionCompleted"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void ThrowingAdmissionAbortHook_ReleasesOnlyItsReservations_ForDisconnectRetry()
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

        Assert.False(ReportAdmissionFailure(session));
        Assert.Equal(0, GetIntField(session, "_admissionFailureReported"));
        Assert.Equal(0, GetIntField(session, "_matchingLifecycleTerminalReported"));

        session.OnDisconnect();

        Assert.Equal(2, Volatile.Read(ref hookCalls));
        Assert.Equal(1, GetIntField(session, "_admissionFailureReported"));
        Assert.Equal(1, GetIntField(session, "_matchingLifecycleTerminalReported"));
    }

    [Fact]
    public void ConnectHandler_WiresConnectProtocol_AndQueuesCountdownBeforeCommittedAck()
    {
        string root = FindRepositoryRoot();
        string sessionSource = File.ReadAllText(
            Path.Combine(root, "game_server", "Network", "GameClientSession.cs"));
        string connectionSource = File.ReadAllText(
            Path.Combine(root, "game_server", "Network", "GameClientSession.Connection.cs"));

        Assert.Contains("ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT", sessionSource);
        Assert.Contains("async bytes => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect)", sessionSource);
        Assert.Contains("trySendConnectSuccessResponse ?? Token.TrySend", sessionSource);
        Assert.Contains("Token.TryMarkAuthenticated(() => Volatile.Write(ref _admissionCompleted, 1))", connectionSource);

        // 세션 등록·초기화 블록·인증 커밋은 매치 잠금 안에서, 성공 ACK 큐 적재는 잠금 밖에서.
        int registration = connectionSource.IndexOf("_matchRuntimes.GetOrCreate(matchingId)", StringComparison.Ordinal);
        int registerCallback = connectionSource.IndexOf("_registerSessionCallback(playerId, this)", registration, StringComparison.Ordinal);
        int countdown = connectionSource.IndexOf("SendMatchStartCountdown(matchingId);", registerCallback, StringComparison.Ordinal);
        int response = connectionSource.IndexOf("CreateConnectResultPacket(", countdown, StringComparison.Ordinal);
        int commitScope = connectionSource.IndexOf("RunUnderLiveMatch(runtime, () =>", response, StringComparison.Ordinal);
        int authentication = connectionSource.IndexOf("Token.TryMarkAuthenticated", commitScope, StringComparison.Ordinal);
        int publication = connectionSource.IndexOf("TryPublishCommittedConnectResult(successResponse)", authentication, StringComparison.Ordinal);
        Assert.True(registration >= 0 && registration < registerCallback && registerCallback < countdown &&
                    countdown < response && response < commitScope && commitScope < authentication &&
                    authentication < publication);

        int registeredFailure = connectionSource.IndexOf("if (registered)", publication, StringComparison.Ordinal);
        int deferredAbort = connectionSource.IndexOf("ReportAdmissionFailureOnce()", registeredFailure, StringComparison.Ordinal);
        int earlyFailureResponse = connectionSource.IndexOf(
            "SendConnectResult(false, ErrorCode.FATAL",
            deferredAbort,
            StringComparison.Ordinal);
        Assert.True(registeredFailure >= 0 &&
                    registeredFailure < deferredAbort &&
                    deferredAbort < earlyFailureResponse);
        Assert.Contains("throw new OperationCanceledException(\"Match became terminal during game admission.\")", connectionSource);
    }

    private static bool CommitAuthentication(
        MatchRuntimeStore store,
        long matchingId,
        UserToken token,
        GameClientSession session)
    {
        MatchRuntime? runtime = store.Get(matchingId);
        if (runtime == null)
            return false;

        using MatchScope scope = store.Enter(runtime);
        if (runtime.IsTerminal)
            return false;

        if (!token.TryMarkAuthenticated(
                () => SetIntField(session, "_admissionCompleted", 1)))
        {
            throw new OperationCanceledException("Connection closed before authentication commit.");
        }

        return true;
    }

    private static Packet CreateSuccessPacket(GameClientSession session) =>
        Assert.IsType<Packet>(typeof(GameClientSession).GetMethod(
            "CreateConnectResultPacket",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            session,
            [true, ErrorCode.SUCCESS, "Connected to GameServer"]));

    private static bool PublishCommittedSuccess(GameClientSession session, Packet packet) =>
        Assert.IsType<bool>(typeof(GameClientSession).GetMethod(
            "TryPublishCommittedConnectResult",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [packet]));

    private static bool ReportAdmissionFailure(GameClientSession session) =>
        Assert.IsType<bool>(typeof(GameClientSession).GetMethod(
            "ReportAdmissionFailureOnce",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null));

    private static (Protocol Protocol, long PlayerId, G_TO_C_CONNECT_RESULT Body) DeserializeConnectResult(Packet packet)
    {
        using Packet wirePacket = Packet.Create(new Const<byte[]>(packet.ToBytes()));
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
        SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);
    }

    private static void SetProperty(GameClientSession session, string name, object value) =>
        typeof(GameClientSession).GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

    private static void Activate(UserToken token)
    {
        int active = (int)typeof(UserToken).GetField(
            "StateActive",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        SetIntField(token, "_state", active);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "game_server", "Network")))
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
        public UserToken Token { get; } = new();
        public IReadOnlyList<SentPacket> SentPackets => _sentPackets.ToList();

        public GameClientSession CreateSession(
            long matchingId,
            long playerId,
            Func<Packet, bool> sender,
            Action<GameClientSession>? recordAdmissionFailure = null)
        {
            Activate(Token);
            Store.GetOrCreate(matchingId);
            var session = new GameClientSession(
                Token,
                null!,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                static (_, _) => [],
                new InteractableStateManager(),
                new InGameInventoryManager(),
                new AreaItemStockManager(false),
                new GroundItemManager(),
                new SummonStoneManager(),
                new DoorStateManager(),
                new MatchRosterManager(NullLogger.Instance),
                new AreaClosureManager(NullLogger.Instance),
                new BotPlayerManager(NullLogger.Instance),
                new GameEventLogManager(),
                new MatchSummaryFileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
                new EncounterRevealManager(),
                Store,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                recordAdmissionFailure ?? (_ => { }),
                sender);
            SetIdentity(session, matchingId, playerId);
            return session;
        }

        public void Record(Packet packet)
        {
            using Packet wirePacket = Packet.Create(new Const<byte[]>(packet.ToBytes()));
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
