using System.Collections.Concurrent;
using System.Reflection;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.packets;

namespace demo_regression_tests;

/// <summary>
///     매치 종료 (#331): 결과·GAME_END·MarkGameEnded는 매치 잠금 안에서 chunk-major로 나가고, 정리는 잠금
///     탈출에서 한 번, lifecycle 발행·요약 영속은 잠금 밖 후처리로 돈다.
/// </summary>
public sealed class GameClientSessionTerminalPublicationTests
{
    public GameClientSessionTerminalPublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public async Task TryEndMatch_WaitsForMatchLock_ThenPublishesResultBeforeCleanupLifecycleAndSummary()
    {
        const long matchingId = 73001;
        const long otherMatchingId = 73901;
        using var fixture = new TerminalFixture();
        RecordingSession winner = fixture.CreateSession(matchingId, 101, PlayerMatchStatus.ACTIVE);
        RecordingSession survivor = fixture.CreateSession(matchingId, 202, PlayerMatchStatus.ACTIVE);
        RecordingSession spectator = fixture.CreateSession(matchingId, 303, PlayerMatchStatus.SPECTATING);
        RecordingSession[] sessions = [winner, survivor, spectator];
        fixture.SeedRoster(matchingId, sessions, totalEntries: 8, useLongProfiles: true);
        MatchRuntime runtime = fixture.Store.GetOrCreate(matchingId);
        fixture.TrackedRuntime = runtime;
        fixture.Store.GetOrCreate(otherMatchingId);

        using var lockHeld = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using (fixture.Store.Enter(runtime))
            {
                lockHeld.Set();
                Assert.True(releaseLock.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));

        Task terminal = Task.Run(() => winner.TryEndMatch(winner.PlayerId!.Value, "terminal_behavior"));
        try
        {
            await Task.Delay(100);
            Assert.False(terminal.IsCompleted);
            Assert.All(sessions, session => Assert.Empty(fixture.ConnectionFor(session).AttemptedProtocols));

            // 다른 매치는 잠금이 독립이라 그대로 진행한다.
            Assert.True(fixture.Store.TryEnter(otherMatchingId, out MatchScope otherScope));
            otherScope.Dispose();
            Assert.False(fixture.Store.TryEnter(matchingId, out _));
        }
        finally
        {
            releaseLock.Set();
        }

        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.WaitAsync(TimeSpan.FromSeconds(5));

        G_TO_C_GAME_RESULT winnerResult = Assert.Single(fixture.ConnectionFor(winner)
            .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT));
        Assert.Equal(winner.PlayerId, winnerResult.WinnerId);
        Assert.False(winnerResult.IsTimeout);
        // 긴 프로필 로스터는 I/O 버퍼(2KB)를 넘는다 — 결과는 나누지 않고 한 메시지로 간다.
        Assert.True(MessagePackSerializer.Serialize(winnerResult).Length > Config.BUFFER_SIZE);

        long[] recipientIds = sessions.Select(session => session.PlayerId!.Value).ToArray();
        Assert.Equal(
            recipientIds,
            fixture.Deliveries
                .Where(delivery => delivery.Protocol == Protocol.G_TO_C_GAME_RESULT)
                .Select(delivery => delivery.PlayerId)
                .ToArray());
        Assert.Equal(
            Enumerable.Repeat(Protocol.G_TO_C_GAME_RESULT, sessions.Length)
                .Concat(Enumerable.Repeat(Protocol.G_TO_C_GAME_END, sessions.Length)),
            fixture.Deliveries.Select(delivery => delivery.Protocol));

        foreach (RecordingSession session in sessions)
        {
            RecordingTcpConnection connection = fixture.ConnectionFor(session);
            Assert.Single(connection.DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT));

