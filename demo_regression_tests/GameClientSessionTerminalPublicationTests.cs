using System.Collections.Concurrent;
using System.Reflection;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.contracts.authentication;
using network.core;
using network.packets;
using network.utils;

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
    public async Task TryEndMatch_WaitsForMatchLock_ThenPublishesChunkMajorBeforeCleanupLifecycleAndSummary()
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
            Assert.All(sessions, session => Assert.Empty(fixture.TokenFor(session).AttemptedProtocols));

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

        IReadOnlyList<G_TO_C_GAME_RESULT> winnerChunks = fixture.TokenFor(winner)
            .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT);
        Assert.True(winnerChunks.Count > 1);
        Assert.Equal(
            Enumerable.Range(0, winnerChunks.Count),
            winnerChunks.Select(chunk => chunk.ResultChunkIndex));
        Assert.All(winnerChunks, chunk =>
        {
            Assert.Equal(winner.PlayerId, chunk.WinnerId);
            Assert.False(chunk.IsTimeout);
        });

        long[] recipientIds = sessions.Select(session => session.PlayerId!.Value).ToArray();
        var expectedChunkMajorOrder = Enumerable.Range(0, winnerChunks.Count)
            .SelectMany(chunkIndex => recipientIds.Select(playerId => (playerId, chunkIndex)))
            .ToArray();
        Assert.Equal(
            expectedChunkMajorOrder,
            fixture.Deliveries
                .Where(delivery => delivery.Protocol == Protocol.G_TO_C_GAME_RESULT)
                .Select(delivery => (delivery.PlayerId, delivery.ResultChunkIndex!.Value))
                .ToArray());
        Assert.Equal(
            Enumerable.Repeat(Protocol.G_TO_C_GAME_RESULT, winnerChunks.Count * sessions.Length)
                .Concat(Enumerable.Repeat(Protocol.G_TO_C_GAME_END, sessions.Length)),
            fixture.Deliveries.Select(delivery => delivery.Protocol));

        foreach (RecordingSession session in sessions)
        {
            RecordingUserToken token = fixture.TokenFor(session);
            IReadOnlyList<G_TO_C_GAME_RESULT> chunks =
                token.DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT);
            Assert.Equal(winnerChunks.Count, chunks.Count);
            Assert.Equal(
                winnerChunks.Select(chunk => chunk.ResultChunkIndex),
                chunks.Select(chunk => chunk.ResultChunkIndex));

            G_TO_C_GAME_END gameEnd =
                token.DeserializeSingle<G_TO_C_GAME_END>(Protocol.G_TO_C_GAME_END);
            Assert.Equal(matchingId, gameEnd.MatchingId);
            Assert.Equal(session.PlayerId == winner.PlayerId, gameEnd.IsEscaped);
            Assert.True(session.IsGameEnded);
        }

        GameResultPlayerInfo winnerRow = Assert.Single(
            winnerChunks.SelectMany(chunk => chunk.Players),
            player => player.PlayerId == winner.PlayerId);
        GameResultPlayerInfo spectatorRow = Assert.Single(
            fixture.TokenFor(spectator)
                .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT)
                .SelectMany(chunk => chunk.Players),
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

        long publishedWinnerId = fixture.TokenFor(sessions[0])
            .DeserializeSingle<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT)
            .WinnerId;
        Assert.Contains(publishedWinnerId, sessions.Select(session => session.PlayerId!.Value));
        foreach (RecordingSession session in sessions)
        {
            RecordingUserToken token = fixture.TokenFor(session);
            G_TO_C_GAME_RESULT result =
                token.DeserializeSingle<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT);
            G_TO_C_GAME_END gameEnd =
                token.DeserializeSingle<G_TO_C_GAME_END>(Protocol.G_TO_C_GAME_END);
            Assert.Equal(publishedWinnerId, result.WinnerId);
            Assert.Equal(matchingId, gameEnd.MatchingId);
            Assert.Equal(session.PlayerId == publishedWinnerId, gameEnd.IsEscaped);
            Assert.Equal(
                [Protocol.G_TO_C_GAME_RESULT, Protocol.G_TO_C_GAME_END],
                token.AttemptedProtocols);
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
        fixture.TokenFor(resultFailure).ThrowOnceOn = Protocol.G_TO_C_GAME_RESULT;
        fixture.TokenFor(endFailure).ThrowOnceOn = Protocol.G_TO_C_GAME_END;

        markFailure.TryEndMatch(markFailure.PlayerId!.Value, "isolated_terminal_failures");

        int chunkCount = fixture.TokenFor(markFailure)
            .DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT)
            .Count;
        Assert.True(chunkCount > 1);

        RecordingUserToken resultFailureToken = fixture.TokenFor(resultFailure);
        Assert.Equal(
            chunkCount,
            resultFailureToken.AttemptedProtocols.Count(protocol => protocol == Protocol.G_TO_C_GAME_RESULT));
        IReadOnlyList<G_TO_C_GAME_RESULT> deliveredAfterFailure =
            resultFailureToken.DeserializeAll<G_TO_C_GAME_RESULT>(Protocol.G_TO_C_GAME_RESULT);
        Assert.Equal(chunkCount - 1, deliveredAfterFailure.Count);
        Assert.Contains(deliveredAfterFailure, chunk => chunk.ResultChunkIndex > 0);
        Assert.Contains(Protocol.G_TO_C_GAME_END, resultFailureToken.DeliveredProtocols);

        RecordingUserToken endFailureToken = fixture.TokenFor(endFailure);
        Assert.Equal(chunkCount, endFailureToken.DeliveredProtocols.Count(
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
        private readonly Dictionary<GameClientSession, RecordingUserToken> _tokens = [];
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

            Interactables.Initialize();
            Inventories.Initialize();
        }

        public MatchRuntimeStore Store { get; }
        public ConcurrentQueue<string> Timeline { get; } = new();
        public ConcurrentDictionary<long, int> LifecycleDispatchCounts { get; } = new();
        public TimelineLogger Logger { get; }
        public InteractableStateManager Interactables { get; } = new();
        public InGameInventoryManager Inventories { get; } = new();
        public AreaItemStockManager AreaStocks { get; } = new(false);
        public GroundItemManager GroundItems { get; } = new();
        public SummonStoneManager SummonStones { get; } = new();
        public DoorStateManager Doors { get; } = new();
        public MatchRosterManager Roster { get; } = new(NullLogger.Instance);
        public AreaClosureManager Closures { get; } = new(NullLogger.Instance);
        public BotPlayerManager Bots { get; } = new(NullLogger.Instance);
        public GameEventLogManager EventLog { get; } = new();
        public MatchSummaryFileStore Summaries => new(_summaryDirectory);
        public EncounterRevealManager Encounters { get; } = new();
        public long? ThrowPrepareCompletionForPlayerId { get; set; }
        public MatchRuntime? TrackedRuntime { get; set; }
        public bool? LockHeldDuringLifecycle { get; private set; }
        public int CleanupCount => Volatile.Read(ref _cleanupCount);
        public IReadOnlyList<string> SummaryFiles => Directory.Exists(_summaryDirectory)
            ? Directory.EnumerateFiles(_summaryDirectory, "match-*.json").ToList()
            : [];
        public IReadOnlyList<TerminalDelivery> Deliveries => _tokens.Values
            .SelectMany(token => token.Deliveries)
            .OrderBy(delivery => delivery.Sequence)
            .ToList();

        public RecordingSession CreateSession(
            long matchingId,
            long playerId,
            PlayerMatchStatus status)
        {
            var token = new RecordingUserToken(playerId, RecordDelivery);
            Activate(token);
            var session = new RecordingSession(
                token,
                Logger,
                _sessions,
                Interactables,
                Inventories,
                AreaStocks,
                GroundItems,
                SummonStones,
                Doors,
                Roster,
                Closures,
                Bots,
                EventLog,
                Summaries,
                Encounters,
                Store,
                PrepareGameCompletion);
            SetIdentity(session, matchingId, playerId, status);
            _sessions.Add(session);
            _tokens.Add(session, token);
            return session;
        }

        public RecordingUserToken TokenFor(GameClientSession session) => _tokens[session];

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

            for (int index = 0; index < entries.Count; index++)
            {
                RosterEntry entry = entries[index];
                entry.TargetPlayerId = entries[(index + 1) % entries.Count].PlayerId;
                Roster.RegisterEntry(matchingId, entry);
                string name = useLongProfiles
                    ? $"Player{entry.PlayerId}_{new string('x', 300)}"
                    : $"Player{entry.PlayerId}";
                Roster.UpdatePlayerProfile(
                    matchingId,
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
            int? resultChunkIndex = protocol == Protocol.G_TO_C_GAME_RESULT
                ? Deserialize<G_TO_C_GAME_RESULT>(wireBytes, protocol).ResultChunkIndex
                : null;
            long sequence = Interlocked.Increment(ref _deliverySequence);
            Timeline.Enqueue($"packet:{protocol}:{playerId}:{resultChunkIndex?.ToString() ?? "-"}");
            _tokens.Values.FirstOrDefault(token => token.PlayerId == playerId)?.RecordDelivery(
                new TerminalDelivery(sequence, playerId, protocol, resultChunkIndex, wireBytes));
        }

        private static void SetIdentity(
            GameClientSession session,
            long matchingId,
            long playerId,
            PlayerMatchStatus status)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);
            SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
            SetProperty(session, nameof(GameClientSession.CurrentArea), Config.SWARM_MATCH_GROUND_AREA);
            SetProperty(session, nameof(GameClientSession.PlayerMatchStatus), status);
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
            typeof(UserToken).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(token, active);
        }
    }

    private sealed class RecordingSession : GameClientSession
    {
        public RecordingSession(
            UserToken token,
            ILogger logger,
            List<GameClientSession> sessions,
            InteractableStateManager interactables,
            InGameInventoryManager inventories,
            AreaItemStockManager areaStocks,
            GroundItemManager groundItems,
            SummonStoneManager summonStones,
            DoorStateManager doors,
            MatchRosterManager roster,
            AreaClosureManager closures,
            BotPlayerManager bots,
            GameEventLogManager eventLog,
            MatchSummaryFileStore summaries,
            EncounterRevealManager encounters,
            MatchRuntimeStore matchRuntimes,
            Func<long, long, Action?> prepareGameCompletion)
            : base(
                token,
                null!,
                logger,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                (_, matchingId) => sessions
                    .Where(session => session.CurrentMapSubId == matchingId)
                    .ToList(),
                interactables,
                inventories,
                areaStocks,
                groundItems,
                summonStones,
                doors,
                roster,
                closures,
                bots,
                eventLog,
                summaries,
                encounters,
                matchRuntimes,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                static (_, _) => { },
                prepareGameCompletion,
                static (_, _) => { },
                static () => false,
                static _ => { })
        {
        }
    }

    private sealed class RecordingUserToken(
        long playerId,
        Action<long, Protocol, byte[]> onDelivered) : UserToken
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

        public override void Send(Packet msg)
        {
            msg.RecordSize();
            var protocol = (Protocol)msg.ProtocolId;
            lock (_gate)
                _attempted.Add(protocol);

            if (ThrowOnceOn == protocol && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException($"transport failed for {protocol}");

            byte[] wireBytes = msg.ToBytes();
            onDelivered(PlayerId, protocol, wireBytes);
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
        using var packet = Packet.Create(new Const<byte[]>(wireBytes));
        Assert.Equal((int)protocol, packet.PopProtocolId());
        _ = packet.PopPlayerId();
        return MessagePackSerializer.Deserialize<T>(packet.PopBody());
    }

    private sealed record TerminalDelivery(
        long Sequence,
        long PlayerId,
        Protocol Protocol,
        int? ResultChunkIndex,
        byte[] WireBytes);
}
