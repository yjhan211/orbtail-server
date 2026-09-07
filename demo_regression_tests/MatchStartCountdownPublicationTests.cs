using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.helpers;
using network.hosting;
using network.packets;

namespace demo_regression_tests;

/// <summary>
///     카운트다운 방송과 입장 실패 중단 (#331): 둘 다 매치 잠금 안에서 확정된다 — 초는 한 번만,
///     입장 실패는 터미널을 이긴 호출이 로스터 전원을 한 번 끊고 늦은 호출은 자기 세션만 정리한다.
/// </summary>
public sealed class MatchStartCountdownPublicationTests
{
    [Fact]
    public void PeriodicCountdown_SourceContract_PublishesInsideMatchLockFromMatchTick()
    {
        string repositoryRoot = FindRepositoryRoot();
        string server = ReadNormalizedSource(repositoryRoot, "game_server", "GameServer.cs");
        string combat = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "GameServer.ProximityAutoCombat.cs");
        string broadcast = ReadMethodSlice(
            server,
            "private void BroadcastMatchStartCountdowns(",
            "private IConnectionSession? CreateClientSession(");
        string matchTick = ReadMethodSlice(
            combat,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");

        Assert.DoesNotContain("_lastMatchStartCountdownBroadcast", server);
        AssertInOrder(
            broadcast,
            "MatchStartGate.IsEntryTimedOut(matchingId, DateTime.UtcNow)",
            "EntryFailureHandler.Handle(anchorSession);",
            "MatchRuntimes.Enter(matchingId, out MatchScope scope)",
            "scope.Runtime.IsTerminal",
            "var snapshot = MatchStartGate.GetSnapshot(matchingId);",
            "pacing.LastCountdownSecondsPublished == snapshot.RemainingSeconds",
            "pacing.LastCountdownSecondsPublished = snapshot.RemainingSeconds;",
            ".Where(session => session.MatchingId == matchingId)",
            "if (matchingSessions.Count == 0)",
            "Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN)",
            "MatchingId = matchingId",
            "RemainingSeconds = snapshot.RemainingSeconds",
            "ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()",
            "foreach (var session in matchingSessions)",
            "session.TrySend(packet);");
        Assert.DoesNotContain("anchorSession.DisconnectForEntryFailure();", broadcast);

        // 매치 틱은 잠금 안에서 카운트다운을 먼저 보내고 전투·봇 걸음을 잇는다.
        AssertInOrder(
            matchTick,
            "MatchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "using (scope)",
            "BroadcastMatchStartCountdowns([matchingId], countdownSessions);",
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);",
            "ProcessBotMovementForMatching(matchingId)");
    }

    [Fact]
    public void DirectCountdownPathsAndClockOnlyStartBarrier_RemainUnchanged()
    {
        string repositoryRoot = FindRepositoryRoot();
        string connection = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Network",
            "GameClientSession.Connection.cs");
        string startGate = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Services",
            "MatchStartGate.cs");
        string connect = ReadMethodSlice(
            connection,
            "private async Task HandleConnect(C_TO_G_CONNECT msg)",
            "private void RunUnderLiveMatch(");
        string directCountdown = ReadMethodSlice(
            connection,
            "private void SendMatchStartCountdown(long matchingId)",
            "private Task HandleHeartbeat()");
        string gameplayActive = ReadMethodSlice(
            startGate,
            "public static bool IsGameplayActive(long matchingId)",
            "public static bool IsEntryTimedOut(");

        AssertInOrder(
            connect,
            "MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);",
            "SendMatchStartCountdown(matchingId);",
            "using Packet successResponse = CreateConnectResultPacket(",
            "RunUnderLiveMatch(runtime, () =>",
            "Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1))",
            "TryPublishCommittedConnectResult(successResponse)");
        AssertInOrder(
            directCountdown,
            "private void SendMatchStartCountdown(long matchingId)",
            "MatchStartGate.GetSnapshot(matchingId)",
            "Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0)",
            "Send(packet);",
            "private Task HandleMatchStartReady()",
            "MatchStartGate.MarkHumanReady(MatchingId, PlayerId.Value);",
            "SendMatchStartCountdown(MatchingId);");
        Assert.DoesNotContain("LastCountdownSecondsPublished", connection);

        AssertInOrder(
            gameplayActive,
            "state.CountdownEndsAtUtc is { } endsAt",
            "DateTime.UtcNow >= endsAt");
        Assert.DoesNotContain("PeriodicCountdown", gameplayActive);
        Assert.DoesNotContain("publication", gameplayActive, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PeriodicCountdown_PublishesEachSecondOnceUnderMatchLock()
    {
        const long matchingId = 71_002;
        GameServer server = CreateEntryTestServer();
        MatchRuntime runtime = server.MatchRuntimes.GetOrCreate(matchingId);
        MatchStartGate.RegisterHumanPlayer(
            matchingId,
            playerId: 301,
            botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
        try
        {
            var first = new RecordingEntrySession();
            SetSessionIdentity(first, 301, matchingId);
            var second = new RecordingEntrySession();
            SetSessionIdentity(second, 302, matchingId);
            var differentMatch = new RecordingEntrySession();
            SetSessionIdentity(differentMatch, 999, matchingId + 1);

            using var lockHeld = new ManualResetEventSlim();
            using var releaseLock = new ManualResetEventSlim();
            Task holder = Task.Run(() =>
            {
                using (server.MatchRuntimes.Enter(runtime))
                {
                    lockHeld.Set();
                    Assert.True(releaseLock.Wait(TimeSpan.FromSeconds(5)));
                }
            });
            Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));

            Task broadcast = Task.Run(() => InvokePeriodicBroadcast(
                server,
                [matchingId],
                [first, second, differentMatch]));
            await Task.Delay(100);
            Assert.False(broadcast.IsCompleted);
            Assert.Equal(0, first.SendCount);
            Assert.Equal(0, second.SendCount);

            releaseLock.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            await broadcast.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, first.SendCount);
            Assert.Equal(1, second.SendCount);
            Assert.Equal(0, differentMatch.SendCount);
            byte[] firstWire = Assert.Single(first.DeliveredWireBytes);
            byte[] secondWire = Assert.Single(second.DeliveredWireBytes);
            Assert.Equal(firstWire, secondWire);
            Assert.Equal(
                (int)Protocol.G_TO_C_MATCH_START_COUNTDOWN,
                BitConverter.ToInt32(firstWire, Config.HEADER_SIZE));
            var body = MessagePackSerializer.Deserialize<G_TO_C_MATCH_START_COUNTDOWN>(
                firstWire[(Config.HEADER_SIZE + sizeof(int) + sizeof(long))..]);
            Assert.Equal(matchingId, body.MatchingId);
            Assert.Equal(-1, body.RemainingSeconds);
            Assert.True(body.ServerUnixMs > 0);

            // 같은 초는 다시 보내지 않는다.
            InvokePeriodicBroadcast(
                server,
                [matchingId],
                [first, second, differentMatch]);
            Assert.Equal(1, first.SendCount);
            Assert.Equal(1, second.SendCount);

            // 수신자가 없어도 초는 기록된다 — 늦게 붙은 세션이 옛 초를 받지 않는다.
            const long noRecipientMatchingId = 71_003;
            server.MatchRuntimes.GetOrCreate(noRecipientMatchingId);
            MatchStartGate.RegisterHumanPlayer(
                noRecipientMatchingId,
                playerId: 401,
                botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
            try
            {
                InvokePeriodicBroadcast(server, [noRecipientMatchingId], []);
                Assert.Equal(-1, GetPacing(server, noRecipientMatchingId).LastCountdownSecondsPublished);
            }
            finally
            {
                MatchStartGate.RemoveMatching(noRecipientMatchingId);
            }
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    [Fact]
    public void PeriodicCountdown_TransportFailureCommitsSecondWithoutRetry()
    {
        const long matchingId = 71_004;
        GameServer server = CreateEntryTestServer();
        server.MatchRuntimes.GetOrCreate(matchingId);
        MatchStartGate.RegisterHumanPlayer(
            matchingId,
            playerId: 501,
            botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
        try
        {
            var failing = new RecordingEntrySession(throwOnSend: true);
            SetSessionIdentity(failing, 501, matchingId);

            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(
                () => InvokePeriodicBroadcast(server, [matchingId], [failing]));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal(1, failing.SendCount);
            Assert.Equal(-1, GetPacing(server, matchingId).LastCountdownSecondsPublished);
            // 실패해도 잠금은 풀린다.
            Assert.True(server.MatchRuntimes.TryEnter(matchingId, out MatchScope scope));
            scope.Dispose();

            InvokePeriodicBroadcast(server, [matchingId], [failing]);
            Assert.Equal(1, failing.SendCount);
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    [Fact]
    public void EntryFailure_WinnerDisconnectsRosterOnce_AndLateCallsFallBackToSelf()
    {
        const long matchingId = 71_001;
        GameServer server = CreateEntryTestServer();
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(GameSessionRegistry))
                .GetValue(server));
        MatchRuntime runtime = server.MatchRuntimes.GetOrCreate(matchingId);

        var anchor = new RecordingEntrySession();
        SetSessionIdentity(anchor, playerId: 101, matchingId);
        var other = new RecordingEntrySession();
        SetSessionIdentity(other, playerId: 202, matchingId);
        Assert.Null(sessionRegistry.Register(101, anchor, out bool anchorAdded));
        Assert.True(anchorAdded);
        Assert.Null(sessionRegistry.Register(202, other, out bool otherAdded));
        Assert.True(otherAdded);
        Assert.Equal(2, sessionRegistry.GetByMatch(matchingId).Count);

        InvokeEntryAbort(server, anchor);

        Assert.True(runtime.IsTerminal);
        Assert.Null(server.MatchRuntimes.Get(matchingId));
        Assert.Equal(1, anchor.FatalCount);
        Assert.Equal(1, anchor.DisconnectCount);
        Assert.Equal(1, other.FatalCount);
        Assert.Equal(1, other.DisconnectCount);
        Assert.Empty(sessionRegistry.GetByMatch(matchingId));
        ConcurrentDictionary<long, string> playerSubjects = GetTerminalSubjects(server)[matchingId];
        Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[101]);

        // 이미 끝난 매치에 늦게 온 호출은 자기 세션의 entry_failed만 발행하고 끊기는 반복하지 않는다.
        InvokeEntryAbort(server, other);
        InvokeEntryAbort(server, other);

        Assert.Equal(2, playerSubjects.Count);
        Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[202]);
        Assert.Equal(1, other.FatalCount);
        Assert.Equal(1, other.DisconnectCount);
    }

    [Fact]
    public void EntryFailure_AfterNormalCompletion_KeepsCompletedSubjectAndClosesOnce()
    {
        const long matchingId = 71_005;
        const long completedPlayerId = 601;
        GameServer server = CreateEntryTestServer();
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(GameSessionRegistry))
                .GetValue(server));
        var completedSession = new RecordingEntrySession();
        SetSessionIdentity(completedSession, completedPlayerId, matchingId);
        Assert.Null(sessionRegistry.Register(completedPlayerId, completedSession, out bool completedAdded));
        Assert.True(completedAdded);

        // 정상 종료가 잠금 안에서 subject를 먼저 선점하고 터미널로 끝난다.
        MatchRuntime runtime = server.MatchRuntimes.GetOrCreate(matchingId);
        Action? completion;
        using (server.MatchRuntimes.Enter(runtime))
        {
            completion = PrepareLifecyclePublication(
                server,
                MatchingLifecycleSubjects.PlayerCompleted,
                completedPlayerId,
                matchingId);
            Assert.True(runtime.TryMarkTerminal());
        }

        Assert.NotNull(completion);
        Assert.Null(server.MatchRuntimes.Get(matchingId));

        InvokeEntryAbort(server, completedSession);
        InvokeEntryAbort(server, completedSession);

        ConcurrentDictionary<long, string> playerSubjects = GetTerminalSubjects(server)[matchingId];
        Assert.Single(playerSubjects);
        Assert.Equal(MatchingLifecycleSubjects.PlayerCompleted, playerSubjects[completedPlayerId]);
        Assert.Equal(1, completedSession.FatalCount);
        Assert.Equal(1, completedSession.DisconnectCount);
    }

    [Fact]
    public void EntryDisconnect_StillBuildsFatalPacketAndRequestsGracefulClose()
    {
        string repositoryRoot = FindRepositoryRoot();
        string session = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Network",
            "GameClientSession.cs");
        string method = ReadMethodSlice(
            session,
            "internal virtual void DisconnectForEntryFailure()",
            "internal bool TryMarkMatchingLifecycleHandledExternally()");

        AssertInOrder(
            method,
            "Interlocked.Exchange(ref _entryDisconnectIssued, 1) != 0",
            "MarkServerInitiatedDisconnect();",
            "PacketMaker.G_TO_C_ERROR(ErrorCode.FATAL",
            "Connection.TrySendAndDisconnect(packet);",
            "catch (Exception ex)",
            "Connection.Disconnect();");
        Assert.Equal(1, CountOccurrences(method, "Connection.TrySendAndDisconnect(packet);"));
    }

    private static GameServer CreateEntryTestServer()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return new GameServer(
            configuration: configuration,
            logger: NullLogger<GameServer>.Instance,
            matchingLifecycle: new MatchingLifecycleService(new InMemoryRedisOperations(), new NoOpNatsClient(), NullLogger.Instance),
            redisOperations: null!,
            networkService: null!,
            gameHandoffTicketService: null!,
            readinessState: new ServerReadinessState(),
            gameServerRegistry: new RecordingGameServerRegistry(),
            nodeOptions: new GameServerNodeOptions { NodeId = "game-server-test", PublicHost = "127.0.0.1" },
            devOptions: GameServerDevOptions.Disabled,
            sessions: new GameSessionRegistry());
    }

    internal sealed class NoOpNatsClient : network.infrastructure.messaging.INatsClient
    {
        public void Publish(string subject, byte[] message) { }
        public void Subscribe(string subject, Action<string, byte[]> handler, string? queue = null) { }
        public Task<byte[]> RequestAsync(string subject, byte[] message, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SubscribeRequest(string subject, Func<string, byte[], CancellationToken, Task<byte[]?>> handler, string? queue = null) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Close() { }
    }

    private static SwarmMatchPacingState GetPacing(GameServer server, long matchingId)
    {
        return server.MatchRuntimes.GetRequired(matchingId).Swarm.Pacing;
    }

    private static void InvokePeriodicBroadcast(
        GameServer server,
        IReadOnlyCollection<long> matchingIds,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        typeof(GameServer)
            .GetMethod(
                "BroadcastMatchStartCountdowns",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [matchingIds, sessions]);
    }

    private static void InvokeEntryAbort(GameServer server, GameClientSession session)
    {
        server.EntryFailureHandler.Handle(session);
    }

    private static Action? PrepareLifecyclePublication(
        GameServer server,
        string subject,
        long playerId,
        long matchingId)
    {
        return server.MatchingLifecycle.PreparePublication(subject, playerId, matchingId);
    }

    private static ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        GetTerminalSubjects(GameServer server)
    {
        return Assert.IsType<ConcurrentDictionary<long, ConcurrentDictionary<long, string>>>(
            typeof(MatchingLifecycleService)
                .GetField(
                    "_matchingLifecycleTerminalSubjects",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server.MatchingLifecycle));
    }

    private static void SetSessionIdentity(
        GameClientSession session,
        long playerId,
        long matchingId)
    {
        typeof(GameClientSession)
            .GetProperty(
                nameof(GameClientSession.PlayerId),
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, playerId);
        typeof(GameClientSession)
            .GetProperty(
                nameof(GameClientSession.MatchingId),
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, matchingId);
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{marker}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += marker.Length;
        }

        return count;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] parts)
    {
        return File.ReadAllText(Path.Combine([repositoryRoot, .. parts]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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

    private sealed class RecordingEntrySession : GameClientSession
    {
        private static readonly FieldInfo EntryDisconnectIssuedField =
            typeof(GameClientSession).GetField(
                "_entryDisconnectIssued",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly bool _throwOnSend;

        public RecordingEntrySession(bool throwOnSend = false)
            : base(
                new TcpConnection(),
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                static (_, _) => [],
                null!,
                null!,
                null!,
                new MatchRuntimeStore(NullLogger.Instance),
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                static _ => { },
                GameServerDevOptions.Disabled)
        {
            _throwOnSend = throwOnSend;
        }

        public int FatalCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int SendCount { get; private set; }
        public List<byte[]> DeliveredWireBytes { get; } = [];

        internal override void DisconnectForEntryFailure()
        {
            int wasIssued = (int)EntryDisconnectIssuedField.GetValue(this)!;
            base.DisconnectForEntryFailure();
            int isIssued = (int)EntryDisconnectIssuedField.GetValue(this)!;
            if (wasIssued == 0 && isIssued == 1)
            {
                FatalCount++;
                DisconnectCount++;
            }
        }

        public override bool TrySend(Packet packet)
        {
            SendCount++;
            if (_throwOnSend)
                throw new InvalidOperationException("countdown transport failed");
            DeliveredWireBytes.Add(packet.ToBytes());
            return true;
        }
    }
}
