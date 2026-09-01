using System.Reflection;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.core;
using network.packets;
using network.utils;

namespace demo_regression_tests;

/// <summary>
/// Regression coverage for the successful game-admission ACK boundary.  These tests intentionally
/// invoke the private packet/publish boundary rather than a socket: the important contract is that
/// authentication has committed before a potentially slow enqueue, while the runtime operation
/// lease remains alive until the enqueue completes.
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

        Assert.True(CommitAuthentication(fixture.Registry, matchingId, fixture.Token, session));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));

        Assert.True(PublishCommittedSuccess(session, packet));
        Assert.Single(fixture.SentPackets);
        Assert.Equal(Protocol.G_TO_C_CONNECT_RESULT, fixture.SentPackets[0].Protocol);
    }

    [Fact]
    public async Task BlockedSuccessAck_IsOutsideRuntimeMonitor_LeaseDelaysFinalizationCleanup()
    {
        const long matchingId = 74_002;
        const long otherMatchingId = 74_003;
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

        Task<bool> connect = Task.Run(() =>
        {
            using IDisposable lease = fixture.Registry.TryAcquireOperation(matchingId, static () => { })!;
            using Packet packet = CreateSuccessPacket(session);
            Assert.True(CommitAuthentication(fixture.Registry, matchingId, fixture.Token, session));
            return PublishCommittedSuccess(session, packet);
        });

        Assert.True(senderEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));

        bool sameMatchProgressed = false;
        bool otherMatchProgressed = false;
        Assert.True(await Task.Run(() => fixture.Registry.TryExecute(
            matchingId, () => sameMatchProgressed = true)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await Task.Run(() => fixture.Registry.TryExecute(
            otherMatchingId, () => otherMatchProgressed = true)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sameMatchProgressed);
        Assert.True(otherMatchProgressed);

        int cleanupCount = 0;
        Task<bool> finalization = Task.Run(() => fixture.Registry.TryFinalize(
            matchingId,
            static () => true,
            () => Interlocked.Increment(ref cleanupCount)));
        Assert.True(SpinWait.SpinUntil(() => fixture.Registry.IsTerminal(matchingId), TimeSpan.FromSeconds(5)));
        Assert.True(await finalization.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref cleanupCount));

        releaseSender.Set();
        Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await finalization.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref cleanupCount));
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

        Assert.True(CommitAuthentication(fixture.Registry, matchingId, fixture.Token, session));
        using Packet packet = CreateSuccessPacket(session);

        Assert.False(PublishCommittedSuccess(session, packet));
        Assert.Equal(1, Volatile.Read(ref senderCalls));
        Assert.Equal(1, GetIntField(fixture.Token, "_authenticated"));
        Assert.Equal(1, GetIntField(session, "_admissionCompleted"));
        Assert.Equal(0, GetIntField(session, "_admissionFailureReported"));
        Assert.Empty(fixture.SentPackets);
    }

    [Fact]
    public void FinalizingBeforeCommit_RejectsSuccessWithoutAuthenticationAdmissionOrAck()
    {
        const long matchingId = 74_005;
        using var fixture = new ConnectFixture();
        GameClientSession session = fixture.CreateSession(matchingId, 8_104, _ => true);

        using IDisposable lease = fixture.Registry.TryAcquireOperation(matchingId, static () => { })!;
        Assert.True(fixture.Registry.TryFinalize(matchingId, static () => true, static () => { }));
        Assert.True(fixture.Registry.IsTerminal(matchingId));

        Assert.False(CommitAuthentication(fixture.Registry, matchingId, fixture.Token, session));
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

        int countdown = connectionSource.IndexOf("SendMatchStartCountdown(matchingId);", StringComparison.Ordinal);
        int response = connectionSource.IndexOf("CreateConnectResultPacket(", countdown, StringComparison.Ordinal);
        int authentication = connectionSource.IndexOf("Token.TryMarkAuthenticated", response, StringComparison.Ordinal);
        int publication = connectionSource.IndexOf("TryPublishCommittedConnectResult(successResponse)", authentication, StringComparison.Ordinal);
        Assert.True(countdown >= 0 && countdown < response && response < authentication && authentication < publication);

        int registeredFailure = connectionSource.IndexOf("if (runtimeOperation != null)", publication, StringComparison.Ordinal);
        int deferredAbort = connectionSource.IndexOf("ReportAdmissionFailureOnce()", registeredFailure, StringComparison.Ordinal);
        int earlyFailureResponse = connectionSource.IndexOf(
            "SendConnectResult(false, ErrorCode.FATAL",
            deferredAbort,
            StringComparison.Ordinal);
        Assert.True(registeredFailure >= 0 &&
                    registeredFailure < deferredAbort &&
                    deferredAbort < earlyFailureResponse);
    }

    private static bool CommitAuthentication(
        MatchRuntimeRegistry registry,
        long matchingId,
        UserToken token,
        GameClientSession session)
    {
        return registry.TryExecute(matchingId, () =>
        {
            if (!token.TryMarkAuthenticated(
                    () => SetIntField(session, "_admissionCompleted", 1)))
            {
                throw new OperationCanceledException("Connection closed before authentication commit.");
            }
        });
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

        public MatchRuntimeRegistry Registry { get; } = new();
        public UserToken Token { get; } = new();
        public IReadOnlyList<SentPacket> SentPackets => _sentPackets.ToList();

        public GameClientSession CreateSession(
            long matchingId,
            long playerId,
            Func<Packet, bool> sender,
            Action<GameClientSession>? recordAdmissionFailure = null)
        {
            Activate(Token);
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
                static (_, _) => false,
                static (_, prepare, _) => prepare(),
                static (_, publish) => publish(),
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                Registry.TryAcquireOperation,
                Registry.TryExecute,
                Registry.TryBindOwnerFence,
                static (_, _, _) => { },
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
