using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.matches;
using game_server.matches.entry;
using game_server.players;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.helpers;
using network.hosting;
using network.packets;

namespace demo_regression_tests;

/// <summary>
///     시작 시각 전달과 입장 실패 중단을 검증한다. 매 틱 카운트다운을 보내지 않으며,
///     입장 실패는 터미널을 이긴 호출이 로스터 전원을 한 번 끊고 늦은 호출은 자기 세션만 정리한다.
/// </summary>
public sealed class MatchStartCountdownPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntryTick_StillAbortsWhenDeadlineExpires(bool hasSession)
    {
        const long matchingId = 71004;
        var server = CreateEntryTestServer();
        var session = new RecordingEntrySession();
        SetSessionIdentity(server.GetMatchRuntimes(), session, 301, matchingId);
        var runtime = server.GetMatchRuntimes().GetOrCreate(matchingId);
        if (hasSession) TestGameSessionServices.AttachSession(session);
        server.GetMatchRuntimes().GetOrThrow(matchingId).PrepareEntry(301, 302);
        try
        {
            typeof(MatchRuntime).GetField("_entryDeadlineUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(runtime, DateTime.UtcNow - TimeSpan.FromSeconds(1));
            InvokeEntryTimeoutCheck(server, [matchingId], [session]);
            Assert.True(runtime.IsEnded);
            Assert.Null(server.GetMatchRuntimes().GetOrNull(matchingId));
        }
        finally { }
    }

    [Fact]
    public void EntryTick_DoesNotBroadcastCountdown()
    {
        const long matchingId = 71002;
        var server = CreateEntryTestServer();
        var session = new RecordingEntrySession();
        SetSessionIdentity(server.GetMatchRuntimes(), session, 301, matchingId);
        server.GetMatchRuntimes().GetOrThrow(matchingId).PrepareEntry(301);
        try
        {
            InvokeEntryTimeoutCheck(server, [matchingId], [session]);
            server.GetMatchRuntimes().GetOrThrow(matchingId).MarkPlayerReady(301);
            InvokeEntryTimeoutCheck(server, [matchingId], [session]);
            InvokeEntryTimeoutCheck(server, [matchingId], [session]);
            Assert.Equal(0, session.SendCount);
        }
        finally { }
    }

    [Fact]
    public async Task LastReadyPlayer_SendsSameStartTimeToAllParticipants()
    {
        const long matchingId = 71003;
        var server = CreateEntryTestServer();
        var runtime = server.GetMatchRuntimes().GetOrCreate(matchingId);
        var first = new RecordingEntrySession();
        var second = new RecordingEntrySession();
        SetSessionIdentity(server.GetMatchRuntimes(), first, 301, matchingId);
        SetSessionIdentity(server.GetMatchRuntimes(), second, 302, matchingId);
        TestGameSessionServices.AttachSession(first);
        TestGameSessionServices.AttachSession(second);
        server.GetMatchRuntimes().GetOrThrow(matchingId).PrepareEntry(301, 302);
        server.GetMatchRuntimes().GetOrThrow(matchingId).BeginEntry(302);
        var ready = typeof(GameClientSession).GetMethod("HandleMatchStartReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await (Task)ready.Invoke(first, null)!;
            Assert.Equal(1, first.SendCount);
            await (Task)ready.Invoke(second, null)!;
            Assert.Equal(2, first.SendCount);
            Assert.Equal(1, second.SendCount);
            var firstBody = MessagePackSerializer.Deserialize<G_TO_C_MATCH_START_COUNTDOWN>(first.DeliveredWireBytes.Last()[(Config.HEADER_SIZE + sizeof(int) + sizeof(long))..]);
            var secondBody = MessagePackSerializer.Deserialize<G_TO_C_MATCH_START_COUNTDOWN>(second.DeliveredWireBytes.Last()[(Config.HEADER_SIZE + sizeof(int) + sizeof(long))..]);
            Assert.Equal(firstBody.StartsAtUnixMs, secondBody.StartsAtUnixMs);
            Assert.InRange(firstBody.StartsAtUnixMs - firstBody.ServerUnixMs, 1, 5000);
            Assert.False(server.GetMatchRuntimes().GetOrThrow(matchingId).IsGameplayActive());
        }
        finally { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntryFailure_WinnerDisconnectsRosterOnce_AndLateCallsFallBackToSelf(bool hasComposition)
    {
        const long matchingId = 71_001;
        GameServer server = CreateEntryTestServer();
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(GameSessionRegistry))
                .GetValue(server));
        MatchRuntime runtime = server.GetMatchRuntimes().GetOrCreate(matchingId);
        if (hasComposition)
        {
            using var scope = runtime.Enter();
            runtime.InitializeMatch(
                default, new Dictionary<long, Cell>(),
                [new PlayerInfo { PlayerId = 101 }, new PlayerInfo { PlayerId = 202 },
                    new PlayerInfo { PlayerId = 303 }, new PlayerInfo { PlayerId = -404 }]);
        }

        // 구성 확정 전에도 등록할 세션은 매치의 실제 참가자를 가리켜야 한다.
        if (!hasComposition)
        {
            runtime.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = 101 } });
            runtime.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = 202 } });
            Assert.False(runtime.IsSetupComplete);
        }
        var anchor = new RecordingEntrySession();
        SetSessionIdentity(server.GetMatchRuntimes(), anchor, playerId: 101, matchingId);
        var other = new RecordingEntrySession();
        SetSessionIdentity(server.GetMatchRuntimes(), other, playerId: 202, matchingId);
        Assert.Null(sessionRegistry.Register(101, anchor));
        Assert.Null(sessionRegistry.Register(202, other));
        Assert.Equal(2, anchor.Match.GetSessions().Count);

        InvokeEntryAbort(server, anchor);

        Assert.True(runtime.IsEnded);
        Assert.Null(server.GetMatchRuntimes().GetOrNull(matchingId));
        Assert.Equal(1, anchor.FatalCount);
        Assert.Equal(1, anchor.DisconnectCount);
        Assert.Equal(1, other.FatalCount);
        Assert.Equal(1, other.DisconnectCount);
        Assert.Empty(anchor.Match.GetSessions());
        ConcurrentDictionary<long, string> playerSubjects = GetTerminalSubjects(server)[matchingId];
        if (hasComposition)
        {
            Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[101]);
            // 아직 접속하지 않은 사람도 정리하고 봇은 포함하지 않는다.
            Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[202]);
            Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[303]);
            Assert.False(playerSubjects.ContainsKey(-404));
        }
        else
        {
            // 구성 전 실패를 일으킨 본인은 전체 통지 대상에서 제외된다.
            Assert.False(playerSubjects.ContainsKey(101));
            Assert.Single(playerSubjects);
        }

        // 이미 끝난 매치에 늦게 온 호출은 자기 세션의 entry_failed만 발행하고 끊기는 반복하지 않는다.
        InvokeEntryAbort(server, other);
        InvokeEntryAbort(server, other);

        Assert.Equal(hasComposition ? 3 : 1, playerSubjects.Count);
        Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[202]);
        // 처음 실패한 세션의 늦은 호출도 자신의 통지만 한 번 등록한다.
        InvokeEntryAbort(server, anchor);
        InvokeEntryAbort(server, anchor);
        Assert.Equal(hasComposition ? 3 : 2, playerSubjects.Count);
        Assert.Equal(MatchingLifecycleSubjects.PlayerEntryFailed, playerSubjects[101]);
        Assert.Equal(1, anchor.FatalCount);
        Assert.Equal(1, anchor.DisconnectCount);
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
        SetSessionIdentity(server.GetMatchRuntimes(), completedSession, completedPlayerId, matchingId);
        completedSession.Match.RegisterParticipant(completedSession.Player);
        Assert.Null(sessionRegistry.Register(completedPlayerId, completedSession));
        Assert.Same(completedSession, Assert.Single(completedSession.Match.GetSessions()));

        // 정상 종료가 잠금 안에서 subject를 먼저 선점하고 터미널로 끝난다.
        MatchRuntime runtime = server.GetMatchRuntimes().GetOrCreate(matchingId);
        Action? completion;
        using (runtime.Enter())
        {
            completion = PrepareLifecyclePublication(
                server,
                MatchingLifecycleSubjects.PlayerCompleted,
                completedPlayerId,
                matchingId);
            Assert.True(runtime.TryMarkEnded());
        }

        Assert.NotNull(completion);
        Assert.Null(server.GetMatchRuntimes().GetOrNull(matchingId));

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
            "Sessions",
            "GameClientSession.cs");
        string method = ReadMethodSlice(
            session,
            "internal virtual void DisconnectForEntryFailure()",
            "MarkMatchEndHandledExternally()");

        AssertInOrder(
            method,
            "Interlocked.Exchange(ref _entryDisconnectIssued, 1) != 0",
            "MarkDisconnectedByServer();",
            "PacketMaker.G_TO_C_ERROR(ErrorCode.GAME_ENTRY_FAILED)",
            "Connection.TrySendAndDisconnect(packet);",
            "catch (Exception ex)",
            "Connection.Disconnect();");
        Assert.Equal(1, CountOccurrences(method, "Connection.TrySendAndDisconnect(packet);"));
        Assert.DoesNotContain("게임 입장 초기화에 실패했습니다", method);
    }

    private static GameServer CreateEntryTestServer() => GameServerTestAccess.Create();

    internal sealed class NoOpNatsClient : network.infrastructure.messaging.INatsClient
    {
        public void Publish(string subject, byte[] message) { }
        public void Subscribe(string subject, Action<string, byte[]> handler, string? queue = null) { }
        public Task<byte[]> RequestAsync(string subject, byte[] message, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SubscribeRequest(string subject, Func<string, byte[], CancellationToken, Task<byte[]?>> handler, string? queue = null) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Close() { }
    }

    private static void InvokeEntryTimeoutCheck(
        GameServer server,
        IReadOnlyCollection<long> matchingIds,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        foreach (long matchingId in matchingIds)
            GameServerTestAccess.GetLoop(server, matchingId).ProcessTick();
    }
    private static void InvokeEntryAbort(GameServer server, GameClientSession session)
    {
        server.GetEntryFailureHandler().Handle(session);
    }

    private static Action? PrepareLifecyclePublication(
        GameServer server,
        string subject,
        long playerId,
        long matchingId)
    {
        return server.GetMatchingLifecycle().PrepareNotification(subject, playerId, matchingId);
    }

    private static ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        GetTerminalSubjects(GameServer server)
    {
        return Assert.IsType<ConcurrentDictionary<long, ConcurrentDictionary<long, string>>>(
            typeof(MatchSessionCleanupService)
                .GetField(
                    "_playerNotifications",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server.GetMatchingLifecycle()));
    }

    private static void SetSessionIdentity(
        MatchRuntimeStore store,
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
        TestGameSessionServices.BindMatch(session, matchingId, store);
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
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("public virtual void ", "public void ", StringComparison.Ordinal)
            // 명시 타입과 var 표기는 같은 잠금 호출로 취급한다.
            .Replace("matchRuntimes.Enter(matchingId, out var scope)",
                "matchRuntimes.Enter(matchingId, out MatchLockScope scope)", StringComparison.Ordinal);
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
                static _ => false, TestGameSessionServices.CreateMatchCleanupService(),
                static (_, _) => null,
                null!,
                TestGameSessionServices.CreatePlayerOrbGrowthService(TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance), TestGameEventLogs.Create()),
                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(),
                null!,
                null!)
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