            G_TO_C_GAME_END gameEnd =
                connection.DeserializeSingle<G_TO_C_GAME_END>(Protocol.G_TO_C_GAME_END);
            Assert.Equal(matchingId, gameEnd.MatchingId);
            Assert.Equal(session.PlayerId == winner.PlayerId, gameEnd.IsEscaped);
            Assert.True(session.IsGameEnded);
        }

        GameResultPlayerInfo winnerRow = Assert.Single(
            winnerResult.Players,
            player => player.PlayerId == winner.PlayerId);
        GameResultPlayerInfo spectatorRow = Assert.Single(
            fixture.ConnectionFor(spectator)
                .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT)
                .Single().Players,
            player => player.PlayerId == spectator.PlayerId);
        Assert.Equal(1, winnerRow.Rank);
        Assert.Equal(PlayerMatchStatus.SPECTATING, spectatorRow.FinalStatus);

        Assert.Equal(1, fixture.CleanupCount);
        Assert.True(runtime.IsTerminal);
        Assert.Null(fixture.Store.Get(matchingId));
        Assert.Single(fixture.SummaryFiles);
        Assert.All(recipientIds, playerId =>
            Assert.Equal(1, fixture.LifecycleDispatchCounts.GetValueOrDefault(playerId)));

        string[] timeline = fixture.Timeline.ToArray();
        int lastPacket = Array.FindLastIndex(timeline, entry => entry.StartsWith("packet:", StringComparison.Ordinal));
        int clear = Array.IndexOf(timeline, "clear");
        int firstLifecycle = Array.FindIndex(timeline, entry => entry.StartsWith("lifecycle:", StringComparison.Ordinal));
        int lastLifecycle = Array.FindLastIndex(timeline, entry => entry.StartsWith("lifecycle:", StringComparison.Ordinal));
        int summary = Array.IndexOf(timeline, "summary");
        Assert.True(lastPacket >= 0 && lastPacket < clear, string.Join(" | ", timeline));
        Assert.True(clear < firstLifecycle, string.Join(" | ", timeline));
        Assert.True(lastLifecycle < summary, string.Join(" | ", timeline));
        Assert.False(fixture.LockHeldDuringLifecycle ?? true);
    }

    [Fact]
    public async Task ConcurrentTryEndMatch_FinalizesAndPublishesExactlyOnce()
    {
        const long matchingId = 73002;
        using var fixture = new TerminalFixture();
        RecordingSession[] sessions =
        [
            fixture.CreateSession(matchingId, 111, PlayerMatchStatus.ACTIVE),
            fixture.CreateSession(matchingId, 222, PlayerMatchStatus.ACTIVE),
            fixture.CreateSession(matchingId, 333, PlayerMatchStatus.ACTIVE)
        ];
        fixture.SeedRoster(matchingId, sessions, totalEntries: sessions.Length, useLongProfiles: false);
        fixture.Store.GetOrCreate(matchingId);

        using var start = new ManualResetEventSlim();
        Task[] finalizers = sessions.Select(session => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            session.TryEndMatch(session.PlayerId!.Value, "concurrent_terminal_behavior");
        })).ToArray();
        start.Set();
        await Task.WhenAll(finalizers).WaitAsync(TimeSpan.FromSeconds(5));

        long publishedWinnerId = fixture.ConnectionFor(sessions[0])
            .DeserializeSingle<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT)
            .WinnerId;
        Assert.Contains(publishedWinnerId, sessions.Select(session => session.PlayerId!.Value));
        foreach (RecordingSession session in sessions)
        {
            RecordingTcpConnection connection = fixture.ConnectionFor(session);
            G_TO_C_GAME_RESULT result =
                connection.DeserializeSingle<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT);
            G_TO_C_GAME_END gameEnd =
                connection.DeserializeSingle<G_TO_C_GAME_END>(Protocol.G_TO_C_GAME_END);
            Assert.Equal(publishedWinnerId, result.WinnerId);
            Assert.Equal(matchingId, gameEnd.MatchingId);
            Assert.Equal(session.PlayerId == publishedWinnerId, gameEnd.IsEscaped);
            Assert.Equal(
                [Protocol.G_TO_C_GAME_RESULT, Protocol.G_TO_C_GAME_END],
                connection.AttemptedProtocols);
            Assert.Equal(1, fixture.LifecycleDispatchCounts.GetValueOrDefault(session.PlayerId!.Value));
        }

        Assert.Equal(1, fixture.CleanupCount);
        Assert.Single(fixture.SummaryFiles);
        Assert.Null(fixture.Store.Get(matchingId));
    }

    [Fact]
    public void TerminalPublication_FailuresAreIsolatedPerResultEndAndMarkStep_AndCleanupStillRuns()
    {
        const long matchingId = 73003;
        using var fixture = new TerminalFixture();
        RecordingSession markFailure =
            fixture.CreateSession(matchingId, 121, PlayerMatchStatus.ACTIVE);
        RecordingSession resultFailure =
            fixture.CreateSession(matchingId, 242, PlayerMatchStatus.ACTIVE);
        RecordingSession endFailure =
            fixture.CreateSession(matchingId, 363, PlayerMatchStatus.SPECTATING);
        RecordingSession[] sessions = [markFailure, resultFailure, endFailure];
        fixture.SeedRoster(matchingId, sessions, totalEntries: 8, useLongProfiles: true);
        fixture.Store.GetOrCreate(matchingId);
        fixture.ThrowPrepareCompletionForPlayerId = markFailure.PlayerId;
        fixture.ConnectionFor(resultFailure).ThrowOnceOn = Protocol.G_TO_C_GAME_RESULT;
        fixture.ConnectionFor(endFailure).ThrowOnceOn = Protocol.G_TO_C_GAME_END;

        markFailure.TryEndMatch(markFailure.PlayerId!.Value, "isolated_terminal_failures");

        Assert.Single(fixture.ConnectionFor(markFailure)
            .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT));

        RecordingTcpConnection resultFailureToken = fixture.ConnectionFor(resultFailure);
        Assert.Equal(
            1,
            resultFailureToken.AttemptedProtocols.Count(protocol => protocol == Protocol.G_TO_C_GAME_RESULT));
        Assert.Empty(resultFailureToken.DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT));
        Assert.Contains(Protocol.G_TO_C_GAME_END, resultFailureToken.DeliveredProtocols);

        RecordingTcpConnection endFailureToken = fixture.ConnectionFor(endFailure);
        Assert.Equal(1, endFailureToken.DeliveredProtocols.Count(
            protocol => protocol == Protocol.G_TO_C_GAME_RESULT));
        Assert.Contains(Protocol.G_TO_C_GAME_END, endFailureToken.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_GAME_END, endFailureToken.DeliveredProtocols);

        Assert.Equal(0, fixture.LifecycleDispatchCounts.GetValueOrDefault(markFailure.PlayerId!.Value));
        Assert.Equal(1, fixture.LifecycleDispatchCounts.GetValueOrDefault(resultFailure.PlayerId!.Value));
        Assert.Equal(1, fixture.LifecycleDispatchCounts.GetValueOrDefault(endFailure.PlayerId!.Value));
        Assert.All(sessions, session => Assert.True(session.IsGameEnded));
        Assert.Equal(1, fixture.CleanupCount);
        Assert.Single(fixture.SummaryFiles);
        Assert.Null(fixture.Store.Get(matchingId));

        TerminalDelivery[] deliveries = fixture.Deliveries.ToArray();
        int lastResult = Array.FindLastIndex(
            deliveries,
            delivery => delivery.Protocol == Protocol.G_TO_C_GAME_RESULT);
        int firstEnd = Array.FindIndex(
            deliveries,
            delivery => delivery.Protocol == Protocol.G_TO_C_GAME_END);
        Assert.True(lastResult >= 0 && lastResult < firstEnd);
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

    private sealed class TerminalFixture : IDisposable
    {
        private readonly List<GameClientSession> _sessions = [];
        private readonly Dictionary<GameClientSession, RecordingTcpConnection> _connections = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-terminal-publication-tests",
            Guid.NewGuid().ToString("N"));
        private long _deliverySequence;
        private int _cleanupCount;

        public TerminalFixture()
        {
            Logger = new TimelineLogger(Timeline);
            Store = new MatchRuntimeStore(
                Logger,
                cleanupSteps:
                [
                    new MatchCleanupStep("clear", _ =>
                    {
                        Timeline.Enqueue("clear");
                        Interlocked.Increment(ref _cleanupCount);
                    })
                ]);

        }

        public MatchRuntimeStore Store { get; }
        public ConcurrentQueue<string> Timeline { get; } = new();
        public ConcurrentDictionary<long, int> LifecycleDispatchCounts { get; } = new();
        public TimelineLogger Logger { get; }

        public GameEventLogManager EventLog { get; } = TestGameEventLogs.Create();
        public MatchSummaryFileStore Summaries => new(_summaryDirectory);
        public long? ThrowPrepareCompletionForPlayerId { get; set; }
        public MatchRuntime? TrackedRuntime { get; set; }
        public bool? LockHeldDuringLifecycle { get; private set; }
        public int CleanupCount => Volatile.Read(ref _cleanupCount);
        public IReadOnlyList<string> SummaryFiles => Directory.Exists(_summaryDirectory)
            ? Directory.EnumerateFiles(_summaryDirectory, "match-*.json").ToList()
            : [];
        public IReadOnlyList<TerminalDelivery> Deliveries => _connections.Values
            .SelectMany(connection => connection.Deliveries)
            .OrderBy(delivery => delivery.Sequence)
            .ToList();

        public RecordingSession CreateSession(
            long matchingId,
            long playerId,
            PlayerMatchStatus status)
        {
            var connection = new RecordingTcpConnection(playerId, RecordDelivery);
            Activate(connection);
            var session = new RecordingSession(
                connection,
                Logger,
                _sessions,

                EventLog,
                Summaries,
                Store,
                PrepareGameCompletion);
            SetIdentity(session, matchingId, playerId, status);
            _sessions.Add(session);
            _connections.Add(session, connection);
            return session;
        }

        public RecordingTcpConnection ConnectionFor(GameClientSession session) => _connections[session];

        public void SeedRoster(
            long matchingId,
            IReadOnlyList<RecordingSession> humanSessions,
            int totalEntries,
            bool useLongProfiles)
        {
            Assert.True(totalEntries >= humanSessions.Count);
            var entries = humanSessions
                .Select(session => new RosterEntry
                {
                    PlayerId = session.PlayerId!.Value,
                    Status = session.PlayerMatchStatus
                })
                .ToList();
            for (int index = entries.Count; index < totalEntries; index++)
            {
                entries.Add(new RosterEntry
                {
                    PlayerId = 80_000 + index,
                    Status = PlayerMatchStatus.ACTIVE
                });
            }

            Store.GetOrCreate(matchingId);
            for (int index = 0; index < entries.Count; index++)
            {
                RosterEntry entry = entries[index];
                Store.GetRequired(matchingId).Roster.RegisterEntry(entry);
                string name = useLongProfiles
                    ? $"Player{entry.PlayerId}_{new string('x', 300)}"
                    : $"Player{entry.PlayerId}";
                Store.GetRequired(matchingId).Roster.UpdatePlayerProfile(
                    entry.PlayerId,
                    name,
                    [1001, 1002, 1003, 1004]);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private Action? PrepareGameCompletion(long playerId, long matchingId)
        {
            if (ThrowPrepareCompletionForPlayerId == playerId)
                throw new InvalidOperationException($"completion preparation failed for {playerId}");

            return () =>
            {
                if (TrackedRuntime != null)
                    LockHeldDuringLifecycle = Monitor.IsEntered(TrackedRuntime.Sync);
                LifecycleDispatchCounts.AddOrUpdate(playerId, 1, static (_, count) => count + 1);
                Timeline.Enqueue($"lifecycle:{playerId}");
            };
        }

        private void RecordDelivery(long playerId, Protocol protocol, byte[] wireBytes)
        {
            long sequence = Interlocked.Increment(ref _deliverySequence);
            Timeline.Enqueue($"packet:{protocol}:{playerId}");
            _connections.Values.FirstOrDefault(connection => connection.PlayerId == playerId)?.RecordDelivery(
                new TerminalDelivery(sequence, playerId, protocol, wireBytes));
        }

        private static void SetIdentity(
            GameClientSession session,
            long matchingId,
            long playerId,
            PlayerMatchStatus status)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
            SetProperty(session, nameof(GameClientSession.CurrentArea), Config.SWARM_MATCH_GROUND_AREA);
            SetProperty(session, nameof(GameClientSession.PlayerMatchStatus), status);
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
            typeof(TcpConnection).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        }
    }

    private sealed class RecordingSession : GameClientSession
    {
        public RecordingSession(
            TcpConnection connection,
            ILogger logger,
            List<GameClientSession> sessions,

            GameEventLogManager eventLog,
            MatchSummaryFileStore summaries,
            MatchRuntimeStore matchRuntimes,
            Func<long, long, Action?> prepareGameCompletion)
            : base(
                connection,
                logger,
                null!,
                static _ => Task.FromResult<GameEntryContext?>(null),
                TestGameSessionServices.CreateLeaveHandler(),
                static (_, _) => null,
                matchingId => sessions
                    .Where(session => session.MatchingId == matchingId)
                    .ToList(),

                eventLog,
                TestGameSessionServices.CreateEliminationService(matchRuntimes, eventLog, summaries, GameServerDevOptions.Disabled,
                    id => sessions.Where(session => session.MatchingId == id).ToList(), logger),
                matchRuntimes,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                new FakeGameSessionLifecycle(prepareGameCompletion),
                static () => false,
                new FakeMatchEntryFailureHandler(),
                GameServerDevOptions.Disabled,
                new GameMatchEntryService(null!, matchRuntimes, GameServerDevOptions.Disabled, NullLogger.Instance),
                new ItemCombinationService(eventLog),
                new MovementValidationService(NullLogger<MovementValidationService>.Instance))
        {
        }
    }

    private sealed class RecordingTcpConnection(
        long playerId,
        Action<long, Protocol, byte[]> onDelivered) : TcpConnection
    {
        private readonly object _gate = new();
        private readonly List<Protocol> _attempted = [];
        private readonly List<TerminalDelivery> _deliveries = [];
        private int _thrown;

        public long PlayerId { get; } = playerId;
        public Protocol? ThrowOnceOn { get; set; }
        public IReadOnlyList<TerminalDelivery> Deliveries
        {
            get
            {
                lock (_gate)
                    return _deliveries.ToList();
            }
        }
        public IReadOnlyList<Protocol> AttemptedProtocols
        {
            get
            {
                lock (_gate)
                    return _attempted.ToList();
            }
        }
        public IReadOnlyList<Protocol> DeliveredProtocols =>
            Deliveries.Select(delivery => delivery.Protocol).ToList();

        public override bool TrySend(Packet msg)
        {
            msg.RecordSize();
            var protocol = (Protocol)msg.ProtocolId;
            lock (_gate)
                _attempted.Add(protocol);

            if (ThrowOnceOn == protocol && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException($"transport failed for {protocol}");

            byte[] wireBytes = msg.ToBytes();
            onDelivered(PlayerId, protocol, wireBytes);
            return true;
        }

        public void RecordDelivery(TerminalDelivery delivery)
        {
            lock (_gate)
                _deliveries.Add(delivery);
        }

        public T DeserializeSingle<T>(Protocol protocol) =>
            Assert.Single(DeserializeAll<T>(protocol));

        public IReadOnlyList<T> DeserializeAll<T>(Protocol protocol) => Deliveries
            .Where(delivery => delivery.Protocol == protocol)
            .OrderBy(delivery => delivery.Sequence)
            .Select(delivery => Deserialize<T>(delivery.WireBytes, protocol))
            .ToList();
    }

    private sealed class TimelineLogger(ConcurrentQueue<string> timeline) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (message.StartsWith("Match summary persisted:", StringComparison.Ordinal))
                timeline.Enqueue("summary");
        }
    }

    private static T Deserialize<T>(byte[] wireBytes, Protocol protocol)
    {
        using var packet = Packet.Create(wireBytes);
        Assert.Equal((int)protocol, packet.PopProtocolId());
        _ = packet.PopPlayerId();
        return MessagePackSerializer.Deserialize<T>(packet.PopBody());
    }

    private sealed record TerminalDelivery(
        long Sequence,
        long PlayerId,
        Protocol Protocol,
        byte[] WireBytes);
}
